-- One-shot read-only comparison probe for the runtime armor registry.
local existing=rawget(_G,'HD2ArmorMetadataProbe')
if existing then return existing end
local M={version='0.1.0',status='starting'}
rawset(_G,'HD2ArmorMetadataProbe',M)

local loader=rawget(_G,'CowboyBingusModLoader')
local shared_loader=type(loader)=='table' and loader.api==1
    and type(loader.open_log)=='function'

local ok,ffi=pcall(require,'ffi')
if not ok or ffi.os~='Windows' or not ffi.abi('64bit') then
    M.status='unsupported'
    log_file:write('probe_error=windows_x64_ffi_required\n');log_file:flush()
    return M
end
ffi.cdef[[
void *GetCurrentProcess(void);
uint32_t GetEnvironmentVariableA(const char *,char *,uint32_t);
int ReadProcessMemory(void *,const void *,void *,size_t,size_t *);
int CreateDirectoryA(const char *,void *);
uint32_t GetLastError(void);
]]
local kernel=ffi.load('kernel32')
local process=kernel.GetCurrentProcess()
local function environment(name)
    local required=tonumber(kernel.GetEnvironmentVariableA(name,nil,0))
    if not required or required<2 or required>32768 then return nil end
    local buffer=ffi.new('char[?]',required)
    local written=tonumber(kernel.GetEnvironmentVariableA(name,buffer,required))
    if not written or written<1 or written>=required then return nil end
    return ffi.string(buffer,written)
end
local function mkdir(path)
    if kernel.CreateDirectoryA(path,nil)~=0 then return true end
    return tonumber(kernel.GetLastError())==183
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
if not base_directory then M.status='base_directory_unavailable';return M end
local logs_directory=base_directory..'/Logs'
local directory=base_directory..'/ArmorRegistry'
for _,path in ipairs({base_directory,logs_directory,directory}) do
    if not mkdir(path) then M.status='directory_unavailable';return M end
end
local log_file
if shared_loader then
    local opened,result=pcall(loader.open_log,'ArmorMetadataProbe.log')
    if opened then log_file=result end
else
    log_file=io.open(logs_directory..'/ArmorMetadataProbe.log','ab')
end
if not log_file then M.status='log_open_failed';return M end
local request_path=directory..'/metadata-probe.request'
local report_path=directory..'/metadata-probe.report'

local CORE=ARMOR_METADATA_PROBE_CORE
local function read(address,size)
    if not address or address<65536 or size<1 or size>0x400000 then return nil end
    local buffer=ffi.new('uint8_t[?]',size)
    local got=ffi.new('size_t[1]')
    if kernel.ReadProcessMemory(process,ffi.cast('const void *',address),
        buffer,size,got)==0 or tonumber(got[0])~=size then return nil end
    return ffi.string(buffer,size)
end
local function pointer(data,offset)
    local value=CORE.read_u64(data,offset,ffi)
    if not value or value<65536 or value>0x00007fffffffffff then return nil end
    return value
end
local function json_quote(value)
    return '"'..tostring(value):gsub('[%z\1-\31\\"]',function(c)
        if c=='\\' then return '\\\\' end
        if c=='"' then return '\\"' end
        return string.format('\\u%04x',string.byte(c))
    end)..'"'
