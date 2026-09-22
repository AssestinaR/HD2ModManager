-- Pure layout builder for a future independently allocated Kit clone.
-- It never allocates or writes process memory.
local M={}

local function u32(s,at)
    assert(at>=0 and at+4<=#s,'truncated u32')
    local a,b,c,d=s:byte(at+1,at+4)
    return a+b*256+c*65536+d*16777216
end

local function small64(s,at)
    assert(at>=0 and at+8<=#s,'truncated u64')
    assert(u32(s,at+4)==0,'value exceeds bounded layout range')
    return u32(s,at)
end

local function pack64(value)
    local out={}
    for _=1,8 do
        out[#out+1]=string.char(value%256)
        value=math.floor(value/256)
    end
    return table.concat(out)
end

local function replace64(data,at,value)
    return data:sub(1,at)..pack64(value)..data:sub(at+9)
end

function M.build(target_payload,donor_payload)
    assert(#target_payload>=64,'target Kit header is truncated')
    assert(#donor_payload>=64,'donor Kit header is truncated')
    local body_offset=small64(donor_payload,48)
    local body_count=small64(donor_payload,56)
    assert(body_count>=1 and body_count<=16,'invalid donor Body count')
    assert(body_offset+body_count*24<=#donor_payload,'donor Body array is outside payload')

    local body_rows,piece_chunks,fixups={}, {}, {48}
    local next_offset=64+body_count*24
    local piece_count=0
    for i=0,body_count-1 do
        local at=body_offset+i*24
        local piece_offset=small64(donor_payload,at+8)
        local count=small64(donor_payload,at+16)
        assert(count>=1 and count<=4096,'invalid donor Piece count')
        local bytes=count*96
        assert(piece_offset+bytes<=#donor_payload,'donor Piece array is outside payload')
        body_rows[#body_rows+1]=donor_payload:sub(at+1,at+8)
            ..pack64(next_offset)..pack64(count)
        fixups[#fixups+1]=64+i*24+8
        piece_chunks[#piece_chunks+1]=donor_payload:sub(piece_offset+1,piece_offset+bytes)
        next_offset=next_offset+bytes
        piece_count=piece_count+count
    end

    local payload=target_payload:sub(1,48)..pack64(64)..pack64(body_count)
        ..table.concat(body_rows)..table.concat(piece_chunks)
    assert(#payload==next_offset,'clone payload length mismatch')
    return {
        payload=payload,
        payload_bytes=#payload,
        body_count=body_count,
        piece_count=piece_count,
        pointer_fixups=fixups,
        target_item_id=u32(target_payload,0),
        donor_item_id=u32(donor_payload,0)
    }
end

function M.relocate(plan,base)
    assert(type(plan)=='table' and type(plan.payload)=='string','invalid clone plan')
    assert(type(base)=='number' and base>=65536,'invalid relocation base')
    local relocated=plan.payload
    for _,at in ipairs(plan.pointer_fixups) do
        relocated=replace64(relocated,at,base+small64(plan.payload,at))
    end
    return relocated
end

return M
