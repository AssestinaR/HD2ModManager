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
                local candidates=slot==1
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
        local plan,reason=ARMOR_REGISTRY_COMPOSITION.plan(target,source,model,
            function(body,unit,slot,piece_type)
                return piece_enabled(overrides,owner,body,unit,slot,piece_type)
            end,ARMOR_METADATA_PROBE_CORE,checkpoint)
        if not plan then return nil,'target '..mapping.target..': '..tostring(reason) end
        requests[#requests+1]={key=tostring(mapping.target),original=entry.address,
            slot=registry.index_base+entry.index*8,signature=plan.payload,bytes=plan.bytes,
            build=function()return allocate_plan(plan)end}
        checkpoint()
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
    plan.snapshots=snapshots
    return plan
end
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
    return true,stats
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
            local prepared=clock()
            local okay,stats=publish_document(plan,document)
            if not okay then return {okay=false,reason=stats} end
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
            set_status('ready',string.format('incremental applied: built=%d reused=%d removed=%d writes=%d',
                result.stats.built,result.stats.reused,result.stats.removed,result.stats.writes),
                apply_document_pending.revision)
            desired=nil
        else
            attempts=attempts+1;retry_at=elapsed+attempts*2
            local reason=okay and result.reason or tostring(result)
            if not reason:find('registry changed during preparation',1,true) then attempts=3 end
            set_status('apply_error',reason,M.applied_revision)
        end
    end
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
    -- Never swallow the game's own errors or suppress its normal update.
    return previous(dt,...)
end
rawset(_G,'update',wrapper)
return M
