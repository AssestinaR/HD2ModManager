-- 衣柜 section 掩码（wardrobe.masks）的只读解析器。
-- 规格：HD2RuntimeArmorOverride/notes/wardrobe-schema-v2-20261017.md
--
-- 只解析、只查表，**不写文件**：写方永远是 C# 那一侧。运行时多做一件事就多一处
-- 可能和编辑器不一致的地方，而这份文件的语义必须两边完全一致。
--
-- 文件里没有继承关系：每个目标直接指向一个掩码集，掩码集里就是最终要隐藏的 section。
-- 之所以不在运行时做 inherit/override，是因为不同目标可以映射到不同的视觉来源，
-- 全局一份"来源掩码"表达不了；而把继承解析成扁平投影之后，运行时只剩两次查表。
-- Read-only parser. Writing is the C# side's job only. The file carries no inheritance:
-- each target points at one mask set, so the runtime needs two lookups and no rules.
local M={}

local MAX_BYTES=1024*1024
local MAX_SECTION_INDEX=65535
local MAX_SET_ID=65535

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
    return {schema=2,revision=0,enabled=true,sets={},targets={},
        entry_count=0,target_count=0}
end

function M.parse(text)
    assert(type(text)=='string','wardrobe.masks text is required')
    assert(#text<=MAX_BYTES,'wardrobe.masks exceeds 1 MiB')
    local result=M.empty()
    local seen_schema,seen_revision,seen_enabled=false,false,false
    local entry_seen,target_seen={},{}
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
                result.schema=integer(value,'schema',2,2)
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
                local set,unit,index,fp=value:match('^([^,]+),([^,]+),([^,]+),([^,]+)$')
                assert(set,'off must be setId,unit,sectionIndex,fingerprint')
                local set_id=integer(set,'mask set id',0,MAX_SET_ID)
                local key_text=M.key(unit_id(unit,'off unit'),
                    integer(index,'section index',0,MAX_SECTION_INDEX),
                    fingerprint(fp,'off fingerprint'))
                local bucket=result.sets[set_id]
                if not bucket then bucket={};result.sets[set_id]=bucket end
                assert(not entry_seen[set_id..'/'..key_text],
                    'duplicate mask entry at line '..line_number)
                entry_seen[set_id..'/'..key_text]=true
                bucket[key_text]=true
                result.entry_count=result.entry_count+1
            elseif key=='target' then
                local target,set=value:match('^([^,]+),([^,]+)$')
                assert(target,'target must be targetItemId,setId')
                local target_id=item_id(target,'target item')
                local set_id=integer(set,'mask set id',0,MAX_SET_ID)
                assert(not target_seen[target_id],
                    'duplicate mask set for target '..tostring(target_id))
                target_seen[target_id]=true
                result.targets[target_id]=set_id
                result.target_count=result.target_count+1
            else
                error('unknown wardrobe.masks key: '..key)
            end
        end
    end
    assert(seen_schema,'schema=2 is required')
    assert(seen_revision,'revision is required')
    assert(seen_enabled,'enabled is required')
    return result
end

-- 目标关联的掩码集；没有关联返回 nil（= 不隐藏任何 section）。
function M.set_of(document,target)
    return (document.targets or {})[target]
end

-- 返回一个查询函数，供运行时逐 section 判断。闭包只捕获一次 document，
-- 避免每帧重新解析。
function M.lookup(document,target)
    if not document or document.enabled==false then
        return function() return false end
    end
    local set_id=M.set_of(document,target)
    if set_id==nil then return function() return false end end
    local bucket=document.sets[set_id]
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
