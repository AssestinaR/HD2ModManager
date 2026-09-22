-- Offline serialized LDLD decoder. This is NOT a native-memory layout.
-- Relative offsets are payload-relative, never presumed to be heap pointers.
local M = {}
local roles = {'MaterialLut', 'PatternLut', 'CapeLut', 'CapeGradient',
    'CapeNac', 'DecalScalarFields', 'BaseData', 'DecalSheet'}
local function u32(s, at)
    assert(at >= 0 and at + 4 <= #s, 'truncated u32')
    local a,b,c,d = s:byte(at+1, at+4)
    return a + b*256 + c*65536 + d*16777216
end
local function small64(s, at)
    assert(u32(s, at+4) == 0, 'offset/count exceeds bounded decoder range')
    return u32(s, at)
end
local function hash(s, at)
    assert(at >= 0 and at+8 <= #s, 'truncated hash')
    local out = {}
    for i=8,1,-1 do out[#out+1] = string.format('%02x', s:byte(at+i)) end
    return table.concat(out)
end
local function span(s, at, count, stride)
    assert(at >= 0 and at <= #s and count <= math.floor((#s-at)/stride),
        'array outside payload')
end
function M.kit(payload)
    assert(#payload >= 64, 'truncated kit')
    local result = {itemId=u32(payload,0), archive=hash(payload,32),
        dlcId=u32(payload,4), setId=u32(payload,8), kitType=u32(payload,40),
        bodies={}, pieces={}}
    local offset, count = small64(payload,48), small64(payload,56)
    span(payload,offset,count,24)
    for i=0,count-1 do
        local at = offset+i*24
        local body = {rawBodyType=u32(payload,at),
            offset=small64(payload,at+8), count=small64(payload,at+16)}
        span(payload,body.offset,body.count,96)
        result.bodies[#result.bodies+1] = body
        for j=0,body.count-1 do
            local p = body.offset+j*96
            local piece = {rawBodyType=body.rawBodyType, unit=hash(payload,p),
                slot=u32(payload,p+8), pieceType=u32(payload,p+12),
                weight=u32(payload,p+16), toneVariations=payload:byte(p+89),
                resources={}}
            for r,role in ipairs(roles) do
                local id = hash(payload,p+16+r*8)
                if id ~= '0000000000000000' then piece.resources[role] = id end
            end
            result.pieces[#result.pieces+1] = piece
        end
    end
    return result
end
function M.library(data)
    local count, at, kits = u32(data,0), 4, {}
    assert(count <= math.floor((#data-4)/24), 'invalid record count')
    for _=1,count do
        assert(at+24 <= #data, 'truncated record')
        assert(data:sub(at+1,at+4) == 'LDLD', 'invalid record magic')
        assert(u32(data,at+4) == 1, 'unsupported record version')
        local kind, size = u32(data,at+8), u32(data,at+12)
        span(data,at+24,size,1)
        if kind == 0xd9a55aa0 then
            kits[#kits+1] = M.kit(data:sub(at+25,at+24+size))
        end
        at = at+24+size
    end
    assert(at == #data, 'unparsed trailing bytes')
    return kits
end
return M
