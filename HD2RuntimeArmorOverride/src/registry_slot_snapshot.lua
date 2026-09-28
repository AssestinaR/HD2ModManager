-- One-shot, read-only snapshot of original and published Kit compositions.
local M={status='starting'}
rawset(_G,'HD2RegistrySlotSnapshot',M)
local loader=rawget(_G,'CowboyBingusModLoader')
local ok,ffi=pcall(require,'ffi')
if type(loader)~='table' or loader.api~=1 or not ok
    or ffi.os~='Windows' or not ffi.abi('64bit') then
    M.status='unsupported';return M
end
ffi.cdef[[
void *GetCurrentProcess(void);
uint32_t GetCurrentProcessId(void);
int ReadProcessMemory(void *,const void *,void *,size_t,size_t *);
]]
local kernel=ffi.load('kernel32')
local process=kernel.GetCurrentProcess()
local pid=tonumber(kernel.GetCurrentProcessId())
local function read(address,size)
    if not address or address<65536 or size<1 or size>0x400000 then return nil end
    local data=ffi.new('uint8_t[?]',size)
    local got=ffi.new('size_t[1]')
    if kernel.ReadProcessMemory(process,ffi.cast('const void *',address),
        data,size,got)==0 or tonumber(got[0])~=size then return nil end
    return ffi.string(data,size)
end
local function u32(data,at)
    assert(data and at>=0 and at+4<=#data,'truncated dword')
    local a,b,c,d=data:byte(at+1,at+4)
    return a+b*256+c*65536+d*16777216
end
local function pointer(data,at)
    local hi=u32(data,at+4)
    assert(hi<0x200000,'invalid pointer/count')
    return u32(data,at)+hi*4294967296
end
local function hex(data)
    return (data:gsub('.',function(c)return string.format('%02x',c:byte())end))
end
local function address(value)return string.format('0x%016x',value)end
local function status()
    local runtime=rawget(_G,'HD2RuntimeArmorRegistry')
    if type(runtime)~='table' or runtime.status~='ready'
        or not runtime.registry or not runtime.applied_revision then return nil end
    local registry=runtime.registry
    if registry.count~=411 or registry.bytes~=384196 or #registry.roots~=411 then return nil end
    return runtime,registry
end
local function snapshot(root,item)
    local header=assert(read(root,64),'Kit unreadable')
    assert(u32(header,0)==item,'Kit identity changed')
    local bodies,count=pointer(header,48),pointer(header,56)
    assert(count>=1 and count<=16,'invalid Body count')
    local body_data=assert(read(bodies,count*24),'Body array unreadable')
    local rows,total={},0
    for i=0,count-1 do
        local at=i*24
        local body_header=body_data:sub(at+1,at+24)
        local pieces,n=pointer(body_header,8),pointer(body_header,16)
        total=total+n
        assert(n>=1 and total<=4096,'invalid Piece count')
        rows[#rows+1]={header=hex(body_header),
            data=hex(assert(read(pieces,n*96),'Piece array unreadable'))}
    end
    return {header=hex(header),rows=rows,bodies=count,pieces=total}
end
local function emit(file,kind,fields)
    local fields_out={'"record_type":"'..kind..'"'}
    for key,value in pairs(fields) do
        local encoded
        if type(value)=='number' then encoded=tostring(value)
        elseif type(value)=='boolean' then encoded=value and 'true' or 'false'
        else encoded='"'..value..'"' end
        fields_out[#fields_out+1]='"'..key..'":'..encoded
    end
    file:write('{'..table.concat(fields_out,',')..'}\n')
    file:flush()
end
local previous=rawget(_G,'update')
if type(previous)~='function' then M.status='update_unavailable';return M end
local elapsed,worker=0,nil
local wrapper
local function capture()
    local runtime,registry=status()
    assert(runtime and registry,'runtime not ready')
    local revision=runtime.applied_revision
    local dir=assert(os.getenv('LOCALAPPDATA'),'LOCALAPPDATA unavailable')
        ..'/CowboyBingus/Helldivers2/ArmorRegistry'
    local path=dir..'/slot-snapshot-'..pid..'.jsonl'
    local file=assert(io.open(path,'wb'),'snapshot file unavailable')
    M.file=file
        emit(file,'start',{pid=pid,revision=revision,count=registry.count,
            blob_bytes=registry.bytes,read_only=true})
        local index_before=assert(read(registry.index_base,registry.count*8),'index unreadable')
        local mapped=0
        for i,root in ipairs(registry.roots) do
            local item=u32(assert(read(root,4),'original item unreadable'),0)
            local active=pointer(index_before,(i-1)*8)
            assert(registry.items[item] and registry.items[item].index==i-1,
                'original index mismatch')
            local original=snapshot(root,item)
            emit(file,'kit',{index=i-1,item=item,variant='original',
                root=address(root),header=original.header,
                bodies=original.bodies,pieces=original.pieces,
                rows=(function()
                    local parts={}
                    for _,row in ipairs(original.rows) do
                        parts[#parts+1]=''..row.header..':'..row.data
                    end
                    return table.concat(parts,';')
                end)()})
            if active~=root then
                local clone=snapshot(active,item)
                emit(file,'kit',{index=i-1,item=item,variant='published',
                    root=address(active),header=clone.header,
                    bodies=clone.bodies,pieces=clone.pieces,
                    rows=(function()
                        local parts={}
                        for _,row in ipairs(clone.rows) do
                            parts[#parts+1]=''..row.header..':'..row.data
                        end
                        return table.concat(parts,';')
                    end)()})
                mapped=mapped+1
            end
            if i%4==0 then coroutine.yield() end
        end
        assert(runtime.applied_revision==revision and runtime.status=='ready',
            'protocol changed during capture')
        assert(read(registry.index_base,registry.count*8)==index_before,
            'index changed during capture')
        emit(file,'complete',{pid=pid,revision=revision,count=registry.count,
            mapped=mapped,expected_mapped=runtime.active_count or 0})
    file:close()
    M.file=nil
    M.path=path
    M.status='complete'
end
wrapper=function(dt,...)
    if type(dt)=='number' and dt>=0 and dt<10 then elapsed=elapsed+dt end
    if M.status=='active' and elapsed>=12 then
        if not worker and status() then worker=coroutine.create(capture) end
        if worker then
            local started=os.clock()
            repeat
                local okay,reason=coroutine.resume(worker)
                if not okay then
                    M.status='error';M.error=tostring(reason)
                    if M.file then
                        pcall(emit,M.file,'error',{pid=pid,
                            reason=tostring(reason):gsub('[^%w_ -]','_')})
                        M.file:close();M.file=nil
                    end
                    break
                end
            until coroutine.status(worker)=='dead' or os.clock()-started>0.001
            if coroutine.status(worker)=='dead' and M.status=='active' then M.status='error' end
        end
        if M.status~='active' or elapsed>180 then
            if M.status=='active' then M.status='timeout' end
            if rawget(_G,'update')==wrapper then rawset(_G,'update',previous) end
        end
    end
    return previous(dt,...)
end
M.status='active'
rawset(_G,'update',wrapper)
return M
