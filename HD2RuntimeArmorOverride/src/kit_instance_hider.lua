-- HD2-Addon: mods/hd2modmanager/kit_instance_hider
-- 按实例隐藏共用部件。规划见 docs/agent/kit-instance-isolation-productization-plan-20261113.md
--
-- 规则不再写死：每 2 秒读一次管理器写出的 <base>/ArmorRegistry/kit-instance.rules（协议见规划 §2）。
-- 一条规则 = 一组「签名」（all 全部在场、none 都不在场，且都在同一个角色身上 = radius 内）
--          + 一组 hide（签名成立时，在这个角色身上隐藏的共用部件）。
-- 同一个资源被别人穿着时，因为身边没有签名，不受影响。
--
-- 只用允许清单（hd2pbx_api.txt）里的 Application / World / Unit / Vector3：
--   读：Application.worlds、Application.time_since_launch、World.units、Unit.alive、
--       Unit.resource_name、Unit.world_position、Vector3.x/y/z
--   写：Unit.set_unit_visibility（只作用于这一个实例，不改资源、不改件记录）
-- 不调用 IdString64：资源 ID 取自 resource_name 的 tostring 文本（探针 0.3 实机验证）。
-- 不保留任何 Unit 句柄跨帧：每帧重新取列表，调用前先 Unit.alive。
-- 另用 Lua 标准库 io.open：读规则文件，以及没有加载器时写日志（决策 D1）。
local GLOBAL_KEY = 'HD2KitInstanceHider'
local existing = rawget(_G, GLOBAL_KEY)
if existing then return existing end

local M = { version = '0.3.0', status = 'starting' }
rawset(_G, GLOBAL_KEY, M)

local SWEEP_INTERVAL = 0.5          -- 秒：两次完整扫描的最小间隔
local RULES_INTERVAL = 2            -- 秒：两次读规则文件的最小间隔
local UNITS_PER_FRAME = 300         -- 每帧最多检查的 unit 数（探针实测约 0.25 ms）
local MAX_WORLDS = 32
local MAX_UNITS_PER_WORLD = 8192
local MAX_LOG_BYTES = 48 * 1024     -- 加载器的日志约 64 KB 截断开头
local MAX_HIDE_ERRORS = 10          -- set_unit_visibility 连续失败这么多次就停手

-- 规则文件上限（§2；P6 S3 扩容，规划 kit-instance-mapping-deploy-plan §11.6）。每个规则文件各自适用。
-- 压测（lupa）：每帧开销与规则数、hide 长度无关，约束只有文件大小和一次性解析。
local MAX_RULES_BYTES = 2 * 1024 * 1024
local MAX_RULES = 1024
local MAX_ALL, MAX_NONE, MAX_HIDE = 8, 8, 256
local RULES_FILE = 'kit-instance.rules'
-- 软映射（低污染模式）的规则：由映射页「应用」写，与部署写的主文件互不覆盖（§11.3）。
local MAPPING_RULES_FILE = 'kit-instance.mapping.rules'

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

local function trim(text)
	return (text:gsub('^%s+', ''):gsub('%s+$', ''))
end

