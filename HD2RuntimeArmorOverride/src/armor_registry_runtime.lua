local existing=rawget(_G,'HD2RuntimeArmorRegistry')
if existing then return existing end
local M={version='0.2.0',status='starting',active={}}
rawset(_G,'HD2RuntimeArmorRegistry',M)

local loader=rawget(_G,'CowboyBingusModLoader')
local shared_loader=type(loader)=='table' and loader.api==1
    and type(loader.open_log)=='function'

local ok,ffi=pcall(require,'ffi')
if not ok or ffi.os~='Windows' or not ffi.abi('64bit') then
    M.status='unsupported';return M
end
ffi.cdef[[
void *GetModuleHandleA(const char *);
void *GetCurrentProcess(void);
uint32_t GetCurrentProcessId(void);
uint32_t GetEnvironmentVariableA(const char *,char *,uint32_t);
int ReadProcessMemory(void *,const void *,void *,size_t,size_t *);
int WriteProcessMemory(void *,void *,const void *,size_t,size_t *);
size_t VirtualQuery(const void *,void *,size_t);
int QueryPerformanceCounter(int64_t *);
int QueryPerformanceFrequency(int64_t *);
int CreateDirectoryA(const char *,void *);
uint32_t GetLastError(void);
int MoveFileExA(const char *,const char *,uint32_t);
]]
local kernel=ffi.load('kernel32')
local process=kernel.GetCurrentProcess()
local module_base=tonumber(ffi.cast('uintptr_t',kernel.GetModuleHandleA('game.dll')))
local pid=tonumber(kernel.GetCurrentProcessId())
local frequency=ffi.new('int64_t[1]')
local counter=ffi.new('int64_t[1]')
assert(kernel.QueryPerformanceFrequency(frequency)~=0,'performance clock unavailable')
local clock_frequency=tonumber(frequency[0])
local function clock()
    kernel.QueryPerformanceCounter(counter)
    return tonumber(counter[0])/clock_frequency
end
local runtime_started=clock()
-- Discovery shares the soft frame budget with preparation and auditing.
-- Timing diagnostics are not an engine resource-readiness guarantee.
M.locator_pacing='frame_budget'

local function read(address,size)
    if not address or address<65536 or size<1 or size>0x400000 then return nil end
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
local function qword(address)
    local data=assert(read(address,8),'unreadable pointer/count')
    local low,high=u32(data,0),u32(data,4)
    assert(high<0x200000,'pointer/count exceeds exact integer range')
    return low+high*4294967296
end
local function dword(address)
    return assert(u32(read(address,4),0),'unreadable dword')
end
local function put_qword(address,value)
    if address%8~=0 then return false end
    ffi.cast('uint64_t *',address)[0]=ffi.new('uint64_t',value)
    return qword(address)==value
