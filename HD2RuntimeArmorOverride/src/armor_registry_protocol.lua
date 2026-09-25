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
    local result={mappings={},models={},piece_overrides={},target_modes={}}
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
                local fields={}
                for field in (value..','):gmatch('(.-),') do fields[#fields+1]=field end
                assert(#fields==6 or #fields==7,
                    'model_piece must be key,body,unit,slot,piece_type,tone[,enabled]')
                local name,body,unit,slot,piece_type,tone=
                    fields[1],fields[2],fields[3],fields[4],fields[5],fields[6]
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
                local piece_enabled=true
                if fields[7]~=nil then
                    assert(fields[7]=='true' or fields[7]=='false',
                        'model piece enabled must be true or false')
                    piece_enabled=fields[7]=='true'
                end
                model.pieces[#model.pieces+1]={
                    body=parsed_body,
                    unit=hex_id(unit,'model unit'),
                    slot=parsed_slot,
                    piece_type=parsed_piece_type,
                    tone=integer(tone,'model tone',0,255),
                    enabled=piece_enabled}
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
            elseif key=='piece_override' then
                local owner,body,unit,slot,piece_type,enabled=value:match(
                    '^([^,]+),([^,]+),([^,]+),([^,]+),([^,]+),([^,]+)$')
                assert(owner and body and unit and slot and piece_type and enabled,
                    'piece_override must be item,body,unit,slot,piece_type,enabled')
                assert(enabled=='true' or enabled=='false',
                    'piece_override enabled must be true or false')
                result.piece_overrides[#result.piece_overrides+1]={
                    owner=item_id(owner,'piece override owner'),
                    body=integer(body,'override body',0,0xffffffff),
                    unit=hex_id(unit,'override unit'),
                    slot=integer(slot,'override slot',1,0xffffffff),
                    piece_type=integer(piece_type,'override piece type',0,0xffffffff),
                    enabled=enabled=='true'}
            elseif key=='target_mode' then
                local target,mode=value:match('^([^,]+),([^,]+)$')
                assert(target and mode,'target_mode must be target_item,follow|manual')
                assert(mode=='follow' or mode=='manual','target_mode must be follow or manual')
                result.target_modes[item_id(target,'target mode item')]=mode=='follow'
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
    table.sort(result.piece_overrides,function(a,b)
        if a.owner~=b.owner then return a.owner<b.owner end
        if a.body~=b.body then return a.body<b.body end
        if a.slot~=b.slot then return a.slot<b.slot end
        return a.piece_type<b.piece_type
    end)
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
            if piece.enabled==false then
                lines[#lines]=lines[#lines]..',false'
            end
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
    for _,item in ipairs(document.piece_overrides or {}) do
        lines[#lines+1]=string.format('piece_override=%u,%d,%s,%d,%d,%s',
            item.owner,item.body,item.unit,item.slot,item.piece_type,
            item.enabled==false and 'false' or 'true')
    end
    local target_modes=document.target_modes or {}
    local mode_targets={}
    for target in pairs(target_modes) do mode_targets[#mode_targets+1]=target end
    table.sort(mode_targets)
    for _,target in ipairs(mode_targets) do
        lines[#lines+1]=string.format('target_mode=%u,%s',target,
            target_modes[target] and 'follow' or 'manual')
    end
    return table.concat(lines,'\n')..'\n'
end

return M
