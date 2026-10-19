# -*- coding: utf-8 -*-
"""生成「只读 TOC 布局」探针 Lua。

要回答的问题：**内存里的 unit TOC 副本，结构头（0x5c/0x64/0x70）是否与文件一致**。
如果一致，运行时就能自己从 TOC 走出 section 偏移，不必读构建期的 sections.json；
如果不一致，阶段 3 必须退回"运行时读映射表"的方案。

探针**不写任何内存**：没有 WriteProcessMemory，不 VirtualProtect，不动几何。
它只定位副本、读结构头、走一遍 mesh 表，然后把结论写到
`%LOCALAPPDATA%\\CowboyBingus\\Helldivers2\\ArmorRegistry\\section-layout.status`。

与 make_section_toggle_probe.py 的区别：
  * 那边把 LOD 的 section 表起点硬编码成常量（只对某一次构建有效）；
  * 这边**从 TOC 自己走出**每个 mesh 的 lod / numSections / sectionsOffset，
    所以任何 patch 都能直接生成，同时也顺带验证了这套偏移对不对。

用法：

    python make_section_layout_probe.py --patch <patch_0> \
        --install "%LOCALAPPDATA%\\MDL\\Helldivers2\\Mods\\section_layout_probe\\mod.lua"
"""

import argparse
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import find_unit_toc

sys.stdout.reconfigure(encoding='utf-8')

