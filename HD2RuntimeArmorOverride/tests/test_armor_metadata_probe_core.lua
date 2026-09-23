local core=dofile(arg[1])
local header=string.rep(string.char(0),64)
assert(core.decode_kit_header(header).item_id==0)
assert(core.decode_kit_header(header).archive_hash=='0000000000000000')
local function bytes(...)
    local values={...}
    local out={}
    for index=1,96 do out[index]=string.char(values[index] or 0) end
    return table.concat(out)
end
local function put32(data,offset,value)
    local out={}
    for index=0,3 do out[#out+1]=string.char(math.floor(value/256^index)%256) end
    return data:sub(1,offset)..table.concat(out)..data:sub(offset+5)
end

local target=put32(bytes(),8,2)
target=put32(target,12,5)
target=put32(target,16,0)
local donor=put32(bytes(),8,2)
donor=put32(donor,12,5)
donor=put32(donor,16,1)
donor=string.char(0x78)..donor:sub(2)
local index=core.weight_index({{body=7,slot=2,piece_type=5,weight=0}})
local exact,exact_level=core.resolve_weight(index,7,2,5)
assert(exact==0 and exact_level=='exact')
local simulated=core.replace_weight(donor,0)
assert(#simulated==96)
local diff=core.piece_diff(target,donor,simulated)
assert(diff.unit)
assert(not diff.slot and not diff.piece_type)
assert(not diff.weight and diff.source_weight==1 and diff.target_weight==0)
assert(core.decode_piece(simulated,nil).weight==0)
assert(core.decode_piece(simulated,nil).unit=='0000000000000078')
local body_weight,body_level=core.resolve_weight(index,7,2,6)
assert(body_weight==0 and body_level=='body')

local exact_majority=core.weight_index({
    {body=7,slot=2,piece_type=5,weight=0},
    {body=7,slot=2,piece_type=5,weight=2},
    {body=7,slot=2,piece_type=5,weight=2}
})
local majority,majority_level=core.resolve_weight(exact_majority,7,2,5)
assert(majority==2 and majority_level=='exact')

local exact_tie_falls_back=core.weight_index({
    {body=7,slot=2,piece_type=5,weight=0},
    {body=7,slot=2,piece_type=5,weight=2},
    {body=7,slot=2,piece_type=6,weight=2},
    {body=7,slot=2,piece_type=7,weight=2}
})
local exact_fallback,exact_fallback_level=core.resolve_weight(
    exact_tie_falls_back,7,2,5)
assert(exact_fallback==2 and exact_fallback_level=='body')

local body_majority=core.weight_index({
    {body=7,slot=2,piece_type=1,weight=0},
    {body=7,slot=2,piece_type=2,weight=2},
    {body=7,slot=2,piece_type=3,weight=2}
})
local body_result,body_result_level=core.resolve_weight(body_majority,7,2,5)
assert(body_result==2 and body_result_level=='body')

local fallback_after_body_tie=core.weight_index({
    {body=7,slot=2,piece_type=1,weight=0},
    {body=7,slot=2,piece_type=2,weight=2},
    {body=8,slot=2,piece_type=3,weight=2},
    {body=8,slot=2,piece_type=4,weight=2},
    {body=8,slot=2,piece_type=5,weight=2}
})
local global_result,global_level=core.resolve_weight(
    fallback_after_body_tie,7,2,5)
assert(global_result==2 and global_level=='global')

local unresolved=core.weight_index({
    {body=7,slot=2,piece_type=1,weight=0},
    {body=7,slot=2,piece_type=2,weight=2},
    {body=8,slot=2,piece_type=3,weight=0},
    {body=8,slot=2,piece_type=4,weight=2}
})
assert(core.resolve_weight(unresolved,7,2,5)==nil)
print('PASS: piece decoding, dominant target weights, scoped fallback, and tie rejection')
