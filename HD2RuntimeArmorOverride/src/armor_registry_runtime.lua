local existing=rawget(_G,'HD2RuntimeArmorRegistry')
if existing then return existing end
local M={version='0.1.0',status='starting',active={}}
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
size_t VirtualQuery(const void *,void *,size_t);
int CreateDirectoryA(const char *,void *);
uint32_t GetLastError(void);
int MoveFileExA(const char *,const char *,uint32_t);
]]
local kernel=ffi.load('kernel32')
local process=kernel.GetCurrentProcess()
local module_base=tonumber(ffi.cast('uintptr_t',kernel.GetModuleHandleA('game.dll')))
local pid=tonumber(kernel.GetCurrentProcessId())

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
    return tonumber(ffi.cast('uint64_t *',address)[0])
end
local function dword(address)
    return tonumber(ffi.cast('uint32_t *',address)[0])
end
local function put_qword(address,value)
    if address%8~=0 then return false end
    ffi.cast('uint64_t *',address)[0]=ffi.new('uint64_t',value)
    return qword(address)==value
end
local function put_dword(address,value)
    ffi.cast('uint32_t *',address)[0]=ffi.new('uint32_t',value)
end
local function put_hex64(address,value)
    for i=0,7 do
        local at=#value-(i+1)*2+1
        local byte=tonumber(value:sub(at,at+1),16)
        ffi.cast('uint8_t *',address+i)[0]=byte
    end
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
        if queries%2048==0 then coroutine.yield() end
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
local function find_target_piece(root,body,slot,piece_type)
    local bodies,body_count=qword(root+48),qword(root+56)
    if bodies<65536 or body_count<1 or body_count>16 then return nil end
    for i=0,body_count-1 do
        local row=bodies+i*24
        if dword(row)==body then
            local pieces,count=qword(row+8),qword(row+16)
            if pieces<65536 or count<1 or count>4096 then return nil end
            for piece=0,count-1 do
                local address=pieces+piece*96
                if dword(address+8)==slot and dword(address+12)==piece_type then
                    return address
                end
            end
        end
    end
    return nil
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
local function build_clone(target_root,source_root)
    local weights,weight_error=target_weights(target_root)
    if not weights then return nil,weight_error end
    local source_bodies,body_count=qword(source_root+48),qword(source_root+56)
    if source_bodies<65536 or body_count<1 or body_count>16 then
        return nil,'invalid source Body array'
    end
    local rows,total={},0
    for i=0,body_count-1 do
        local row=source_bodies+i*24
        local pieces,count=qword(row+8),qword(row+16)
        if pieces<65536 or count<1 or count>4096 then
            return nil,'invalid source Piece array'
        end
        rows[i+1]={body=dword(row),source_row=row,pieces=pieces,count=count}
        total=total+count
        if total>4096 then return nil,'source Piece total too large' end
    end
    local bytes=64+body_count*24+total*96
    local buffer=ffi.new('uint8_t[?]',bytes)
    local base=tonumber(ffi.cast('uintptr_t',buffer))
    ffi.copy(buffer,ffi.cast('const void *',target_root),64)
    put_qword(base+48,base+64);put_qword(base+56,body_count)
    local piece_at=64+body_count*24
    local resolutions={exact=0,body=0,global=0}
    for i,row in ipairs(rows) do
        local target_row=base+64+(i-1)*24
        ffi.copy(ffi.cast('void *',target_row),
            ffi.cast('const void *',row.source_row),8)
        put_qword(target_row+8,base+piece_at);put_qword(target_row+16,row.count)
        local piece_bytes=row.count*96
        ffi.copy(ffi.cast('void *',base+piece_at),
            ffi.cast('const void *',row.pieces),piece_bytes)
        for piece=0,row.count-1 do
            local address=base+piece_at+piece*96
            local weight,level=resolve_weight(weights,row.body,dword(address+8),dword(address+12))
            if weight==nil then
                return nil,'target weight metadata has no unique majority'
            end
            put_dword(address+16,weight)
            resolutions[level]=resolutions[level]+1
        end
        piece_at=piece_at+piece_bytes
    end
    if piece_at~=bytes then return nil,'clone length self-check failed' end
    return {buffer=buffer,base=base,bytes=bytes,bodies=body_count,
        pieces=total,weight_resolutions=resolutions}
