-- HD2-Addon: mods/hd2modmanager/kit_instance_probe
-- 按实例隔离共用部件：阶段 1 只读探针。计划见
-- docs/agent/kit-instance-isolation-plan-20261113.md
--
-- 只读：不调用任何 set_* / spawn / destroy / link，不写游戏状态。
-- 只用允许清单（hd2pbx_api.txt）里的 Application / World / Unit / Vector3。
-- 不调用 IdString64：资源 ID 取自 Unit.resource_name 的 tostring 文本（见 decode_resource）。
-- 日志走 MDL3/Bingus 加载器的 open_log，写到 Logs/KitInstanceProbe.log。
local GLOBAL_KEY = 'HD2KitInstanceProbe'
local existing = rawget(_G, GLOBAL_KEY)
if existing then return existing end

local M = { version = '0.4.0', status = 'starting' }
rawset(_G, GLOBAL_KEY, M)

local AVATAR_RESOURCE = 'content/fac_helldivers/cha_avatar/avatar_helldiver'
local SWEEP_INTERVAL = 1.0      -- 秒：两次完整扫描的最小间隔
local UNITS_PER_FRAME = 300     -- 每帧最多检查的 unit 数
local MAX_WORLDS = 32
local MAX_UNITS_PER_WORLD = 8192
-- 0.3 实机：加载器的日志约 64 KB 就截掉开头，所以自己先收紧，并尽量少写。
local MAX_LOG_BYTES = 56 * 1024
local HEARTBEAT_SECONDS = 30
local OWNER_RADIUS = 1.0         -- 米：离玩家超过它的部件不算这个玩家的（飞船上有静态展示模型）
local CLUSTER_GRID = 0.05       -- 米：按根坐标聚类的格子
local AMBIGUOUS_MARGIN = 0.3    -- 米：离最近与次近玩家的距离差小于它记为歧义

