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
        stats.writes=applied
        return true,stats
    end

    return self
end

function P.summary(stats)
    if not stats then return 'stats=nil' end
    return string.format('targets=%d units=%d hidden=%d writes=%d missing=%d',
        stats.targets,stats.units,stats.hidden_sections,stats.writes,stats.missing)
end

return P