LUA_TEMPLATE = r'''-- HD2-Addon: __RESOURCE__
-- 只读探针：定位 Unit TOC 副本，读结构头并走一遍 mesh 表。
-- 不写内存、不改保护属性、不动几何；唯一副作用是写一个 status 文本。
local ffi=require('ffi')
if ffi.os~='Windows' or not ffi.abi('64bit') then return end
ffi.cdef[[
void *GetCurrentProcess(void);
int ReadProcessMemory(void *,const void *,void *,size_t,size_t *);
size_t VirtualQuery(const void *,void *,size_t);
]]
local kernel=ffi.load('kernel32')
local process=kernel.GetCurrentProcess()

local SIG=__SIGNATURE__
local SIG_OFFSET=__SIG_OFFSET__
local UNIT=__UNIT__
local EXPECT={stream=__STREAM__,mesh=__MESH__,materials=__MATERIALS__,
  meshes=__MESH_COUNT__,layout={__LAYOUT__}}

local lines={}
local function log(s) lines[#lines+1]=tostring(s) end

local function read(address,size)
    if not address or address<65536 or size<1 or size>0x800000 then return nil end
    local buffer=ffi.new('uint8_t[?]',size)
    local got=ffi.new('size_t[1]')
    if kernel.ReadProcessMemory(process,ffi.cast('const void *',address),
        buffer,size,got)==0 or tonumber(got[0])~=size then return nil end
    return ffi.string(buffer,size)
end

local function u32(data,at)
    if not data or at<0 or at+4>#data then return nil end
    local a,b,c,d=data:byte(at+1,at+4)
    return a+b*256+c*65536+d*16777216
end

local function i32(data,at)
    local value=u32(data,at)
    if value and value>=2147483648 then return value-4294967296 end
    return value
end

-- 64 位材质 ID 必须按字节拼 hex，**不能**先拼成 Lua number。
-- Lua 5.1/LuaJIT 的 number 是 double，只有 53 位精度：
-- high*2^32 一过 2^53，低位就被悄悄截断（实测 0xa648fd251c740b12 会打印成 ...0b00）。
-- 这个坑不会报错，只会让"指纹对不上"变成一个查不出原因的谜。
local function hex64(data,at)
    if not data or at<0 or at+8>#data then return nil end
    local out={}
    for index=8,1,-1 do out[#out+1]=string.format('%02x',data:byte(at+index)) end
    return table.concat(out)
end

local function hex(bytes)
    if not bytes then return '<unreadable>' end
    local out={}
    for index=1,#bytes do out[#out+1]=string.format('%02x',bytes:byte(index)) end
    return table.concat(out)
end

local function status_path()
    local base=os.getenv('LOCALAPPDATA')
    if not base then return nil end
    return base..'\\CowboyBingus\\Helldivers2\\ArmorRegistry\\section-layout.status'
end

local function flush()
    local path=status_path()
    if not path then return end
    local handle=io.open(path,'wb')
    if not handle then return end
    handle:write(table.concat(lines,'\n'))
    handle:close()
end

-- 结构头必须与文件一致，否则 base 就不是我们要的那份副本（或者头部被改写过）。
local function header_matches(base)
    local header=read(base,0x80)
    if not header then log('header unreadable') return nil end
    local stream=u32(header,0x5c)
    local mesh=u32(header,0x64)
    local materials=u32(header,0x70)
    log(string.format('candidate base 0x%x stream=0x%x mesh=0x%x materials=0x%x',
        base,stream or 0,mesh or 0,materials or 0))
    if stream==EXPECT.stream and mesh==EXPECT.mesh and materials==EXPECT.materials then
        return header
    end
    return nil
end

local function describe(base,header)
    log('header128 '..hex(header))
    log(string.format('streamInfoOffset=0x%x meshInfoOffset=0x%x materialsOffset=0x%x',
        u32(header,0x5c),u32(header,0x64),u32(header,0x70)))
    local mesh_info=u32(header,0x64)
    local materials_offset=u32(header,0x70)

    local mesh_count=u32(read(base+mesh_info,4),0)
    log('meshes='..tostring(mesh_count))
    if mesh_count~=EXPECT.meshes then log('MISMATCH mesh count') end

    local table_data=read(base+mesh_info+4,math.max(mesh_count,1)*4)
    if not table_data then log('mesh offset table unreadable') return false end

    local matched=0
    for index=0,mesh_count-1 do
        local relative=u32(table_data,index*4)
        if not relative then break end
        local mesh=base+mesh_info+relative
        local record=read(mesh,140)
        if not record then
            log(string.format('m%d unreadable',index))
        else
            local lod=i32(record,56)
            local num_materials=u32(record,104)
            local num_sections=u32(record,120)
            local sections_offset=u32(record,124)
            log(string.format('m%d off=0x%x lod=%d nmat=%d nsec=%d secOff=%d',
                index,relative,lod or -99,num_materials or -1,num_sections or -1,sections_offset or -1))
            local expected=EXPECT.layout[index+1]
            if expected and expected.lod==lod and expected.nsec==num_sections then
                matched=matched+1
            end
            -- 首个 LOD 网格的首节，用来和离线映射表比对。
            if index==0 and num_sections and num_sections>=1 and sections_offset then
                local section=read(mesh+sections_offset,24)
                if section then
                    log(string.format('  first section materialIndex=%d numIndices=%d groupIndex=%d',
                        u32(section,0) or -1,u32(section,16) or -1,u32(section,20) or -1))
                end
            end
        end
    end
    log(string.format('layout matched %d/%d',matched,EXPECT.meshes))

    local material_count=u32(read(base+materials_offset,4),0)
    log('materials='..tostring(material_count))
    if material_count and material_count>0 and material_count<=512 then
        local ids=read(base+materials_offset+4,material_count*4)
        local values=read(base+materials_offset+4+material_count*4,material_count*8)
        if ids and values then
            for index=0,math.min(material_count,3)-1 do
                log(string.format('  mat%d sectionId=%d materialId=0x%s',
                    index,u32(ids,index*4) or 0,hex64(values,index*8) or '?' ))
            end
        else
            log('  material table unreadable')
        end
    end

    if matched==EXPECT.meshes and mesh_count==EXPECT.meshes then
        log('VERDICT=PASS unit='..UNIT)
    else
        log('VERDICT=FAIL unit='..UNIT..' matched='..matched)
    end
    return true
end

log('layout probe start (read-only)')
log('unit '..UNIT)
log('signature offset '..string.format('0x%x',SIG_OFFSET))
flush()

local cursor=0
local done=false
local reported=false

local function step()
    local budget=32
    while budget>0 and not done do
        local mbi=ffi.new('uint8_t[48]')
        if kernel.VirtualQuery(ffi.cast('const void *',cursor),mbi,48)==0 then
            log('region enumeration ended')
            if not reported then log('VERDICT=NOT_FOUND signature not found') reported=true end
            return true
        end
        local base=tonumber(ffi.cast('uintptr_t *',mbi)[0])
        local size=tonumber(ffi.cast('size_t *',mbi+24)[0]) or 0
        local state=tonumber(ffi.cast('uint32_t *',mbi+32)[0]) or 0
        local protect=tonumber(ffi.cast('uint32_t *',mbi+36)[0]) or 0
        local kind=tonumber(ffi.cast('uint32_t *',mbi+40)[0]) or 0
        local p=protect%256
        local readable=state==0x1000 and math.floor(protect/256)%2==0
            and (p==2 or p==4 or p==8 or p==32 or p==64 or p==128)
        if readable and kind==0x20000 and size>=0x4000 and size<=0x4000000 then
            local chunk=read(base,math.min(size,0x800000))
            if chunk then
                local at=1
                while true do
                    local found=string.find(chunk,SIG,at,true)
                    if not found then break end
                    local hit=base+found-1
                    local candidate=hit-SIG_OFFSET
                    log(string.format('signature hit 0x%x base 0x%x',hit,candidate))
                    local header=header_matches(candidate)
                    if header then
                        describe(candidate,header)
                        done=true
                        flush()
                        return true
                    end
                    at=found+1
                end
            end
        end
        cursor=base+math.max(size,0x1000)
        budget=budget-1
    end
    if done then return true end
    if cursor>=0x0000800000000000 then
        log('address space exhausted')
        if not reported then log('VERDICT=NOT_FOUND address space exhausted') reported=true end
        flush()
        return true
    end
    return false
end

-- MDL 用环境代理捕获对 update 的赋值，所以这里必须用普通赋值；rawset 会绕过代理。
local previous=update
if type(previous)~='function' then previous=function() end end
local finished=false
update=function(dt,...)
    if not finished then
        local ok,err=pcall(step)
        if not ok then log('step error: '..tostring(err)) flush() finished=true
        elseif err then finished=true flush() end
    end
    return previous(dt,...)
end
log('update callback installed')
flush()
'''