-- 由 asset-index.sqlite 的 game_data_authority_pieces 导出（kit_kind=0）。
-- FS-05 在数据里有三条记录（a/b/c），身体部件大多相同，左肩与部分胸甲不同。
local KITS = {
	FS05a = { item = 124623533, name = 'FS-05' },
	FS05b = { item = 1490803405, name = 'FS-05' },
	FS05c = { item = 3970674111, name = 'FS-05' },
	CE64 = { item = 1732669856, name = 'CE-64' },
}
local WATCH = {
	['ea846ef460cc5ba0'] = { slot = 1, kits = { 'FS05a', 'FS05b', 'FS05c', 'CE64' } },
	['3bdfa900f32cd83f'] = { slot = 2, kits = { 'FS05a', 'FS05b', 'FS05c' } },
	['4d09803a20112fe4'] = { slot = 2, kits = { 'FS05b' } },
	['7e3398302c900674'] = { slot = 2, kits = { 'CE64' } },
	['8d02a82d0c214171'] = { slot = 2, kits = { 'FS05a', 'FS05b', 'FS05c' } },
	['be65eae060a0267c'] = { slot = 2, kits = { 'FS05a', 'FS05c', 'CE64' } },
	['c6b7a26a5b255f4b'] = { slot = 2, kits = { 'CE64' } },
	['c82bcd4aaabb6cd3'] = { slot = 2, kits = { 'FS05b' } },
	['e722e122ac44cb28'] = { slot = 2, kits = { 'CE64' } },
	['f2715c19b24ecd8b'] = { slot = 2, kits = { 'FS05a', 'FS05c', 'CE64' } },
	['210fb2f60f8308ea'] = { slot = 3, kits = { 'FS05a', 'FS05b', 'FS05c', 'CE64' } },
	['418b4fb786159bac'] = { slot = 3, kits = { 'FS05a', 'FS05b', 'FS05c', 'CE64' } },
	['99f001cb5a6e7116'] = { slot = 3, kits = { 'FS05a', 'FS05b', 'FS05c', 'CE64' } },
	['d19d17bc42ab9275'] = { slot = 3, kits = { 'FS05a', 'FS05b', 'FS05c', 'CE64' } },
	['74b2e79c1c5af767'] = { slot = 4, kits = { 'FS05a', 'FS05b', 'FS05c', 'CE64' } },
	['901fc9c35857dad2'] = { slot = 4, kits = { 'FS05a', 'FS05b', 'FS05c', 'CE64' } },
	['7e5111d05840be1a'] = { slot = 5, kits = { 'FS05a', 'FS05b', 'FS05c', 'CE64' } },
	['c4c7a5de567a87fe'] = { slot = 5, kits = { 'FS05a', 'FS05b', 'FS05c', 'CE64' } },
	['23a244c877843da3'] = { slot = 6, kits = { 'FS05b' } },
	['324fb206612ce4f7'] = { slot = 6, kits = { 'FS05a', 'FS05c', 'CE64' } },
	['64a71fbe6ef75b80'] = { slot = 6, kits = { 'FS05a', 'FS05b', 'FS05c' } },
	['85dbc2391afeb51e'] = { slot = 6, kits = { 'CE64' } },
	['db487f9a3f738dc5'] = { slot = 6, kits = { 'FS05a', 'FS05c', 'CE64' } },
	['f6d665ef313b5a3c'] = { slot = 6, kits = { 'FS05b' } },
	['30204ad0fe720352'] = { slot = 7, kits = { 'FS05b' } },
	['3b2875408869343e'] = { slot = 7, kits = { 'FS05b' } },
	['b36a6f99545a6784'] = { slot = 7, kits = { 'CE64' } },
	['d0007ea2f834da58'] = { slot = 7, kits = { 'FS05a', 'FS05c', 'CE64' } },
	['e15680e4c4a89420'] = { slot = 7, kits = { 'FS05a', 'FS05b', 'FS05c' } },
	['e40aa3f8f6141a86'] = { slot = 7, kits = { 'FS05a', 'FS05c', 'CE64' } },
	['755f0bcd4d36a76d'] = { slot = 8, kits = { 'FS05b', 'FS05c' } },
	['79957cb82c5e6e9e'] = { slot = 8, kits = { 'CE64' } },
	['82595b5d94c57343'] = { slot = 8, kits = { 'FS05a' } },
	['90b57e547e7ff7bb'] = { slot = 8, kits = { 'FS05b', 'FS05c' } },
	['cef8d899ccf1a336'] = { slot = 8, kits = { 'CE64' } },
	['3469d2c00eefa9f8'] = { slot = 9, kits = { 'CE64' } },
	['99a130858b3d8ff1'] = { slot = 9, kits = { 'FS05a', 'FS05b', 'FS05c' } },
	['effdfd2386aef629'] = { slot = 9, kits = { 'FS05a', 'FS05b', 'FS05c' } },
	['f76154acfd6022fb'] = { slot = 9, kits = { 'CE64' } },
}
M.WATCH, M.KITS = WATCH, KITS

---------------------------------------------------------------------------
-- 纯函数（离线测试直接调用）
---------------------------------------------------------------------------

--- 0.3：把 Unit.resource_name 的返回值解析成 16 位小写 hex。
--- 0.2 实机采样证实：name_hash 是另一套哈希（#IDPRE_! + 8 字节，和资源 ID 对不上）；
--- resource_name 返回 IdString64 userdata，tostring 后形如
---   [IdString64 "#ID[4d1c334d294dfa97]" 0x4d1c334d294dfa97]
--- 玩家 avatar 的这一串正好等于其资源路径的哈希。tostring 是 Lua 自带函数，不调用 IdString64。
function M.decode_resource(value)
	if value == nil then return nil, 'nil' end
	if type(value) == 'string' then
		local text = value:gsub('^0[xX]', '')
		if #text == 16 and text:match('^%x+$') then return text:lower(), 'hex_text' end
		return nil, 'string_len:' .. #value
	end
	local ok, text = pcall(tostring, value)
	if not ok or type(text) ~= 'string' then return nil, 'tostring_failed' end
	local hex = text:match('#ID%[(%x+)%]') or text:match('0x(%x+)')
	if hex and #hex == 16 then return hex:lower(), 'idstring_text' end
	return nil, 'unparsed'
end

