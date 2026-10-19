-- 把一份 wardrobe.masks 应用到内存：定位 unit → 算写入计划 → 一次性应用。
--
-- 为什么单独成一层：运行时主文件很大且事务很讲究，接线越短越安全。
-- 这一层拿注入的 read/write/regions，所以能离线用假内存端到端跑通
-- "多个目标、同一 unit、掩码集不同"这类最容易出错的情形。
--
-- 全有或全无：所有目标的写入合并成一批，交给 apply 一次执行。
-- 任何一条失败，apply 内部会整体回滚——不会出现"关了一半"的中间态。
local P={}

-- env = {read=,write=,regions=,layout=,apply=,locate=,log=}
--   read(address,size) -> string|nil
--   write(address,value,expected) -> bool
--   regions() -> 迭代器，产出 {base=,size=}
--   layout / apply / locate 是三个模块
function P.new(env)
    assert(type(env)=='table','env is required')
    assert(type(env.read)=='function' and type(env.write)=='function','read/write required')
    assert(type(env.regions)=='function','regions required')
    assert(env.layout and env.apply and env.locate and env.masks,'modules required')

    local locator=env.locate.new(env.read,env.regions,env.layout)
    local self={}

    -- 换装/重载后基址会变，缓存必须整体失效。
    function self.invalidate(unit_id)
        locator.invalidate(unit_id)
    end

    -- masks: 已解析的 wardrobe.masks 文档
    -- units_by_target: { [target_id] = { "unithex", ... } }
    -- 返回 ok, stats 或 nil, reason
    function self.apply_document(masks,units_by_target,checkpoint)
        local stats={targets=0,units=0,missing=0,writes=0,hidden_sections=0}
        if not masks or masks.enabled==false then return true,stats end

        local merged={}       -- address_offset -> write
        local order={}
        local function merge(write)
            local existing=merged[write.address_offset]
            if not existing then
                merged[write.address_offset]=write
                order[#order+1]=write
                return true
            end
            -- 同一个地址被两个目标要求不同的值：这是数据错误，不能猜谁赢。
            if existing.desired~=write.desired then
                return false,'conflicting_targets_for_section_'
                    ..tostring(write.unit_section)
            end
            return true
        end

        local target_ids={}
        for target in pairs(units_by_target or {}) do target_ids[#target_ids+1]=target end
        table.sort(target_ids)

        for _,target in ipairs(target_ids) do
            local units=units_by_target[target]
            if units and #units>0 then
                stats.targets=stats.targets+1
                for _,unit_id in ipairs(units) do
                    local base,parsed=locator.locate(unit_id,checkpoint)
                    if not base then
                        -- 没加载/没找到不是致命错误：换装前后目标不同，跳过并计数。
                        stats.missing=stats.missing+1
                        if env.log then env.log('section_mask_unit_missing: '..tostring(unit_id)) end
                    else
                        local canonical=env.layout.canonical_mesh(parsed)
                        -- masks.lookup 的签名是 (unit, index, fp)，而 plan 要的是 (index, fp)：
                        -- 在这里补上 unit，别把两个顺序搞混（搞混了 fp 会变成 nil）。
                        local lookup=env.masks.lookup(masks,target)
                        local plan,reason=env.apply.plan(parsed,canonical,
                            function(index,fp) return lookup(unit_id,index,fp) end)
                        if not plan then
                            stats.missing=stats.missing+1
                            if env.log then
                                env.log('section_mask_plan_failed: '..tostring(unit_id)..' '..tostring(reason))
                            end
                        else
                            stats.units=stats.units+1
                            stats.hidden_sections=stats.hidden_sections+plan.hidden_sections
                            for _,write in ipairs(plan.writes) do
                                local okay,conflict=merge(write)
                                if not okay then return nil,conflict end
                            end
                        end
                    end
                    if checkpoint then checkpoint() end
                end
            end
        end

        if #order==0 then return true,stats end
        -- apply 要的是"读一个 dword"，而 env.read 是"读一段字节"：在这里适配，
        -- 别指望两边签名一样（第一版就是把字节读取当 dword 读取传进去了）。
        local function read_dword(address)
            local data=env.read(address,4)
            if not data or #data<4 then return nil end
            local a,b,c,d=data:byte(1,4)
            return a+b*256+c*65536+d*16777216
        end
        local okay,applied=env.apply.apply({read=read_dword,write=env.write},order)
        if not okay then return nil,applied end
        stats.writes=#applied
        stats.applied=applied
        return true,stats
    end

    return self
end

-- 从注册表快照里取出该护甲用到的 unit 列表（去重、排序）。
-- 快照形状（运行时既有结构）：{ rows = { { body = n, pieces = {...} }, ... } }
-- 单个 piece 的 unit 由 composition 模块解出——掩码文件里的 unit 就是它。
-- 抽成纯函数是为了能离线测：真实快照只能在游戏里拿到。
function P.units_from_snapshot(snapshot,composition)
    local seen,list={},{}
    for _,row in ipairs(snapshot and snapshot.rows or {}) do
        for _,piece in ipairs(row.pieces or {}) do
            local unit=composition and composition.unit and composition.unit(piece) or nil
            -- 只收 16 位 hex：composition.unit 对畸形 piece 可能返回空串或垃圾，
            -- 而空串在 Lua 里是真值——放进去会变成"一个不存在的 unit"，只在游戏里才炸。
            if type(unit)=='string' and #unit==16 and not seen[unit] then
                seen[unit]=true
                list[#list+1]=unit
            end
        end
    end
    table.sort(list)
    return list
end
-- 从掩码文档自身推出"每个目标涉及哪些 unit"。
--
-- 为什么不再从注册表映射反推：这个产品的常见形态是**原地替换**——patch 直接换掉
-- 那件护甲自己的 unit，玩家穿的还是同一个护甲 item，协议里根本没有 map 行。
-- 按映射反推的话，原地替换的情形一个目标都算不出来，掩码就永远不会生效。
--
-- 掩码条目里已经写明了 unit，所以这里只做一次分组：target → 它那个集合里的 unit 列表。
-- 这同时让"目标没被穿在身上"变得无害：它的 unit 没加载，定位会失败并计入 missing。
function P.units_for_masks(masks)
    local by_target={}
    if not masks or masks.enabled==false then return by_target end
    -- 解析后的文档按集合存放，键是 "unit:sectionIndex:fingerprint"（见 armor_wardrobe_masks.key）。
    -- unit 恒为前 16 个字符，所以取前缀即可，不需要再拆整串。
    local units_by_set={}
    for set,bucket in pairs(masks.sets or {}) do
        local units={}
        for key in pairs(bucket) do
            local unit=key:sub(1,16)
            if #unit==16 then units[unit]=true end
        end
        units_by_set[set]=units
    end
    for target,set in pairs(masks.targets or {}) do
        local bucket=units_by_set[set]
        if bucket then
            local list={}
            for unit in pairs(bucket) do list[#list+1]=unit end
            table.sort(list)
            by_target[target]=list
        end
    end
    return by_target
end
function P.summary(stats)
    if not stats then return 'stats=nil' end
    return string.format('targets=%d units=%d hidden=%d writes=%d missing=%d',
        stats.targets,stats.units,stats.hidden_sections,stats.writes,stats.missing)
end

return P
