-- 衣柜 section 掩码（wardrobe.masks）的只读解析器。
-- 规格：HD2RuntimeArmorOverride/notes/wardrobe-schema-v2-20261017.md
--
-- 只解析、只查表，**不写文件**：写方永远是 C# 那一侧。运行时多做一件事就多一处
-- 可能和编辑器不一致的地方，而这份文件的语义必须两边完全一致。
-- Read-only parser for wardrobe.masks. Writing is the C# side's job only; the runtime
-- must not have a second opinion about what this file means.
local M={}

local MAX_BYTES=1024*1024
local MAX_SECTION_INDEX=65535

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

-- Unit ID 在文件里是不带 0x 的 16 位小写 hex。这里同时接受 0x 前缀，
-- 是为了让"从 sections.json 里抄过来"这种用法不会因为前缀而失败。
local function unit_id(value,name)
    value=trim(value):lower()
    if value:sub(1,2)=='0x' then value=value:sub(3) end
    assert(#value==16 and value:match('^%x%x%x%x%x%x%x%x%x%x%x%x%x%x%x%x$'),
        name..' must be a 16-digit hex unit ID')
    return value
end

-- 指纹 = <materialId|none>-<triangles>。按字符串处理，不做数值运算：
-- MaterialId 是 64 位、按小端读，Lua 侧一旦做位运算就很容易把高低双字写反。
local function fingerprint(value,name)
    value=trim(value):lower()
    local material,triangles=value:match('^([%w]+)%-(%d+)$')
    assert(material,name..' is not a section fingerprint')
    if material~='none' then
        assert(#material==16 and material:match('^%x+$'),
            name..' material id must be 16 hex digits')
    end
    assert(tonumber(triangles)<=4294967295,name..' triangle count is out of range')
    return material..'-'..tostring(math.floor(tonumber(triangles)))
end

-- 查表键：unit:sectionIndex:fingerprint。用字符串而不是嵌套表，
-- 因为运行时要按 (unit,section) 做几万次查找，字符串哈希最省事。
function M.key(unit,section_index,fp)
    return unit..':'..tostring(section_index)..':'..fp
end

function M.fingerprint(material_id,triangles)
    return tostring(material_id):lower()..'-'..tostring(math.floor(triangles))
end

function M.empty()
    return {schema=1,revision=0,enabled=true,source={},targets={},modes={},
        source_count=0,target_count=0}
end

function M.parse(text)
    assert(type(text)=='string','wardrobe.masks text is required')
    assert(#text<=MAX_BYTES,'wardrobe.masks exceeds 1 MiB')
    local result=M.empty()
    local seen_schema,seen_revision,seen_enabled=false,false,false
    local source_seen,target_seen,mode_seen={},{},{}
    local line_number=0
    for raw in (text..'\n'):gmatch('(.-)\r?\n') do
        line_number=line_number+1
        local line=trim(raw)
        if line~='' and line:sub(1,1)~='#' then
            local key,value=line:match('^([%w_]+)%s*=%s*(.-)%s*$')
            assert(key and value,'invalid wardrobe.masks line '..line_number..': '..line)
            if key=='schema' then
                assert(not seen_schema,'schema is duplicated')
                seen_schema=true
                result.schema=integer(value,'schema',1,1)
            elseif key=='revision' then
                assert(not seen_revision,'revision is duplicated')
                seen_revision=true
                result.revision=integer(value,'revision',0,0x7fffffff)
            elseif key=='enabled' then
                assert(not seen_enabled,'enabled is duplicated')
                seen_enabled=true
                assert(value=='true' or value=='false','enabled must be true or false')
                result.enabled=value=='true'
            elseif key=='off' then
                local unit,index,fp=value:match('^([^,]+),([^,]+),([^,]+)$')
                assert(unit,'off must be unit,sectionIndex,fingerprint')
                local key_text=M.key(unit_id(unit,'off unit'),
                    integer(index,'section index',0,MAX_SECTION_INDEX),
                    fingerprint(fp,'off fingerprint'))
                assert(not source_seen[key_text],'duplicate mask entry at line '..line_number)
                source_seen[key_text]=true
                result.source[key_text]=true
                result.source_count=result.source_count+1
            elseif key=='off_target' then
                local target,unit,index,fp=value:match('^([^,]+),([^,]+),([^,]+),([^,]+)$')
                assert(target,'off_target must be targetItemId,unit,sectionIndex,fingerprint')
                local target_id=item_id(target,'off_target item')
                local key_text=M.key(unit_id(unit,'off_target unit'),
                    integer(index,'section index',0,MAX_SECTION_INDEX),
                    fingerprint(fp,'off_target fingerprint'))
                local bucket=result.targets[target_id]
                if not bucket then bucket={};result.targets[target_id]=bucket end
                assert(not target_seen[target_id..'/'..key_text],
                    'duplicate mask entry at line '..line_number)
                target_seen[target_id..'/'..key_text]=true
                bucket[key_text]=true
                result.target_count=result.target_count+1
            elseif key=='mask_mode' then
                local target,mode=value:match('^([^,]+),([^,]+)$')
                assert(target,'mask_mode must be targetItemId,inherit|override')
                assert(mode=='inherit' or mode=='override','mask_mode must be inherit or override')
                local target_id=item_id(target,'mask_mode item')
                assert(not mode_seen[target_id],'duplicate mask_mode for target '..tostring(target_id))
                mode_seen[target_id]=true
                -- 缺省就是 inherit，所以显式 inherit 不落进表里，保持规范化。
                if mode=='override' then result.modes[target_id]='override' end
            else
                error('unknown wardrobe.masks key: '..key)
            end
        end
    end
    assert(seen_schema,'schema=1 is required')
    assert(seen_revision,'revision is required')
    assert(seen_enabled,'enabled is required')
    return result
end

function M.mode_of(document,target)
    return (document.modes or {})[target] or 'inherit'
end

-- 返回一个查询函数，供运行时逐 section 判断。闭包只捕获一次 document，
-- 避免每帧重新解析。
function M.lookup(document,target)
    if not document or document.enabled==false then
        return function() return false end
    end
    local bucket
    if M.mode_of(document,target)=='override' then
        bucket=document.targets[target]
    else
        bucket=document.source
    end
    if not bucket then return function() return false end end
    return function(unit,section_index,fp)
        return bucket[M.key(unit,section_index,fp)]==true
    end
end

-- 整体禁用判定：unit 的全部 section 都被禁用时返回 true。
-- 空列表返回 false——没有 section 不等于"全关"。
function M.unit_fully_disabled(document,target,unit,sections)
    if not document or document.enabled==false then return false end
    if not sections or #sections==0 then return false end
    local disabled=M.lookup(document,target)
    for _,section in ipairs(sections) do
        if not disabled(unit,section.index,section.fingerprint) then return false end
    end
    return true
end

return M
