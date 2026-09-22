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

function M.parse(text)
    assert(type(text)=='string','protocol text is required')
    assert(#text<=65536,'protocol exceeds 64 KiB')
    local result={mappings={}}
    local seen={}
    for raw in (text..'\n'):gmatch('(.-)\r?\n') do
        local line=trim(raw)
        if line~='' and line:sub(1,1)~='#' then
            local key,value=line:match('^([%w_]+)%s*=%s*(.-)%s*$')
            assert(key and value,'invalid protocol line: '..line)
            if key=='schema' then
                result.schema=integer(value,'schema',1,1)
            elseif key=='revision' then
                result.revision=integer(value,'revision',0,0x7fffffff)
            elseif key=='enabled' then
                assert(value=='true' or value=='false','enabled must be true or false')
                result.enabled=value=='true'
            elseif key=='map' then
                local target,source=value:match('^([^,]+),([^,]+)$')
                assert(target and source,'map must be target_item,source_item')
                target=item_id(target,'target item')
                source=item_id(source,'source item')
                assert(target~=source,'target and source item must differ')
                assert(not seen[target],'duplicate target item: '..tostring(target))
                seen[target]=true
                result.mappings[#result.mappings+1]={target=target,source=source}
            else
                error('unknown protocol key: '..key)
            end
        end
    end
    assert(result.schema==1,'schema=1 is required')
    assert(result.revision~=nil,'revision is required')
    assert(result.enabled~=nil,'enabled is required')
    table.sort(result.mappings,function(a,b) return a.target<b.target end)
    return result
end

function M.serialize(document)
    assert(type(document)=='table','protocol document is required')
    local lines={'schema=1','revision='..tostring(document.revision or 0),
        'enabled='..tostring(document.enabled==true)}
    local mappings={}
    for _,mapping in ipairs(document.mappings or {}) do
        mappings[#mappings+1]={target=mapping.target,source=mapping.source}
    end
    table.sort(mappings,function(a,b) return a.target<b.target end)
    for _,mapping in ipairs(mappings) do
        lines[#lines+1]=string.format('map=%u,%u',mapping.target,mapping.source)
    end
    return table.concat(lines,'\n')..'\n'
end

return M
