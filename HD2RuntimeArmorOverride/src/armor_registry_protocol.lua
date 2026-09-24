local M={}

local function trim(value)
    return (value:gsub('^%s+',''):gsub('%s+$',''))
end

local function integer(value,name,min,max)
    local number=tonumber(value)
    assert(number and number==math.floor(number),name..' must be an integer')
    assert(number>=min and number<=max,name..' is outside the supported range')
    return number
end

local function item_id(value,name)
    value=trim(value)
    local number
    if value:sub(1,2):lower()=='0x' then
        number=tonumber(value:sub(3),16)
    else
        number=tonumber(value)
    end
    assert(number and number==math.floor(number)
        and number>=0 and number<=0xffffffff,name..' is not a 32-bit item ID')
    return number
end

local function model_key(value)
    value=trim(value)
    assert(#value>=1 and #value<=96,'model key length is invalid')
    assert(value:match('^[A-Za-z0-9_.%-/:]+$'),'model key contains unsupported characters')
    return value
end

local function hex_id(value,name)
    value=trim(value):lower()
    assert(value:match('^%x%x%x%x%x%x%x%x%x%x%x%x%x%x%x%x$'),
        name..' must be a 64-bit hex id')
    return value
end

function M.parse(text)
    assert(type(text)=='string','protocol text is required')
    assert(#text<=65536,'protocol exceeds 64 KiB')
    local result={mappings={},models={}}
    local seen={}
    local model_seen={}
    for raw in (text..'\n'):gmatch('(.-)\r?\n') do
        local line=trim(raw)
        if line~='' and line:sub(1,1)~='#' then
            local key,value=line:match('^([%w_]+)%s*=%s*(.-)%s*$')
            assert(key and value,'invalid protocol line: '..line)
            if key=='schema' then
                result.schema=integer(value,'schema',1,2)
            elseif key=='revision' then
                result.revision=integer(value,'revision',0,0x7fffffff)
            elseif key=='enabled' then
                assert(value=='true' or value=='false','enabled must be true or false')
                result.enabled=value=='true'
            elseif key=='model' then
                local name,source=value:match('^([^,]+),([^,]*)$')
                assert(name and source,'model must be key,source_item_or_empty')
                name=model_key(name)
                assert(not model_seen[name],'duplicate model key: '..name)
                model_seen[name]=true
                result.models[#result.models+1]={key=name,
                    source=source~='' and item_id(source,'model source item') or nil,
                    pieces={},piece_keys={}}
            elseif key=='model_piece' then
                local name,body,unit,slot,piece_type,tone=value:match(
                    '^([^,]+),([^,]+),([^,]+),([^,]+),([^,]+),([^,]+)$')
                assert(name and body and unit and slot and piece_type and tone,
                    'model_piece must be key,body,unit,slot,piece_type,tone')
                name=model_key(name)
                local model
                for _,candidate in ipairs(result.models) do
                    if candidate.key==name then model=candidate end
                end
                assert(model,'model_piece references unknown model '..name)
                local parsed_body=integer(body,'model body',0,0xffffffff)
                local parsed_slot=integer(slot,'model slot',1,0xffffffff)
                local parsed_piece_type=integer(piece_type,'model piece type',0,0xffffffff)
                local piece_key=table.concat({parsed_body,parsed_slot,parsed_piece_type},':')
                assert(not model.piece_keys[piece_key],
                    'duplicate model Piece body/slot/piece_type: '..piece_key)
                model.piece_keys[piece_key]=true
                model.pieces[#model.pieces+1]={
                    body=parsed_body,
                    unit=hex_id(unit,'model unit'),
                    slot=parsed_slot,
                    piece_type=parsed_piece_type,
                    tone=integer(tone,'model tone',0,255)}
            elseif key=='map' then
                local target,source=value:match('^([^,]+),([^,]+)$')
                assert(target and source,'map must be target_item,source_item')
                target=item_id(target,'target item')
                source=item_id(source,'source item')
                assert(target~=source,'target and source item must differ')
                assert(not seen[target],'duplicate target item: '..tostring(target))
                seen[target]=true
                result.mappings[#result.mappings+1]={target=target,source=source}
            elseif key=='map_model' then
                local target,name=value:match('^([^,]+),(.+)$')
                assert(target and name,'map_model must be target_item,model_key')
                target=item_id(target,'target item')
                name=model_key(name)
                assert(not seen[target],'duplicate target item: '..tostring(target))
                seen[target]=true
                result.mappings[#result.mappings+1]={target=target,model=name}
            else
                error('unknown protocol key: '..key)
            end
        end
    end
    assert(result.schema==1 or result.schema==2,'schema=1 or schema=2 is required')
    assert(result.revision~=nil,'revision is required')
    assert(result.enabled~=nil,'enabled is required')
    table.sort(result.mappings,function(a,b) return a.target<b.target end)
    table.sort(result.models,function(a,b) return a.key<b.key end)
    return result
end

function M.serialize(document)
    assert(type(document)=='table','protocol document is required')
    local lines={'schema=2','revision='..tostring(document.revision or 0),
        'enabled='..tostring(document.enabled==true)}
    local models={}
    for _,model in ipairs(document.models or {}) do
        models[#models+1]={key=model.key,source=model.source,pieces=model.pieces or {}}
    end
    table.sort(models,function(a,b) return a.key<b.key end)
    for _,model in ipairs(models) do
        lines[#lines+1]=string.format('model=%s,%s',model.key,model.source and tostring(model.source) or '')
        for _,piece in ipairs(model.pieces or {}) do
            lines[#lines+1]=string.format('model_piece=%s,%d,%s,%d,%d,%d',
                model.key,piece.body,piece.unit,piece.slot,piece.piece_type,piece.tone or 0)
        end
    end
    local mappings={}
    for _,mapping in ipairs(document.mappings or {}) do
        mappings[#mappings+1]={target=mapping.target,source=mapping.source,model=mapping.model}
    end
    table.sort(mappings,function(a,b) return a.target<b.target end)
    for _,mapping in ipairs(mappings) do
        if mapping.model then
            lines[#lines+1]=string.format('map_model=%u,%s',mapping.target,mapping.model)
        else
            lines[#lines+1]=string.format('map=%u,%u',mapping.target,mapping.source)
        end
    end
    return table.concat(lines,'\n')..'\n'
end

return M
