-- 离线测试：armor_identity_probe.lua 8.0.0（假引擎，不需要游戏）。
-- 运行：luajit HD2RuntimeArmorOverride/tests/test_armor_identity_probe.lua <src>
-- 没有 luajit 时：python -I HD2RuntimeArmorOverride/scripts/run_lua_test.py <pylib> <本文件> <src>

-- 假日志句柄：抓所有写进来的 JSON 行 ---------------------------------------
local lines = {}
local logFile = {}
function logFile:write(text) lines[#lines + 1] = text; return true end
function logFile:flush() return true end
rawset(_G, 'CowboyBingusModLoader', {
	api = 1,
	log_directory = '/tmp/hd2/Logs',
	open_log = function() return logFile end,
})

-- 假触发文件：io.open 只拦 probe-scan.txt ----------------------------------
local triggerText = nil
local real_open = io.open
io.open = function(path, mode)
	if type(path) == 'string' and path:find('probe%-scan%.txt$') then
		if triggerText == nil then return nil end
		local handle = {}
		function handle:read() return triggerText end
		function handle:close() return true end
		return handle
	end
	return real_open(path, mode)
end

-- 假时钟：每调用一次前进 1 秒（离线跑得比真实时间快，自动扫描才能触发）
local clock = 1000
os.time = function() clock = clock + 1; return clock end

-- 假引擎：unit 是带字段的表，所有 API 都按字段回答 ---------------------------
local AVATAR_PATH = 'content/fac_helldivers/cha_avatar/avatar_helldiver'
local PX, PY, PZ = 0.000, 2.514, 20.067          -- v6/v7 实机：探针认的"玩家"
local OX, OY, OZ = 1.470, 13.869, 19.570         -- v6/v7 实机：民主官那一簇

local ALL, AVATARS = {}, {}
local visibility = {}                            -- unit → bool（set_unit_visibility 的结果）
local function unit(id, nodes, x, y, z, anim)
	local u = setmetatable({ id = id, nodes = nodes, x = x, y = y, z = z, anim = anim == true },
		{ __tostring = function(self) return self.id end })
	ALL[#ALL + 1] = u
	return u
end

local officerRoot = unit('15678e360612b805', 84, OX, OY, OZ, true)
local officerHead = unit('387f271c7f49614f', 179, OX, OY, OZ, true)   -- 头候选（要被藏的那个）
local marker = unit('82595b5d94c57343', 82, OX, OY, OZ)               -- 独有左肩
local cape = unit('ea846ef460cc5ba0', 50, OX, OY, OZ)                 -- 斗篷（s1）
local face = unit('7862870bc9822eeb', 0)                              -- 纯材质持有者（0 节点）
local headUp = unit('a065ebfcf8ad1afe', 16, OX, OY + 1.6, OZ)          -- 头顶 1.6 米
local below = unit('1111111111111111', 6, OX, OY - 0.5, OZ)
local faraway = unit('2222222222222222', 6, OX, OY + 5, OZ)            -- 半径外
local sameIdFar = unit('387f271c7f49614f', 179, 100, 0, 0, true)       -- 同 id 的另一实例（半径外，不该被藏）
local unrelated = unit('3333333333333333', 6, 100, 0, 0)
local zeroUnrelated = unit('4444444444444444', 0)                      -- 不在 watchlist 的 0 节点
local avatarNear = unit('avatar', 37, PX, PY, PZ, true)
local avatarFar = unit('avatar', 37, 6.9, -7.75, 20.96, true)
AVATARS[1], AVATARS[2] = avatarNear, avatarFar
local world = {}

local stingray = {
	Application = { worlds = function() return { world } end },
	World = {
		units = function()
			local list = {}
			for index, item in ipairs(ALL) do list[index] = item end
			return list
		end,
		units_by_resource = function(_, key)
			local list = {}
			if key == AVATAR_PATH then
				for index, item in ipairs(AVATARS) do list[index] = item end
				return list
			end
			if type(key) == 'table' and key.hex ~= nil then
				for _, item in ipairs(ALL) do
					if item.id == key.hex then list[#list + 1] = item end
				end
			end
			return list
		end,
	},
	Unit = {
		alive = function(item) return item ~= nil end,
		num_scene_graph_items = function(item) return item.nodes or 0 end,
		world_position = function(item)
			if (item.nodes or 0) <= 0 then error('no scene graph items') end
			return { x = item.x, y = item.y, z = item.z }
		end,
		debug_name = function(item) return '#ID[' .. item.id .. ']' end,
		has_animation_state_machine = function(item) return item.anim == true end,
		set_unit_visibility = function(item, visible)
			visibility[item] = visible
			return true
		end,
	},
	--- IdString64.from_hex 的假实现：包一个带 hex 字段的表（units_by_resource 认它）
	IdString64 = {
		from_hex = function(hex) return { hex = hex:lower() } end,
	},
	Vector3 = { to_elements = function(v) return v.x, v.y, v.z end },
	Keyboard = { pressed = function() return false end, released = function() return false end, F8 = 21 },
	Window = { get_main_window = function() return 'window' end, keyboard = function() return 'string' end },
}
rawset(_G, 'stingray', stingray)

-- 跑 ---------------------------------------------------------------------
local path = (arg and arg[1]) or 'HD2RuntimeArmorOverride/src/armor_identity_probe.lua'
local M = dofile(path)
assert(M.version == '8.0.0', 'version')
assert(type(_G.update) == 'function', 'update hooked')

local function run(ticks) for _ = 1, (ticks or 60) do _G.update() end end
local function slice(from) return table.concat(lines, '', from + 1) end
local function has(text, fragment, why)
	assert(text:find(fragment, 1, true), 'missing: ' .. (why or fragment))
end
local function linesOf(text)
	local list = {}
	for line in (text .. '\n'):gmatch('([^\n]*)\n') do list[#list + 1] = line end
	return list
end

-- ① 自动那一轮：只读 ------------------------------------------------------
run()
local auto = slice(0)
has(auto, '"r":"start"', 'start')
has(auto, '"version":"8.0.0"', 'start version')
has(auto, '"r":"loaded"', 'loaded')
has(auto, '"r":"avatar"', 'avatar')
has(auto, '"r":"sweep_begin"', 'sweep_begin')
has(auto, '"r":"target_scan_end"', 'target_scan_end')
has(auto, '"r":"done"', 'done')
has(auto, '"mode":"ok"', 'done mode ok')
has(auto, '"shape":"pressed(F8)"', 'keyboard shape')
has(auto, '"instances":1', 'marker has 1 instance')
assert(not auto:find('"r":"hide"', 1, true), 'auto run must not hide anything')
assert(not auto:find('"r":"lookup"', 1, true), 'auto run skips the 36 lookups')
assert(visibility[officerHead] == nil, 'auto run touched visibility')

-- watchlist：有坐标的写 watch，0 节点的写 watch_zero
has(auto, '"r":"watch_zero"', 'watch_zero lines')
local faceZero, headWatch = false, false
for _, line in ipairs(linesOf(auto)) do
	if line:find('"r":"watch_zero"', 1, true) and line:find('7862870bc9822eeb', 1, true) then faceZero = true end
	if line:find('"r":"watch"', 1, true) and line:find('387f271c7f49614f', 1, true) then headWatch = true end
end
assert(faceZero, 'face must be reported as watch_zero (0 nodes)')
assert(headWatch, 'head candidate must be reported as watch')
assert(not auto:find('4444444444444444', 1, true), 'unrelated zero-node unit must be ignored')

-- 簇：按 dy 从大到小排；1.6 米那个在最前，-0.5 米那个在最后；5 米的在半径外
local order = {}
for _, line in ipairs(linesOf(auto)) do
	if line:find('"r":"cluster"', 1, true) then order[#order + 1] = line:match('"unit":"([^"]+)"') end
end
assert(#order >= 4, 'cluster lines expected, got ' .. #order)
assert(order[1] == 'a065ebfcf8ad1afe', 'head-like unit must sort first by dy, got ' .. tostring(order[1]))
assert(order[#order] == '1111111111111111', 'lowest unit must sort last, got ' .. tostring(order[#order]))
assert(not auto:find('2222222222222222', 1, true), 'unit outside radius must be excluded')

-- ② 按 trigger 跑：36 条 lookup + 隐藏 ------------------------------------
local mark = #lines
triggerText = 'A'
run()
local tail = slice(mark)
has(tail, '"r":"lookup"', 'lookup records')
has(tail, '"shape":"id64"', 'id64 shape')
local lookupCount = 0
for _, line in ipairs(linesOf(tail)) do
	if line:find('"r":"lookup"', 1, true) then lookupCount = lookupCount + 1 end
end
assert(lookupCount >= 30, 'expected the whole watchlist to be looked up, got ' .. lookupCount)
has(tail, '"r":"hide_begin"', 'hide stage starts')
has(tail, '"r":"hide"', 'hide records')
has(tail, '"r":"hide_end"', 'hide stage ends')
-- 只有半径内那个实例被藏；同 id 的远处实例不动
assert(visibility[officerHead] == false, 'head candidate must be hidden')
assert(visibility[sameIdFar] == nil, 'same-id instance outside radius must be untouched')
do
	local line
	for _, candidate in ipairs(linesOf(tail)) do
		if candidate:find('"r":"hide"', 1, true) and candidate:find('387f271c7f49614f', 1, true) then line = candidate end
	end
	assert(line ~= nil, 'hide record for the head candidate expected')
	assert(line:find('"instances":1', 1, true), 'exactly one instance hidden, got: ' .. line)
end
has(tail, '"r":"done"', 'triggered run finishes')

-- ③ show=1 恢复 ----------------------------------------------------------
local mark2 = #lines
triggerText = 'show=1'
run()
local tail2 = slice(mark2)
has(tail2, '"r":"show_begin"', 'show stage starts')
has(tail2, '"r":"show"', 'show records')
has(tail2, '"r":"show_end"', 'show stage ends')
assert(not tail2:find('"r":"target_scan_begin"', 1, true), 'show run must skip the cluster scan')
assert(visibility[officerHead] == true, 'head candidate must be visible again')

-- ④ hide= 指定别的 id + 半径 ---------------------------------------------
local mark3 = #lines
triggerText = 'id=15678e360612b805;r=2;hide=ea846ef460cc5ba0,a065ebfcf8ad1afe'
run()
local tail3 = slice(mark3)
has(tail3, '"target":"15678e360612b805"', 'triggered target must be the root')
has(tail3, '"radius":2.000', 'triggered radius must be 2')
assert(visibility[cape] == false, 'cape must be hidden by the custom list')
assert(visibility[headUp] == false, 'head-up unit must be hidden by the custom list')

-- ⑤ 目标不存在：写 target_missing，不崩 ----------------------------------
local mark4 = #lines
triggerText = 'id=9999999999999999;r=2'
run()
local tail4 = slice(mark4)
has(tail4, '"r":"target_missing"', 'missing target must be reported')
assert(not tail4:find('"r":"error"', 1, true), 'missing target must not raise')

print('probe v8 offline tests passed (' .. #lines .. ' log lines)')
