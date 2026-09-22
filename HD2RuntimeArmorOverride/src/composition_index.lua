-- Structural evidence only: neither parser establishes consumer ownership.
local M={}
local function u32(s,p)
    local a,b,c,d=s:byte(p+1,p+4)
    return a+b*256+c*65536+d*16777216
end
local function ptr(s,p)
    local high=u32(s,p+4)
    if high>=32768 then return nil end
    return u32(s,p)+high*4294967296
end
function M.library(read,base,size,anchor)
    local prefix=read(base,4)
    if not prefix then return nil end
    local count=u32(prefix,0)
    if count<1 or count>1024 then return nil end
    local offset,roots,indices,found=4,{}, {},anchor==nil
    for i=1,count do
        if offset+24>size then return nil end
        local header=read(base+offset,24)
        if not header or header:sub(1,4)~='LDLD' or u32(header,4)~=1
            or u32(header,8)~=0xd9a55aa0 then return nil end
        local length=u32(header,12)
        if length<64 or offset+24+length>size then return nil end
        local root=base+offset+24
        roots[i]=root;indices[root]=i-1
        if root==anchor then found=true end
        offset=offset+24+length
    end
    if not found then return nil end
    return {base=base,count=count,bytes=offset,roots=roots,indices=indices}
end
function M.check(read,layout,reference,root)
    local index=layout.indices[root]
    if not index then return nil end
    local base=reference-index*8
    if base<65536 then return nil end
    local data=read(base,layout.count*8)
    if not data then return nil end
    for i,address in ipairs(layout.roots) do
        if ptr(data,(i-1)*8)~=address then return nil end
    end
    return {index_base=string.format('0x%016x',base),
        blob_base=string.format('0x%016x',layout.base),
        matched_entries=layout.count,blob_bytes=layout.bytes,
        index_bytes=layout.count*8,ownership_proven=false,
        consumer_proven=false,self_copy_excluded=false}
end
return M
