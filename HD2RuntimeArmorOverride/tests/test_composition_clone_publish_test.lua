local ffi=require('ffi')
local target=ffi.new('uint8_t[?]',64+3*24+15*96)
local donor=ffi.new('uint8_t[?]',64+3*24+23*96)
local donor_base=tonumber(ffi.cast('uintptr_t',donor))
local bodies=donor_base+64
ffi.cast('uint64_t *',donor_base+48)[0]=bodies
ffi.cast('uint64_t *',donor_base+56)[0]=3
local counts={7,8,8}
local piece_at=64+72
for i=0,2 do
    local row=bodies+i*24
    ffi.cast('uint32_t *',row)[0]=i
    ffi.cast('uint64_t *',row+8)[0]=donor_base+piece_at
    ffi.cast('uint64_t *',row+16)[0]=counts[i+1]
    ffi.fill(ffi.cast('void *',donor_base+piece_at),counts[i+1]*96,0x30+i)
    for piece=0,counts[i+1]-1 do
        ffi.cast('uint32_t *',donor_base+piece_at+piece*96+8)[0]=
            (i==2 and piece==0) and 1 or (piece+2)
        ffi.cast('uint32_t *',donor_base+piece_at+piece*96+16)[0]=1
    end
    piece_at=piece_at+counts[i+1]*96
end
ffi.cast('uint32_t *',target)[0]=488691438
for i=4,47 do target[i]=i end
local target_base=tonumber(ffi.cast('uintptr_t',target))
local target_bodies=target_base+64
ffi.cast('uint64_t *',target_base+48)[0]=target_bodies
ffi.cast('uint64_t *',target_base+56)[0]=3
local target_piece_at=64+72
for i=0,2 do
    local row=target_bodies+i*24
    ffi.cast('uint32_t *',row)[0]=i
    ffi.cast('uint64_t *',row+8)[0]=target_base+target_piece_at
    ffi.cast('uint64_t *',row+16)[0]=5
    for piece=0,4 do
        local address=target_base+target_piece_at+piece*96
        ffi.cast('uint32_t *',address+8)[0]=(i==2 and piece==0) and 1 or (piece+2)
        ffi.cast('uint32_t *',address+16)[0]=(i==2 and piece==0) and 1 or 0
    end
    target_piece_at=target_piece_at+5*96
end
local index=ffi.new('uint64_t[411]')
local target_index,donor_index=229,227
index[target_index]=target_base
index[donor_index]=donor_base
local env=setmetatable({}, {__index=_G});env._G=env
env.require=function(name) assert(name=='ffi');return ffi end
env.CowboyBingusModLoader={api=1,open_log=function()
    return {write=function() end,flush=function() end,close=function() end}
end}
env.update=function() return 'ok' end
env.HD2ArmorCompositionProbe={result={
    index_base=tonumber(ffi.cast('uintptr_t',index)),matched_entries=411,
    roots={
        ['488691438']={address=target_base,index=target_index},
        ['820333399']={address=donor_base,index=donor_index}
    }
}}
local chunk=assert(loadfile(arg[1]));setfenv(chunk,env)
local m=chunk();assert(m.status=='waiting');assert(env.update()=='ok')
assert(m.status=='applied' and m.body_count==3 and m.piece_count==23)
assert(m.target_visible_weight==0 and m.target_metadata_weight==1)
assert(m.clone_bytes==64+72+23*96)
assert(tonumber(index[target_index])==m.clone_base)
assert(ffi.cast('uint32_t *',m.clone_base)[0]==488691438)
for i=4,47 do assert(ffi.cast('uint8_t *',m.clone_base)[i]==i) end
assert(tonumber(ffi.cast('uint64_t *',m.clone_base+56)[0])==3)
for i=0,22 do
    local address=m.clone_base+64+72+i*96
    local slot=tonumber(ffi.cast('uint32_t *',address+8)[0])
    local weight=tonumber(ffi.cast('uint32_t *',address+16)[0])
    assert(weight==(slot==1 and 1 or 0))
end
assert(m.clear() and tonumber(index[target_index])==target_base)
print('PASS: retained target header, donor 23-piece clone, publication, rollback')
