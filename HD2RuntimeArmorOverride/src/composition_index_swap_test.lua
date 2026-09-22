-- Minimal consumption test. It swaps one aligned index pointer between two
-- already-owned Kit roots. It does not change counts or allocate engine data.
local existing=rawget(_G,'HD2ArmorCompositionIndexSwap')
if existing then return existing end
local M={version='0.1.0',status='starting'}
rawset(_G,'HD2ArmorCompositionIndexSwap',M)

local loader=rawget(_G,'CowboyBingusModLoader')
if type(loader)~='table' or loader.api~=1 or type(loader.open_log)~='function' then
    M.status='loader_unavailable';return M
end
local opened,file=pcall(loader.open_log,'ArmorCompositionIndexSwap.log')
if not opened or not file then M.status='log_open_failed';return M end
local function log(text)
    file:write(tostring(text)..'\n');file:flush()
end
local ok,ffi=pcall(require,'ffi')
if not ok or ffi.os~='Windows' or not ffi.abi('64bit') then
    M.status='unsupported';log('windows_x64_ffi_required');file:close();return M
end

local TARGET_ITEM='3340614564' -- RE-1861, 15 pieces
local DONOR_ITEM='820333399'   -- RE-2310, 23 pieces
local previous=rawget(_G,'update')
if type(previous)~='function' then
    M.status='update_unavailable';file:close();return M
end

local function number_at(slot)
    return tonumber(ffi.cast('uint64_t *',slot)[0])
end
local function aligned_store(slot,value)
    assert(slot%8==0,'unaligned index slot')
    ffi.cast('uint64_t *',slot)[0]=ffi.new('uint64_t',value)
    return number_at(slot)==value
end
function M.clear()
    if M.status~='applied' or not M.slot or not M.original then return false end
    if number_at(M.slot)~=M.donor then
        M.status='rollback_refused';log('rollback_refused_current_pointer_changed')
        return false
    end
    if not aligned_store(M.slot,M.original) then
        M.status='rollback_failed';log('rollback_write_verification_failed')
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
            local slot=result.index_base+target.index*8
            local current=number_at(slot)
            if slot%8~=0 or current~=target.address then
                M.status='validation_failed'
                log('validation_failed')
            elseif not aligned_store(slot,donor.address) then
                M.status='write_failed'
                log('write_verification_failed')
            else
                M.slot,M.original,M.donor=slot,target.address,donor.address
                M.target_index,M.donor_index=target.index,donor.index
                M.status='applied'
                log(string.format(
                    'applied target=RE-1861 target_index=%d donor=RE-2310 donor_index=%d slot=0x%016x',
                    target.index,donor.index,slot))
            end
        end
    end
    return previous(dt,...)
end
M.status='waiting'
rawset(_G,'update',wrapper)
log('waiting_for_validated_411_entry_index')
return M