--- 一组部件（同一个玩家或同一个坐标簇）判断是哪套护甲。
--- 0.4：取「包含全部这些部件的护甲」的交集。0.3 用「标记部件优先」，
--- 结果把 14 米外静态模型的左肩算到了玩家头上，而且 FS-05 有三条记录、左肩各不相同。
function M.classify(ids)
	local candidates = nil
	for id in pairs(ids) do
		local info = WATCH[id]
		if info then
			local here = {}
			for _, k in ipairs(info.kits) do here[k] = true end
			if candidates == nil then
				candidates = here
			else
				for k in pairs(candidates) do
					if not here[k] then candidates[k] = nil end
				end
			end
		end
	end
	if candidates == nil then return 'none', 'none' end
	local found = {}
	for k in pairs(candidates) do found[#found + 1] = k end
	table.sort(found)
	if #found == 0 then return 'conflict', 'no_kit_has_all' end
	if #found == 1 then return found[1], 'exact' end
	return table.concat(found, '+'), 'ambiguous'
end

function M.cluster_key(x, y, z)
	local function q(v) return math.floor(v / CLUSTER_GRID + 0.5) end
	return string.format('%d,%d,%d', q(x), q(y), q(z))
end

---------------------------------------------------------------------------
-- JSON 行日志
---------------------------------------------------------------------------

local function quote(s)
	return '"' .. tostring(s):gsub('[%z\1-\31\\"]', function(c)
		if c == '\\' then return '\\\\' end
		if c == '"' then return '\\"' end
		return string.format('\\u%04x', c:byte())
	end) .. '"'
end

local function encode(v)
	local t = type(v)
	if t == 'nil' then return 'null' end
	if t == 'boolean' then return v and 'true' or 'false' end
	if t == 'number' then
		if v ~= v or v == math.huge or v == -math.huge then return 'null' end
		if v == math.floor(v) and math.abs(v) < 2 ^ 53 then return string.format('%d', v) end
		return string.format('%.4f', v)
	end
	if t == 'table' then
		if #v > 0 or next(v) == nil then
			local parts = {}
			for i = 1, #v do parts[i] = encode(v[i]) end
			return '[' .. table.concat(parts, ',') .. ']'
		end
		local keys = {}
		for k in pairs(v) do keys[#keys + 1] = tostring(k) end
		table.sort(keys)
		local parts = {}
		for _, k in ipairs(keys) do parts[#parts + 1] = quote(k) .. ':' .. encode(v[k]) end
		return '{' .. table.concat(parts, ',') .. '}'
	end
	return quote(tostring(v))
end
M.encode = encode

---------------------------------------------------------------------------
-- 探针本体：M.new(env) 便于离线注入假引擎
---------------------------------------------------------------------------

function M.new(env)
	local S = env.stingray
	local App, World, Unit, Vector3 = S.Application, S.World, S.Unit, S.Vector3
	local write = env.write
	local now = env.now
	local cpu = env.cpu
	local P = { errors = 0, sweeps = 0, stopped = false }
	local bytes = 0

	local function emit(kind, fields)
		if P.stopped then return end
		fields = fields or {}
		fields.kind = kind
		fields.t = now()
		local line = encode(fields) .. '\n'
		if bytes + #line > MAX_LOG_BYTES then
			P.stopped = true
			pcall(write, '{"kind":"log_cap_reached"}\n')
			return
		end
		bytes = bytes + #line
		pcall(write, line)
	end
	P.emit = emit

	local function try(f, ...)
		if type(f) ~= 'function' then return false, 'missing_function' end
		return pcall(f, ...)
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

	local function alive(unit)
		local ok, v = try(Unit.alive, unit)
		return ok and v == true
	end

	-- 一次扫描的状态
	local sweep = nil
	local nextSweepAt = 0
	local lastSignature = nil
	local lastHeartbeat = -math.huge
	local stats = { frames = 0, max_frame_ms = 0, total_ms = 0, scanned = 0,
		hash_forms = {}, sweep_seconds = 0 }

	local function startSweep()
		local ok, worlds = try(App.worlds)
		if not ok or type(worlds) ~= 'table' then
			P.errors = P.errors + 1
			emit('error', { at = 'Application.worlds' })
			return nil
		end
		local count = 0
		for i = 1, MAX_WORLDS do
			if rawget(worlds, i) == nil then break end
			count = i
		end
		return { started = now(), world = 1, cursor = 1, worldCount = count,
			hits = {}, avatarsByWorld = {}, scanned = 0, forms = {} }
	end

	-- 读某个 world 的玩家坐标（同一帧内与部件坐标配对使用）
	local function avatars(world)
		local ok, list = try(World.units_by_resource, world, AVATAR_RESOURCE)
		local result = {}
		if not ok or type(list) ~= 'table' then return result end
		for i = 1, #list do
			local unit = list[i]
			if unit ~= nil and alive(unit) then
				local x, y, z = position(unit)
				if x ~= nil then result[#result + 1] = { x = x, y = y, z = z } end
			end
		end
		return result
	end

	local function nearest(list, x, y, z)
		local best, bestD, second = nil, math.huge, math.huge
		for i, a in ipairs(list) do
			local dx, dy, dz = x - a.x, y - a.y, z - a.z
			local d = math.sqrt(dx * dx + dy * dy + dz * dz)
			if d < bestD then
				second = bestD
				best, bestD = i, d
			elseif d < second then
				second = d
			end
		end
		return best, bestD, second
	end

	-- 每帧推进：在当前 world 里检查至多 UNITS_PER_FRAME 个 unit。
	local function step()
		local okW, worlds = try(App.worlds)
		if not okW or type(worlds) ~= 'table' then return true end
		local world = rawget(worlds, sweep.world)
		if world == nil or sweep.world > sweep.worldCount then return true end
		local okU, units = try(World.units, world)
		if not okU or type(units) ~= 'table' then
			sweep.world, sweep.cursor = sweep.world + 1, 1
			return sweep.world > sweep.worldCount
		end
		local count = math.min(#units, MAX_UNITS_PER_WORLD)
		local people = nil
		local budget = UNITS_PER_FRAME
		while budget > 0 and sweep.cursor <= count do
			local unit = rawget(units, sweep.cursor)
			sweep.cursor = sweep.cursor + 1
			budget = budget - 1
			if unit ~= nil and alive(unit) then
				sweep.scanned = sweep.scanned + 1
				local okR, raw = try(Unit.resource_name, unit)
				local id, form = nil, 'call_failed'
				if okR then id, form = M.decode_resource(raw) end
				sweep.forms[form] = (sweep.forms[form] or 0) + 1
				if id ~= nil and WATCH[id] then
					local x, y, z = position(unit)
					if people == nil then people = avatars(world) end
					local hit = { id = id, world = sweep.world, slot = WATCH[id].slot }
					if x ~= nil then
						hit.x, hit.y, hit.z = x, y, z
						local idx, d, d2 = nearest(people, x, y, z)
						hit.avatar, hit.distance, hit.second = idx, d, d2
						hit.ambiguous = idx ~= nil and d2 < math.huge and (d2 - d) < AMBIGUOUS_MARGIN
					end
					local okM, meshes = try(Unit.num_meshes, unit)
					if okM then hit.meshes = meshes end
					sweep.hits[#sweep.hits + 1] = hit
					sweep.avatarsByWorld[sweep.world] = #people
				end
			end
		end
		if sweep.cursor > count then
			sweep.world, sweep.cursor = sweep.world + 1, 1
		end
		return sweep.world > sweep.worldCount
	end

	local function finishSweep()
		P.sweeps = P.sweeps + 1
		local groups, order = {}, {}
		local function add(key, hit)
			local g = groups[key]
			if g == nil then
				g = { key = key, ids = {}, max_distance = 0, ambiguous = 0, count = 0,
					x = hit.x, y = hit.y, z = hit.z }
				groups[key] = g
				order[#order + 1] = key
			end
			if not g.ids[hit.id] then g.count = g.count + 1 end
			g.ids[hit.id] = true
			if hit.distance and hit.distance < math.huge and hit.distance > g.max_distance then
				g.max_distance = hit.distance
			end
			if hit.ambiguous then g.ambiguous = g.ambiguous + 1 end
		end
		for _, hit in ipairs(sweep.hits) do
			if hit.avatar ~= nil and hit.distance <= OWNER_RADIUS then
				add(string.format('w%d/avatar%d', hit.world, hit.avatar), hit)
			elseif hit.x ~= nil then
				add(string.format('w%d/pos%s', hit.world, M.cluster_key(hit.x, hit.y, hit.z)), hit)
			else
				add(string.format('w%d/nopos', hit.world), hit)
			end
		end
		table.sort(order)
		local rows, signatureParts = {}, {}
		for _, key in ipairs(order) do
			local g = groups[key]
			local kit, basis = M.classify(g.ids)
			local ids = {}
			for id in pairs(g.ids) do ids[#ids + 1] = id end
			table.sort(ids)
			local row = { group = key, kit = kit, basis = basis, pieces = g.count,
				max_distance = g.max_distance, ambiguous = g.ambiguous }
			if g.x ~= nil then
				row.at = string.format('%.2f,%.2f,%.2f', g.x, g.y, g.z)
			end
			-- 部件 ID 只取前 8 位：日志有 64 KB 上限，8 位足够对照。
			local short = {}
			for i, id in ipairs(ids) do short[i] = id:sub(1, 8) end
			row.ids = table.concat(short, ' ')
			rows[#rows + 1] = row
			-- 签名只看「哪组是哪套、几件」，不看次序抖动，避免每秒重写。
			signatureParts[#signatureParts + 1] = key .. '=' .. kit .. '#' .. g.count
		end
		local signature = table.concat(signatureParts, ';')
		stats.sweep_seconds = now() - sweep.started
		stats.scanned = sweep.scanned
		stats.hash_forms = sweep.forms
		if signature ~= lastSignature then
			lastSignature = signature
			emit('snapshot', { sweep = P.sweeps, worlds = sweep.worldCount,
				scanned = sweep.scanned, hits = #sweep.hits, groups = rows,
				sweep_seconds = stats.sweep_seconds })
		end
		local t = now()
		if t - lastHeartbeat >= HEARTBEAT_SECONDS then
			lastHeartbeat = t
			emit('heartbeat', { sweeps = P.sweeps, errors = P.errors,
				max_frame_ms = stats.max_frame_ms, avg_frame_ms = stats.frames > 0 and stats.total_ms / stats.frames or 0,
				frames = stats.frames, scanned = stats.scanned, sweep_seconds = stats.sweep_seconds,
				hash_forms = stats.hash_forms })
			stats.max_frame_ms, stats.total_ms, stats.frames = 0, 0, 0
		end
		sweep = nil
	end

	function P.tick()
		if P.stopped then return end
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
		stats.frames = stats.frames + 1
		stats.total_ms = stats.total_ms + ms
		if ms > stats.max_frame_ms then stats.max_frame_ms = ms end
		if done then finishSweep() end
	end

	emit('start', { version = M.version, avatar_resource = AVATAR_RESOURCE,
		watch = (function() local n = 0 for _ in pairs(WATCH) do n = n + 1 end return n end)(),
		units_per_frame = UNITS_PER_FRAME, sweep_interval = SWEEP_INTERVAL })
	return P
end

---------------------------------------------------------------------------
-- 游戏内安装（离线测试时 _G.HD2_KIT_PROBE_OFFLINE = true，跳过）
---------------------------------------------------------------------------

if rawget(_G, 'HD2_KIT_PROBE_OFFLINE') then
	M.status = 'offline'
	return M
end

local loader = rawget(_G, 'CowboyBingusModLoader')
if type(loader) ~= 'table' or rawget(loader, 'api') ~= 1 or type(rawget(loader, 'open_log')) ~= 'function' then
	M.status = 'loader_unavailable'
	return M
end
local opened, file = pcall(loader.open_log, 'KitInstanceProbe.log')
if not opened or not file then
	M.status = 'log_open_failed'
	return M
end
local engine = rawget(_G, 'stingray')
if type(engine) ~= 'table' or type(engine.Application) ~= 'table' then
	M.status = 'engine_unavailable'
	pcall(file.write, file, '{"kind":"engine_unavailable"}\n')
	return M
end

local App = engine.Application
local function now()
	local ok, v = pcall(App.time_since_launch)
	return ok and type(v) == 'number' and v or 0
end
local osTable = rawget(_G, 'os')
local cpu = type(osTable) == 'table' and type(osTable.clock) == 'function' and osTable.clock or now

local probe = M.new({
	stingray = engine,
	now = now,
	cpu = cpu,
	write = function(line)
		file:write(line)
		file:flush()
	end,
})
M.probe = probe

local previous = rawget(_G, 'update')
if type(previous) ~= 'function' then
	-- 没有可串联的 update：仍然挂上，但记下来，便于判断「日志为空」是不是这个原因。
	probe.emit('no_previous_update', {})
end
local wrapper
wrapper = function(dt, ...)
	local ok, err = pcall(probe.tick)
	if not ok then
		probe.errors = probe.errors + 1
		probe.emit('tick_error', { message = tostring(err) })
		if probe.errors >= 20 then
			probe.emit('stopped_after_errors', {})
			probe.stopped = true
		end
	end
	if type(previous) == 'function' then return previous(dt, ...) end
end
rawset(_G, 'update', wrapper)
M.status = 'active'
return M
