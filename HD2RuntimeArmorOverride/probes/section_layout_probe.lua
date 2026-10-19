-- HD2-Addon: mods/codex/section_layout_probe
-- 只读探针：在游戏进程内定位 Unit TOC 副本，读出**结构头**并走一遍 mesh/section 表。
--
-- 它**不写任何内存**：没有 WriteProcessMemory 调用，不 VirtualProtect，不改几何。
-- 唯一副作用是往注册表目录写一个 status 文本。
--
-- 目的：阶段 3 打算让运行时自己从 TOC 走出 section 偏移，而不是读构建期的
-- sections.json。这条路成立的前提是内存副本里 0x5c/0x64/0x70 这三个结构偏移
-- 与文件一致。交接文档记录"内存副本与文件 99.19% 相同，差异只在头部"——
-- 而这三个字段恰好都在头部，所以必须先实测，不能假设。
local ffi=require('ffi')
if ffi.os~='Windows' or not ffi.abi('64bit') then return end
ffi.cdef[[
void *GetCurrentProcess(void);
int ReadProcessMemory(void *,const void *,void *,size_t,size_t *);
size_t VirtualQuery(const void *,void *,size_t);
]]
local kernel=ffi.load('kernel32')
local process=kernel.GetCurrentProcess()

-- 与 section_toggle_probe 用同一个签名：已经实测能唯一定位这份 TOC 副本。
local SIG="\0\0\0\0\0\0\0\0\104\95\0\0\0\0\0\0\224\55\0\0\0\0\0\0\1\0\0\0\0\0\0\0\104\95\0\0\224\55\0\0\122\31\0\0\1\0\0\0"
local SIG_OFFSET=0x6e6c

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

local function u64(data,at)
    local low=u32(data,at)
    local high=u32(data,at+4)
    if not low or not high then return nil end
    return low+high*4294967296
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

-- 读一个 unit TOC 切片（base 起）的头部与 mesh 表。
-- 偏移来自权威读取器 UnitMeshReader.cs：0x5c StreamInfo / 0x64 MeshInfo / 0x70 Materials。
local function describe(base)
    local header=read(base,0x80)
    if not header then log('header unreadable at base') return false end
    log('header128 '..hex(header))
    local stream_info=u32(header,0x5c)
    local mesh_info=u32(header,0x64)
    local materials_offset=u32(header,0x70)
    log(string.format('streamInfoOffset=0x%x meshInfoOffset=0x%x materialsOffset=0x%x',
        stream_info or 0,mesh_info or 0,materials_offset or 0))

    -- mesh 表：u32 数量，随后 数量×u32 相对偏移
    local count_data=read(base+mesh_info,4)
    local mesh_count=u32(count_data,0)
    if not mesh_count or mesh_count<1 or mesh_count>64 then
        log('mesh count implausible: '..tostring(mesh_count))
        return false
    end
    log('meshes='..mesh_count)
    local table_data=read(base+mesh_info+4,mesh_count*4)
    if not table_data then log('mesh offset table unreadable') return false end

    for index=0,mesh_count-1 do
        local relative=u32(table_data,index*4)
        if not relative then break end
        local mesh=base+mesh_info+relative
        local record=read(mesh,140)
        if not record then
            log(string.format('m%d unreadable at 0x%x',index,mesh))
        else
            local lod=i32(record,56)
            local num_materials=u32(record,104)
            local num_sections=u32(record,120)
            local sections_offset=u32(record,124)
            log(string.format('m%d off=0x%x lod=%d nmat=%d nsec=%d secOff=%d',
                index,relative,lod or -99,num_materials or -1,num_sections or -1,sections_offset or -1))
            -- 只对第一个真正的 LOD 网格打印首节内容，用来和离线映射表比对。
            if index==0 and num_sections and num_sections>=1 and sections_offset then
                local section=read(mesh+sections_offset,24)
                if section then
                    log(string.format('  first section materialIndex=%d numIndices=%d groupIndex=%d',
                        u32(section,0) or -1,u32(section,16) or -1,u32(section,20) or -1))
                end
            end
        end
    end

    -- 材质表：u32 数量，随后 数量×u32 sectionId，随后 数量×u64 materialId
    local material_count=u32(read(base+materials_offset,4),0)
    log('materials='..tostring(material_count))
    if material_count and material_count>0 and material_count<=512 then
        local ids=read(base+materials_offset+4,material_count*4)
        local values=read(base+materials_offset+4+material_count*4,material_count*8)
        if ids and values then
            for index=0,math.min(material_count,3)-1 do
                log(string.format('  mat%d sectionId=%d materialId=0x%016x',
                    index,u32(ids,index*4) or 0,u64(values,index*8) or 0))
            end
        else
            log('  material table unreadable')
        end
    end
    return true
end

log('layout probe start (read-only)')
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
                    flush()
                    if describe(candidate) then
                        done=true
                        log('DONE')
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
        if not reported then log('signature not found') reported=true end
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
