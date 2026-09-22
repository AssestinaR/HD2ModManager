local schema=assert(loadfile(arg[1]))()
local clone=assert(loadfile(arg[2]))()

local function p32(n)
    local out={}
    for _=1,4 do out[#out+1]=string.char(n%256);n=math.floor(n/256) end
    return table.concat(out)
end
local function p64(n) return p32(n)..p32(0) end
local function hash(n) return p64(n) end

local function payload(item,counts)
    local bodies,pieces={},{}
    local next_offset=64+#counts*24
    for body_type,count in ipairs(counts) do
        bodies[#bodies+1]=p32(body_type-1)..p32(0)..p64(next_offset)..p64(count)
        for piece=1,count do
            pieces[#pieces+1]=hash(item*100+body_type*10+piece)
                ..p32(piece-1)..p32(piece%3)..p32(0)
                ..string.rep('\0',68)..string.char(piece%2)..string.rep('\0',7)
        end
        next_offset=next_offset+count*96
    end
    return p32(item)..p32(7)..p32(8)..string.rep('\0',20)
        ..hash(item+1000)..p32(0)..p32(0)..p64(64)..p64(#counts)
        ..table.concat(bodies)..table.concat(pieces)
end

local target=payload(1861,{5,5,5})
local donor=payload(2310,{7,8,8})
local plan=clone.build(target,donor)
assert(plan.target_item_id==1861 and plan.donor_item_id==2310)
assert(plan.body_count==3 and plan.piece_count==23)
assert(plan.payload_bytes==64+3*24+23*96)
assert(#plan.pointer_fixups==4)

local decoded=schema.kit(plan.payload)
assert(decoded.itemId==1861 and #decoded.bodies==3 and #decoded.pieces==23)
assert(decoded.pieces[1].unit==schema.kit(donor).pieces[1].unit)

local base=0x200000
local relocated=clone.relocate(plan,base)
local function small64(s,at)
    local a,b,c,d=s:byte(at+1,at+4)
    return a+b*256+c*65536+d*16777216
end
assert(small64(relocated,48)==base+64)
assert(small64(relocated,72)==base+64+72)
assert(small64(relocated,96)==base+64+72+7*96)
assert(small64(relocated,120)==base+64+72+(7+8)*96)
assert(relocated:sub(1,48)==target:sub(1,48))
print('PASS: target identity with donor Body/Piece clone and absolute relocation fixups')
