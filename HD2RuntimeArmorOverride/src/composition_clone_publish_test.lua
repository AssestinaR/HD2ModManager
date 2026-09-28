-- Controlled composition expansion test. It retains the target Kit header
-- and publishes an independently allocated donor Body/Piece composition.
local existing=rawget(_G,'HD2ArmorCompositionClonePublish')
if existing then return existing end
local M={version='0.3.0',status='starting'}
rawset(_G,'HD2ArmorCompositionClonePublish',M)

local loader=rawget(_G,'CowboyBingusModLoader')
if type(loader)~='table' or loader.api~=1 or type(loader.open_log)~='function' then
    M.status='loader_unavailable';return M
end
local opened,file=pcall(loader.open_log,'ArmorCompositionClonePublish.log')
if not opened or not file then M.status='log_open_failed';return M end
local function log(text) file:write(tostring(text)..'\n');file:flush() end
local ok,ffi=pcall(require,'ffi')
if not ok or ffi.os~='Windows' or not ffi.abi('64bit') then
    M.status='unsupported';log('windows_x64_ffi_required');file:close();return M
end

local TARGET_ITEM='488691438'  -- B-08, 19 pieces
local DONOR_ITEM='820333399'   -- RE-2310, 23 pieces
local previous=rawget(_G,'update')
if type(previous)~='function' then
    M.status='update_unavailable';file:close();return M
end

local function qword(address)
    return tonumber(ffi.cast('uint64_t *',address)[0])
end
local function dword(address)
    return tonumber(ffi.cast('uint32_t *',address)[0])
end
local function put_qword(address,value)
    assert(address%8==0,'unaligned qword store')
    ffi.cast('uint64_t *',address)[0]=ffi.new('uint64_t',value)
end
local function put_dword(address,value)
    assert(address%4==0,'unaligned dword store')
    ffi.cast('uint32_t *',address)[0]=ffi.new('uint32_t',value)
end
local function publish(slot,expected,value)
    if slot%8~=0 or qword(slot)~=expected then return false,'slot_mismatch' end
    put_qword(slot,value)
    if qword(slot)~=value then return false,'write_verification_failed' end
    return true