def u32(data, at):
    return struct.unpack_from('<I', data, at)[0]


def i32(data, at):
    return struct.unpack_from('<i', data, at)[0]


def lua_byte_string(data):
    return '"' + ''.join('\\%d' % byte for byte in data) + '"'


def walk_unit(toc):
    """按 UnitMeshReader 的偏移走出结构头与每个 mesh。返回 dict 或 None。"""
    if len(toc) < 0x80:
        return None
    stream_info = u32(toc, 0x5C)
    mesh_info = u32(toc, 0x64)
    materials_offset = u32(toc, 0x70)
    if not (0 < mesh_info < len(toc) - 8):
        return None
    mesh_count = u32(toc, mesh_info)
    if not (0 < mesh_count <= 64):
        return None
    meshes = []
    for index in range(mesh_count):
        relative = u32(toc, mesh_info + 4 + index * 4)
        mesh = mesh_info + relative
        if mesh + 140 > len(toc):
            return None
        num_sections = u32(toc, mesh + 120)
        sections_offset = u32(toc, mesh + 124)
        sections_at = mesh + sections_offset
        if sections_at + num_sections * 24 > len(toc):
            return None
        sections = []
        for section_index in range(num_sections):
            record = sections_at + section_index * 24
            material_index, _, _, _, num_indices, group_index = struct.unpack_from('<6I', toc, record)
            sections.append((record, material_index, num_indices, group_index))
        meshes.append({
            'index': index,
            'relative': relative,
            'lod': i32(toc, mesh + 56),
            'num_materials': u32(toc, mesh + 104),
            'num_sections': num_sections,
            'sections_offset': sections_offset,
            'sections_at': sections_at,
            'sections': sections,
        })
    return {
        'stream_info': stream_info,
        'mesh_info': mesh_info,
        'materials_offset': materials_offset,
        'mesh_count': mesh_count,
        'meshes': meshes,
    }


