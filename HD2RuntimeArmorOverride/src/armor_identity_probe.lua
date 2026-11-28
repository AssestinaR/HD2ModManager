-- HD2-Addon: mods/hd2modmanager/armor_identity_probe
-- 只读探针 v8：在 v7（定点扫描 + watchlist + lookup 键形状）之上加**按实例隐藏/恢复测试**。
--
-- v7 实机结论（2026-11-24，两轮：file:B / file:A）：
--   * `World.units_by_resource(world, IdString64.from_hex(hex))` **能查到 unit**（
--     `82595b5d94c57343`=1、`15678e360612b805`=1、`8085023e1b2d62bf`=8 …），
--     而 hex 字符串 / `#ID[hex]` / 大写 hex 全是 0 ⇒ 运行时那条通路是对的，键必须是 IdString64。
--   * 民主官簇（3 米）共 28 个 unit：17 个护甲件与根同坐标，另外两个 anim 也在同一坐标 ——
--     `15678e360612b805`（84 节点，根）和 `387f271c7f49614f`（179 节点，HEAD_CANDIDATES 里的）。
--     旁边 1.5～2 米处另有 5 个非护甲件（dx 全是 -1.470，即它们的 x=0）。
--   * 世界里 NPC 的结构很一致：每具 = 一个 84～95 节点的根 + 一个 179 节点的伴生 unit，
--     179 那些全在 HEAD_CANDIDATES 里（女舰长/技师/SEAF/守岸人那几套）。⇒ 179 = NPC 的头。
--   * `7862870bc9822eeb`（管理器记录的"民主官脸"）**在 watchlist 里也没出现**：既不在
--     `World.units` 的名字里，也不是 0 节点。v8 对全部 watchlist id 都做 id64 查询把它钉死。
--   * `Keyboard.pressed(Keyboard.F8)` 可用；`Window.keyboard(main)` 不通。
--
-- v8 的一轮 = 四个阶段：
--   W：全量扫（anim + watchlist + 目标实例 + 全部 avatar 实例）→ 写 anim / watch / watch_zero /
--      target / avatar_unit。
--   T：以目标实例为圆心、半径 r（默认 3.0）再扫一遍，按 dy 从大到小写 cluster。
--   H：把 `hide=` 名单里的 unit **在半径内按实例** set_unit_visibility(false)，每个 id 写一行 hide。
--      ⇒ 用来确认"某个 unit 到底是不是 NPC 的头"：藏了它，游戏里看头有没有消失。
--   U：恢复（`show=1` 触发）——把 `hide=` 名单里的 unit 在半径内 set_unit_visibility(true)。
--
-- 触发文件 ArmorRegistry/probe-scan.txt（`;` 分隔 key=value；纯文本用默认值跑一轮）：
--   id=<16位hex>   锚点目标，默认 82595b5d94c57343（民主官独有左肩）
--   r=<米>         半径，默认 3.0（0.3～12）
--   hide=<hex>[,<hex>…]  要藏/要恢复的 id，默认 387f271c7f49614f（民主官的头候选）
--   show=1         只恢复，不扫描
--   nohide=1       扫描但不隐藏
--   label=<名字>   日志里的标签
--
-- 扫描纪律（kit_instance_hider 实机结论，v3…v7 已验证零崩溃）：
--   每拍重新取 World.units（句柄不跨帧）→ alive → 先问 id → 要坐标时先 num_scene_graph_items>0。
local existing = rawget(_G, 'HD2ArmorIdentityProbe')
if existing then return existing end
local M = { version = '8.0.0', status = 'starting' }
rawset(_G, 'HD2ArmorIdentityProbe', M)