end
local function emit(file,kind,fields)
    local row={'"record_type":'..json_quote(kind)}
    for key,value in pairs(fields or {}) do
        local encoded
        if type(value)=='boolean' then encoded=value and 'true' or 'false'
        elseif type(value)=='number' then encoded=tostring(value)
        elseif value==nil then encoded='null'
        else encoded=json_quote(value) end
        row[#row+1]=json_quote(key)..':'..encoded
    end
    file:write('{'..table.concat(row,',')..'}\n')
end
local function kit_fields(kit)
    local fields=CORE.decode_kit_header(kit.header)
    fields.body_count=kit.body_count
    fields.piece_count=#kit.pieces
    return fields
end
local function read_kit(item_id,item)
    local header=read(item.address,64)
    if not header then return nil,'kit_header_unreadable' end
    local body_base=pointer(header,48)
    local body_count=CORE.read_u64(header,56,ffi)
    if not body_base or not body_count or body_count<1 or body_count>16 then
        return nil,'invalid_body_array'
    end
    local kit={id=item_id,index=item.index,root=item.address,header=header,
        body_count=body_count,bodies={},pieces={}}
    for body_index=0,body_count-1 do
        local row=read(body_base+body_index*24,24)
        if not row then return nil,'body_row_unreadable' end
        local body_type=CORE.u32(row,0)
        local piece_base=pointer(row,8)
        local piece_count=CORE.read_u64(row,16,ffi)
        if not piece_base or not piece_count or piece_count<1 or piece_count>4096
            or #kit.pieces+piece_count>4096 then return nil,'invalid_piece_array' end
        local body={body_type=body_type,raw_prefix=row:sub(1,8),
            piece_count=piece_count,pieces={}}
        for piece_index=0,piece_count-1 do
            local raw=read(piece_base+piece_index*96,96)
            if not raw then return nil,'piece_record_unreadable' end
            local decoded=CORE.decode_piece(raw,ffi)
            decoded.body=body_type
            decoded.body_index=body_index
            decoded.piece_index=piece_index
            decoded.raw=raw
            body.pieces[#body.pieces+1]=decoded
            kit.pieces[#kit.pieces+1]=decoded
        end
        kit.bodies[#kit.bodies+1]=body
    end
    return kit
end
local function request_ids(text)
    local source,targets=nil,{}
    for line in text:gmatch('[^\r\n]+') do
        local value=line:match('^source=(%d+)$')
        if value then
            if source then return nil,nil,'duplicate_source' end
            source=tonumber(value)
        else
            value=line:match('^target=(%d+)$')
            if not value or #targets>=16 then return nil,nil,'invalid_request' end
            targets[#targets+1]=tonumber(value)
        end
    end
    if not source or #targets==0 then return nil,nil,'missing_source_or_targets' end
    return source,targets
end
local function read_request()
    local file=io.open(request_path,'rb')
    if not file then return nil end
    local text=file:read(4097);file:close()
    if not text or #text>4096 then return nil,'request_too_large' end
    if os.remove(request_path)==nil then return nil,'request_remove_failed' end
    return text
end
local function snapshot(registry,source_id,target_ids)
    local source_entry=registry.items[source_id]
    if not source_entry then return nil,'source_not_registered' end
    local source,reason=read_kit(source_id,source_entry)
    if not source then return nil,'source_'..reason end
    local output,open_error=io.open(report_path,'wb')
    if not output then return nil,'report_open_failed:'..tostring(open_error) end
    emit(output,'run',{version=M.version,source_item_id=source_id,
        registry_count=registry.count,targets_requested=#target_ids,
        private_simulation_only=true,probe_game_memory_writes=false,
        field_map='Piece+0 Unit hash; +8 slot; +12 piece_type; +16 weight; +24..+80 resource hashes; +88 tone; +89..+95 reserved',
        field_roles='Unit/resources/tone are visual candidates; slot/body/piece_type are structural; weight is gameplay-sensitive based on prior controlled armor-value tests'})
    local source_header=kit_fields(source)
    source_header.role='source'
    source_header.index=source.index
    source_header.identity_metadata_hex=source.header:sub(1,48):gsub('.',function(c)
        return string.format('%02x',string.byte(c))
    end)
    emit(output,'kit_header',source_header)
    for _,body in ipairs(source.bodies) do
        emit(output,'body',{role='source',item_id=source.id,
            body_index=body.body_index,body_type=body.body_type,
            raw_prefix_hex=body.raw_prefix:gsub('.',function(c)
                return string.format('%02x',string.byte(c))
            end),piece_count=body.piece_count})
    end
    -- Keep the complete source records alongside target and simulated rows.
    for _,piece in ipairs(source.pieces) do
        emit(output,'piece',{role='source',item_id=source.id,
            body_index=piece.body_index,body_type=piece.body,
            piece_index=piece.piece_index,unit=piece.unit,slot=piece.slot,
            piece_type=piece.piece_type,weight=piece.weight,
            resources=piece.resources,tone_variations=piece.tone_variations,
            reserved_hex=piece.reserved_hex,raw_hex=piece.raw_hex})
    end
    for _,target_id in ipairs(target_ids) do
        local target_entry=registry.items[target_id]
        if not target_entry then
            emit(output,'target_error',{target_item_id=target_id,
                reason='target_not_registered'})
        else
            local target,target_error=read_kit(target_id,target_entry)
            if not target then
                emit(output,'target_error',{target_item_id=target_id,
                    reason=target_error})
            else
                local active=rawget(_G,'HD2RuntimeArmorRegistry').active
                local live_pointer=read(registry.index_base+target.index*8,8)
                local live_number=live_pointer and CORE.read_u64(live_pointer,0,ffi)
                local target_header=kit_fields(target)
                target_header.role='target'
                target_header.index=target.index
                target_header.identity_metadata_hex=target.header:sub(1,48):gsub('.',function(c)
                    return string.format('%02x',string.byte(c))
                end)
                target_header.currently_mapped=active and active[tostring(target_id)]~=nil or false
                target_header.live_index_matches_original=live_number==target.root
                emit(output,'kit_header',target_header)
                local target_weights={}
                for _,body in ipairs(target.bodies) do
                    emit(output,'body',{role='target',item_id=target.id,
                        body_index=body.body_index,body_type=body.body_type,
                        raw_prefix_hex=body.raw_prefix:gsub('.',function(c)
                            return string.format('%02x',string.byte(c))
                        end),piece_count=body.piece_count})
                end
                for _,piece in ipairs(target.pieces) do
                    target_weights[#target_weights+1]={
                        body=piece.body,slot=piece.slot,piece_type=piece.piece_type,
                        weight=piece.weight
                    }
                    emit(output,'piece',{role='target',item_id=target.id,
                        body_index=piece.body_index,body_type=piece.body,
                        piece_index=piece.piece_index,unit=piece.unit,slot=piece.slot,
                        piece_type=piece.piece_type,weight=piece.weight,
                        resources=piece.resources,tone_variations=piece.tone_variations,
                        reserved_hex=piece.reserved_hex,raw_hex=piece.raw_hex})
                end
                local weights=CORE.weight_index(target_weights)
                local simulated_count,ambiguous,weight_changes=0,0,0
                for _,body in ipairs(source.bodies) do
                    for _,piece in ipairs(body.pieces) do
                        local weight=CORE.resolve_weight(weights,piece.body,
                            piece.slot,piece.piece_type)
                        if weight==nil then
                            ambiguous=ambiguous+1
                            emit(output,'simulation_error',{target_item_id=target.id,
                                body_type=piece.body,slot=piece.slot,
                                piece_type=piece.piece_type,
                                reason='target_weight_metadata_ambiguous'})
                        else
                            local simulated=CORE.replace_weight(piece.raw,weight)
                            local diff=CORE.piece_diff(
                                piece.raw,piece.raw,simulated)
                            simulated_count=simulated_count+1
                            if diff.weight then weight_changes=weight_changes+1 end
                            local decoded=CORE.decode_piece(simulated,ffi)
                            emit(output,'piece',{role='simulated',item_id=target.id,
                                body_index=piece.body_index,body_type=piece.body,
                                piece_index=piece.piece_index,unit=decoded.unit,
                                slot=decoded.slot,piece_type=decoded.piece_type,
                                weight=decoded.weight,resources=decoded.resources,
                                tone_variations=decoded.tone_variations,
                                reserved_hex=decoded.reserved_hex,
                                raw_hex=decoded.raw_hex,
                                changed_from_source_unit=diff.unit,
                                changed_from_source_slot=diff.slot,
                                changed_from_source_piece_type=diff.piece_type,
                                changed_from_source_weight=diff.weight,
                                changed_from_source_resources=diff.resources,
                                changed_from_source_tone=diff.tone_variations,
                                changed_from_source_reserved=diff.reserved})
                        end
                    end
                end
                emit(output,'clone_summary',{target_item_id=target.id,
                    source_item_id=source.id,target_header_bytes_0_47_preserved=true,
                    source_body_and_piece_shape_used=true,
                    pieces_simulated=simulated_count,ambiguous_weights=ambiguous,
                    donor_piece_weights_overwritten=weight_changes,
                    other_donor_piece_fields_overwritten=0,
                    target_header_non_pointer_fields_preserved=true,
                    gameplay_causality_proven=false})
            end
        end
    end
    output:write('') -- Keep report creation explicit even for empty target arrays.
    output:close()
    return true
end
local function log(text)
    log_file:write(tostring(text)..'\n');log_file:flush()
end
local previous=rawget(_G,'update')
if type(previous)~='function' then M.status='update_unavailable';return M end
local elapsed,poll_at=0,0
local wrapper
wrapper=function(dt,...)
    if type(dt)=='number' and dt>=0 and dt<10 then elapsed=elapsed+dt end
    if elapsed>=poll_at then
        poll_at=elapsed+1
        local text,request_error=read_request()
        if request_error then
            log('request_error='..request_error)
        elseif text then
            local source,targets,parse_error=request_ids(text)
            local runtime=rawget(_G,'HD2RuntimeArmorRegistry')
            if not source then
                log('request_error='..parse_error)
            elseif type(runtime)~='table' or type(runtime.registry)~='table' then
                log('probe_waiting_runtime_registry')
                local retry=io.open(request_path,'wb')
                if retry then retry:write(text);retry:close() end
            else
                local okay,result,reason=pcall(snapshot,runtime.registry,source,targets)
                if not okay then
                    log('probe_error='..tostring(result):gsub('[\r\n]',' '))
        elseif not result then
            log('probe_error='..tostring(reason))
            local report=io.open(report_path,'wb')
            if report then
                emit(report,'probe_error',{reason=tostring(reason),
                    game_memory_writes=false})
                report:close()
            end
                else
                    M.status='complete'
                    log('probe_complete source='..source..' targets='..#targets)
                end
            end
        end
    end
    return previous(dt,...)
end
rawset(_G,'update',wrapper)
M.status='waiting_for_request'
log('probe_ready version='..M.version..' writes_game_memory=false')
return M
