-- Execute the shipped runtime against real, process-local FFI buffers and a fake
-- game/module/filesystem. Never attach to a game or touch the deployed protocol.
local ffi=require('ffi')
ffi.cdef[[
void *GetCurrentProcess(void);
int ReadProcessMemory(void *,const void *,void *,size_t,size_t *);
int WriteProcessMemory(void *,void *,const void *,size_t,size_t *);
]]
local real_kernel=ffi.load('kernel32')
local C=assert(loadfile('HD2RuntimeArmorOverride/src/armor_registry_composition.lua'))()
local root='HD2RuntimeArmorOverride/src/'
local function fixture()
    local env=setmetatable({},{__index=_G});env._G=env
    local blobs={}
    local function allocation(size)
        local buffer=ffi.new('uint8_t[?]',size)
        local base=tonumber(ffi.cast('uintptr_t',buffer))
        blobs[#blobs+1]={base=base,size=size,buffer=buffer}
        return buffer,base
    end
    local image,image_base=allocation(0x1000)
    local function p32(address,value)ffi.cast('uint32_t *',address)[0]=value end
    local function p64(address,value)ffi.cast('uint64_t *',address)[0]=value end
    p32(image_base+0x3c,0x80);ffi.copy(image+0x80,'PE\0\0',4)
    p32(image_base+0x88,123);p32(image_base+0xd0,4096)
    local blob,base=allocation(0x10000)
    local index,index_base=allocation(0x1000)
    p32(base,100)
    local roots={}
    local offset=4
    for i=1,100 do
        local header=base+offset
        ffi.copy(ffi.cast('void *',header),'LDLD',4)
        p32(header+4,1);p32(header+8,0xd9a55aa0);p32(header+12,280)
        local kit=header+24;roots[i]=kit
        p32(kit,i);p64(kit+48,kit+64);p64(kit+56,1)
        p32(kit+64,0);p64(kit+72,kit+88);p64(kit+80,2)
        for j=0,1 do
            local piece=kit+88+j*96
            ffi.cast('uint64_t *',piece)[0]=0x9841b16544c8fbaeULL+j
            p32(piece+8,j==0 and 2 or 6);p32(piece+12,0);p32(piece+16,0)
        end
        p64(index_base+(i-1)*8,kit)
        offset=offset+304
    end
    table.sort(blobs,function(a,b)return a.base<b.base end)
    local time=0
    local kernel=setmetatable({
        GetCurrentProcessId=function()return 1234 end,
        GetModuleHandleA=function()return ffi.cast('void *',image_base)end,
        QueryPerformanceFrequency=function(out)out[0]=1000000;return 1 end,
        QueryPerformanceCounter=function(out)time=time+(env.clock_step or 10);out[0]=time;return 1 end,
        CreateDirectoryA=function()return 1 end,
        MoveFileExA=function(from,to)env.files[to]=env.files[from];env.files[from]=nil;return 1 end,
        VirtualQuery=function(pointer,mbi)
            local address=tonumber(ffi.cast('uintptr_t',pointer))
            for _,region in ipairs(blobs) do
                if address<region.base+region.size then
                    ffi.fill(mbi,48,0)
                    ffi.cast('uintptr_t *',mbi)[0]=region.base
                    ffi.cast('size_t *',mbi+24)[0]=region.size
                    ffi.cast('uint32_t *',mbi+32)[0]=0x1000
                    ffi.cast('uint32_t *',mbi+36)[0]=4
                    ffi.cast('uint32_t *',mbi+40)[0]=0x20000
                    return 48
                end
            end
            return 0
        end
    },{__index=function(_,key)return real_kernel[key]end})
    env.files={};env.logs={};env.calls=0
    env.io={open=function(path,mode)
        if mode=='rb' and env.files[path]==nil then return nil end
        local content=mode=='rb' and env.files[path] or ''
        local f={}
        function f:read(n)return content:sub(1,n)end
        function f:write(data)content=content..data;return self end
        function f:flush()end
        function f:close()if mode~='rb' then env.files[path]=content end end
        return f
    end}
    env.os=setmetatable({remove=function(path)
        if not env.files[path] then return nil end
        env.files[path]=nil;return true
    end},{__index=os})
    local proxy=setmetatable({load=function(name)assert(name=='kernel32');return kernel end},{__index=ffi})
    env.require=function(name)if name=='ffi' then return proxy end return require(name)end
    env.CowboyBingusModLoader={api=1,log_directory='test/Logs',open_log=function()
        return {write=function(_,text)env.logs[#env.logs+1]=text end,flush=function()end}
    end}
    env.update=function()env.calls=env.calls+1 end
    env.ARMOR_REGISTRY_PROTOCOL=assert(loadfile(root..'armor_registry_protocol.lua'))()
    env.ARMOR_METADATA_PROBE_CORE=assert(loadfile(root..'armor_metadata_probe_core.lua'))()
    env.ARMOR_REGISTRY_TRANSACTION=assert(loadfile(root..'armor_registry_transaction.lua'))()
    env.ARMOR_REGISTRY_COMPOSITION=C
    local chunk=assert(loadfile(root..'armor_registry_runtime.lua'));setfenv(chunk,env)
    local runtime=chunk()
    local function protocol(text)
        env.files['test/ArmorRegistry/armor-registry.protocol']=text
    end
    local function settle(revision)
        for _=1,1000 do
            env.update(0.1)
            if runtime.applied_revision==revision then return end
        end
        error('did not apply revision '..revision..': '..tostring(runtime.status)..' '..table.concat(env.logs))
    end
    return env,runtime,protocol,settle,index_base,roots
end
local env,m,write,settle,index,roots=fixture()
write('schema=2\nrevision=1\nenabled=true\nmap=1,3\nmap=2,3\n')
settle(1)
assert(m.registry.count==100 and m.last_apply.built==2 and m.last_apply.writes==2)
assert(m.locator_pacing=='frame_budget')
assert(m.first_applied_after_ms>=m.located_after_ms)
local old=m.active['1'].base
write('schema=2\nrevision=2\nenabled=true\nmap=2,3\nmap=1,3\n# comment\n')
settle(2)
assert(m.active['1'].base==old and m.last_apply.built==0 and m.last_apply.writes==0)
write('schema=2\nrevision=3\nenabled=true\nmap=1,3\nmap=2,3\n'..
    'target_mode=1,manual\npiece_override=1,0,9841b16544c8fbae,2,0,false\n')
settle(3)
assert(m.last_apply.built==1 and m.last_apply.reused==1 and m.last_apply.writes==1)
assert(m.active['1'].pieces==1)
write('schema=2\nrevision=4\nenabled=true\nmap=1,3\nmap=2,3\n')
settle(4)
assert(m.last_apply.built==0 and m.active['1'].base==old)
write('schema=2\nrevision=5\nenabled=true\nmodel=custom,\n'..
    'model_piece=custom,0,9841b16544c8fbae,2,0,0\nmap_model=1,custom\n')
settle(5)
assert(m.last_apply.removed==1 and m.active['1'].pieces==1)
local buffer=m.active['1'].buffer
local body=tonumber(ffi.cast('uint64_t *',buffer+48)[0])
local piece=tonumber(ffi.cast('uint64_t *',body+8)[0])
assert(C.unit(ffi.string(ffi.cast('void *',piece),8))=='9841b16544c8fbae')
-- Invalid custom composition fails normally, keeps active revision, no fallback.
write('schema=2\nrevision=6\nenabled=true\nmodel=custom,\n'..
    'model_piece=custom,0,9841b16544c8fbae,2,0,0,false\nmap_model=1,custom\n')
for _=1,30 do env.update(0.1) end
assert(m.status=='apply_error' and m.applied_revision==5)
local logs=#env.logs
for _=1,30 do env.update(0.1) end
assert(#env.logs==logs,'permanent failure should not spam/retry')
write('schema=bad\n')
for _=1,30 do env.update(0.1) end
logs=#env.logs
for _=1,30 do env.update(0.1) end
assert(#env.logs==logs and m.applied_revision==5)
write('schema=2\nrevision=7\nenabled=false\n')
settle(7)
assert(m.active_count==0 and tonumber(ffi.cast('uint64_t *',index)[0])==roots[1])
collectgarbage('collect')
assert(m.transaction.retained_count>=4)
-- An unreadable native source must become an error, not an access violation.
m.registry.items[3].address=65536
write('schema=2\nrevision=8\nenabled=true\nmap=1,3\n')
local calls=env.calls
for _=1,30 do env.update(0.1) end
assert(m.applied_revision==7 and m.status=='apply_error' and env.calls==calls+30)
assert(m.transaction.faulted==false)
local env2,m2,write2,settle2=fixture()
write2('schema=2\nrevision=1\nenabled=false\n')
settle2(1)
env2.clock_step=200
local maps={}
for i=1,80 do maps[#maps+1]='map='..i..',99' end
write2('schema=2\nrevision=2\nenabled=true\n'..table.concat(maps,'\n')..'\n')
-- Long preparation has yielded but has not published. New input supersedes it.
for _=1,12 do env2.update(0.1) end
assert(m2.applied_revision==1,'expected time-sliced preparation')
write2('schema=2\nrevision=3\nenabled=false\n')
settle2(3)
assert(m2.active_count==0 and m2.transaction.retained_count==0)
env2.clock_step=10
write2('schema=2\nrevision=4\nenabled=true\n'..table.concat(maps,'\n')..'\n')
settle2(4)
assert(m2.last_apply.built==80 and m2.last_apply.writes==80)
write2('schema=2\nrevision=5\nenabled=true\n'..table.concat(maps,'\n')..'\n')
settle2(5)
assert(m2.last_apply.built==0 and m2.last_apply.writes==0 and m2.last_apply.reused==80)
print('PASS: 80-target incremental no-op and superseding unpublished time-sliced work')
print('PASS: actual runtime discovery, no-op/delta/revert, custom IDs, failed validation, disable, bad pointer and update isolation')
local env3,m3,write3,settle3=fixture()
write3('schema=2\nrevision=1\nenabled=true\nmodel=diagnostic,\n'
    ..'model_piece=diagnostic,0,6fe9c822322c0001,2,0,0\n'
    ..'test_piece_template=diagnostic,6fe9c822322c0001,3,0,9841b16544c8fbae\n'
    ..'map_model=1,diagnostic\n')
settle3(1)
assert(m3.active_count==1 and m3.active['1'].pieces==1)
local b3=m3.active['1'].buffer
assert(C.unit(ffi.string(b3+88,96))=='6fe9c822322c0001')
write3('schema=2\nrevision=2\nenabled=true\nmodel=diagnostic,\n'
    ..'model_piece=diagnostic,0,6fe9c822322c0001,2,0,0\n'
    ..'test_piece_template=diagnostic,6fe9c822322c0001,3,0,0000000000000000\n'
    ..'map_model=1,diagnostic\n')
for _=1,30 do env3.update(0.1) end
assert(m3.status=='apply_error' and m3.applied_revision==1)
print('PASS: diagnostic runtime resolves exact donor Unit and rejects missing provenance')