local function base_directory()
	local local_app_data = type(os) == 'table' and type(os.getenv) == 'function'
		and os.getenv('LOCALAPPDATA') or nil
	if type(local_app_data) == 'string' and local_app_data ~= '' and not local_app_data:find('?', 1, true) then
		return local_app_data .. '/CowboyBingus/Helldivers2'
	end
	local loader = rawget(_G, 'CowboyBingusModLoader')
	if type(loader) == 'table' and type(loader.log_directory) == 'string' and loader.log_directory ~= '' then
		return (loader.log_directory:gsub('[/\\]Logs$', ''))
	end
	return nil
end

local base = base_directory()
local file = nil
local loader = rawget(_G, 'CowboyBingusModLoader')
if type(loader) == 'table' and rawget(loader, 'api') == 1 and type(rawget(loader, 'open_log')) == 'function' then
	local okLog, handle = pcall(loader.open_log, 'ArmorIdentityProbe.log')
	if okLog and handle then file = handle end
end
if file == nil then
	local path = base and (base .. '/Logs/ArmorIdentityProbe.log') or nil
	if path ~= nil and type(io) == 'table' and io.open then
		local ok, handle = pcall(io.open, path, 'ab')
		if ok and handle then file = handle end
	end
end
if file == nil then M.status = 'log_open_failed'; return M end
local triggerPath = base and (base .. '/ArmorRegistry/probe-scan.txt') or nil

local S = rawget(_G, 'stingray')
if type(S) ~= 'table' then M.status = 'no_stingray'; return M end
local App = rawget(S, 'Application')
local World = rawget(S, 'World')
local Unit = rawget(S, 'Unit')
local Vector3 = rawget(S, 'Vector3')
local Keyboard = rawget(S, 'Keyboard')
local Window = rawget(S, 'Window')
local Id64 = rawget(S, 'IdString64')

local function q(s)
	s = tostring(s)
	return '"' .. s:gsub('[%z\1-\31\\"]', function(c)
		if c == '\\' then return '\\\\' end
		if c == '"' then return '\\"' end
		return string.format('\\u%04x', string.byte(c))
	end) .. '"'
