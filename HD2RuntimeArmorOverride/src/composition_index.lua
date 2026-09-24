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
function M.check(read,layout,reference,root,allowed_mapped_items)
    local index=layout.indices[root]
    if not index then return nil end
    local base=reference-index*8
    if base<65536 then return nil end
    local data=read(base,layout.count*8)
    if not data then return nil end
    local mapped,unmodified={},0
    for i,address in ipairs(layout.roots) do
        local actual=ptr(data,(i-1)*8)
        if actual~=address then
            if not allowed_mapped_items then return nil end
            local original_header=read(address,4)
            local actual_header=actual and read(actual,4)
            local item=original_header and u32(original_header,0)
            if not item or not allowed_mapped_items[item]
                or not actual_header or u32(actual_header,0)~=item then
                return nil
            end
            mapped[#mapped+1]={
                index=i-1,item=item,
                original=string.format('0x%016x',address),
                actual=string.format('0x%016x',actual)
            }
        else
            unmodified=unmodified+1
        end
    end
    return {index_base=string.format('0x%016x',base),
        blob_base=string.format('0x%016x',layout.base),
        matched_entries=layout.count,blob_bytes=layout.bytes,
        index_bytes=layout.count*8,ownership_proven=false,
        consumer_proven=false,self_copy_excluded=false,
        unmodified_entries=unmodified,mapped_entries=mapped}
end
return M