def pick_signature(toc, layout):
    """优先用"section 记录最多"的那张表的头 48 字节做签名。"""
    candidates = sorted(layout['meshes'], key=lambda m: (-m['num_sections'], m['index']))
    for mesh in candidates:
        if mesh['num_sections'] < 2:
            continue
        window = toc[mesh['sections_at']:mesh['sections_at'] + 48]
        if len(window) == 48 and toc.count(window) == 1:
            return mesh['sections_at'], window
    return None, None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--patch', required=True)
    parser.add_argument('--file-id', help='不指定则自动挑 section 最多的 unit')
    parser.add_argument('--install', help='直接写到这个 mod.lua 路径')
    parser.add_argument('--dump-lua')
    parser.add_argument('--resource', default='mods/codex/section_layout_probe')
    args = parser.parse_args()

    patch = open(args.patch, 'rb').read()
    entries = find_unit_toc.parse_entries(patch)
    units = [e for e in entries if e[1] == find_unit_toc.UNIT_TYPE_ID]
    if not units:
        raise SystemExit('patch 里没有 Unit 条目')

    chosen = None
    for file_id, _, toc_offset, toc_size in units:
        if args.file_id and file_id != int(args.file_id, 16):
            continue
        toc = patch[toc_offset:toc_offset + toc_size]
        layout = walk_unit(toc)
        if layout is None:
            continue
        total = sum(mesh['num_sections'] for mesh in layout['meshes'])
        if chosen is None or total > chosen[0]:
            chosen = (total, file_id, toc, layout)
    if chosen is None:
        raise SystemExit('没有能走通的 unit')

    _, file_id, toc, layout = chosen
    sig_offset, signature = pick_signature(toc, layout)
    if signature is None:
        raise SystemExit('找不到唯一签名')

    print('patch        %s' % args.patch)
    print('unit         0x%016x' % file_id)
    print('streamInfo   0x%x' % layout['stream_info'])
    print('meshInfo     0x%x' % layout['mesh_info'])
    print('materialsOff 0x%x' % layout['materials_offset'])
    print('meshes       %d' % layout['mesh_count'])
    for mesh in layout['meshes']:
        print('  m%d lod=%d nsec=%d secOff=%d sectionsAt=0x%x' % (
            mesh['index'], mesh['lod'], mesh['num_sections'],
            mesh['sections_offset'], mesh['sections_at']))
    print('signature    0x%x 48 bytes' % sig_offset)

    lua_layout = ','.join('{lod=%d,nsec=%d}' % (mesh['lod'], mesh['num_sections'])
                          for mesh in layout['meshes'])
    source = (LUA_TEMPLATE
              .replace('__RESOURCE__', args.resource)
              .replace('__SIGNATURE__', lua_byte_string(signature))
              .replace('__SIG_OFFSET__', '0x%x' % sig_offset)
              .replace('__UNIT__', '"0x%016x"' % file_id)
              .replace('__STREAM__', '0x%x' % layout['stream_info'])
              .replace('__MESH__', '0x%x' % layout['mesh_info'])
              .replace('__MATERIALS__', '0x%x' % layout['materials_offset'])
              .replace('__MESH_COUNT__', '%d' % layout['mesh_count'])
              .replace('__LAYOUT__', lua_layout))

    for path in [p for p in (args.dump_lua, args.install) if p]:
        directory = os.path.dirname(os.path.abspath(path))
        if directory:
            os.makedirs(directory, exist_ok=True)
        with open(path, 'w', encoding='utf-8', newline='\n') as handle:
            handle.write(source)
        print('写出         %s' % os.path.abspath(path))
    return 0


if __name__ == '__main__':
    sys.exit(main())
