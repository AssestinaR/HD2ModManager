-- 离线测试：kit_instance_hider.lua（假引擎，不需要游戏）。
-- 运行：luajit HD2RuntimeArmorOverride/tests/test_kit_instance_hider.lua <src>
rawset(_G, 'HD2_KIT_HIDER_OFFLINE', true)
local path = (arg and arg[1]) or 'HD2RuntimeArmorOverride/src/kit_instance_hider.lua'
local M = dofile(path)
assert(M.status == 'offline', 'offline guard')

local MARKER = '82595b5d94c57343'
local CAPE = 'ea846ef460cc5ba0'
local LEG = '74b2e79c1c5af767'      -- 民主官与玩家神射手、CE-64 共用
local ARM = 'e15680e4c4a89420'
local PLAYER_SHOULDER = '90b57e547e7ff7bb'

-- 纯函数 -------------------------------------------------------------------
local obj = setmetatable({}, { __tostring = function() return '[IdString64 "#ID[82595B5D94C57343]" 0x82595b5d94c57343]' end })
assert(M.decode_resource(obj) == MARKER)
assert(M.decode_resource('nope') == nil)
assert(M.decode_resource(nil) == nil)
assert(M.near_marker({ 1 }, { { rule = 1, x = 0, y = 0, z = 0 } }, 0.1, 0.1, 0.1) == 1)
assert(M.near_marker({ 1 }, { { rule = 1, x = 0, y = 0, z = 0 } }, 0.3, 0.3, 0) == nil)
assert(M.near_marker({ 1 }, { { rule = 2, x = 0, y = 0, z = 0 } }, 0, 0, 0) == nil)
-- 规则里不能有披风与标记本身
for _, id in ipairs(M.RULES[1].hide) do
	assert(id ~= CAPE and id ~= MARKER, 'rule must not hide ' .. id)
end
assert(#M.RULES[1].hide == 20)

-- 假引擎 --------------------------------------------------------------------
local function unit(id, x, y, z, opts)
	return { id = id, x = x, y = y, z = z, alive = not (opts and opts.dead), visible = true }
end
-- world1 = 飞船：玩家在原点穿神射手（与民主官共用腿和手臂），民主官在 14 米外
local player = { unit(LEG, 0, 0, 0), unit(ARM, 0, 0, 0), unit(PLAYER_SHOULDER, 0, 0, 0), unit(CAPE, 0, 0, 0) }
-- 民主官：腿排在标记前面（同一轮先扫到腿，下一轮才能藏）
local officer = { unit(LEG, 1.47, 13.87, 19.57), unit(MARKER, 1.47, 13.87, 19.57),
	unit(ARM, 1.48, 13.87, 19.57), unit(CAPE, 1.47, 13.87, 19.57) }
local deadLeg = unit(LEG, 1.47, 13.87, 19.57, { dead = true })
local w1 = {}
for _, u in ipairs(player) do w1[#w1 + 1] = u end
for _, u in ipairs(officer) do w1[#w1 + 1] = u end
w1[#w1 + 1] = deadLeg
-- world2 = 军械库预览：只有玩家的神射手，没有标记
local preview = { unit(LEG, 0, 0, -0.93), unit(ARM, 0, 0, -0.93) }
local worlds = { { units = w1 }, { units = preview } }

local failVisibility = false
local visibilityCalls = 0
local S = {
	Application = { worlds = function() return worlds end },
	World = { units = function(w) return w.units end },
	Unit = {
		alive = function(u) return u.alive end,
		resource_name = function(u)
			assert(u.alive, 'resource_name on dead unit')
			return setmetatable({}, { __tostring = function()
				return '[IdString64 "#ID[' .. u.id .. ']" 0x' .. u.id .. ']'
			end })
		end,
		world_position = function(u, node)
			assert(node == 1 and u.alive, 'world_position contract')
			return { u.x, u.y, u.z }
		end,
		set_unit_visibility = function(u, visible)
			assert(u.alive, 'set_unit_visibility on dead unit')
			assert(visible == false, 'only hides')
			visibilityCalls = visibilityCalls + 1
			if failVisibility then error('engine refused') end
			u.visible = visible
		end,
	},
	Vector3 = {
		x = function(v) return v[1] end, y = function(v) return v[2] end, z = function(v) return v[3] end,
	},
}
-- 允许清单之外的调用一律报错
for _, ns in ipairs({ 'Unit', 'World', 'Application' }) do
	setmetatable(S[ns], { __index = function(_, k) error('unexpected ' .. ns .. '.' .. k) end })
end

local clock, lines = 0, {}
local hider = M.new({
	stingray = S,
	now = function() return clock end,
	cpu = function() return clock end,
	write = function(line) lines[#lines + 1] = line end,
})

local function run_sweep()
	clock = clock + 1
	for _ = 1, 10 do hider.tick() end
end

-- 第 1 轮：标记在腿后面，所以腿这轮还没藏；手臂在标记后面，已经藏了
run_sweep()
assert(officer[1].visible == true, 'leg seen before marker in first sweep')
assert(officer[3].visible == false, 'arm after marker hidden in first sweep')
-- 第 2 轮：用上一轮的标记位置，腿也藏了
run_sweep()
assert(officer[1].visible == false, 'leg hidden on second sweep')
assert(officer[2].visible == true, 'marker (Aemeath) must stay visible')
assert(officer[4].visible == true, 'cape must stay visible')
-- 玩家与预览完全不受影响
for _, u in ipairs(player) do assert(u.visible == true, 'player piece hidden: ' .. u.id) end
for _, u in ipairs(preview) do assert(u.visible == true, 'preview piece hidden: ' .. u.id) end

-- 日志：有 sweep 行，带 hidden 与 left_visible
local found = false
for _, l in ipairs(lines) do
	if l:find('"kind":"sweep"', 1, true) and l:find('"hidden":2', 1, true)
		and l:find('"left_visible":4', 1, true) then found = true end
end
assert(found, table.concat(lines))

-- 结果不变不重复写
local n = #lines
run_sweep(); run_sweep()
assert(#lines == n, 'unchanged sweeps must not log')

-- 民主官离场（标记消失）后再生成的腿不再被藏
officer[2].alive = false
local respawnLeg = unit(LEG, 1.47, 13.87, 19.57)
w1[#w1 + 1] = respawnLeg
run_sweep() -- 这一轮仍用上一轮的标记位置
run_sweep()
respawnLeg.visible = true
run_sweep()
assert(respawnLeg.visible == true, 'no marker ⇒ no hiding')

-- set_unit_visibility 连续失败 ⇒ 停手并记一行
officer[2].alive = true
failVisibility = true
for _ = 1, 8 do run_sweep() end
local disabled = false
for _, l in ipairs(lines) do if l:find('hide_disabled', 1, true) then disabled = true end end
assert(disabled, 'hide_disabled not logged')
local before = visibilityCalls
run_sweep()
assert(visibilityCalls == before, 'must stop calling after repeated failures')

print('test_kit_instance_hider: ok (' .. #lines .. ' log lines)')
