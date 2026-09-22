local schema = dofile(arg[1])
local function u32(n)
    local out = {}
    for _=1,4 do out[#out+1] = string.char(n%256); n=math.floor(n/256) end
    return table.concat(out)
end
local function u64(n) return u32(n)..u32(0) end
local function fixture(n)
    local header = u32(123)..string.rep('\0',28)
        ..string.char(255,254,253,252,251,250,249,248)..string.rep('\0',8)
        ..u64(64)..u64(1)
    local body = u32(3)..u32(0)..u64(88)..u64(n)
    local piece = string.rep('\255',8)..u32(8)..u32(2)..u32(1)..u32(0)
        ..u64(17)..string.rep('\0',56)..string.char(4)..string.rep('\0',7)
    assert(#piece == 96)
    return header..body..piece:rep(n)
end
local function rejected(data)
    assert(not pcall(schema.kit,data), 'malformed payload accepted')
end
local payload = fixture(40)
local kit = schema.kit(payload)
assert(#kit.pieces == 40 and kit.bodies[1].count == 40)
assert(kit.archive == 'f8f9fafbfcfdfeff')
assert(kit.pieces[40].unit == 'ffffffffffffffff')
assert(kit.pieces[1].resources.MaterialLut == '0000000000000011')
assert(kit.pieces[1].rawBodyType == 3 and kit.pieces[1].slot == 8)
assert(kit.pieces[1].pieceType == 2 and kit.pieces[1].toneVariations == 4)
rejected(payload:sub(1,-2))
rejected(payload:sub(1,52)..u32(1)..payload:sub(57))
rejected(payload:sub(1,56)..u64(100000)..payload:sub(65))
local library = u32(1)..'LDLD'..u32(1)..u32(0xd9a55aa0)
    ..u32(#payload)..u64(0)..payload
assert(#schema.library(library) == 1)
assert(not pcall(schema.library,library..'\0'))
assert(not pcall(schema.library,library:sub(1,-2)))
print('PASS: exact 64-bit IDs, full pieces/resources, bounds, oversized counts, LDLD framing')
if arg[2] then
    local f = assert(io.open(arg[2],'rb'))
    local data = f:read('*a'); f:close()
    local actual = schema.library(data)
    print('READ ONLY: decoded '..#actual..' kits from supplied plaintext cache')
    for _,k in ipairs(actual) do
        if k.itemId == 3340614564 or k.itemId == 820333399 then
            print(string.format('item=%u archive=%s pieces=%d bodies=%d',
                k.itemId,k.archive,#k.pieces,#k.bodies))
        end
    end
end