end
local function pack64(value)
    local out={}
    for _=1,8 do
        out[#out+1]=string.char(value%256)
        value=math.floor(value/256)
    end
    return table.concat(out)
end
local function hex(value) return string.format('0x%016x',value or 0) end

local function slot_writable(address)
    if not address or address<65536 or address%8~=0 then return false end
    local mbi=ffi.new('uint8_t[48]')
    if kernel.VirtualQuery(ffi.cast('void *',address),mbi,48)==0 then return false end
    local base=tonumber(ffi.cast('uintptr_t *',mbi)[0])
    local size=tonumber(ffi.cast('size_t *',mbi+24)[0])
    local state=tonumber(ffi.cast('uint32_t *',mbi+32)[0])
    local protection=tonumber(ffi.cast('uint32_t *',mbi+36)[0])
    local p=protection%256
    return state==0x1000 and math.floor(protection/256)%2==0
        and (p==4 or p==8 or p==64 or p==128) and address+8<=base+size
end
local function slot_read(address)
    local data=read(address,8)
    if not data then return nil end
    return u32(data,0)+u32(data,4)*4294967296
end
local function slot_write(address,value,expected)
    if not slot_writable(address) or slot_read(address)~=expected then return false end
    local buffer=ffi.new('uint64_t[1]',value)
    local written=ffi.new('size_t[1]')
    return kernel.WriteProcessMemory(process,ffi.cast('void *',address),buffer,8,written)~=0
        and tonumber(written[0])==8 and slot_read(address)==value
end
local transaction=ARMOR_REGISTRY_TRANSACTION.new({
    read=slot_read,write=slot_write,writable=slot_writable})
-- This strong reference also pins buffers after an unexpected script exception.
M.transaction=transaction

local function mkdir(path)
    if kernel.CreateDirectoryA(path,nil)~=0 then return true end
    return tonumber(kernel.GetLastError())==183
end
local function environment(name)
    local required=tonumber(kernel.GetEnvironmentVariableA(name,nil,0))
    if not required or required<2 or required>32768 then return nil end
    local buffer=ffi.new('char[?]',required)
    local written=tonumber(kernel.GetEnvironmentVariableA(name,buffer,required))
    if not written or written<1 or written>=required then return nil end
    return ffi.string(buffer,written)
end
local function mkdir_tree(path)
    local current=''
    local drive=path:match('^(%a:)[/\\]')
    if drive then current=drive;path=path:sub(4) end
    for part in path:gmatch('[^/\\]+') do
        current=current=='' and part or (current..'/'..part)
        if not mkdir(current) then return false end
    end
    return true
end
local base_directory
if shared_loader and type(loader.log_directory)=='string' then
    base_directory=loader.log_directory:gsub('[/\\]Logs$','')
else
    local local_app_data=environment('LOCALAPPDATA')
    base_directory=local_app_data and (local_app_data..'/CowboyBingus/Helldivers2')
end
if not base_directory or not mkdir_tree(base_directory) then
    M.status='directory_unavailable';return M
end
local logs_directory=base_directory..'/Logs'
local registry_directory=base_directory..'/ArmorRegistry'
if not mkdir_tree(logs_directory) or not mkdir_tree(registry_directory) then
    M.status='directory_create_failed';return M
end
local log_file
if shared_loader then
    local opened,result=pcall(loader.open_log,'ArmorRegistryRuntime.log')
    if opened then log_file=result end
else
    log_file=io.open(logs_directory..'/ArmorRegistryRuntime.log','ab')
end
local function log(text)
    if not log_file then return end
    log_file:write(tostring(text)..'\n');log_file:flush()
end
M.deployment_mode=shared_loader and 'shared_loader' or 'direct'
log('deployment_mode='..M.deployment_mode)
local protocol_path=registry_directory..'/armor-registry.protocol'
local status_path=registry_directory..'/runtime.status'
local cache_path=registry_directory..'/locator.cache'
local wardrobe_masks_path=registry_directory..'/wardrobe.masks'
local weight_audit_request_path=registry_directory..'/weight-audit.request'
local weight_audit_report_path=registry_directory..'/weight-audit.report'

local function read_file(path,limit)
    local file=io.open(path,'rb')
    if not file then return nil end
    local data=file:read((limit or 65536)+1);file:close()
    if not data or #data>(limit or 65536) then return nil end
    return data
end
local function write_atomic(path,text)
    local temporary=path..'.tmp'
    local file=io.open(temporary,'wb')
    if not file then return false end
    local ok_write=file:write(text)
    file:flush();file:close()
    if not ok_write then return false end
    if kernel.MoveFileExA(temporary,path,9)~=0 then return true end
    os.remove(temporary)
    return false
end
if not read_file(protocol_path) then
    write_atomic(protocol_path,
        'schema=1\nrevision=0\nenabled=false\n# map=target_item_id,source_item_id\n')
end

local refresh_probe
-- Diagnostic-only, fixed-size inspection of a known Lua C binding. Never
-- accepts an address from the protocol or calls the inspected binding.
local function inspect_refresh_binding(f,name,unit,code_cache,resolve_only)
    local ok_util,util=pcall(require,'jit.util')
    if not ok_util or type(util)~='table' or type(util.funcinfo)~='function' then
        return {state='introspection_unavailable'}
    end
    local ok_info,info=pcall(util.funcinfo,f)
    local address=ok_info and type(info)=='table' and rawget(info,'addr')
    if type(address)~='number' or address~=math.floor(address) then
        return {state='c_address_unavailable'}
    end
    local function capture(address)
    if code_cache and code_cache[address] then return code_cache[address] end
    local binding_base,binding_name,pe,size
    for _,name in ipairs({'game.dll','helldivers2.exe','lua51.dll'}) do
        local base=tonumber(ffi.cast('uintptr_t',kernel.GetModuleHandleA(name)))
        local dos=base and base>=65536 and read(base,64)
        local peoff=dos and u32(dos,0x3c)
        local header=peoff and peoff<0x100000 and read(base+peoff,0x58)
        if header and header:sub(1,4)=='PE\0\0' then
            local bytes=u32(header,0x50)
            if address>=base and address+2048<=base+bytes then
                binding_base,binding_name,pe,size=base,name,header,bytes;break
            end
        end
    end
    if not binding_base then return {state='outside_allowed_modules'} end
    local mbi=ffi.new('uint8_t[48]')
    if kernel.VirtualQuery(ffi.cast('void *',address),mbi,48)==0 then
        return {state='region_unavailable'}
    end
    local region_base=tonumber(ffi.cast('uintptr_t *',mbi)[0])
    local region_size=tonumber(ffi.cast('size_t *',mbi+24)[0])
    local state=tonumber(ffi.cast('uint32_t *',mbi+32)[0])
    local protection=tonumber(ffi.cast('uint32_t *',mbi+36)[0])
    local p=protection%256
    if state~=0x1000 or math.floor(protection/256)%2~=0
        or (p~=0x20 and p~=0x40 and p~=0x80)
        or address+2048>region_base+region_size then
        return {state='not_readable_executable_region'}
    end
    local bytes=read(address,2048)
    if not bytes then return {state='read_failed'} end
    local result={state='captured',module=binding_name,rva=address-binding_base,
        module_base=hex(binding_base),timestamp=u32(pe,8),image_size=size,
        code_hex=(bytes:gsub('.',function(c)return string.format('%02x',c:byte())end)),
        byte_count=#bytes,called=false}
    if code_cache then code_cache[address]=result end
    return result
    end
    local row=capture(address)
    -- Offline-reviewed E8 sites, pinned to the observed build and binding.
    -- This copies the callee's bytes; it never executes or recursively follows it.
    local reviewed_site=name=='Unit.is_child' and row.rva==4267568 and 0x4e
        or name=='Unit.world' and row.rva==4224528 and 0x26
        or name=='World.link_unit' and row.rva==0x3eb860 and 0xb8
    if reviewed_site and row.state=='captured'
        and row.module=='helldivers2.exe' and row.timestamp==1790149348
        and row.image_size==60719104 then
        local instruction=read(address+reviewed_site,5)
        if instruction and instruction:byte(1)==0xe8 then
            local delta=u32(instruction,1)
            if delta>=0x80000000 then delta=delta-0x100000000 end
            local target=capture(address+reviewed_site+5+delta)
            row.callee_state=target.state
            if target.state=='captured' then
                row.callee_rva=target.rva;row.callee_module=target.module
                row.callee_code_hex=target.code_hex;row.callee_byte_count=target.byte_count
                row.callee_module_base=target.module_base;row.callee_site_offset=reviewed_site
                -- One explicitly reviewed tail edge, not a recursive code walk.
                -- Captured 2026-09-26: link wrapper resolves two Unit handles,
                -- then tail-jumps from 3361c0+6e to the World implementation.
                if name=='World.link_unit' and target.module=='helldivers2.exe'
                    and target.rva==0x3361c0 then
                    local tail_at=address-row.rva+target.rva+0x6e
                    local tail=read(tail_at,5)
                    row.implementation_state='reviewed_tail_mismatch'
                    if tail and tail:byte(1)==0xe9 then
                        local d=u32(tail,1)
                        if d>=0x80000000 then d=d-0x100000000 end
                        local destination=tail_at+5+d
                        if destination==address-row.rva+0x1af240 then
                            local implementation=capture(destination)
                            row.implementation_state=implementation.state
                            if implementation.state=='captured' then
                                row.implementation_rva=implementation.rva
                                row.implementation_module=implementation.module
                                row.implementation_code_hex=implementation.code_hex
                                row.implementation_byte_count=implementation.byte_count
                            end
                        else row.implementation_state='reviewed_destination_mismatch' end
                    end
                end
            end
        else row.callee_state='reviewed_site_mismatch' end
    end
    if unit~=nil then
        local out={state='native_unit_unavailable',called=false}
        if name~='Unit.world' or row.callee_state~='captured'
            or row.callee_rva~=0x9d8c0 or row.callee_module~='helldivers2.exe' then
            out.state='resolver_build_mismatch';return out
        end
        local base=address-row.rva
        local resolver=base+row.callee_rva
        local load=read(resolver+0xf,7)
        if not load or load:sub(1,3)~='\x48\x8b\x35' then
            out.state='resolver_instruction_mismatch';return out
        end
        local function ptr(at)
            local b=read(at,8)
            if not b then return nil end
            local lo,hi=u32(b,0),u32(b,4)
            if hi>=0x8000 then return nil end
            local v=lo+hi*4294967296
            return v>=65536 and v or nil
        end
        local okay,encoded=pcall(function()return tonumber(ffi.cast('uintptr_t',unit))end)
        if not okay or type(encoded)~='number' or encoded<0 or encoded>0xffffffff
            or encoded~=math.floor(encoded) then out.state='unsupported_unit_handle';return out end
        local id=math.floor(encoded/4)
        local index=id%0x400000
        local generation=math.floor(id/0x400000)
        local delta=u32(load,3)
        if delta>=0x80000000 then delta=delta-0x100000000 end
        local manager_slot=resolver+0x16+delta
        local manager=ptr(manager_slot)
        if not manager then out.state='manager_unreadable';return out end
        local count_bytes=read(manager+0x98,4)
        local count=count_bytes and u32(count_bytes,0)
        local objects,generations=ptr(manager+0x88),ptr(manager+0xa0)
        if not count or count>0x400000 or index>=count or not objects or not generations then
            out.state='invalid_handle_table';return out
        end
        local gen=read(generations+index,1)
        if not gen or gen:byte(1)~=generation then out.state='generation_mismatch';return out end
        local object=ptr(objects+index*8)
        local vtable=object and ptr(object)
        local method=vtable and ptr(vtable+0x1e8)
        if not object or not vtable or not method then out.state='object_unreadable';return out end
        local method_code=resolve_only and {state='not_requested'} or capture(method)
        local object_world=ptr(object+0x18)
        -- No lock is taken: reject observations when the table/slot changes.
        if ptr(manager_slot)~=manager or read(manager+0x98,4)~=count_bytes
            or ptr(manager+0x88)~=objects or ptr(manager+0xa0)~=generations
            or read(generations+index,1)~=gen or ptr(objects+index*8)~=object
            or ptr(object)~=vtable or ptr(vtable+0x1e8)~=method then
            out.state='unstable_handle_table';return out
        end
        out.state='resolved_candidate';out.handle_index=index;out.handle_generation=generation
        out.encoded_handle=encoded
        out.object=hex(object);out.vtable=hex(vtable)
        out.object_world=object_world and hex(object_world)
        if resolve_only then return out end
        out.method_state=method_code.state;out.relationship_proven=false
        if method_code.state=='captured' then
            out.method_module=method_code.module;out.method_rva=method_code.rva
            out.method_code_hex=method_code.code_hex;out.method_byte_count=method_code.byte_count
            out.method_module_base=method_code.module_base
            out.method_timestamp=method_code.timestamp;out.method_image_size=method_code.image_size
        end
        -- Reviewed implementation copies object+1d8, then follows node+1e0.
        -- Read that bounded list directly, without calling its allocating method.
        out.association_state='method_not_reviewed'
        if method_code.state=='captured' and method_code.module=='helldivers2.exe'
            and method_code.rva==0x2beb90 and method_code.timestamp==1790149348
            and method_code.image_size==60719104
            and method_code.code_hex:sub(0xa*2+1,0x11*2)=='488db9d8010000'
            and method_code.code_hex:sub(0x65*2+1,0x71*2)=='488b3f8b134881c7e0010000' then
            local observations={}
            local function link(at)
                local bytes=read(at,8)
                if not bytes or #bytes~=8 then return nil end
                observations[#observations+1]={at=at,bytes=bytes}
                local low,high=u32(bytes,0),u32(bytes,4)
                if high>=0x8000 then return nil end
                local value=low+high*4294967296
                if value~=0 and value<65536 then return nil end
                return value
            end
            local node=link(object+0x1d8)
            local seen,list={},{}
            out.association_state='complete'
            while node and node~=0 do
                if seen[node] then out.association_state='cycle';break end
                if #list>=64 then out.association_state='capped';break end
                seen[node]=true
                local child_vtable=link(node)
                local child_world=link(node+0x18)
                if not child_vtable or child_vtable==0 or child_world~=object_world then
                    out.association_state='invalid_child';break
                end
                list[#list+1]=hex(node)
                node=link(node+0x1e0)
            end
            if not node then out.association_state='unreadable' end
            for _,observation in ipairs(observations) do
                if read(observation.at,8)~=observation.bytes then
                    out.association_state='unstable';break
                end
            end
            if ptr(manager_slot)~=manager or ptr(manager+0x88)~=objects
                or ptr(manager+0xa0)~=generations or read(generations+index,1)~=gen
                or ptr(objects+index*8)~=object or ptr(object)~=vtable
                or ptr(object+0x18)~=object_world or ptr(vtable+0x1e8)~=method then
                out.association_state='unstable'
            end
            if out.association_state=='complete' then
                out.association_count=#list
                out.association_objects=table.concat(list,';')
                out.association_atomic=false
            end
        end
        return out
    end
    return row
end
local refresh_hotkey
if ARMOR_REFRESH_HOTKEY then
    refresh_hotkey=ARMOR_REFRESH_HOTKEY.new({
        pid=pid,clock=clock,frequency=clock_frequency,globals=_G,
        world_probe=ARMOR_REFRESH_WORLD_PROBE,
        resource_probe=ARMOR_REFRESH_RESOURCES,
        component_probe=ARMOR_REFRESH_MULTISITE,
        component_host={read=read,u32=u32,clock=clock,base=function(name)
            return tonumber(ffi.cast('uintptr_t',kernel.GetModuleHandleA(name)))
        end},
        inspect_unit=function(f,unit,cache,resolve_only)return inspect_refresh_binding(f,'Unit.world',unit,cache,resolve_only)end,
        revision=function()return M.applied_revision or -1 end,
        read=function(name,limit)return read_file(registry_directory..'/'..name,limit)end,
        write=function(name,text)return write_atomic(registry_directory..'/'..name,text)end,
        open=function(name)return io.open(registry_directory..'/'..name,'wb')end
    })
end
local function refresh_pending_write(address,manager,index,guards)
    if index~=0 or address~=manager+0x995 or M.status~='ready'
        then return false end
    -- Writable data only; do not change page protection or write code.
    local mbi=ffi.new('uint8_t[48]')
    if kernel.VirtualQuery(ffi.cast('void *',address),mbi,48)==0 then return false end
    local state=tonumber(ffi.cast('uint32_t *',mbi+32)[0])
    local protection=tonumber(ffi.cast('uint32_t *',mbi+36)[0])
    if state~=0x1000 or (protection~=4 and protection~=8) then return false end
    for _,g in ipairs(guards) do
        if read(g[1],#g[2])~=g[2] then return false end
    end
    if read(address,1)~='\0' then return false end
    local buffer=ffi.new('uint8_t[1]',1)
    local written=ffi.new('size_t[1]')
    return kernel.WriteProcessMemory(process,ffi.cast('void *',address),buffer,1,written)~=0
        and tonumber(written[0])==1
end
if ARMOR_REFRESH_PROBE then
    local dbg=rawget(_G,'debug')
    refresh_probe=ARMOR_REFRESH_PROBE.new({
        globals=_G,pid=pid,clock=clock,
        world_probe=ARMOR_REFRESH_WORLD_PROBE,
        code_probe=ARMOR_REFRESH_CODE_PROBE,
        route_probe=ARMOR_REFRESH_ROUTE_PROBE,
        -- Explicit diagnostic mode only. Never wired to protocol application.
        pulse_allowed=function()
            return M.status=='ready' and M.applied_revision~=nil and next(M.active)==nil
        end,
        pulse_write=function(address,manager,index,guards)
            if next(M.active)~=nil then return false end
            return refresh_pending_write(address,manager,index,guards)
        end,
        code_host={read=read,u32=u32,base=function(name)
            return tonumber(ffi.cast('uintptr_t',kernel.GetModuleHandleA(name)))
        end},
        inspect_binding=inspect_refresh_binding,
        inspect_unit=function(f,unit)return inspect_refresh_binding(f,'Unit.world',unit)end,
        getinfo=type(dbg)=='table' and rawget(dbg,'getinfo'),
        revision=function()return M.applied_revision or -1 end,
        read=function(name,limit)return read_file(registry_directory..'/'..name,limit)end,
        write=function(name,text)return write_atomic(registry_directory..'/'..name,text)end,
        open=function(name)return io.open(registry_directory..'/'..name,'wb')end
    })
end

local function game_fingerprint()
    local dos=read(module_base,0x40)
    local pe_offset=dos and u32(dos,0x3c)
    local pe=pe_offset and read(module_base+pe_offset,0x58)
    if not pe or pe:sub(1,4)~='PE\0\0' then return nil end
    return {timestamp=u32(pe,8),image_size=u32(pe,0x50)}
end
local fingerprint=game_fingerprint()
if not fingerprint then
    M.status='fingerprint_failed';log('game_fingerprint_failed');return M
end

local function parse_key_values(text)
    local result={}
    if not text then return result end
    for line in (text..'\n'):gmatch('(.-)\r?\n') do
        local key,value=line:match('^([%w_]+)=(.-)%s*$')
        if key then result[key]=value end
    end
    return result
end
local cache=parse_key_values(read_file(cache_path,4096))
local cache_matches=tonumber(cache.schema)==1
    and tonumber(cache.game_timestamp)==fingerprint.timestamp
    and tonumber(cache.game_image_size)==fingerprint.image_size

local function status_text(state,message,applied_revision)
    local registry=M.registry
    local lines={
        'schema=1',
        'runtime_version='..M.version,
        'deployment_mode='..M.deployment_mode,
        'state='..state,
        'pid='..tostring(pid),
        'game_timestamp='..tostring(fingerprint.timestamp),
        'game_image_size='..tostring(fingerprint.image_size),
        'revision='..tostring(applied_revision or -1),
        'active_mappings='..tostring(M.active_count or 0)
    }
    lines[#lines+1]='retained_clone_count='..transaction.retained_count
    lines[#lines+1]='retained_clone_bytes='..transaction.retained_bytes
    for _,key in ipairs({'built','reused','removed','writes','prepare_ms','publish_ms'}) do
        if M.last_apply and M.last_apply[key]~=nil then
            lines[#lines+1]='last_'..key..'='..tostring(M.last_apply[key])
        end
    end
    if M.locate_ms then lines[#lines+1]='locate_ms='..tostring(M.locate_ms) end
    lines[#lines+1]='locator_pacing='..M.locator_pacing
    if M.located_after_ms then lines[#lines+1]='located_after_ms='..M.located_after_ms end
    if M.first_applied_after_ms then lines[#lines+1]='first_applied_after_ms='..M.first_applied_after_ms end
    if registry then
        lines[#lines+1]='record_count='..tostring(registry.count)
        lines[#lines+1]='blob_bytes='..tostring(registry.bytes)
        lines[#lines+1]='blob_base='..hex(registry.base)
        lines[#lines+1]='index_base='..hex(registry.index_base)
    end
    if message then
        lines[#lines+1]='message='..tostring(message):gsub('[\r\n=]',' ')
    end
    return table.concat(lines,'\n')..'\n'
end
local function set_status(state,message,revision)
    M.status=state
    write_atomic(status_path,status_text(state,message,revision))
    log(state..(message and (': '..message) or ''))
end

local function memory_regions()
    local mbi=ffi.new('uint8_t[48]')
    local cursor,regions,queries=0,{},0
    while cursor<0x0000800000000000 and queries<32768 do
        local got=kernel.VirtualQuery(ffi.cast('const void *',cursor),mbi,48)
        if got==0 then break end
        local base=tonumber(ffi.cast('uintptr_t *',mbi)[0])
        local size=tonumber(ffi.cast('size_t *',mbi+24)[0]) or 0
        local state=tonumber(ffi.cast('uint32_t *',mbi+32)[0]) or 0
        local protect=tonumber(ffi.cast('uint32_t *',mbi+36)[0]) or 0
        local kind=tonumber(ffi.cast('uint32_t *',mbi+40)[0]) or 0
        local p=protect%256
        local readable=state==0x1000 and math.floor(protect/256)%2==0
            and (p==2 or p==4 or p==8 or p==32 or p==64 or p==128)
        if readable and size>0 then
            regions[#regions+1]={base=base,size=size,kind=kind,protection=p}
        end
        cursor=base+math.max(size,0x1000)
        queries=queries+1
        if queries%64==0 then coroutine.yield() end
    end
    return regions
end

local function parse_blob(base,region_size,expected_count,expected_bytes)
    local prefix=read(base,28)
    if not prefix then return nil end
    local count=u32(prefix,0)
    if not count or count<100 or count>2048 then return nil end
    if expected_count and count~=expected_count then return nil end
    local offset,roots,items=4,{},{}
    for index=1,count do
        if offset+24>region_size then return nil end
        local header=read(base+offset,24)
        if not header or header:sub(1,4)~='LDLD' or u32(header,4)~=1
            or u32(header,8)~=0xd9a55aa0 then return nil end
        local length=u32(header,12)
        if not length or length<64 or offset+24+length>region_size then return nil end
        local root=base+offset+24
        local item_data=read(root,4)
        local item=item_data and u32(item_data,0)
        if not item or items[item] then return nil end
        roots[index]=root
        items[item]={address=root,index=index-1}
        offset=offset+24+length
        if index%64==0 then coroutine.yield() end
    end
    if expected_bytes and offset~=expected_bytes then return nil end
    return {base=base,count=count,bytes=offset,roots=roots,items=items,
        region_size=region_size}
end

local function find_index(regions,layout,expected_region_size)
    local needle=pack64(layout.roots[1])..pack64(layout.roots[2])
        ..pack64(layout.roots[3])
    local function search(require_size)
        for _,region in ipairs(regions) do
            if region.kind==0x20000 and region.size>=layout.count*8
                and region.size<=0x100000
                and (not require_size or region.size==require_size) then
                local data=read(region.base,region.size)
                if data then
                    local from=1
                    while true do
                        local found=data:find(needle,from,true)
                        if not found then break end
                        local candidate=region.base+found-1
                        local pointers=read(candidate,layout.count*8)
                        local valid=pointers~=nil
                        if valid then
                            for i,root in ipairs(layout.roots) do
                                local at=(i-1)*8
                                local low=u32(pointers,at)
                                local high=u32(pointers,at+4)
                                if not low or not high or low+high*4294967296~=root then
                                    valid=false;break
                                end
                            end
                        end
                        if valid then return candidate,region.size end
                        from=found+1
                    end
                end
            end
            coroutine.yield()
        end
    end
    if expected_region_size then
        local address,size=search(expected_region_size)
        if address then return address,size end
    end
    return search(nil)
end

local function locate_registry()
    local regions=memory_regions()
    local layout
    local cached_blob_size=cache_matches and tonumber(cache.blob_region_size)
    local cached_count=cache_matches and tonumber(cache.record_count)
    local cached_bytes=cache_matches and tonumber(cache.blob_bytes)
    local function find_blob(require_cache)
        for _,region in ipairs(regions) do
            if region.kind==0x20000 and region.size>=0x10000
                and region.size<=0x400000
                and (not require_cache or region.size==cached_blob_size) then
                local first=read(region.base,28)
                if first and first:sub(5,8)=='LDLD'
                    and u32(first,8)==1 and u32(first,12)==0xd9a55aa0 then
                    local candidate=parse_blob(region.base,region.size,
                        require_cache and cached_count or nil,
                        require_cache and cached_bytes or nil)
                    if candidate then return candidate end
                end
            end
            coroutine.yield()
        end
    end
    if cache_matches and cached_blob_size and cached_count and cached_bytes then
        layout=find_blob(true)
    end
    if not layout then layout=find_blob(false) end
    if not layout then return nil,'armor registry blob not found' end
    local index_base,index_region_size=find_index(regions,layout,
        cache_matches and tonumber(cache.index_region_size) or nil)
    if not index_base then return nil,'armor registry index not found' end
    layout.index_base=index_base
    layout.index_region_size=index_region_size
    write_atomic(cache_path,table.concat({
        'schema=1',
        'game_timestamp='..fingerprint.timestamp,
        'game_image_size='..fingerprint.image_size,
        'record_count='..layout.count,
        'blob_bytes='..layout.bytes,
        'blob_region_size='..layout.region_size,
        'index_region_size='..index_region_size,
        ''
    },'\n'))
    return layout
end

local function target_weights(root)
    local bodies,body_count=qword(root+48),qword(root+56)
    if bodies<65536 or body_count<1 or body_count>16 then
        return nil,'invalid target Body array'
    end
    local pieces_index={}
    for i=0,body_count-1 do
        local row=bodies+i*24
        local body=dword(row)
        local pieces,count=qword(row+8),qword(row+16)
        if pieces<65536 or count<1 or count>4096 then
            return nil,'invalid target Piece array'
        end
        for piece=0,count-1 do
            local address=pieces+piece*96
            local slot,piece_type,weight=dword(address+8),dword(address+12),dword(address+16)
            pieces_index[#pieces_index+1]={body=body,slot=slot,
                piece_type=piece_type,weight=weight}
        end
    end
    return ARMOR_METADATA_PROBE_CORE.weight_index(pieces_index)
end
local function resolve_weight(meta,body,slot,piece_type)
    return ARMOR_METADATA_PROBE_CORE.resolve_weight(meta,body,slot,piece_type)
end
local function weight_values(set)
    local values={}
    for value,count in pairs(set or {}) do
        values[#values+1]=tostring(value)..'x'..tostring(count)
    end
    table.sort(values)
    return table.concat(values,'|')
end
local function audit_clone_weights(target_root,source_root)
    local weights,weight_error=target_weights(target_root)
    if not weights then return nil,weight_error end
    local source_bodies,body_count=qword(source_root+48),qword(source_root+56)
    if source_bodies<65536 or body_count<1 or body_count>16 then
        return nil,'invalid source Body array'
    end
    local total,ambiguous,resolutions=0,{},{}
    for i=0,body_count-1 do
        local row=source_bodies+i*24
        local body=dword(row)
        local pieces,count=qword(row+8),qword(row+16)
        if pieces<65536 or count<1 or count>4096 then
            return nil,'invalid source Piece array'
        end
        for piece=0,count-1 do
            local address=pieces+piece*96
            local slot,piece_type=dword(address+8),dword(address+12)
            local weight,level=resolve_weight(weights,body,slot,piece_type)
            if weight==nil then
                local candidates=slot==0
                    and (weights.body_metadata[body] or weights.global_metadata)
                    or (weights.body_visible[body] or weights.global_visible)
                if #ambiguous<32 then
                    ambiguous[#ambiguous+1]=table.concat({
                        body,slot,piece_type,weight_values(candidates)
                    },':')
                end
            else
                resolutions[level]=(resolutions[level] or 0)+1
            end
            total=total+1
        end
        if i%4==0 then coroutine.yield() end
    end
    return {pieces=total,ambiguous=ambiguous,resolutions=resolutions}
end
local function parse_weight_audit_request(text)
    local source,targets=nil,{}
    for line in text:gmatch('[^\r\n]+') do
        local source_value=line:match('^source=(%d+)$')
        local target_value=line:match('^target=(%d+)$')
        if source_value then
            if source then return nil,nil,'duplicate_source' end
            source=source_value
        elseif target_value then
            if #targets>=2048 then return nil,nil,'too_many_targets' end
            targets[#targets+1]=target_value
        else
            return nil,nil,'invalid_request_line'
        end
    end
    if not source or #targets==0 then return nil,nil,'missing_source_or_targets' end
    return source,targets
end
local function run_weight_audit(source_id,target_ids)
    local source=tonumber(source_id)
    if not source or source<0 or source>0xffffffff or source~=math.floor(source) then
        write_atomic(weight_audit_report_path,
            'schema=1\nstate=error\nmessage=invalid_source_item_id\n')
        return
    end
    local source_item=M.registry.items[source]
    if not source_item then
        write_atomic(weight_audit_report_path,table.concat({
            'schema=1','state=error','source_item_id='..source,
            'message=source_item_not_found',''
        },'\n'))
        return
    end
    local ids,seen={},{}
    for _,raw_id in ipairs(target_ids) do
        local item=tonumber(raw_id)
        if not item or item<0 or item>0xffffffff or item~=math.floor(item) then
            write_atomic(weight_audit_report_path,
                'schema=1\nstate=error\nmessage=invalid_target_item_id\n')
            return
        end
        if not seen[item] then
            seen[item]=true
            ids[#ids+1]=item
        end
    end
    table.sort(ids)
    local rows={}
    local compatible,failed,registered=0,0,0
    for index,target_id in ipairs(ids) do
        local target_item=M.registry.items[target_id]
        local result,reason
        if target_item then
            registered=registered+1
            result,reason=audit_clone_weights(target_item.address,source_item.address)
        else
            reason='target_item_not_found'
        end
        if result and #result.ambiguous==0 then
            compatible=compatible+1
            local resolution_parts={}
            for _,level in ipairs({'exact','body','global'}) do
                local count=result.resolutions[level] or 0
                if count>0 then resolution_parts[#resolution_parts+1]=level..'='..count end
            end
            rows[#rows+1]=table.concat({
                'target',target_id,'ok','pieces='..result.pieces
                    ..';weight_resolution='..table.concat(resolution_parts,'/')
            },',')
        else
            failed=failed+1
            local detail=result
                and ('ambiguous='..#result.ambiguous
                    ..';pieces='..result.pieces
                    ..';details='..table.concat(result.ambiguous,'~'))
                or ('error='..tostring(reason):gsub('[\r\n,=]',' '))
            rows[#rows+1]=table.concat({
                'target',target_id,'failed',detail
            },',')
        end
        if index%8==0 then coroutine.yield() end
    end
    local report={
        'schema=1',
        'state=complete',
        'source_item_id='..source,
        'runtime_registry_count='..(M.registry.count or 0),
        'scope=explicit_target_list',
        'requested_target_count='..#ids,
        'registered_target_count='..registered,
        'compatible_count='..compatible,
        'failed_count='..failed
    }
    for _,row in ipairs(rows) do report[#report+1]=row end
    report[#report+1]=''
    write_atomic(weight_audit_report_path,table.concat(report,'\n'))
    log('weight_audit_complete source='..source
        ..' targets='..#ids..' compatible='..compatible..' failed='..failed)
end
local function build_override_index(document)
    local exact,position={},{}
    for _,item in ipairs(document.piece_overrides or {}) do
        exact[item.owner]=exact[item.owner] or {}
        position[item.owner]=position[item.owner] or {}
        local exact_key=table.concat({item.body,item.unit,item.slot,item.piece_type},':')
        local position_key=table.concat({item.body,item.slot,item.piece_type},':')
        exact[item.owner][exact_key]=item.enabled~=false
        position[item.owner][position_key]=item.enabled~=false
    end
    return {exact=exact,position=position}
end

local function piece_enabled(overrides,owner,body,unit,slot,piece_type)
    if not owner or not overrides then return true end
    local exact_owner=overrides.exact[owner]
    if not exact_owner then return true end
    local exact_value=exact_owner[table.concat({body,unit,slot,piece_type},':')]
    if exact_value~=nil then return exact_value end
    local position_owner=overrides.position[owner]
    local position_value=position_owner
        and position_owner[table.concat({body,slot,piece_type},':')]
    return position_value==nil and true or position_value
end

-- Snapshot original engine data with checked reads; clone builders never dereference
-- a game pointer. Snapshots are scoped to one transaction (no stale cross-run cache).
local function snapshot_root(root,checkpoint)
    local header=assert(read(root,64),'unreadable Kit header')
    local function pointer(data,at)
        local high=u32(data,at+4)
        assert(high<0x200000,'invalid pointer/count')
        return u32(data,at)+high*4294967296
    end
    local bodies,count=pointer(header,48),pointer(header,56)
    assert(count>=1 and count<=16,'invalid Body count')
    local body_data=assert(read(bodies,count*24),'unreadable Body array')
    local rows,total,guards={},0,{{address=root,data=header},{address=bodies,data=body_data}}
    for i=0,count-1 do
        local at=i*24
        local address,n=pointer(body_data,at+8),pointer(body_data,at+16)
        total=total+n
        assert(n>=1 and total<=4096,'invalid Piece count')
        local data=assert(read(address,n*96),'unreadable Piece array')
        guards[#guards+1]={address=address,data=data}
        local pieces={}
        for j=0,n-1 do pieces[#pieces+1]=data:sub(j*96+1,(j+1)*96) end
        rows[#rows+1]={body=u32(body_data,at),header=body_data:sub(at+1,at+8),pieces=pieces}
        checkpoint()
    end
    return {header=header,rows=rows,guards=guards}
end
local function allocate_plan(plan)
    local buffer=ffi.new('uint8_t[?]',plan.bytes)
    local base=tonumber(ffi.cast('uintptr_t',buffer))
    ffi.copy(buffer,plan.payload,plan.bytes)
    for _,at in ipairs(plan.fixups) do
        local offset=u32(plan.payload,at)+u32(plan.payload,at+4)*4294967296
        assert(put_qword(base+at,base+offset),'private clone relocation failed')
    end
    return {buffer=buffer,base=base,bytes=plan.bytes,bodies=plan.bodies,pieces=plan.pieces}
end
local function prepare_document(document,checkpoint)
    local registry=M.registry
    if not registry then return nil,'registry is not ready' end
    local models={}
    for _,model in ipairs(document.models or {}) do models[model.key]=model end
    local overrides=build_override_index(document)
    local snapshots={}
    local function snapshot(item)
        local entry=registry.items[item]
        assert(entry,'unknown armor item '..tostring(item))
        if not snapshots[item] then
            snapshots[item]=snapshot_root(entry.address,checkpoint)
            assert(u32(snapshots[item].header,0)==item,'Kit identity changed')
        end
        return snapshots[item],entry
    end
    local requests={}
    local skipped={}
    for _,mapping in ipairs(document.enabled and document.mappings or {}) do
        local target,entry=snapshot(mapping.target)
        local model,source,owner
        if mapping.model then
            model=assert(models[mapping.model],'unknown model '..mapping.model)
            owner=model.source
            if #(model.pieces or {})==0 then source=snapshot(model.source);model=nil end
            if model and model.test_templates then
                local templates={}
                for unit,ref in pairs(model.test_templates) do
                    local donor=snapshot(ref.item)
                    local found
                    for _,row in ipairs(donor.rows) do
                        if row.body==ref.body then
                            for _,piece in ipairs(row.pieces) do
                                if ARMOR_REGISTRY_COMPOSITION.unit(piece)==ref.unit then
                                    assert(not found,'ambiguous diagnostic donor Piece')
                                    found={body=row.body,data=piece}
                                end
                            end
                        end
                    end
                    assert(found,'diagnostic donor Piece not found: '..ref.unit)
                    templates[unit]=found
                end
                source={test_templates=templates}
                log('diagnostic_B: exact donor Piece templates for target '..mapping.target)
            end
        else source=snapshot(mapping.source);owner=mapping.source end
        if (document.target_modes or {})[mapping.target]==false then owner=mapping.target end
        local okay,plan,reason=pcall(ARMOR_REGISTRY_COMPOSITION.plan,target,source,model,
            function(body,unit,slot,piece_type)
                return piece_enabled(overrides,owner,body,unit,slot,piece_type)
            end,ARMOR_METADATA_PROBE_CORE,checkpoint,document.composition_mode)
        if not okay then
            reason=plan
            plan=nil
        end
        if not plan then
            reason=tostring(reason or 'composition failed')
            skipped[#skipped+1]={target=tostring(mapping.target),reason=reason}
            log('skipped_target: target '..tostring(mapping.target)..' reason='..reason)
        else
            requests[#requests+1]={key=tostring(mapping.target),original=entry.address,
            slot=registry.index_base+entry.index*8,signature=plan.payload,bytes=plan.bytes,
            composition=plan,
            build=function()return allocate_plan(plan)end}
        end
        checkpoint()
    end
    -- A target that failed composition may already be active from an older
    -- revision. Retain that clone instead of interpreting the skip as a
    -- removal; a later valid revision can replace it normally.
    for _,item in ipairs(skipped) do
        local active=transaction.active[item.target]
        if active then
            requests[#requests+1]={key=item.target,original=active.original,
                slot=active.slot,signature=active.signature,retain_active=true}
        end
    end
    -- Removal is a native publication too: never restore an unreadable/stale
    -- original root merely because its index still points at our current clone.
    local requested={}
    for _,request in ipairs(requests) do requested[request.key]=true end
    for key,active in pairs(transaction.active) do
        if not requested[key] then
            local _,entry=snapshot(tonumber(key))
            if entry.address~=active.original then return nil,'original Kit identity changed' end
        end
    end
    local plan,reason=transaction.prepare(requests,checkpoint)
    if not plan then return nil,reason end
    plan.stats.skipped=#skipped
    plan.stats.skipped_targets=skipped
    plan.snapshots=snapshots
    return plan
end
local auto_refresh
local function publish_document(plan,document)
    -- Revalidate snapshots after yielding during preparation. No yielding after
    -- this check and during publication; this is NOT an engine-wide thread lock.
    for _,snapshot in pairs(plan.snapshots) do
        for _,guard in ipairs(snapshot.guards) do
            if read(guard.address,#guard.data)~=guard.data then
                return false,'registry changed during preparation'
            end
        end
    end
    local okay,stats=transaction.commit(plan)
    if not okay then return false,stats end
    M.active=transaction.active
    M.active_count=0
    for _ in pairs(M.active) do M.active_count=M.active_count+1 end
    M.applied_revision=document.revision
    if auto_refresh then
        local changed={}
        for _,op in ipairs(plan.operations) do changed[op.key]=true end
        local notified,err=pcall(auto_refresh.published,document.revision,changed)
        if not notified then auto_refresh.fail(err) end
    end
    return true,stats
end


-- ---- 衣柜 section 掩码 -------------------------------------------------------
-- 顺序是刻意的：section 写入便宜且可回滚，先做；blob 提交失败时再把它撤回。
-- 反过来（先提交 blob 再写 section）一旦 section 失败，就会出现"映射生效了、
-- 掩码没生效"的中间态，而用户看到的是"勾了没反应"，最难查。
local section_publisher
local function section_write_dword(address,value,expected)
    -- 只碰已可写的页：这是交接文档定的边界，不改保护属性。
    if not slot_writable(address) then return false end
    local buffer=ffi.new('uint32_t[1]',value)
    local written=ffi.new('size_t[1]')
    if kernel.WriteProcessMemory(process,ffi.cast('void *',address),buffer,4,written)==0 then
        return false
    end
    if tonumber(written[0])~=4 then return false end
    return dword(address)==value
end
local function section_io()
    return {read=function(address)return dword(address)end,write=section_write_dword}
end
local section_region_cache
local function section_regions()
    -- 只扫私有堆：TOC 副本落在那里；映像区/显卡映射扫了也没用，还会拖慢。
    -- 一次应用里枚举一遍就够：7 个 unit 各枚举一次全地址空间是不可接受的。
    if not section_region_cache then
        local filtered={}
        for _,region in ipairs(memory_regions()) do
            if region.kind==0x20000 and region.size>=0x4000 and region.size<=0x4000000 then
                filtered[#filtered+1]=region
            end
        end
        section_region_cache=filtered
    end
    local list=section_region_cache
    local index=0
    return function()
        index=index+1
        return list[index]
    end
end
local function section_publisher_instance()
    if section_publisher then return section_publisher end
    if not (ARMOR_SECTION_LAYOUT and ARMOR_SECTION_APPLY and ARMOR_SECTION_LOCATE
        and ARMOR_SECTION_PUBLISH and ARMOR_WARDROBE_MASKS) then return nil end
    section_publisher=ARMOR_SECTION_PUBLISH.new({read=read,write=section_write_dword,
        regions=section_regions,layout=ARMOR_SECTION_LAYOUT,apply=ARMOR_SECTION_APPLY,
        locate=ARMOR_SECTION_LOCATE,masks=ARMOR_WARDROBE_MASKS,log=log})
    return section_publisher
end

local function prepare_section_masks()
    -- 每次应用重新枚举：重载后基址会变，旧区域表不能用。
    section_region_cache=nil
    local text=read_file(wardrobe_masks_path,1024*1024)
    if not text then return {skipped=true} end   -- 没有文件 = 全部可见，旧库不受影响
    local parsed,masks=pcall(ARMOR_WARDROBE_MASKS.parse,text)
    if not parsed then
        -- 整份拒绝，不做部分接受：半懂的掩码比没有掩码更危险。
        return nil,'wardrobe.masks invalid: '..tostring(masks)
    end
    if masks.enabled==false then return {skipped=true} end
    local publisher=section_publisher_instance()
    if not publisher then return {skipped=true} end
    -- unit 列表直接从掩码条目里取，不从注册表映射反推：
    -- 原地替换（patch 换掉护甲自己的 unit）没有 map 行，反推会一个目标都算不出来。
    return {masks=masks,
        units_by_target=ARMOR_SECTION_PUBLISH.units_for_masks(masks),
        publisher=publisher}
end

local function apply_section_masks(prepared,checkpoint)
    if not prepared or prepared.skipped then return true,nil end
    local okay,stats=prepared.publisher.apply_document(prepared.masks,
        prepared.units_by_target,checkpoint)
    if not okay then return false,stats end
    return true,stats
end

local function restore_section_masks(stats)
    if not stats or not stats.applied or #stats.applied==0 then return true end
    local okay=ARMOR_SECTION_APPLY.rollback(section_io(),stats.applied)
    log(okay and 'section masks rolled back' or 'section mask rollback FAILED')
    return okay
end

local desired,last_protocol_text
local audit_worker
local audit_poll_elapsed,audit_poll_at=0,0
local function poll_weight_audit(dt)
    if type(dt)=='number' and dt>=0 and dt<10 then
        audit_poll_elapsed=audit_poll_elapsed+dt
    end
    if not M.registry or audit_worker or audit_poll_elapsed<audit_poll_at then return end
    audit_poll_at=audit_poll_elapsed+1
    local request=read_file(weight_audit_request_path,32768)
    if not request then return end
    if os.remove(weight_audit_request_path)==nil then
        log('weight_audit_request_remove_failed')
        return
    end
    local source_id,target_ids,request_error=parse_weight_audit_request(request)
    if not source_id then
        write_atomic(weight_audit_report_path,table.concat({
            'schema=1','state=error','message='..request_error,''
        },'\n'))
        return
    end
    audit_worker=coroutine.create(function()
        run_weight_audit(source_id,target_ids)
    end)
end
local apply_worker,apply_document_pending,attempts,retry_at=nil,nil,0,0
local function poll_protocol()
    local text=read_file(protocol_path,65536)
    if not text or text==last_protocol_text then return end
    last_protocol_text=text -- Deduplicate malformed input as well.
    apply_worker=nil;apply_document_pending=nil;desired=nil
    attempts=0;retry_at=0
    local parsed,document=pcall(ARMOR_REGISTRY_PROTOCOL.parse,text)
    if not parsed then
        set_status('protocol_error',tostring(document),M.applied_revision)
        return
    end
    desired=document
end

-- A soft 1 ms/frame budget shared by locator, auditing and preparation.
-- Native reads and the final publication cannot be preempted mid-operation.
local frame_deadline=0
local function checkpoint()
    if clock()>=frame_deadline then coroutine.yield() end
end
local function advance(worker)
    repeat
        local okay,result=coroutine.resume(worker)
        if not okay then return false,result end
        if coroutine.status(worker)=='dead' then return true,result,true end
    until clock()>=frame_deadline
    return true,nil,false
end
local function step_apply(elapsed)
    if not M.registry or not desired or elapsed<retry_at or attempts>=3 then return end
    if not apply_worker then
        apply_document_pending=desired
        local document=desired
        local protocol_text=last_protocol_text
        apply_worker=coroutine.create(function()
            local started=clock()
            local plan,reason=prepare_document(document,checkpoint)
            if not plan then return {okay=false,reason=reason} end
            -- A replacement may arrive between polling ticks while preparation
            -- spans frames. Do not briefly publish an obsolete composition.
            if read_file(protocol_path,65536)~=protocol_text then
                return {superseded=true}
            end
            local prepared_sections,section_reason=prepare_section_masks()
            if not prepared_sections then return {okay=false,reason=section_reason} end
            local section_okay,section_stats=apply_section_masks(prepared_sections,checkpoint)
            if not section_okay then
                return {okay=false,reason='section mask failed: '..tostring(section_stats)}
            end
            local prepared=clock()
            local okay,stats=publish_document(plan,document)
            if not okay then
                -- blob 没提交成功：section 写入必须撤回，否则几何被藏了、映射却没生效。
                restore_section_masks(section_stats)
                return {okay=false,reason=stats}
            end
            if section_stats then
                stats.section_writes=section_stats.writes
                stats.section_hidden=section_stats.hidden_sections
                stats.section_missing=section_stats.missing
            end
            stats.prepare_ms=math.floor((prepared-started)*1000+0.5)
            stats.publish_ms=math.floor((clock()-prepared)*1000+0.5)
            return {okay=true,stats=stats}
        end)
    end
    local okay,result,done=advance(apply_worker)
    if not okay or done then
        apply_worker=nil
        if okay and result.superseded then
            desired=nil
            poll_protocol()
        elseif okay and result.okay then
            M.last_apply=result.stats
            if not M.first_applied_after_ms then
                M.first_applied_after_ms=math.floor((clock()-runtime_started)*1000+0.5)
                log('timing: locator_pacing='..M.locator_pacing
                    ..' located_after_ms='..tostring(M.located_after_ms)
                    ..' first_applied_after_ms='..M.first_applied_after_ms)
            end
            local mode_suffix=''
            if result.stats and result.stats.mode=='existing_only' then
                mode_suffix=string.format(' mode=existing_only replaced=%d skipped_source=%d',
                    result.stats.replaced or 0,result.stats.skipped_source or 0)
            end
            if result.stats and (result.stats.skipped or 0)>0 then
                mode_suffix=mode_suffix..string.format(' skipped=%d',result.stats.skipped)
                for _,item in ipairs(result.stats.skipped_targets or {}) do
                    log('partial_apply: revision='..tostring(apply_document_pending.revision)
                        ..' skipped_target='..item.target..' reason='..tostring(item.reason))
                end
            end
            if result.stats and result.stats.section_writes then
                mode_suffix=mode_suffix..string.format(' sections=%d hidden=%d',
                    result.stats.section_writes,result.stats.section_hidden or 0)
            end
            set_status('ready',string.format('incremental applied: built=%d reused=%d removed=%d writes=%d%s',
                result.stats.built,result.stats.reused,result.stats.removed,result.stats.writes,mode_suffix),
                apply_document_pending.revision)
            if mode_suffix~='' then
                log('composition'..mode_suffix)
            end
            desired=nil
        else
            attempts=attempts+1;retry_at=elapsed+attempts*2
            local reason=okay and result.reason or tostring(result)
            if not reason:find('registry changed during preparation',1,true) then attempts=3 end
            log('apply_error: revision='..tostring(apply_document_pending.revision)
                ..' reason='..tostring(reason))
            set_status('apply_error',reason,M.applied_revision)
        end
    end
end

-- Guarded automatic refresh for all body armor IDs. Explicit local kill
-- switch remains separate from mapping definitions and runtime protocol.
if ARMOR_REFRESH_ROUTE_PROBE then
    local config_path=registry_directory..'/refresh-auto.config'
    local config_cache,config_at=nil,0
    local function test_config()
        if clock()<config_at then return config_cache end
        config_at=clock()+0.5;config_cache=nil
        local c=parse_key_values(read_file(config_path,1024))
        if c.schema=='2' and c.enabled=='true' and c.scope=='local_equipped_body'
            and c.confirm=='single_player_ship_armory_closed' then config_cache={} end
        return config_cache
    end
    local file,log_bytes=nil,0
    local refresh_log_path=registry_directory..'/refresh-auto-'..pid..'.log'
    local function report(row)
        if not file then
            file=io.open(refresh_log_path,'ab')
            if file then log_bytes=file:seek('end') or 0 end
        end
        local fields={'utc='..os.date('!%Y-%m-%dT%H:%M:%SZ'),'pid='..pid}
        for k,v in pairs(row) do
            if type(v)~='table' then fields[#fields+1]=k..'='..tostring(v):gsub('[\r\n]',' ') end
        end
        local line=table.concat(fields,' ')..'\n'
        if file and log_bytes+#line>2*1024*1024 then
            file:close();file=nil
            os.remove(refresh_log_path..'.previous')
            os.rename(refresh_log_path,refresh_log_path..'.previous')
            file=io.open(refresh_log_path,'wb');log_bytes=0
        end
        if file then file:write(line);file:flush();log_bytes=log_bytes+#line end
        if row.kind~='pulse_observation' then log('refresh: '..line:gsub('\n$','')) end
        M.refresh_test_state=row.kind
        if row.kind~='pulse_observation' then
            write_atomic(registry_directory..'/refresh-auto.status','schema=1\npid='..pid
                ..'\nstate='..tostring(row.kind)..'\nrevision='..tostring(M.applied_revision or -1)
                ..'\nreason='..tostring(row.reason or row.skipped or ''):gsub('[\r\n]',' ')
                ..'\nattempt='..tostring(row.attempt or '')..'\n')
        end
    end
    auto_refresh=ARMOR_REFRESH_ROUTE_PROBE.auto({clock=clock,read=read,u32=u32,
        base=function(name)return tonumber(ffi.cast('uintptr_t',kernel.GetModuleHandleA(name)))end,
        config=test_config,revision=function()return M.applied_revision or -1 end,
        ready=function()
            return M.status=='ready' and not desired and not apply_worker
                and read_file(protocol_path,65536)==last_protocol_text
        end,
        pulse_write=refresh_pending_write,emit=report,
        flush=function()if file then file:flush()end end})
end

local previous=rawget(_G,'update')
if type(previous)~='function' then
    M.status='update_unavailable';log('update_unavailable');return M
end
local elapsed,poll_at,locate_at=0,0,5
local worker
set_status('waiting','waiting for armor registry',nil)
local function tick(dt)
    frame_deadline=clock()+0.001
    if type(dt)=='number' and dt>=0 and dt<10 then elapsed=elapsed+dt end
    if elapsed>=poll_at then poll_at=elapsed+1;poll_protocol() end
    poll_weight_audit(dt)
    if audit_worker and clock()<frame_deadline then
        local okay,result,done=advance(audit_worker)
        if not okay then
            write_atomic(weight_audit_report_path,'schema=1\nstate=error\nmessage='
                ..tostring(result):gsub('[\r\n=]',' ')..'\n')
            log('weight_audit_error: '..tostring(result));audit_worker=nil
        elseif done then audit_worker=nil end
    end
    if not M.registry and elapsed>=locate_at and clock()<frame_deadline then
        if not worker then
            local started=clock()
            worker=coroutine.create(function()
                local registry,reason=locate_registry()
                if registry then
                    M.registry=registry
                    M.locate_ms=math.floor((clock()-started)*1000+0.5)
                    M.located_after_ms=math.floor((clock()-runtime_started)*1000+0.5)
                    set_status('located','validated runtime registry',M.applied_revision)
                else M.last_locate_error=reason end
            end)
        end
        local okay,result,done=advance(worker)
        if not okay or done then
            worker=nil;locate_at=elapsed+5
            if not okay then set_status('locator_error',tostring(result),M.applied_revision)
            elseif not M.registry then
                set_status('waiting',M.last_locate_error or 'registry not ready',M.applied_revision)
            end
        end
    end
    if clock()<frame_deadline then step_apply(elapsed) end
end
local wrapper=function(dt,...)
    local okay,reason=pcall(tick,dt)
    if not okay then
        apply_worker=nil;desired=nil
        pcall(set_status,'runtime_error',tostring(reason),M.applied_revision)
    end
    if auto_refresh then
        local refresh_ok,err=pcall(auto_refresh.tick)
        if not refresh_ok then pcall(auto_refresh.fail,err) end
    end
    if refresh_probe then
        local probe_ok=pcall(refresh_probe.tick)
        if not probe_ok then refresh_probe.fail() end
    end
    if refresh_hotkey then
        local okay=pcall(refresh_hotkey.tick)
        if not okay then refresh_hotkey.fail() end
    end
    -- Never swallow the game's own errors or suppress its normal update.
    return previous(dt,...)
end
rawset(_G,'update',wrapper)
return M
