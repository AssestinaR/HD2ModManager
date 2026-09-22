local schema=dofile(arg[1])
local factory=dofile(arg[2])
local function pack(v,n)
    local out={}
    for _=1,n do out[#out+1]=string.char(v%256);v=math.floor(v/256) end
    return table.concat(out)
end
local piece=string.rep('\255',8)..pack(8,4)..pack(2,4)..pack(1,4)
    ..string.rep('\0',76)
assert(#piece==96)
local header=pack(123,4)..string.rep('\0',28)..string.rep('\254',8)
    ..string.rep('\0',8)..pack(64,8)..pack(1,8)
local body=pack(3,4)..pack(0,4)..pack(88,8)..pack(2,8)
local data=header..body..piece..piece
local expected={[123]={archive='fefefefefefefefe',bodyCount=1,pieceCount=2,
    signature=('3|8|2|1|0|ffffffffffffffff|\n'):rep(2):sub(1,-2)}}
local verify=factory(schema,expected)
local root=0x1f819d70000
local function read(at,n)
    local off=at-root
    if off<0 or off+n>#data then return nil end
    return data:sub(off+1,off+n)
end
local found=assert(verify(read,root))
assert(found.mode=='relative' and found.matched_pieces==2)
assert(found.consumer_proven==false and found.ownership_proven==false)
data=header:sub(1,48)..pack(root+64,8)..pack(1,8)
    ..body:sub(1,8)..pack(root+88,8)..pack(2,8)..piece..piece
assert(verify(read,root).mode=='absolute')
local wrapper='LDLD'..pack(1,4)..pack(0xd9a55aa0,4)..pack(#data,4)..pack(1,8)
local function wrapped_read(at,n)
    if at==root-24 and n==24 then return wrapper end
    if at==root+#data and n==24 then return wrapper end
    return read(at,n)
end
local storage=verify(wrapped_read,root).storage
assert(storage.wrapper_valid and storage.packed_payload)
assert(storage.nonoverlapping_inside_payload and storage.followed_by_ldld)
assert(storage.arrays[1].gap_before_next_span==0)
assert(storage.in_place_growth_proven==false)
assert(storage.arrays[1].start==string.format('0x%016x',root+88))
wrapper=wrapper:sub(1,12)..pack(#data-1,4)..wrapper:sub(17)
assert(not verify(wrapped_read,root).storage.nonoverlapping_inside_payload)
assert(not verify(read,root).storage.wrapper_valid)
data=data:sub(1,96)..pack(9,4)..data:sub(101)
assert(not verify(read,root), 'wrong slot accepted')
data=header..body..piece
assert(not verify(read,root), 'truncated array accepted')
data=header..body:sub(1,16)..pack(2000,8)..piece..piece
assert(not verify(read,root), 'invalid count accepted')
print('PASS: relative/absolute composition chains, exact tuple multiset, bad slot/count, truncated arrays')
print('PASS: packed payload boundaries, missing wrapper, out-of-payload spans')
