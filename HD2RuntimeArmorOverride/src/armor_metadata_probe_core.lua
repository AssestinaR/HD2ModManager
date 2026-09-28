local M={}

local function u32(data,offset)
    local a,b,c,d=data:byte(offset+1,offset+4)
    return a+b*256+c*65536+d*16777216
end
M.u32=u32

local function le_hex(data,offset,size)
    local out={}
    for i=size-1,0,-1 do
        out[#out+1]=string.format('%02x',data:byte(offset+i+1))
    end
    return table.concat(out)
end
M.le_hex=le_hex

function M.decode_kit_header(data)
    if not data or #data<64 then return nil,'kit_header_size_invalid' end
    return {
        item_id=u32(data,0),
        dlc_id=u32(data,4),
        set_id=u32(data,8),
        name_upper_string_id=u32(data,12),
        name_cased_string_id=u32(data,16),
        description_string_id=u32(data,20),
        rarity=u32(data,24),
        passive=u32(data,28),
        archive_hash=le_hex(data,32,8),
        kit_type=u32(data,40)
    }
end

function M.read_u64(data,offset,ffi)
    local value=ffi.new('uint64_t[1]')
    ffi.copy(value,data:sub(offset+1,offset+8),8)
    return tonumber(value[0])
end

function M.decode_piece(data,ffi)
    if not data or #data~=96 then return nil,'piece_size_invalid' end
    local resources={}
    for index=0,7 do
        local value=le_hex(data,24+index*8,8)
        if value~='0000000000000000' then
            resources[#resources+1]=index..':'..value
        end
    end
    return {
        unit=le_hex(data,0,8),
        slot=u32(data,8),
        piece_type=u32(data,12),
        weight=u32(data,16),
        resources=table.concat(resources,';'),
        tone_variations=data:byte(89),
        reserved_hex=le_hex(data,89,7),
        raw_hex=(data:gsub('.',function(c)
            return string.format('%02x',string.byte(c))
        end))
    }
end

local function add_weight(counts,value)
    counts[value]=(counts[value] or 0)+1
end

local function dominant_weight(counts)
    local winner,highest,tied
    for value,count in pairs(counts or {}) do
        if highest==nil or count>highest then
            winner,highest,tied=value,count,false
        elseif count==highest then
            tied=true
        end
    end
    if winner==nil or tied then return nil end
    return winner
end

function M.weight_index(pieces)
    local meta={exact={},body_visible={},body_metadata={},
        global_visible={},global_metadata={}}
    for _,piece in ipairs(pieces) do
        local body=piece.body
        local exact_key=body..':'..piece.slot..':'..piece.piece_type
        meta.exact[exact_key]=meta.exact[exact_key] or {}
        add_weight(meta.exact[exact_key],piece.weight)
        local body_set,global_set
        if piece.slot==0 then
            body_set=meta.body_metadata
            global_set=meta.global_metadata
        else
            body_set=meta.body_visible
            global_set=meta.global_visible
        end
        body_set[body]=body_set[body] or {}
        add_weight(body_set[body],piece.weight)
        add_weight(global_set,piece.weight)
    end
    return meta
end

function M.resolve_weight(meta,body,slot,piece_type)
    local exact=dominant_weight(meta.exact[body..':'..slot..':'..piece_type])
    if exact~=nil then return exact,'exact' end
    local by_body,global
    if slot==0 then
        by_body=meta.body_metadata[body]
        global=meta.global_metadata
    else
        by_body=meta.body_visible[body]
        global=meta.global_visible
    end
    local value=dominant_weight(by_body)
    if value~=nil then return value,'body' end
    value=dominant_weight(global)
    if value~=nil then return value,'global' end
end

function M.replace_weight(data,value)
    local bytes={}
    for index=0,3 do
        bytes[#bytes+1]=string.char(value%256)
        value=math.floor(value/256)
    end
    return data:sub(1,16)..table.concat(bytes)..data:sub(21)
end

function M.piece_diff(target,source,simulated)
    local a,b,c=M.decode_piece(target),M.decode_piece(source),M.decode_piece(simulated)
    if not a or not b or not c then return nil end
    return {
        unit=a.unit~=c.unit,
        slot=a.slot~=c.slot,
        piece_type=a.piece_type~=c.piece_type,
        weight=a.weight~=c.weight,
        resources=a.resources~=c.resources,
        tone_variations=a.tone_variations~=c.tone_variations,
        reserved=a.reserved_hex~=c.reserved_hex,
        source_weight=b.weight,
        target_weight=a.weight,
        simulated_weight=c.weight
    }
end

return M
