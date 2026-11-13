-- HD2-Addon: mods/hd2modmanager/kit_instance_hider
-- 按实例隐藏共用部件（阶段 3）。计划见 docs/agent/kit-instance-isolation-plan-20261113.md
--
-- 规则：场上出现「标记部件」时，只把与它根坐标重合（RADIUS 内）的那几个共用部件实例隐藏。
-- 同一个资源被别人穿着时，因为不在标记部件旁边，不受影响。
--
-- 只用允许清单（hd2pbx_api.txt）里的 Application / World / Unit / Vector3：
--   读：Application.worlds、World.units、Unit.alive、Unit.resource_name、Unit.world_position
--   写：Unit.set_unit_visibility（只作用于这一个实例，不改资源、不改件记录）
-- 不调用 IdString64：资源 ID 取自 resource_name 的 tostring 文本（探针 0.3 实机验证）。
-- 不保留任何 Unit 句柄跨帧：每帧重新取列表，调用前先 Unit.alive。
local GLOBAL_KEY = 'HD2KitInstanceHider'
local existing = rawget(_G, GLOBAL_KEY)
if existing then return existing end

local M = { version = '0.1.0', status = 'starting' }
rawset(_G, GLOBAL_KEY, M)

local SWEEP_INTERVAL = 0.5      -- 秒：两次完整扫描的最小间隔
local UNITS_PER_FRAME = 300     -- 每帧最多检查的 unit 数（探针实测约 0.25 ms）
local MAX_WORLDS = 32
local MAX_UNITS_PER_WORLD = 8192
local MAX_LOG_BYTES = 48 * 1024 -- 加载器的日志约 64 KB 截断开头
local MAX_HIDE_ERRORS = 10      -- set_unit_visibility 连续失败这么多次就停手

-- 规则。marker 只出现在目标实例身上；hide 是要在该实例上隐藏的部件。
-- 爱弥斯民主官：民主官 NPC 穿 FS-05 记录 124623533，左肩 82595b5d 只有它在用
-- （探针 0.4 实机：玩家穿的神射手是 3970674111，左肩是 90b57e54）。
-- hide = 124623533 中被原 Mod 那 9 个隐藏材质覆盖到的部件（slot 2–7、9），
-- 披风 ea846ef460cc5ba0 原 Mod 也没藏，这里同样保留。
local RULES = {
	{
		name = 'aemeath_democracy_officer',
		marker = '82595b5d94c57343',
		radius = 0.3,
		hide = {
			'8d02a82d0c214171', '3bdfa900f32cd83f', 'f2715c19b24ecd8b', 'be65eae060a0267c', -- slot 2 胸
			'210fb2f60f8308ea', 'd19d17bc42ab9275', '99f001cb5a6e7116', '418b4fb786159bac', -- slot 3 胯
			'74b2e79c1c5af767', '901fc9c35857dad2',                                         -- slot 4 左腿
			'c4c7a5de567a87fe', '7e5111d05840be1a',                                         -- slot 5 右腿
			'db487f9a3f738dc5', '324fb206612ce4f7', '64a71fbe6ef75b80',                     -- slot 6 左臂
			'e40aa3f8f6141a86', 'd0007ea2f834da58', 'e15680e4c4a89420',                     -- slot 7 右臂
			'effdfd2386aef629', '99a130858b3d8ff1',                                         -- slot 9 右肩
		},
	},
}
M.RULES = RULES