end
local function build_clone(target_root,donor_root)
    local target_bodies=qword(target_root+48)
    local target_body_count=qword(target_root+56)
    if target_body_count<1 or target_body_count>16 or target_bodies<65536 then
        return nil,'invalid_target_body_header'
    end
    local target_visible_weight,target_metadata_weight
    for i=0,target_body_count-1 do
        local row=target_bodies+i*24
        local pieces,count=qword(row+8),qword(row+16)
        if pieces<65536 or count<1 or count>4096 then
            return nil,'invalid_target_piece_header'
        end
        for piece=0,count-1 do
            local slot=dword(pieces+piece*96+8)
            local weight=dword(pieces+piece*96+16)
            if slot==0 then
                if target_metadata_weight==nil then target_metadata_weight=weight end
                if weight~=target_metadata_weight then
                    return nil,'mixed_target_metadata_weights'
                end
            else
                if target_visible_weight==nil then target_visible_weight=weight end
                if weight~=target_visible_weight then
                    return nil,'mixed_target_visible_weights'
                end
            end
        end
    end
    if target_visible_weight==nil or target_metadata_weight==nil then
        return nil,'target_weight_classes_incomplete'
    end
    local donor_bodies=qword(donor_root+48)
    local body_count=qword(donor_root+56)
    if body_count<1 or body_count>16 or donor_bodies<65536 then
        return nil,'invalid_donor_body_header'
    end
    local counts,total={},0
    for i=0,body_count-1 do
        local row=donor_bodies+i*24
        local pieces,count=qword(row+8),qword(row+16)
        if pieces<65536 or count<1 or count>4096 then
            return nil,'invalid_donor_piece_header'
        end
        counts[i+1]={source=pieces,count=count}
        total=total+count
        if total>4096 then return nil,'donor_piece_total_too_large' end
    end
    local bytes=64+body_count*24+total*96
    local buffer=ffi.new('uint8_t[?]',bytes)
    local base=tonumber(ffi.cast('uintptr_t',buffer))
    ffi.copy(buffer,ffi.cast('const void *',target_root),64)
    put_qword(base+48,base+64)
    put_qword(base+56,body_count)
    local piece_at=64+body_count*24
    for i=0,body_count-1 do
        local source_row=donor_bodies+i*24
        local target_row=base+64+i*24
        ffi.copy(ffi.cast('void *',target_row),ffi.cast('const void *',source_row),8)
        put_qword(target_row+8,base+piece_at)
        put_qword(target_row+16,counts[i+1].count)
        local piece_bytes=counts[i+1].count*96
        ffi.copy(ffi.cast('void *',base+piece_at),
            ffi.cast('const void *',counts[i+1].source),piece_bytes)
        for piece=0,counts[i+1].count-1 do
            local piece_address=base+piece_at+piece*96
            local weight=dword(piece_address+8)==0
                and target_metadata_weight or target_visible_weight
            put_dword(piece_address+16,weight)
        end
        piece_at=piece_at+piece_bytes
    end
    if piece_at~=bytes or qword(base+48)~=base+64 or qword(base+56)~=body_count then
        return nil,'clone_self_check_failed'
    end
    for i=0,total-1 do
        local piece_address=base+64+body_count*24+i*96
        local expected=dword(piece_address+8)==0
            and target_metadata_weight or target_visible_weight
        if dword(piece_address+16)~=expected then
            return nil,'clone_weight_self_check_failed'
        end
    end
    return {buffer=buffer,base=base,bytes=bytes,
        body_count=body_count,piece_count=total,
        target_visible_weight=target_visible_weight,
        target_metadata_weight=target_metadata_weight}
end

function M.clear()
    if M.status~='applied' or not M.slot or not M.original then return false end
    local restored,reason=publish(M.slot,M.clone_base,M.original)
    if not restored then
        M.status='rollback_refused';log('rollback_refused '..tostring(reason))
        return false
    end
    M.status='rolled_back';log('rollback_ok');return true
end

local wrapper
wrapper=function(dt,...)
    if M.status=='waiting' then
        local probe=rawget(_G,'HD2ArmorCompositionProbe')
        local result=type(probe)=='table' and probe.result
        local target=result and result.roots and result.roots[TARGET_ITEM]
        local donor=result and result.roots and result.roots[DONOR_ITEM]
        if result and result.matched_entries==411 and target and donor then
            local clone,reason=build_clone(target.address,donor.address)
            if not clone then
                M.status='clone_failed';log('clone_failed '..tostring(reason))
            else
                local slot=result.index_base+target.index*8
                local applied,publish_reason=publish(slot,target.address,clone.base)
                if not applied then
                    M.status='publish_failed'
                    log('publish_failed '..tostring(publish_reason))
                else
                    M.buffer=clone.buffer
                    M.clone_base=clone.base
                    M.clone_bytes=clone.bytes
                    M.body_count=clone.body_count
                    M.piece_count=clone.piece_count
                    M.target_visible_weight=clone.target_visible_weight
                    M.target_metadata_weight=clone.target_metadata_weight
                    M.slot,M.original=slot,target.address
                    M.target_index,M.donor_index=target.index,donor.index
                    M.status='applied'
                    log(string.format(
                        'applied target=B-08 donor=RE-2310 target_index=%d donor_index=%d clone=0x%016x bytes=%d bodies=%d pieces=%d visible_weight=%d metadata_weight=%d',
                        target.index,donor.index,clone.base,clone.bytes,
                        clone.body_count,clone.piece_count,
                        clone.target_visible_weight,clone.target_metadata_weight))
                end
            end
        end
    end
    return previous(dt,...)
end
M.status='waiting'
rawset(_G,'update',wrapper)
log('waiting_for_validated_411_entry_index')
return M
