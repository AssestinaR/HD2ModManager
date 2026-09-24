local real_ffi = require('ffi')
local source = arg[1]
local function run(mode)
    local lines, closed, queries, calls = {}, 0, 0, 0
    local file = {
        write=function(_,s) lines[#lines+1]=s end,
        flush=function() end, close=function() closed=closed+1 end
    }
    local kernel = {
        GetCurrentProcess=function() return real_ffi.cast('void *',1) end,
        GetModuleHandleA=function() return real_ffi.cast('void *',0x100000) end,
        ReadProcessMemory=function() error('unexpected read') end,
        VirtualQuery=function()
            queries=queries+1
            if mode=='failure' then error('query failed\n"quoted"') end
            return 0
        end
    }
    local fake = {os='Windows',abi=function() return true end,
        cdef=function() end,load=function() return kernel end}
    setmetatable(fake,{__index=real_ffi})
    local env=setmetatable({}, {__index=_G})
    env._G=env
    env.require=function(name) assert(name=='ffi'); return fake end
    env.CowboyBingusModLoader={api=1,open_log=function()
        if mode=='log_failure' then error('log unavailable') end
        return file
    end}
    local original=function()
        calls=calls+1
        if mode=='original_error' then error('original error') end
        return nil,'preserved',nil
    end
    env.update=mode~='no_update' and original or nil
    local chunk=assert(loadfile(source)); setfenv(chunk,env)
    local m=chunk()
    assert(chunk()==m, 'duplicate guard')
    if mode=='log_failure' then assert(m.status=='log_open_failed'); return end
    if mode=='no_update' then assert(m.status=='update_unavailable' and closed==1); return end
    if mode=='original_error' then
        local ok,err=pcall(env.update,1)
        assert(not ok and err:find('original error',1,true)); return
    end
    for _=1,15 do
        local result={n=select('#',env.update(1))}
        assert(result.n==3, 'return arity')
    end
    assert(queries==1 and calls==15)
    assert(env.update==original and closed==1, 'callback restoration/close')
    assert(m.status==(mode=='failure' and 'failed' or 'complete'))
    if mode=='failure' then
        assert(table.concat(lines):find('\\u000a',1,true), 'JSON control escaping')
    else
        assert(table.concat(lines):find('"consumer_proven":false',1,true))
    end
end
for _,mode in ipairs({'empty','failure','log_failure','no_update','original_error'}) do run(mode) end
local function memory_fixture(fail_reads)
    local start,size=0x200000,0x101000
    local bytes=real_ffi.new('uint8_t[?]',size)
    -- Place a valid header across the 1 MiB boundary.
    local offset=0x100000-16
    real_ffi.cast('uint32_t *',bytes+offset)[0]=820333399
    local archive={0x87,0x59,0x81,0x8b,0x6f,0x90,0x23,0xd9}
    for i,v in ipairs(archive) do bytes[offset+31+i]=v end
    bytes[offset+56]=3
    real_ffi.cast('uint64_t *',bytes+offset+48)[0]=start+offset+64
    real_ffi.cast('uint64_t *',bytes+128)[0]=start+offset
    local lines={}
    local kernel={
        GetCurrentProcess=function() return real_ffi.cast('void *',1) end,
        GetModuleHandleA=function() return real_ffi.cast('void *',0x100000) end,
        VirtualQuery=function(address,mbi)
            if tonumber(real_ffi.cast('uintptr_t',address))>=start+size then return 0 end
            real_ffi.fill(mbi,48)
            real_ffi.cast('uintptr_t *',mbi)[0]=start
            real_ffi.cast('size_t *',mbi+24)[0]=size
            real_ffi.cast('uint32_t *',mbi+32)[0]=0x1000
            real_ffi.cast('uint32_t *',mbi+36)[0]=4
            real_ffi.cast('uint32_t *',mbi+40)[0]=0x20000
            return 48
        end,
        ReadProcessMemory=function(_,address,out,length,got)
            if fail_reads then return 0 end
            local at=tonumber(real_ffi.cast('uintptr_t',address))-start
            if at<0 or at+length>size then return 0 end
            real_ffi.copy(out,bytes+at,length);got[0]=length;return 1
        end
    }
    local fake={os='Windows',abi=function() return true end,
        cdef=function() end,load=function() return kernel end}
    setmetatable(fake,{__index=real_ffi})
    local env=setmetatable({}, {__index=_G});env._G=env
    env.COMPOSITION_VERIFY=function(_,root)
        return {bodies=string.format('0x%016x',root+64),arrays={},
            item=820333399,storage={wrapper_valid=true},
            consumer_proven=false,self_copy_excluded=false}
    end
    env.COMPOSITION_INDEX=nil
    env.HD2ArmorCompositionProbeAllowUnhintedScan=true
    env.require=function() return fake end
    env.update=function() end
    env.CowboyBingusModLoader={api=1,open_log=function()
        return {write=function(_,s) lines[#lines+1]=s end,
            flush=function() end,close=function() end}
    end}
    local chunk=assert(loadfile(source));setfenv(chunk,env)
    local m=chunk()
    for _=1,60 do env.update(1) end
    assert(m.status=='complete')
    local log=table.concat(lines)
    assert(log:find('"header_anchor_hits":'..(fail_reads and '0' or '1'),1,true))
    assert(log:find('"successful_bytes":'..(fail_reads and '0' or tostring(size)),1,true))
    assert(log:find('"failed_bytes":'..(fail_reads and tostring(size) or '0'),1,true))
    assert(log:find('"self_buffer_matches_possible":true',1,true))
    assert(log:find('"complete_address_space":false',1,true))
    if not fail_reads and not source:find('generated',1,true) then
        assert(log:find('"local_window_only":true',1,true))
        assert(log:find(string.format('0x%016x',start+offset+48),1,true))
        assert(log:find('"record_type":"composition_root_backrefs"',1,true))
        assert(log:find(string.format('0x%016x',start+128),1,true))
    end
end
memory_fixture(false)
memory_fixture(true)
print('PASS: probe scheduling, duplicate guard, error exit, callback restore, log failure, original errors')
print('PASS: cross-chunk header, zero-based offsets, read-success/failure accounting')