-- 按分隔符切开，**保留空段**（"a,,b" 要能被识别成非法）。
local function split(text, separator)
	local parts, from = {}, 1
	while true do
		local at = text:find(separator, from, true)
		if at == nil then
			parts[#parts + 1] = text:sub(from)
			return parts
		end
		parts[#parts + 1] = text:sub(from, at - 1)
		from = at + 1
	end
end

local function parse_id_list(value, minimum, maximum, field)
	if value == '' then
		if minimum > 0 then return nil, field .. ' is empty' end
		return {}
	end
	local list = {}
	for _, item in ipairs(split(value, ',')) do
		item = trim(item)
		if #item ~= 16 or not item:match('^[0-9a-f]+$') then
			return nil, field .. ' has a bad id: ' .. item
		end
		list[#list + 1] = item
	end
	if #list < minimum or #list > maximum then
		return nil, field .. ' count ' .. #list .. ' out of range'
	end
	return list
end

local function parse_rule(value, seen_names)
	local fields = {}
	for _, segment in ipairs(split(value, ';')) do
		segment = trim(segment)
		if segment ~= '' then
			local key, body = segment:match('^([%w_]+)%s*:%s*(.-)$')
			if key == nil then return nil, 'bad rule field: ' .. segment end
			if key == 'name' or key == 'radius' or key == 'all' or key == 'none' or key == 'hide' then
				if fields[key] ~= nil then return nil, 'duplicate rule field: ' .. key end
				fields[key] = trim(body)
			end
			-- 未知字段忽略（为以后加字段留余地）
		end
	end
	local name = fields.name
	if name == nil or #name < 1 or #name > 64 or not name:match('^[a-z0-9_-]+$') then
		return nil, 'bad rule name'
	end
	if seen_names[name] then return nil, 'duplicate rule name: ' .. name end
	local radius_text = fields.radius
	if radius_text == nil or not (radius_text:match('^%d+%.?%d*$') or radius_text:match('^%.%d+$')) then
		return nil, 'bad radius in ' .. name
	end
	local radius = tonumber(radius_text)
	if radius == nil or not (radius > 0 and radius <= 1.0) then return nil, 'radius out of range in ' .. name end
	if fields.all == nil or fields.hide == nil then return nil, 'missing all/hide in ' .. name end
	local all, errAll = parse_id_list(fields.all, 1, MAX_ALL, 'all')
	if all == nil then return nil, errAll .. ' in ' .. name end
	local none, errNone = parse_id_list(fields.none or '', 0, MAX_NONE, 'none')
	if none == nil then return nil, errNone .. ' in ' .. name end
	local hide, errHide = parse_id_list(fields.hide, 1, MAX_HIDE, 'hide')
	if hide == nil then return nil, errHide .. ' in ' .. name end
	seen_names[name] = true
	return { name = name, radius = radius, all = all, none = none, hide = hide }
end

--- 解析规则文本。成功返回 { revision = n, rules = { {name,radius,all,none,hide}, ... } }；
--- 任何一处不合格都返回 nil, 原因（整份无效，调用方保留上一版）。
function M.parse_rules(text)
	if type(text) ~= 'string' then return nil, 'not text' end
	if #text > MAX_RULES_BYTES then return nil, 'file too large' end
	local schema, revision = nil, nil
	local rules, seen_names = {}, {}
	for line in text:gmatch('[^\r\n]+') do
		local trimmed = trim(line)
		if trimmed ~= '' and trimmed:sub(1, 1) ~= '#' then
			local key, value = trimmed:match('^([%w_]+)%s*=%s*(.-)$')
			if key == nil then return nil, 'bad line: ' .. trimmed:sub(1, 40) end
			if key == 'schema' then
				if schema ~= nil then return nil, 'duplicate schema' end
				if value ~= '1' then return nil, 'unsupported schema ' .. value end
				schema = 1
			elseif key == 'revision' then
				if revision ~= nil then return nil, 'duplicate revision' end
				if not value:match('^%d+$') then return nil, 'bad revision' end
				revision = tonumber(value)
			elseif key == 'rule' then
				if #rules >= MAX_RULES then return nil, 'too many rules' end
				local rule, err = parse_rule(value, seen_names)
				if rule == nil then return nil, err end
				rules[#rules + 1] = rule
			end
			-- 未知键忽略
		end
	end
	if schema == nil then return nil, 'schema missing' end
	if revision == nil then return nil, 'revision missing' end
	return { revision = revision, rules = rules }
end

local function within(a, b, radius)
	local dx, dy, dz = a.x - b.x, a.y - b.y, a.z - b.z
	return dx * dx + dy * dy + dz * dz <= radius * radius
end
M.within = within

local function any_within(list, anchor, radius)
	if list == nil then return false end
	for _, item in ipairs(list) do
		if within(item, anchor, radius) then return true end
	end
	return false
end

--- 一轮扫描结束后，用本轮记录的「签名部件」位置确认锚点。
--- seen: { [worldIndex] = { [id] = { {x=,y=,z=}, ... } } }
--- 返回 { [worldIndex] = { {rule = 规则下标, x=, y=, z=}, ... } }，以及锚点总数。
function M.confirm_anchors(rules, seen)
	local confirmed, total = {}, 0
	for world, byId in pairs(seen) do
		for index, rule in ipairs(rules) do
			for _, anchor in ipairs(byId[rule.all[1]] or {}) do
				local ok = true
				for k = 2, #rule.all do
					if not any_within(byId[rule.all[k]], anchor, rule.radius) then ok = false break end
				end
				if ok then
					for _, id in ipairs(rule.none) do
						if any_within(byId[id], anchor, rule.radius) then ok = false break end
					end
				end
				if ok then
					local list = confirmed[world] or {}
					list[#list + 1] = { rule = index, x = anchor.x, y = anchor.y, z = anchor.z }
					confirmed[world] = list
					total = total + 1
				end
			end
		end
	end
	return confirmed, total
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
--   env.stingray   引擎命名空间表
--   env.now()      秒（Application.time_since_launch）
--   env.cpu()      CPU 秒，只用来量帧耗时
--   env.write(line)日志
--   env.read_rules() → 文本 | nil, 'missing' | 'error'    读规则文件（最多 64 KiB + 1 字节）
---------------------------------------------------------------------------

local MISSING = {} -- lastText 的哨兵：上一次读到的是「文件不存在」

function M.new(env)
	local S = env.stingray
	local App, World, Unit, Vector3 = S.Application, S.World, S.Unit, S.Vector3
	local now, cpu, write = env.now, env.cpu, env.write
	local read_rules = env.read_rules or function() return nil, 'missing' end
	-- 第二个规则文件（软映射写的）。没有 = 只有主文件，行为与 0.2.0 一致。
	local read_mapping_rules = env.read_mapping_rules
	local H = { stopped = false, hide_errors = 0, sweeps = 0, revision = nil }
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

	-- 规则与查找表。换规则时整体重建，并丢掉进行中的扫描与已确认的锚点。
	local rules = {}
	local TRACK, HIDES = {}, {}      -- id → true；id → { [规则下标] = true }
	local confirmed, anchorCount = {}, 0
	local lastText = nil
	local nextRulesAt = 0
	local sweep, nextSweepAt = nil, 0
	local lastSummary = nil
	local stats = { max_frame_ms = 0 }

	local function install_rules(list, revision)
		rules = list
		H.revision = revision
		TRACK, HIDES = {}, {}
		for index, rule in ipairs(rules) do
			for _, id in ipairs(rule.all) do TRACK[id] = true end
			for _, id in ipairs(rule.none) do TRACK[id] = true end
			for _, id in ipairs(rule.hide) do
				HIDES[id] = HIDES[id] or {}
				HIDES[id][index] = true
			end
		end
		confirmed, anchorCount = {}, 0
		sweep = nil
		lastSummary = nil
	end

	-- 规则来源：主文件（部署写）和可选的映射文件（映射页写）。各自解析、各自保留上一版；
	-- 两份规则**互相独立、取并集**：hide 只增不减，不存在谁覆盖谁；同名、同签名都照样各自生效。
	local sources = { { file = 'rules', read = read_rules, last = nil, parsed = nil } }
	if read_mapping_rules then
		sources[2] = { file = 'mapping', read = read_mapping_rules, last = nil, parsed = nil }
	end

	-- 返回这个来源这一轮有没有变化。
	local function poll(src)
		local text, status = src.read()
		if text == nil then
			if status == 'missing' and src.last ~= MISSING then
				-- 文件不存在 = 没有规则（已经藏掉的不恢复，见规划 §1）。
				src.last = MISSING
				src.parsed = nil
				return true
			end
			-- 其它读取失败（被写入方占着等）：保留上一版，下一个周期再试。
			return false
		end
		if text == src.last then return false end
		src.last = text
		local parsed, err = M.parse_rules(text)
		if parsed == nil then
			-- 同一份坏文本只记一次（last 已经是它）。保留上一版。
			emit('rules_error', { reason = err, file = src.file })
			return false
		end
		src.parsed = parsed
		return true
	end

	local function reload_rules()
		local changed = false
		for _, src in ipairs(sources) do
			if poll(src) then changed = true end
		end
		if not changed then return end
		local list, mappingCount = {}, 0
		local primary = sources[1].parsed
		for i, src in ipairs(sources) do
			if src.parsed ~= nil then
				for _, rule in ipairs(src.parsed.rules) do
					rule.source = src.file
					list[#list + 1] = rule
				end
				if i == 2 then mappingCount = #src.parsed.rules end
			end
		end
		install_rules(list, primary and primary.revision or nil)
		local fields = { revision = primary and primary.revision or -1, rules = #rules }
		if sources[2] then
			fields.mapping_rules = mappingCount
			fields.mapping_revision = sources[2].parsed and sources[2].parsed.revision or -1
		end
		if #rules == 0 and sources[1].last == MISSING and (sources[2] == nil or sources[2].last == MISSING) then
			fields.note = 'file missing'
		end
		emit('rules', fields)
	end
	H.reload_rules = reload_rules
	function H.rules() return rules end

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
		return { world = 1, cursor = 1, worldCount = count, seen = {}, hidden = 0, missed = 0 }
	end

	local function near_anchor(world, ruleSet, x, y, z)
		local here = { x = x, y = y, z = z }
		for _, anchor in ipairs(confirmed[world] or {}) do
			if ruleSet[anchor.rule] and within(here, anchor, rules[anchor.rule].radius) then return true end
		end
		return false
	end

	local function record(world, id, x, y, z)
		local byId = sweep.seen[world]
		if byId == nil then byId = {}; sweep.seen[world] = byId end
		local list = byId[id]
		if list == nil then list = {}; byId[id] = list end
		list[#list + 1] = { x = x, y = y, z = z }
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
					local tracked, hideSet = TRACK[id], HIDES[id]
					if tracked or hideSet then
						local x, y, z = position(unit)
						if x ~= nil then
							if tracked then record(sweep.world, id, x, y, z) end
							if hideSet then
								-- 只用**上一轮**确认的锚点：本轮的签名要等扫描结束才算得出来。
								if near_anchor(sweep.world, hideSet, x, y, z) then
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
		confirmed, anchorCount = M.confirm_anchors(rules, sweep.seen)
		-- missed 是「同 ID 但不在已确认锚点旁」的实例数：玩家或别的 NPC 穿着同款，正常不隐藏。
		-- 只有 markers / hidden / revision 变化才写日志；missed 抖动不算。
		-- 哪几条规则的签名成立了（实机回报用）：规则名按下标去重，最多列 12 条。
		local hitNames, hitSeen, hitCount = {}, {}, 0
		for _, list in pairs(confirmed) do
			for _, anchor in ipairs(list) do
				if not hitSeen[anchor.rule] then
					hitSeen[anchor.rule] = true
					hitCount = hitCount + 1
					if #hitNames < 12 then hitNames[#hitNames + 1] = rules[anchor.rule].name end
				end
			end
		end
		table.sort(hitNames)
		local hit = table.concat(hitNames, ',')
		local summary = string.format('%d/%d/%s/%s', anchorCount, sweep.hidden, tostring(H.revision), hit)
		if summary ~= lastSummary then
			lastSummary = summary
			emit('sweep', { sweep = H.sweeps, markers = anchorCount, hidden = sweep.hidden,
				left_visible = sweep.missed, revision = H.revision or -1, worlds = sweep.worldCount,
				max_frame_ms = stats.max_frame_ms, rules_hit = hit, rules_hit_count = hitCount })
			stats.max_frame_ms = 0
		end
		sweep = nil
	end

	function H.tick()
		if H.stopped then return end
		local t = now()
		if t >= nextRulesAt then
			nextRulesAt = t + RULES_INTERVAL
			reload_rules()
		end
		if #rules == 0 then return end
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

	emit('start', { version = M.version })
	return H
end

---------------------------------------------------------------------------
-- 游戏内安装。g 是全局表（测试里传假的）。
---------------------------------------------------------------------------

-- 与 armor_override_runtime.lua 的 base_directory() 同一套求法。
local function base_directory(g)
	local loader = rawget(g, 'CowboyBingusModLoader')
	if type(loader) == 'table' and type(loader.log_directory) == 'string' then
		return (loader.log_directory:gsub('[/\\]Logs$', ''))
	end
	local osTable = rawget(g, 'os')
	local local_app_data = type(osTable) == 'table' and type(osTable.getenv) == 'function'
		and osTable.getenv('LOCALAPPDATA') or nil
	if type(local_app_data) == 'string' and local_app_data ~= '' then
		return local_app_data .. '/CowboyBingus/Helldivers2'
	end
	return nil
end
M.base_directory = base_directory

function M.install(g)
	local engine = rawget(g, 'stingray')
	if type(engine) ~= 'table' or type(engine.Application) ~= 'table' then
		M.status = 'engine_unavailable'
		return M
	end
	local ioTable = rawget(g, 'io')
	if type(ioTable) ~= 'table' or type(ioTable.open) ~= 'function' then
		M.status = 'io_unavailable'
		return M
	end
	local base = base_directory(g)
	if base == nil then
		M.status = 'no_base_directory'
		return M
	end
	local rules_path = base .. '/ArmorRegistry/' .. RULES_FILE
	local mapping_rules_path = base .. '/ArmorRegistry/' .. MAPPING_RULES_FILE

	-- 日志：优先用加载器的 open_log；没有加载器（独占 boot）就自己开 <base>/Logs/KitInstanceHider.log。
	local write = nil
	local loader = rawget(g, 'CowboyBingusModLoader')
	if type(loader) == 'table' and rawget(loader, 'api') == 1 and type(rawget(loader, 'open_log')) == 'function' then
		local opened, file = pcall(loader.open_log, 'KitInstanceHider.log')
		if opened and file then
			write = function(line)
				file:write(line)
				file:flush()
			end
		end
	end
	if write == nil then
		local log_path = base .. '/Logs/KitInstanceHider.log'
		write = function(line)
			local ok, file = pcall(ioTable.open, log_path, 'ab')
			if ok and file then
				pcall(file.write, file, line)
				pcall(file.close, file)
			end
		end
	end

	local App = engine.Application
	local function now()
		local ok, v = pcall(App.time_since_launch)
		return ok and type(v) == 'number' and v or 0
	end
	local osTable = rawget(g, 'os')
	local cpu = type(osTable) == 'table' and type(osTable.clock) == 'function' and osTable.clock or now

	local function reader(path)
		return function()
			local ok, file, message, errno = pcall(ioTable.open, path, 'rb')
			if not ok or not file then
				-- ENOENT = 文件不存在 = 没有规则；别的失败（被占用等）保留上一版。
				if ok and (errno == 2 or (type(message) == 'string' and message:find('No such file', 1, true))) then
					return nil, 'missing'
				end
				return nil, 'error'
			end
			local okRead, text = pcall(file.read, file, MAX_RULES_BYTES + 1)
			pcall(file.close, file)
			if not okRead then return nil, 'error' end
			if text == nil then return '' end -- 空文件：读出 nil，按空文本走解析（会被判为无效并保留上一版）
			return text
		end
	end

	local hider = M.new({
		stingray = engine, now = now, cpu = cpu, write = write,
		read_rules = reader(rules_path), read_mapping_rules = reader(mapping_rules_path),
	})
	M.hider = hider

	local previous = rawget(g, 'update')
	local errors = 0
	local hook = function(dt, ...)
		local ok, err = pcall(hider.tick)
		if not ok then
			errors = errors + 1
			hider.emit('tick_error', { message = tostring(err) })
			if errors >= 20 then hider.stopped = true end
		end
		if type(previous) == 'function' then return previous(dt, ...) end
	end
	-- 记下装上去的钩子：armor_override_runtime.lua 靠它认出「前一个 update 是 Hider」并接着调用。
	M.update_hook = hook
	rawset(g, 'update', hook)
	M.status = 'active'
	return M
end

-- 离线测试时 _G.HD2_KIT_HIDER_OFFLINE = true，跳过自动安装。
if rawget(_G, 'HD2_KIT_HIDER_OFFLINE') then
	M.status = 'offline'
	return M
end

return M.install(_G)