end

local function build_model_clone(target_root,model)
    local weights,weight_error=target_weights(target_root)
    if not weights then return nil,weight_error end
    local grouped,order={},{}
    for _,piece in ipairs(model.pieces or {}) do
        if not grouped[piece.body] then
            grouped[piece.body]={body=piece.body,pieces={}}
            order[#order+1]=piece.body
        end
        grouped[piece.body].pieces[#grouped[piece.body].pieces+1]=piece
    end
    if #order<1 or #order>16 then return nil,'model Body count is invalid' end
    -- Stingray's current armor consumer emits the three body arrays in the
    -- stable order Any(3), Stocky(0), Slim(1). Keep custom models in that
    -- order as well; insertion order is not a safe ABI contract.
    local body_rank={ [3]=1, [0]=2, [1]=3 }
    table.sort(order,function(left,right)
        local left_rank=body_rank[left] or 1000+left
        local right_rank=body_rank[right] or 1000+right
        return left_rank<right_rank
    end)
    local total=0
    for _,body in ipairs(order) do
        local count=#grouped[body].pieces
        if count<1 or count>4096 then return nil,'model Piece count is invalid' end
        total=total+count
    end
    if total>4096 then return nil,'model Piece total too large' end
    local bytes=64+#order*24+total*96
    local buffer=ffi.new('uint8_t[?]',bytes)
    local base=tonumber(ffi.cast('uintptr_t',buffer))
    ffi.copy(buffer,ffi.cast('const void *',target_root),64)
    put_qword(base+48,base+64);put_qword(base+56,#order)
    local piece_at=64+#order*24
    local resolutions={exact=0,body=0,global=0}
    for index,body_id in ipairs(order) do
        local group=grouped[body_id]
        local body_row=base+64+(index-1)*24
        put_dword(body_row,body_id)
        put_qword(body_row+8,base+piece_at)
        put_qword(body_row+16,#group.pieces)
        for piece_index,piece in ipairs(group.pieces) do
            local address=base+piece_at+(piece_index-1)*96
            local target_piece=find_target_piece(target_root,body_id,piece.slot,piece.piece_type)
            if target_piece then
                -- Preserve the target's auxiliary resource hashes and other
                -- engine-owned Piece fields. Only visual Unit/slot/type,
                -- target gameplay weight, and tone are replaced below.
                ffi.copy(ffi.cast('void *',address),
                    ffi.cast('const void *',target_piece),96)
            else
                ffi.fill(ffi.cast('void *',address),96,0)
            end
            put_hex64(address,piece.unit)
            put_dword(address+8,piece.slot)
            put_dword(address+12,piece.piece_type)
            local weight,level=resolve_weight(weights,body_id,piece.slot,piece.piece_type)
            if weight==nil then return nil,'target weight metadata has no unique majority' end
            put_dword(address+16,weight)
            -- The serialized Piece contract stores tone variation at +88.
            -- Writing at +89 corrupts the adjacent byte and can surface only
            -- later when the engine revisits the composed armor.
            ffi.cast('uint8_t *',address+88)[0]=piece.tone or 0
            resolutions[level]=resolutions[level]+1
        end
        piece_at=piece_at+#group.pieces*96
    end
    return {buffer=buffer,base=base,bytes=bytes,bodies=#order,
        pieces=total,weight_resolutions=resolutions}
end

local function apply_document(document)
    local registry=M.registry
    if not registry then return false,'registry is not ready' end
    local models={}
    local model_keys={}
    for _,model in ipairs(document.models or {}) do
        if model_keys[model.key] then return false,'duplicate model key '..model.key end
        model_keys[model.key]=true
        local source=model.source and registry.items[model.source] or nil
        if not source and #(model.pieces or {})==0 then
            return false,'unknown model source item '..tostring(model.source)
        end
        models[model.key]={key=model.key,source=model.source,
            address=source and source.address,pieces=model.pieces or {}}
    end
    local desired={}
    if document.enabled then
        for _,mapping in ipairs(document.mappings) do
            desired[tostring(mapping.target)]=mapping
        end
    end
    local prepared={}
    for key,mapping in pairs(desired) do
        local target=registry.items[mapping.target]
        local source
        local model_definition
        if mapping.model then
            source=models[mapping.model]
            if not source then return false,'unknown model '..mapping.model end
            model_definition=source
        else
            source=registry.items[mapping.source]
        end
        if not target then return false,'unknown target item '..mapping.target end
        if not source then return false,'unknown source item '..tostring(mapping.source) end
        local clone,reason=model_definition
            and (#(model_definition.pieces or {})>0
                and build_model_clone(target.address,model_definition)
                or build_clone(target.address,source.address))
            or build_clone(target.address,source.address)
        if not clone then return false,'target '..mapping.target..': '..reason end
        clone.target=mapping.target;clone.source=mapping.source
        clone.model=mapping.model
        clone.original=target.address;clone.slot=registry.index_base+target.index*8
        prepared[key]=clone
    end
    local operations={}
    for key,active in pairs(M.active) do
        if not prepared[key] then
            operations[#operations+1]={slot=active.slot,value=active.original,
                allowed=active.base,key=key}
        end
    end
    for key,clone in pairs(prepared) do
        local active=M.active[key]
        operations[#operations+1]={slot=clone.slot,value=clone.base,
            allowed=active and active.base or clone.original,key=key}
    end
    for _,operation in ipairs(operations) do
        if qword(operation.slot)~=operation.allowed then
            return false,'index slot changed externally for target '..operation.key
        end
    end
    local changed={}
    for _,operation in ipairs(operations) do
        local before=qword(operation.slot)
        if not put_qword(operation.slot,operation.value) then
            for i=#changed,1,-1 do put_qword(changed[i].slot,changed[i].before) end
            return false,'index publication failed for target '..operation.key
        end
        changed[#changed+1]={slot=operation.slot,before=before}
    end
    M.active=prepared
    local count=0
    for _ in pairs(prepared) do count=count+1 end
    M.active_count=count
    M.applied_revision=document.revision
    return true
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
local function poll_protocol()
    local text=read_file(protocol_path,65536)
    if not text or text==last_protocol_text then return end
    local parsed,document=pcall(ARMOR_REGISTRY_PROTOCOL.parse,text)
    if not parsed then
        set_status('protocol_error',tostring(document),M.applied_revision)
        return
    end
    desired=document
    last_protocol_text=text
    if M.registry then
        local applied,reason=apply_document(document)
        if applied then
            set_status('ready','protocol applied',document.revision)
        else
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
local wrapper
wrapper=function(dt,...)
    if type(dt)=='number' and dt>=0 and dt<10 then elapsed=elapsed+dt end
    if elapsed>=poll_at then poll_at=elapsed+1;poll_protocol() end
    poll_weight_audit(dt)
    if audit_worker then
        local resumed,reason=coroutine.resume(audit_worker)
        if not resumed then
            write_atomic(weight_audit_report_path,table.concat({
                'schema=1','state=error',
                'message='..tostring(reason):gsub('[\r\n=]',' '),''
            },'\n'))
            log('weight_audit_error: '..tostring(reason))
            audit_worker=nil
        elseif coroutine.status(audit_worker)=='dead' then
            audit_worker=nil
        end
    end
    if not M.registry and elapsed>=locate_at then
        if not worker then
            worker=coroutine.create(function()
                local registry,reason=locate_registry()
                if registry then
                    M.registry=registry
                    set_status('located','validated runtime registry',M.applied_revision)
                    if desired then
                        local applied,apply_reason=apply_document(desired)
                        if applied then
                            set_status('ready','protocol applied',desired.revision)
                        else
                            set_status('apply_error',apply_reason,M.applied_revision)
                        end
                    end
                else
                    M.last_locate_error=reason
                end
            end)
        end
        local resumed,reason=coroutine.resume(worker)
        if not resumed then
            set_status('locator_error',tostring(reason),M.applied_revision)
            worker=nil;locate_at=elapsed+5
        elseif coroutine.status(worker)=='dead' then
            if not M.registry then
                set_status('waiting',M.last_locate_error or 'registry not ready',
                    M.applied_revision)
                worker=nil;locate_at=elapsed+5
            end
        end
    end
    return previous(dt,...)
end
rawset(_G,'update',wrapper)
return M
