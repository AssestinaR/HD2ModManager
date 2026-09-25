-- Pure, pointer-free composition planning. Its exact canonical payload is the
-- effective visual signature; revision/comments/unused overrides do not enter it.
local C={}
function C.u32(data,at)
    assert(data and at>=0 and at+4<=#data,'truncated u32')
    local a,b,c,d=data:byte(at+1,at+4)
    return a+b*256+c*65536+d*16777216
end
function C.pack(value,size)
    local bytes={}
    for i=1,size do bytes[i]=string.char(value%256);value=math.floor(value/256) end
    assert(value==0,'integer overflow')
    return table.concat(bytes)
end
function C.unit(data)
    assert(#data>=8,'truncated Unit ID')
    local hex={}
    for i=8,1,-1 do hex[#hex+1]=string.format('%02x',data:byte(i)) end
    return table.concat(hex)
end
local function unit_bytes(hex)
    assert(type(hex)=='string' and #hex==16 and hex:match('^%x+$'),'invalid Unit ID')
    local bytes={}
    for i=15,1,-2 do bytes[#bytes+1]=string.char(tonumber(hex:sub(i,i+1),16)) end
    return table.concat(bytes)
end
local function replace(data,offset,value)
    return data:sub(1,offset)..value..data:sub(offset+#value+1)
end
function C.plan(target,source,model,enabled,metadata,checkpoint)
    local facts,by_position={},{}
    for _,row in ipairs(target.rows) do
        for index,piece in ipairs(row.pieces) do
            local slot,kind=C.u32(piece,8),C.u32(piece,12)
            local key=table.concat({row.body,slot,kind},':')
            by_position[key]=by_position[key] or piece
            facts[#facts+1]={body=row.body,slot=slot,piece_type=kind,weight=C.u32(piece,16)}
            if checkpoint and index%64==0 then checkpoint() end
        end
    end
    local weights=metadata.weight_index(facts)
    local rows={}
    if model then
        local grouped={}
        for index,piece in ipairs(model.pieces) do
            if piece.enabled~=false and enabled(piece.body,piece.unit,piece.slot,piece.piece_type) then
                local row=grouped[piece.body]
                if not row then
                    row={body=piece.body,header=C.pack(piece.body,4)..C.pack(0,4),pieces={}}
                    grouped[piece.body]=row;rows[#rows+1]=row
                end
                local key=table.concat({piece.body,piece.slot,piece.piece_type},':')
                local data=by_position[key] or string.rep('\0',96)
                if model.test_templates then
                    local donor=source and source.test_templates[piece.unit]
                    assert(donor,'missing diagnostic template')
                    assert(donor.body==piece.body and C.u32(donor.data,8)==piece.slot,
                        'diagnostic donor body/slot mismatch')
                    assert(piece.slot~=0 and piece.slot~=1,'diagnostic B excludes helmets')
                    -- Preserve native type, tone, auxiliary hashes and padding.
                    -- Only Unit here and target weight below are changed.
                    data=replace(donor.data,0,unit_bytes(piece.unit))
                else
                    data=replace(data,0,unit_bytes(piece.unit))
                    data=replace(data,8,C.pack(piece.slot,4)..C.pack(piece.piece_type,4))
                    data=replace(data,88,string.char(piece.tone or 0))
                end
                row.pieces[#row.pieces+1]=data
            end
            if checkpoint and index%64==0 then checkpoint() end
        end
        local rank={[3]=1,[0]=2,[1]=3}
        table.sort(rows,function(a,b)return (rank[a.body] or 1000+a.body)<(rank[b.body] or 1000+b.body)end)
    else
        assert(source,'missing source snapshot')
        for _,source_row in ipairs(source.rows) do
            local row={body=source_row.body,header=source_row.header,pieces={}}
            for index,piece in ipairs(source_row.pieces) do
                if enabled(row.body,C.unit(piece),C.u32(piece,8),C.u32(piece,12)) then
                    row.pieces[#row.pieces+1]=piece
                end
                if checkpoint and index%64==0 then checkpoint() end
            end
            if #row.pieces==0 then return nil,'source body has no enabled Piece' end
            rows[#rows+1]=row
        end
    end
    if #rows<1 or #rows>16 then return nil,'visual Body count is invalid' end
    local body_rows,pieces,fixups={}, {}, {48}
    local offset,total=64+#rows*24,0
    for i,row in ipairs(rows) do
        total=total+#row.pieces
        if #row.pieces<1 or total>4096 then return nil,'visual Piece count is invalid' end
        body_rows[#body_rows+1]=row.header..C.pack(offset,8)..C.pack(#row.pieces,8)
        fixups[#fixups+1]=64+(i-1)*24+8
        for index,data in ipairs(row.pieces) do
            local weight=metadata.resolve_weight(weights,row.body,C.u32(data,8),C.u32(data,12))
            if weight==nil then return nil,'target weight metadata has no unique majority' end
            pieces[#pieces+1]=replace(data,16,C.pack(weight,4))
            if checkpoint and index%64==0 then checkpoint() end
        end
        offset=offset+#row.pieces*96
    end
    local payload=target.header:sub(1,48)..C.pack(64,8)..C.pack(#rows,8)
        ..table.concat(body_rows)..table.concat(pieces)
    assert(#payload==offset,'clone length self-check failed')
    return {payload=payload,bytes=offset,bodies=#rows,pieces=total,fixups=fixups}
end
return C
