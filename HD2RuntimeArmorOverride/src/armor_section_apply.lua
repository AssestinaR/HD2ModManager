-- 把衣柜掩码翻译成对 unit TOC 的 dword 写入，并保证可回滚。
--
-- 只做两件事：算计划、执行计划。定位（扫内存找 TOC 副本）由调用方提供，
-- 因为那部分依赖 ffi，而这一层必须能在离线状态下用文件读取来测。
--
-- 写入的字段是 section 记录 +16 的 NumIndices（实测：写 0 隐藏该 section 的几何，
-- 写回原值恢复；见 notes/numindices-is-the-visibility-switch-20261017.md）。
-- 偏移是相对该 unit 自己 TOC 切片的，所以绝对地址 = 基址 + 相对偏移。
local A={}

local SECTION_RECORD_SIZE=24
local NUM_INDICES_OFFSET=16

-- 指纹必须按字节拼 hex（见 armor_section_layout.lua 的 hex64）：
-- 64 位材质 ID 经 Lua number 会被 double 精度截断。
local function fingerprint_of(layout,mesh,section)
    local slot
    if section.material_index and mesh.material_slots[section.material_index+1] then
        slot=mesh.material_slots[section.material_index+1]
    end
    local material=slot and layout.material_by_slot[slot] or nil
    local triangles=math.floor((section.num_indices or 0)/3)
    if not material then return 'none-'..tostring(triangles) end
    return material..'-'..tostring(triangles)
end

-- 某个 section 下标在各级 LOD 上的落点。
-- 只收 numSections 与基准 mesh 相等的 LOD：数量不等说明节序不可对齐，
-- 按同一下标写会关错几何（原版 unit 就有 lod3 只有 1 个 section 的情况）。
local function collect(layout,canonical,section_index)
    local toggles={}
    for _,mesh in ipairs(layout.meshes) do
        if mesh.lod and mesh.lod>=0 and mesh.num_sections==canonical.num_sections then
            local section=mesh.sections[section_index+1]
            if section then
                toggles[#toggles+1]={lod=mesh.lod,mesh_index=mesh.index,
                    offset=section.offset,num_indices=section.num_indices}
            end
        end
    end
    return toggles
end

-- 算计划。disabled(section_index,fingerprint) 返回 true 表示该 section 要隐藏。
--
-- 指纹是**用实际读到的 section 内容**去查掩码的，所以"掩码里的指纹对不上"自然
-- 表现为"查不到 → 保持可见"，不需要额外分支。这正是把"关错部件"降级成
-- "设置静默失效"的落点。
function A.plan(layout,canonical,disabled,originals)
    assert(type(disabled)=='function','disabled predicate is required')
    if not canonical then return nil,'no_lod0_mesh' end
    local writes={}
    local hidden=0
    for index=1,canonical.num_sections do
        local section=canonical.sections[index]
        local hide=disabled(index-1,fingerprint_of(layout,canonical,section))==true
        if hide then hidden=hidden+1 end
        local toggles=collect(layout,canonical,index-1)
        if #toggles==0 then return nil,'no_toggles_for_section_'..tostring(index-1) end
        for _,toggle in ipairs(toggles) do
            writes[#writes+1]={
                unit_section=index-1,
                lod=toggle.lod,
                address_offset=toggle.offset+NUM_INDICES_OFFSET,
                -- 原值优先取缓存：section 被写成 0 之后内存里就没有原值了，
                -- 而"取消勾选"必须把它写回去。缓存是唯一还有原值的地方。
                original=(originals and originals[toggle.offset+NUM_INDICES_OFFSET]) or toggle.num_indices,
                desired=hide and 0 or ((originals and originals[toggle.offset+NUM_INDICES_OFFSET]) or toggle.num_indices),
            }
        end
    end
    return {writes=writes,sections=canonical.num_sections,hidden_sections=hidden}
end

-- 执行计划：逐条校验当前值再写，写完回读确认；任何一条失败就整体回滚。
--
-- 接受两种当前值：等于记录的原值（没被动过），或等于 0（上一次被我们隐藏了）。
-- 后者是"恢复"能走同一条路的原因：用户重新启用某件时，内存里已经是 0，
-- 若只认原值，恢复就永远过不了预检——第一版就是这么错的。
-- 0 不会误伤：本来原值就是 0 的 section 不会有写入（previous==desired）。
--
-- 不校验就写是危险的：重载后 TOC 可能换地址，写到别的地方比不写更糟。
function A.apply(io,writes)
    assert(type(io)=='table' and type(io.read)=='function' and type(io.write)=='function',
        'io with read/write is required')
    assert(type(writes)=='table','writes table is required')

    -- 先整体校验，避免"写了一半才发现地址错了"。
    for _,write in ipairs(writes) do
        local current=io.read(write.address_offset)
        if current==nil then
            return false,'unreadable: section '..tostring(write.unit_section)
                ..' lod '..tostring(write.lod)
        end
        if current~=write.original and current~=0 then
            return false,'precheck_failed: section '..tostring(write.unit_section)
                ..' lod '..tostring(write.lod)
                ..' expected '..tostring(write.original)..' or 0, got '..tostring(current)
        end
        write.previous=current
    end

    local applied={}
    for _,write in ipairs(writes) do
        if write.previous~=write.desired then
            local ok=io.write(write.address_offset,write.desired,write.previous)
            if not ok then
                local rolled_back=A.rollback(io,applied)
                return false,rolled_back
                    and ('write_failed at section '..tostring(write.unit_section)..'; rolled back')
                    or ('write_failed and rollback failed; restart the game')
            end
            applied[#applied+1]=write
        end
    end
    -- 返回已写列表而不是数量：主事务若在之后失败，需要拿它整体撤回。
    return true,applied
end

-- 把已写的值恢复成写入前的样子。恢复同样逐条校验，避免覆盖别人的写入。
function A.rollback(io,applied)
    local okay=true
    for index=#applied,1,-1 do
        local write=applied[index]
        local current=io.read(write.address_offset)
        if current==write.desired then
            if not io.write(write.address_offset,write.previous,write.desired) then okay=false end
        elseif current~=write.previous then
            -- 有人在我们之后改过它：不能盲目覆盖，只能认账。
            okay=false
        end
    end
    return okay
end

-- 整体禁用判定：一个 unit 的全部 section 都被隐藏时，引擎根本不该加载它。
-- 返回 true 时调用方应把该 unit 的 Piece 从注册表里去掉（省显存；
-- 收益尚未实测，见交接文档 §7）。
function A.unit_fully_hidden(plan)
    return plan~=nil and plan.sections>0 and plan.hidden_sections==plan.sections
end

function A.summary(plan)
    if not plan then return 'plan=nil' end
    local writes=0
    for _,write in ipairs(plan.writes) do
        if write.desired~=write.original then writes=writes+1 end
    end
    return string.format('sections=%d hidden=%d writes=%d',
        plan.sections,plan.hidden_sections,writes)
end

A.SECTION_RECORD_SIZE=SECTION_RECORD_SIZE
A.NUM_INDICES_OFFSET=NUM_INDICES_OFFSET
return A