-- 反查表：id → 规则下标
local MARKERS, HIDES = {}, {}
for index, rule in ipairs(RULES) do
	MARKERS[rule.marker] = index
	for _, id in ipairs(rule.hide) do
		HIDES[id] = HIDES[id] or {}
		HIDES[id][#HIDES[id] + 1] = index
	end
end

---------------------------------------------------------------------------
-- 纯函数
---------------------------------------------------------------------------

--- Unit.resource_name 的返回值 → 16 位小写 hex。tostring 形如
--- [IdString64 "#ID[4d1c334d294dfa97]" 0x4d1c334d294dfa97]
function M.decode_resource(value)
	if value == nil then return nil end
	if type(value) == 'string' then
		local text = value:gsub('^0[xX]', '')
		if #text == 16 and text:match('^%x+$') then return text:lower() end
		return nil
	end
	local ok, text = pcall(tostring, value)
	if not ok or type(text) ~= 'string' then return nil end
	local hex = text:match('#ID%[(%x+)%]') or text:match('0x(%x+)')
	if hex and #hex == 16 then return hex:lower() end
	return nil
end

--- 该部件是否挨着某个同规则的标记部件。markers: { {rule=, x=, y=, z=}, ... }
function M.near_marker(rules, markers, x, y, z)
	for _, m in ipairs(markers) do
		for _, r in ipairs(rules) do
			if m.rule == r then
				local dx, dy, dz = x - m.x, y - m.y, z - m.z
				local radius = RULES[r].radius
				if dx * dx + dy * dy + dz * dz <= radius * radius then return r end
			end
		end
	end
	return nil
end

local function encode_line(fields)
	local keys = {}
	for k in pairs(fields) do keys[#keys + 1] = k end
	table.sort(keys)
	local parts = {}
	for _, k in ipairs(keys) do
		local v = fields[k]
		local t = type(v)
		if t == 'number' then
			v = (v == math.floor(v) and math.abs(v) < 2 ^ 53) and string.format('%d', v) or string.format('%.3f', v)
		elseif t == 'boolean' then
			v = v and 'true' or 'false'
		else
			v = '"' .. tostring(v):gsub('[%z\1-\31\\"]', ' ') .. '"'
		end
		parts[#parts + 1] = '"' .. k .. '":' .. v
	end
	return '{' .. table.concat(parts, ',') .. '}\n'
end

---------------------------------------------------------------------------
-- 本体：M.new(env) 便于离线注入假引擎
---------------------------------------------------------------------------

function M.new(env)
	local S = env.stingray
	local App, World, Unit, Vector3 = S.Application, S.World, S.Unit, S.Vector3
	local now, cpu, write = env.now, env.cpu, env.write
	local H = { stopped = false, hide_errors = 0, sweeps = 0 }
	local bytes = 0

	local function emit(kind, fields)
		fields = fields or {}
		fields.kind, fields.t = kind, now()
		local line = encode_line(fields)
		if bytes + #line > MAX_LOG_BYTES then return end
		bytes = bytes + #line
		pcall(write, line)
	end
	H.emit = emit

	local function try(f, ...)
		if type(f) ~= 'function' then return false end
		return pcall(f, ...)
	end
	local function alive(unit)
		local ok, v = try(Unit.alive, unit)
		return ok and v == true
	end
	local function position(unit)
		local ok, v = try(Unit.world_position, unit, 1)
		if not ok or v == nil then return nil end
		local okx, x = try(Vector3.x, v)
		local oky, y = try(Vector3.y, v)
		local okz, z = try(Vector3.z, v)
		if okx and oky and okz and type(x) == 'number' then return x, y, z end
		return nil
	end

	local sweep, nextSweepAt = nil, 0
	-- 上一轮各 world 找到的标记位置（只存数字，不存句柄）
	local lastMarkers = {}
	local lastSummary = nil
	local stats = { max_frame_ms = 0 }

	local function hide(unit)
		if H.hide_errors >= MAX_HIDE_ERRORS then return false end
		local ok = try(Unit.set_unit_visibility, unit, false)
		if ok then
			H.hide_errors = 0
			return true
		end
		H.hide_errors = H.hide_errors + 1
		if H.hide_errors >= MAX_HIDE_ERRORS then
			emit('hide_disabled', { reason = 'set_unit_visibility failed repeatedly' })
		end
		return false
	end

	local function startSweep()
		local ok, worlds = try(App.worlds)
		if not ok or type(worlds) ~= 'table' then return nil end
		local count = 0
		for i = 1, MAX_WORLDS do
			if rawget(worlds, i) == nil then break end
			count = i
		end
		return { world = 1, cursor = 1, worldCount = count, markers = {}, hidden = 0,
			missed = 0, markerCount = 0 }
	end

	local function markersFor(worldIndex)
		local list = {}
		for _, m in ipairs(sweep.markers[worldIndex] or {}) do list[#list + 1] = m end
		for _, m in ipairs(lastMarkers[worldIndex] or {}) do list[#list + 1] = m end
		return list
	end

	local function step()
		local okW, worlds = try(App.worlds)
		if not okW or type(worlds) ~= 'table' then return true end
		if sweep.world > sweep.worldCount then return true end
		local world = rawget(worlds, sweep.world)
		if world == nil then return true end
		local okU, units = try(World.units, world)
		if not okU or type(units) ~= 'table' then
			sweep.world, sweep.cursor = sweep.world + 1, 1
			return sweep.world > sweep.worldCount
		end
		local count = math.min(#units, MAX_UNITS_PER_WORLD)
		local budget = UNITS_PER_FRAME
		while budget > 0 and sweep.cursor <= count do
			local unit = rawget(units, sweep.cursor)
			sweep.cursor = sweep.cursor + 1
			budget = budget - 1
			if unit ~= nil and alive(unit) then
				local okR, raw = try(Unit.resource_name, unit)
				local id = okR and M.decode_resource(raw) or nil
				if id ~= nil then
					local markerRule = MARKERS[id]
					local hideRules = HIDES[id]
					if markerRule ~= nil or hideRules ~= nil then
						local x, y, z = position(unit)
						if x ~= nil then
							if markerRule ~= nil then
								local list = sweep.markers[sweep.world] or {}
								list[#list + 1] = { rule = markerRule, x = x, y = y, z = z }
								sweep.markers[sweep.world] = list
								sweep.markerCount = sweep.markerCount + 1
							end
							if hideRules ~= nil then
								if M.near_marker(hideRules, markersFor(sweep.world), x, y, z) then
									if hide(unit) then sweep.hidden = sweep.hidden + 1 end
								else
									sweep.missed = sweep.missed + 1
								end
							end
						end
					end
				end
			end
		end
		if sweep.cursor > count then
			sweep.world, sweep.cursor = sweep.world + 1, 1
		end
		return sweep.world > sweep.worldCount
	end

	local function finishSweep()
		H.sweeps = H.sweeps + 1
		lastMarkers = sweep.markers
		-- missed 是「同 ID 但不在标记旁」的实例数：玩家或别的 NPC 穿着同款，正常不隐藏。
		local summary = string.format('%d/%d/%d', sweep.markerCount, sweep.hidden, sweep.missed)
		if summary ~= lastSummary then
			lastSummary = summary
			emit('sweep', { sweep = H.sweeps, markers = sweep.markerCount, hidden = sweep.hidden,
				left_visible = sweep.missed, worlds = sweep.worldCount, max_frame_ms = stats.max_frame_ms })
			stats.max_frame_ms = 0
		end
		sweep = nil
	end

	function H.tick()
		if H.stopped then return end
		local t = now()
		if sweep == nil then
			if t < nextSweepAt then return end
			nextSweepAt = t + SWEEP_INTERVAL
			sweep = startSweep()
			if sweep == nil then return end
		end
		local began = cpu()
		local done = step()
		local ms = (cpu() - began) * 1000
		if ms > stats.max_frame_ms then stats.max_frame_ms = ms end
		if done then finishSweep() end
	end

	emit('start', { version = M.version, rules = #RULES })
	return H
end

---------------------------------------------------------------------------
-- 游戏内安装（离线测试时 _G.HD2_KIT_HIDER_OFFLINE = true，跳过）
---------------------------------------------------------------------------

if rawget(_G, 'HD2_KIT_HIDER_OFFLINE') then
	M.status = 'offline'
	return M
end

local loader = rawget(_G, 'CowboyBingusModLoader')
if type(loader) ~= 'table' or rawget(loader, 'api') ~= 1 or type(rawget(loader, 'open_log')) ~= 'function' then
	M.status = 'loader_unavailable'
	return M
end
local opened, file = pcall(loader.open_log, 'KitInstanceHider.log')
if not opened or not file then
	M.status = 'log_open_failed'
	return M
end
local engine = rawget(_G, 'stingray')
if type(engine) ~= 'table' or type(engine.Application) ~= 'table' then
	M.status = 'engine_unavailable'
	return M
end

local App = engine.Application
local function now()
	local ok, v = pcall(App.time_since_launch)
	return ok and type(v) == 'number' and v or 0
end
local osTable = rawget(_G, 'os')
local cpu = type(osTable) == 'table' and type(osTable.clock) == 'function' and osTable.clock or now

local hider = M.new({
	stingray = engine,
	now = now,
	cpu = cpu,
	write = function(line)
		file:write(line)
		file:flush()
	end,
})
M.hider = hider

local previous = rawget(_G, 'update')
local errors = 0
rawset(_G, 'update', function(dt, ...)
	local ok, err = pcall(hider.tick)
	if not ok then
		errors = errors + 1
		hider.emit('tick_error', { message = tostring(err) })
		if errors >= 20 then hider.stopped = true end
	end
	if type(previous) == 'function' then return previous(dt, ...) end
end)
M.status = 'active'
return M
