# -*- coding: utf-8 -*-
"""生成「运行期 section 开关」探针 Lua，并打包成可导入的 mod zip。

探针要回答的问题只有一个：**在游戏进程内把某个 section 的 `NumIndices` 写成 0，
几何会不会当场消失**。文件级实验已经证明「加载时读 TOC」，但那不能证明
「运行期改内存也生效」——这正是这个探针要测的。

探针不写代码段、不改内存保护属性，只对已可写的堆数据做 4 字节写入。

用法：

    python make_section_toggle_probe.py \
        --patch <patch_0> --file-id 0x9841b16544c8fbae \
        --sections 0-12 --out <zip 路径>
"""

import argparse
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import build_lua_mod
import find_unit_toc

sys.stdout.reconfigure(encoding='utf-8')

LUA_TEMPLATE = r'''-- HD2-Addon: __RESOURCE__
-- 一次性探针：在游戏进程内定位 Unit TOC 副本，把指定 section 的 NumIndices 写成 0。
-- 只写数据（已可写的堆页），不写代码段、不改内存保护属性。
local ffi=require('ffi')
if ffi.os~='Windows' or not ffi.abi('64bit') then return end
ffi.cdef[[
void *GetCurrentProcess(void);
int ReadProcessMemory(void *,const void *,void *,size_t,size_t *);
int WriteProcessMemory(void *,void *,const void *,size_t,size_t *);
size_t VirtualQuery(const void *,void *,size_t);
]]
local kernel=ffi.load('kernel32')
local process=kernel.GetCurrentProcess()

local SIG=__SIGNATURE__
local SIG_OFFSET=__SIG_OFFSET__
local TARGETS={__TARGETS__}

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

local function write32(address,value)
    local buffer=ffi.new('uint32_t[1]',value)
    local written=ffi.new('size_t[1]')
    if kernel.WriteProcessMemory(process,ffi.cast('void *',address),buffer,4,written)==0 then return false end
    if tonumber(written[0])~=4 then return false end
    return u32(read(address,4),0)==value
end

local function status_path()
    local base=os.getenv('LOCALAPPDATA')
    if not base then return nil end
    return base..'\\CowboyBingus\\Helldivers2\\ArmorRegistry\\section-toggle.status'
end

local function flush()
    local path=status_path()
    if not path then return end
    local handle=io.open(path,'wb')
    if not handle then return end
    handle:write(table.concat(lines,'\n'))
    handle:close()
end

log('probe start')
log('signature offset '..string.format('0x%x',SIG_OFFSET))
log('target count '..#TARGETS)
-- Flush immediately: if the game dies later we still want evidence the chunk ran.
flush()

local cursor=0
local applied=false
local reported=false

local function try_base(base)
    -- 复验：第一个目标的当前值必须等于离线读到的原值，否则不是我们要的那份副本。
    local probe=TARGETS[1]
    local current=u32(read(base+probe.off+16,4),0)
    log(string.format('candidate base 0x%x first target current=%s expected=%d',
        base,tostring(current),probe.val))
    if current~=probe.val then return false end
    local done=0
    for _,target in ipairs(TARGETS) do
        if write32(base+target.off+16,0) then done=done+1 end
    end
    log(string.format('wrote %d/%d numIndices to zero',done,#TARGETS))
    return done==#TARGETS
end

local function step()
    local budget=32
    while budget>0 and not applied do
        local mbi=ffi.new('uint8_t[48]')
        if kernel.VirtualQuery(ffi.cast('const void *',cursor),mbi,48)==0 then
            log('region enumeration ended')
            if not reported then log('signature not found') reported=true end
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
        -- Unit TOC 落在私有堆块里；按尺寸和类型收窄，避免扫显卡/映像区。
        if readable and kind==0x20000 and size>=0x4000 and size<=0x4000000 then
            local chunk=read(base,math.min(size,0x800000))
            if chunk then
                local at=1
                while true do
                    local found=string.find(chunk,SIG,at,true)
                    if not found then break end
                    local hit=base+found-1
                    log(string.format('signature hit 0x%x',hit))
                    if try_base(hit-SIG_OFFSET) then
                        applied=true
                        log('APPLIED')
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
    if applied then return true end
    if cursor>=0x0000800000000000 then
        log('address space exhausted')
        if not reported then log('signature not found') reported=true end
        flush()
        return true
    end
    return false
end

-- MDL 用环境代理捕获对 update 的赋值（字节码里是 captured[key]=value），
-- 所以这里必须用普通赋值。rawset 会绕过代理：MDL 会认为没装回调，
-- 而且它改写过 rawset，等于往真实全局表写，可能顶掉别的 addon 的钩子。
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


def parse_section_range(text):
    if '-' in text:
        lo, hi = text.split('-', 1)
        return list(range(int(lo, 0), int(hi, 0) + 1))
    return [int(text, 0)]


def lua_byte_string(data):
    return '"' + ''.join('\\%d' % byte for byte in data) + '"'


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--patch', required=True)
    parser.add_argument('--file-id', required=True)
    parser.add_argument('--sections', default='0-12')
    parser.add_argument('--out', required=True)
    parser.add_argument('--resource', default='mods/codex/section_toggle_probe')
    parser.add_argument('--guid', default='c4f1a0d2-5b3e-4e77-9a61-0d8f2b7c3e54')
    parser.add_argument('--name', default='HD2 Section Toggle Probe')
    parser.add_argument('--dump-lua')
    args = parser.parse_args()

    patch = open(args.patch, 'rb').read()
    entries = find_unit_toc.parse_entries(patch)
    file_id = int(args.file_id, 16)
    target = [e for e in entries if e[0] == file_id and e[1] == find_unit_toc.UNIT_TYPE_ID]
    if not target:
        raise SystemExit('patch 里没有该 Unit 条目')
    _, _, toc_offset, toc_size = target[0]
    toc = patch[toc_offset:toc_offset + toc_size]

    # Section 表在各 LOD 里各自连续，步长 24。先用 unitprobe 打印出各 LOD 的
    # section 0 记录偏移，再在这里按 24 字节步进读取原值。
    lod_bases = LOD_BASES
    wanted = set(parse_section_range(args.sections))

    targets = []
    for lod_base in lod_bases:
        for index in sorted(wanted):
            record = lod_base + index * 24
            if record + 24 > len(toc):
                continue
            material_index, vertex_offset, num_vertices, index_offset, num_indices, group_index = \
                struct.unpack_from('<IIIIII', toc, record)
            if num_indices == 0:
                continue
            targets.append((record, num_indices))

    if not targets:
        raise SystemExit('没有可写的 section')

    signature = toc[lod_bases[0]:lod_bases[0] + 48]
    sig_offset = lod_bases[0]

    lua_targets = ',\n  '.join('{off=0x%x,val=%d}' % (off, val) for off, val in targets)
    # Plain replacement rather than str.format: the Lua body contains literal
    # braces (table constructors, function bodies) that format() would treat as
    # placeholders.
    source = (LUA_TEMPLATE
              .replace('__RESOURCE__', args.resource)
              .replace('__SIGNATURE__', lua_byte_string(signature))
              .replace('__SIG_OFFSET__', '0x%x' % sig_offset)
              .replace('__TARGETS__', lua_targets))

    if args.dump_lua:
        with open(args.dump_lua, 'w', encoding='utf-8') as handle:
            handle.write(source)
        print('Lua 已写出 %s' % os.path.abspath(args.dump_lua))

    size = build_lua_mod.build_zip(
        args.out, args.resource, source, args.guid, args.name,
        'Runtime probe: locate the Unit TOC in-process and zero section NumIndices.')
    print('目标 section  %d 个（%d 个 LOD × %d 个索引）' % (len(targets), len(lod_bases), len(wanted)))
    print('签名偏移      0x%x  48 字节' % sig_offset)
    print('归档长度      %d 字节' % size)
    print('已写出        %s' % os.path.abspath(args.out))
    return 0


# 各 LOD 的 section 表起点（由 tools/unitprobe --sections 打印得到）。
LOD_BASES = [0x6E6C, 0x6C38, 0x68FC, 0x65C0]


if __name__ == '__main__':
    sys.exit(main())
