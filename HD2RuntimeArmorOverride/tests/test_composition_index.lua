local index=dofile(arg[1])
local function pack(n,size)
    local a={};for _=1,size do a[#a+1]=string.char(n%256);n=math.floor(n/256) end
    return table.concat(a)
end
local base=0x2c324310000
local indexBase=base+0x200000
local cloneBase=base+0x400000
local count=411
local chunk='LDLD'..pack(1,4)..pack(0xd9a55aa0,4)..pack(64,4)..pack(1,8)..string.rep('\0',64)
local blob=pack(count,4)..chunk:rep(count)
local clone=pack(0,4)..string.rep('\0',60)
local addresses={}
for i=0,count-1 do addresses[#addresses+1]=pack(base+28+i*88,8) end
local pointers=table.concat(addresses)
local function read(address,size)
    for _,span in ipairs({{base,blob},{indexBase,pointers},{cloneBase,clone}}) do
        local offset=address-span[1]
        if offset>=0 and offset+size<=#span[2] then
            return span[2]:sub(offset+1,offset+size)
        end
    end
end
local root=base+28+227*88
local layout=assert(index.library(read,base,#blob,root))
assert(layout.count==411 and layout.bytes==#blob)
local anchorless=assert(index.library(read,base,#blob,nil))
assert(anchorless.count==411 and anchorless.bytes==#blob)
local matched=assert(index.check(read,layout,indexBase+227*8,root))
assert(matched.matched_entries==411 and not matched.consumer_proven)
assert(not index.library(read,base,#blob-1,root))
assert(not index.library(read,base,#blob,root+1))
assert(not index.check(read,layout,indexBase+227*8+1,root))
local mappedIndex=89
pointers=pointers:sub(1,mappedIndex*8)..pack(cloneBase,8)
    ..pointers:sub((mappedIndex+1)*8+1)
assert(not index.check(read,layout,indexBase+227*8,root),
    'mapped roots require an explicit allow-list')
local mapped=assert(index.check(read,layout,indexBase+227*8,root,{[0]=true}))
assert(mapped.unmodified_entries==410 and #mapped.mapped_entries==1)
assert(mapped.mapped_entries[1].index==mappedIndex)
clone=pack(1,4)..string.rep('\0',60)
assert(not index.check(read,layout,indexBase+227*8,root,{[0]=true}),
    'mapped root identity must match its original kit')
clone=pack(0,4)..string.rep('\0',60)
pointers=pointers:sub(1,mappedIndex*8)..pack(base+28+mappedIndex*88,8)
    ..pointers:sub((mappedIndex+1)*8+1)
pointers=pointers:sub(1,-9)..pack(root,8)
assert(not index.check(read,layout,indexBase+227*8,root),'must check final entry')
blob=pack(1025,4)..blob:sub(5)
assert(not index.library(read,base,#blob,root))
print('PASS: complete 411-entry index, mapped identity exceptions, final mismatch, bounded framing')