end
local function emit(kind, fields)
	local row = { '"r":' .. q(kind) }
	for k, v in pairs(fields or {}) do
		local t = type(v)
		if t == 'string' then v = q(v)
		elseif t == 'boolean' then v = v and 'true' or 'false'
		elseif t == 'number' and v == v and math.abs(v) < math.huge then v = string.format('%.3f', v)
		else v = 'null' end
		row[#row + 1] = q(k) .. ':' .. v
	end
	local wrote = file:write('{' .. table.concat(row, ',') .. '}\n')
	if wrote and type(file.flush) == 'function' then pcall(file.flush, file) end
end
local function call(fn, ...)
	if type(fn) ~= 'function' then return false, 'not_a_function' end
	return pcall(fn, ...)
end

--- 31 个 NPC 头部候选（与 kit_instance_hider.HEAD_CANDIDATES 同一份）+ 民主官几个已知 id。
local WATCH = {}
for id in ([[000c21cd9f8a2f16 0b52672cc600377f 0e83b059cda78a26 186e14d733c00dfa 1de1795c754cd4fc 1f3bba04dc7d12bf
26ec3eb07a42c439 2cf637f920e10546 3698d7382672d30d 387f271c7f49614f 46f50507603729f3 4b6217d8636cf66b 4d4017549585cb8e
4ec39827b643fc3d 541e4fb19053d6b0 5449813a9b155530 7234ae282b5d733c 7862870bc9822eeb 7b7065399142aa51 7deb6faab1224bf7
8424786ad292815b 85d4ba84db7d27e5 8606f6d72d99f013 8dee773789f9d60c a065ebfcf8ad1afe a653c3fb4d4b36aa c380f637e9dadb28
c685e61d82a4b7a7 dbf91b19df408bf3 e499f3bd49d0dcb9 f1d828c1603bf2d6]]):gmatch('%x+') do
	WATCH[id] = true
end
WATCH['15678e360612b805'] = true   -- 民主官的根（84 节点，anim）
WATCH['82595b5d94c57343'] = true   -- 民主官独有左肩（规则里的 marker）
WATCH['8085023e1b2d62bf'] = true
WATCH['9512a3db760dda95'] = true
WATCH['def7dc54e86d6054'] = true   -- 隐藏占位件
local WATCH_IDS = {}
for id in pairs(WATCH) do WATCH_IDS[#WATCH_IDS + 1] = id end
table.sort(WATCH_IDS)

local AVATAR_PATH = 'content/fac_helldivers/cha_avatar/avatar_helldiver'
local DEFAULT_TARGET = '82595b5d94c57343'
local DEFAULT_HIDE = '387f271c7f49614f'
local DEFAULT_RADIUS = 3.0
local UNITS_PER_TICK = 64
local MAX_ANIM = 64
local MAX_INSTANCES = 8
local MAX_CLUSTER = 80
local MAX_WATCH_LINES = 120

local state = {
	phase = 'wait_avatar',
	ticks = 0,
	done = false,
	world = nil, avatar = nil,
	px = 0, py = 0, pz = 0,
	sweep = nil,
	queue = nil,
	target = DEFAULT_TARGET,
	radius = DEFAULT_RADIUS,
	hideList = { DEFAULT_HIDE },
	label = 'default',
	runs = 0,
	nextAuto = 0,
	triggerText = nil,
	lastPoll = 0,
	keyboardTried = false,
	home = nil,
	targets = nil,
}

emit('start', { version = M.version, note = 'v8: cluster + watchlist lookups + per-instance hide test' })

local function alive(unit)
	local ok, value = call(Unit.alive, unit)
	return ok and value == true
end
local function nodeCount(unit)
	local ok, value = call(Unit.num_scene_graph_items, unit)
	return ok and tonumber(value) or 0
end
--- 只有确认有场景节点才读坐标（0.4.4 的闪退教训）。
local function posOf(unit, count)
	if (count or nodeCount(unit)) <= 0 then return nil end
	local ok, v = call(Unit.world_position, unit, 1)
	if not ok or v == nil or type(Vector3) ~= 'table' then return nil end
	local okEl, x, y, z = pcall(Vector3.to_elements, v)
	if okEl and type(x) == 'number' and x == x then return x, y, z end
	return nil
end
--- Unit.debug_name(u) 形如 "#ID[82595b5d94c57343]"（小写 hex）；取不出来返回 nil。
local function idOf(unit)
	local ok, name = call(Unit.debug_name, unit)
	if ok and type(name) == 'string' then
		local hex = name:match('#ID%[([0-9a-fA-F]+)%]')
		if hex and #hex == 16 then return hex:lower() end
	end
	return nil
end

local function probeKeyboard()
	state.keyboardTried = true
	if type(Keyboard) ~= 'table' then
		emit('keyboard', { note = 'Keyboard 命名空间不存在' })
		return
	end
	emit('keyboard', {
		has_pressed = type(rawget(Keyboard, 'pressed')) == 'function',
		has_released = type(rawget(Keyboard, 'released')) == 'function',
		has_button_id = type(rawget(Keyboard, 'button_id')) == 'function',
		f8 = rawget(Keyboard, 'F8'),
	})
	local ok1, value1 = call(Keyboard.pressed, rawget(Keyboard, 'F8'))
	emit('keyboard_shape', { shape = 'pressed(F8)', ok = ok1, value = tostring(value1) })
	if type(Window) == 'table' and type(rawget(Window, 'get_main_window')) == 'function' then
		local okW, window = call(Window.get_main_window)
		if okW and window ~= nil then
			local okGet, device = call(Window.keyboard, window)
			emit('keyboard_shape', {
				shape = 'Window.keyboard(main)', ok = okGet, value = type(device),
			})
			if okGet and device ~= nil then
				local ok2, value2 = call(Keyboard.pressed, device, rawget(Keyboard, 'F8'))
				emit('keyboard_shape', { shape = 'pressed(device,F8)', ok = ok2, value = tostring(value2) })
			end
		end
	end
end

--- 运行时找 unit 用的就是 `units_by_resource(world, IdString64.from_hex(hex))`。
--- v7 已实测：只有这种键能查到（hex 字符串 / `#ID[hex]` / 大写 hex 全是 0）。
local function lookupCount(world, hex)
	if type(Id64) ~= 'table' or type(Id64.from_hex) ~= 'function' then return -1 end
	local okId, id = pcall(Id64.from_hex, hex)
	if not okId then return -2 end
	local ok, list = call(World.units_by_resource, world, id)
	if not ok or type(list) ~= 'table' then return -1 end
	local count = 0
	for _, unit in ipairs(list) do
		if alive(unit) then count = count + 1 end
	end
	return count
end

--- 全部 watchlist id 的 id64 查询（专门回答"某个 id 到底有没有实例"）。
local function reportLookups(world, label, target)
	for _, id in ipairs(WATCH_IDS) do
		emit('lookup', { label = label, unit = id, shape = 'id64', count = lookupCount(world, id) })
	end
	if target ~= nil and WATCH[target] == nil then
		emit('lookup', { label = label, unit = target, shape = 'id64', count = lookupCount(world, target) })
	end
end

--- 触发文件 → 选项。纯文本也能触发（用默认值）。
local function parseTrigger(text)
	local options = {
		target = DEFAULT_TARGET, radius = DEFAULT_RADIUS,
		hideList = { DEFAULT_HIDE }, label = text:sub(1, 40),
		show = false, nohide = false, structured = false,
	}
	for key, value in text:gmatch('([%w_]+)%s*=%s*([^;%s]+)') do
		options.structured = true
		key = key:lower()
		if key == 'id' then
			local hex = value:lower():gsub('^0x', '')
			if #hex == 16 and hex:match('^%x+$') then options.target = hex end
		elseif key == 'r' or key == 'radius' then
			local num = tonumber(value)
			if num and num >= 0.3 and num <= 12 then options.radius = num end
		elseif key == 'hide' then
			local list = {}
			for hex in value:lower():gmatch('%x+') do
				if #hex == 16 then list[#list + 1] = hex end
			end
			if #list > 0 then options.hideList = list end
		elseif key == 'show' or key == 'unhide' then
			options.show = value ~= '0' and value ~= 'false'
		elseif key == 'nohide' then
			options.nohide = value ~= '0' and value ~= 'false'
		elseif key == 'label' then
			options.label = value
		end
	end
	return options
end

--- avatar_helldiver 的全部实例（确认探针认的"玩家"是哪一个）。
local function fillAvatars(world, sweep)
	local okList, list = call(World.units_by_resource, world, AVATAR_PATH)
	if not okList or type(list) ~= 'table' then return end
	for _, unit in ipairs(list) do
		if alive(unit) and #sweep.avatars < MAX_INSTANCES then
			local count = nodeCount(unit)
			local x, y, z = posOf(unit, count)
			if x ~= nil then
				sweep.avatars[#sweep.avatars + 1] = {
					unit = idOf(unit) or 'avatar', x = x, y = y, z = z, nodes = count,
					chosen = rawequal(unit, state.avatar),
				}
			end
		end
	end
end

local function beginNextStage()
	local stage = state.queue and table.remove(state.queue, 1) or nil
	if stage == nil then
		state.sweep = nil
		state.queue = nil
		emit('done', { runs = state.runs, mode = 'ok' })
		state.nextAuto = os.time() + 20
		return
	end
	if stage == 'W' then
		local sweep = {
			stage = 'W', label = state.label, cursor = 1, scanned = 0,
			anim = {}, watchList = {}, seenAnim = {}, seenWatch = {},
			targets = {}, avatars = {},
		}
		state.sweep = sweep
		fillAvatars(state.world, sweep)
		emit('sweep_begin', {
			label = state.label, target = state.target, radius = state.radius,
			px = state.px, py = state.py, pz = state.pz, avatars = #sweep.avatars,
			hide = table.concat(state.hideList, ','),
		})
		return
	end
	if stage == 'T' then
		if #(state.targets or {}) == 0 then
			emit('target_missing', { label = state.label, target = state.target })
			beginNextStage()
			return
		end
		state.sweep = {
			stage = 'T', label = state.label, cursor = 1, scanned = 0,
			hits = {}, seen = {}, targets = state.targets,
		}
		emit('target_scan_begin', {
			label = state.label, target = state.target, radius = state.radius,
			instances = #state.targets,
		})
		return
	end
	if stage == 'H' or stage == 'U' then
		if state.home == nil then
			emit('skip', { label = state.label, stage = stage, note = '没有锚点坐标' })
			beginNextStage()
			return
		end
		if type(Unit.set_unit_visibility) ~= 'function' then
			emit('skip', { label = state.label, stage = stage, note = 'set_unit_visibility 不可用' })
			beginNextStage()
			return
		end
		state.sweep = {
			stage = stage, label = state.label, cursor = 1, scanned = 0,
			counts = {}, wanted = {}, radius = state.radius, home = state.home,
		}
		for _, hex in ipairs(state.hideList) do state.sweep.wanted[hex] = 0 end
		emit(stage == 'H' and 'hide_begin' or 'show_begin', {
			label = state.label, radius = state.radius,
			ids = table.concat(state.hideList, ','),
		})
		return
	end
	emit('skip', { label = state.label, stage = stage, note = '未知阶段' })
	beginNextStage()
end

local function finishWatchList()
	local sweep = state.sweep
	for _, entry in ipairs(sweep.anim) do
		emit('anim', {
			label = sweep.label, unit = entry.unit,
			x = entry.x, y = entry.y, z = entry.z, nodes = entry.nodes,
		})
	end
	local lines = 0
	for _, entry in ipairs(sweep.watchList) do
		if lines >= MAX_WATCH_LINES then break end
		lines = lines + 1
		if entry.nodes > 0 then
			emit('watch', {
				label = sweep.label, unit = entry.unit, nodes = entry.nodes,
				x = entry.x, y = entry.y, z = entry.z, anim = entry.anim, count = entry.count,
			})
		else
			emit('watch_zero', { label = sweep.label, unit = entry.unit, count = entry.count })
		end
	end
	emit('target_report', {
		label = sweep.label, target = state.target,
		instances = #sweep.targets, radius = state.radius,
	})
	for _, entry in ipairs(sweep.targets) do
		emit('target', { label = sweep.label, unit = entry.unit, x = entry.x, y = entry.y, z = entry.z, nodes = entry.nodes })
	end
	if #sweep.avatars > 0 then
		emit('avatar_report', { label = sweep.label, instances = #sweep.avatars })
		for _, entry in ipairs(sweep.avatars) do
			emit('avatar_unit', {
				label = sweep.label, unit = entry.unit, x = entry.x, y = entry.y, z = entry.z,
				nodes = entry.nodes, chosen = entry.chosen,
			})
		end
	end
	state.targets = sweep.targets
	if #sweep.targets > 0 then
		state.home = { x = sweep.targets[1].x, y = sweep.targets[1].y, z = sweep.targets[1].z }
	else
		state.home = nil
	end
end

local function finishTargetSweep()
	local sweep = state.sweep
	table.sort(sweep.hits, function(a, b)
		if a.dy ~= b.dy then return a.dy > b.dy end
		if a.dist ~= b.dist then return a.dist < b.dist end
		return a.unit < b.unit
	end)
	for _, entry in ipairs(sweep.hits) do
		emit('cluster', {
			label = sweep.label, unit = entry.unit,
			dx = entry.dx, dy = entry.dy, dz = entry.dz, dist = entry.dist,
			nodes = entry.nodes, anim = entry.anim, self = entry.self, watched = entry.watched,
		})
	end
	emit('target_scan_end', { label = sweep.label, target = state.target, hits = #sweep.hits })
end

local function finishHideSweep()
	local sweep = state.sweep
	local kind = sweep.stage == 'H' and 'hide' or 'show'
	local total = 0
	for _, hex in ipairs(state.hideList) do
		local count = sweep.counts[hex] or 0
		total = total + count
		emit(kind, { label = sweep.label, unit = hex, instances = count, radius = sweep.radius })
	end
	emit(kind .. '_end', { label = sweep.label, total = total, ids = #state.hideList })
end

local function stepSweep()
	local sweep = state.sweep
	local ok, units = call(World.units, state.world)
	if not ok or type(units) ~= 'table' then
		emit('scan_error', { note = 'World.units 不可用' })
		state.sweep = nil
		state.queue = nil
		return
	end
	local count = #units
	sweep.scanned = count
	local stage = sweep.stage
	local radiusSquared = (sweep.radius or state.radius) * (sweep.radius or state.radius)
	local budget = UNITS_PER_TICK
	while budget > 0 and sweep.cursor <= count do
		local unit = rawget(units, sweep.cursor)
		sweep.cursor = sweep.cursor + 1
		budget = budget - 1
		if unit ~= nil and alive(unit) then
			local id = idOf(unit)
			local nodes = nodeCount(unit)
			local x, y, z = nil, nil, nil
			if nodes > 0 then x, y, z = posOf(unit, nodes) end
			if stage == 'W' then
				local animated = false
				if x ~= nil then
					local okAnim, anim = call(Unit.has_animation_state_machine, unit)
					animated = okAnim and anim == true
					if animated and id ~= nil and #sweep.anim < MAX_ANIM and sweep.seenAnim[id] == nil then
						sweep.seenAnim[id] = true
						sweep.anim[#sweep.anim + 1] = { unit = id, x = x, y = y, z = z, nodes = nodes }
					end
					if id ~= nil and id == state.target and #sweep.targets < MAX_INSTANCES then
						sweep.targets[#sweep.targets + 1] = { unit = id, x = x, y = y, z = z, nodes = nodes }
					end
				end
				--- watchlist：**不管有没有坐标、几个节点都要记**（0 节点 = 纯材质持有者）
				if id ~= nil and WATCH[id] then
					local entry = sweep.seenWatch[id]
					if entry == nil then
						entry = { unit = id, count = 0, nodes = 0, anim = animated }
						sweep.seenWatch[id] = entry
						sweep.watchList[#sweep.watchList + 1] = entry
					end
					entry.count = entry.count + 1
					if entry.nodes == 0 and x ~= nil then
						entry.nodes, entry.x, entry.y, entry.z = nodes, x, y, z
					end
				end
			elseif stage == 'T' then
				if x ~= nil then
					local best, bestDist = nil, nil
					for _, target in ipairs(sweep.targets) do
						local dx, dy, dz = x - target.x, y - target.y, z - target.z
						local d = dx * dx + dy * dy + dz * dz
						if bestDist == nil or d < bestDist then
							bestDist, best = d, { dx = dx, dy = dy, dz = dz }
						end
					end
					if best ~= nil and bestDist <= radiusSquared then
						local hitId = id or '?'
						if #sweep.hits < MAX_CLUSTER and sweep.seen[hitId] == nil then
							sweep.seen[hitId] = true
							local okAnim, anim = call(Unit.has_animation_state_machine, unit)
							sweep.hits[#sweep.hits + 1] = {
								unit = hitId, dx = best.dx, dy = best.dy, dz = best.dz,
								dist = math.sqrt(bestDist), nodes = nodes,
								anim = okAnim and anim == true,
								self = rawequal(unit, state.avatar),
								watched = WATCH[hitId] == true,
							}
						end
					end
				end
			elseif (stage == 'H' or stage == 'U') and x ~= nil and id ~= nil then
				if sweep.wanted[id] ~= nil then
					local dx, dy, dz = x - sweep.home.x, y - sweep.home.y, z - sweep.home.z
					if dx * dx + dy * dy + dz * dz <= radiusSquared then
						local visible = stage == 'U'
						local okSet = call(Unit.set_unit_visibility, unit, visible)
						if okSet then
							sweep.counts[id] = (sweep.counts[id] or 0) + 1
						end
					end
				end
			end
		end
	end
	if sweep.cursor <= count then return end
	if stage == 'W' then
		finishWatchList()
	elseif stage == 'T' then
		finishTargetSweep()
	else
		finishHideSweep()
	end
	state.sweep = nil
	beginNextStage()
end

local function startRun(label, options)
	state.runs = state.runs + 1
	state.label = label
	state.target = options.target
	state.radius = options.radius
	state.hideList = options.hideList
	state.home = nil
	state.targets = nil
	if options.lookups then reportLookups(state.world, label, options.target) end
	state.queue = { 'W' }
	if options.show then
		state.queue[#state.queue + 1] = 'U'
	else
		state.queue[#state.queue + 1] = 'T'
		if not options.nohide then state.queue[#state.queue + 1] = 'H' end
	end
	beginNextStage()
end

local function pollTrigger(now)
	if triggerPath == nil or now == state.lastPoll then return end
	state.lastPoll = now
	if type(io) ~= 'table' or not io.open then return end
	local ok, handle = pcall(io.open, triggerPath, 'rb')
	if not ok or not handle then return end
	local text = handle:read(256)
	handle:close()
	if type(text) ~= 'string' then return end
	text = text:gsub('[\r\n]+$', '')
	if text == '' or text == state.triggerText then return end
	state.triggerText = text
	if state.sweep == nil then
		if not state.keyboardTried then probeKeyboard() end
		local options = parseTrigger(text)
		options.lookups = true
		startRun('file:' .. options.label, options)
	end
end

local function step()
	state.ticks = state.ticks + 1
	if state.done or state.ticks < 10 then return end
	local now = os.time()

	if state.phase == 'wait_avatar' then
		local ok, worlds = call(App.worlds)
		if not ok or type(worlds) ~= 'table' then return end
		for _, world in ipairs(worlds) do
			local okList, list = call(World.units_by_resource, world, AVATAR_PATH)
			if okList and type(list) == 'table' then
				for _, unit in ipairs(list) do
					if alive(unit) and nodeCount(unit) > 0 then
						local x, y, z = posOf(unit)
						if x ~= nil then
							state.world, state.avatar = world, unit
							state.px, state.py, state.pz = x, y, z
							emit('avatar', { x = x, y = y, z = z })
							state.phase = 'loop'
							state.nextAuto = now + 5
							return
						end
					end
				end
			end
		end
		return
	end

	if state.phase == 'loop' then
		if state.sweep ~= nil then
			stepSweep()
			return
		end
		pollTrigger(now)
		if state.sweep ~= nil then return end
		if now >= state.nextAuto and state.runs < 1 then
			if not state.keyboardTried then probeKeyboard() end
			--- 自动那一轮只读：不隐藏、不做 36 条 lookup（要看测试结果就按 scan-A.bat）
			local options = parseTrigger('')
			options.nohide = true
			options.lookups = false
			startRun('auto', options)
		end
		return
	end
end

local previous = rawget(_G, 'update')
local function update(...)
	if not state.done then
		local ok, err = pcall(step)
		if not ok then
			emit('error', { phase = state.phase, message = tostring(err) })
			state.done = true
		end
	end
	if type(previous) == 'function' then return previous(...) end
end
rawset(_G, 'update', update)

M.status = 'running'
emit('loaded', { version = M.version })
return M
