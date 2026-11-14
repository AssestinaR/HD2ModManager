-- 离线测试：kit_instance_hider.lua 0.3.0（假引擎，不需要游戏）。
-- 运行：luajit HD2RuntimeArmorOverride/tests/test_kit_instance_hider.lua <src>
-- 没有 luajit 时：python -I HD2RuntimeArmorOverride/scripts/run_lua_test.py <pylib> <本文件> <src>
rawset(_G, 'HD2_KIT_HIDER_OFFLINE', true)
local path = (arg and arg[1]) or 'HD2RuntimeArmorOverride/src/kit_instance_hider.lua'
local M = dofile(path)
assert(M.status == 'offline', 'offline guard')
assert(M.version == '0.3.0', 'version')

local MARKER = '82595b5d94c57343'
local CAPE = 'ea846ef460cc5ba0'
local LEG = '74b2e79c1c5af767'      -- 民主官与玩家神射手、CE-64 共用
local ARM = 'e15680e4c4a89420'
local PLAYER_SHOULDER = '90b57e547e7ff7bb'

-- 0.1.0 里写死的那条规则，现在由规则文本表达（与 C# 编译器输出同一格式）。
local OFFICER_HIDE = {
	'8d02a82d0c214171', '3bdfa900f32cd83f', 'f2715c19b24ecd8b', 'be65eae060a0267c',
	'210fb2f60f8308ea', 'd19d17bc42ab9275', '99f001cb5a6e7116', '418b4fb786159bac',
	'74b2e79c1c5af767', '901fc9c35857dad2',
	'c4c7a5de567a87fe', '7e5111d05840be1a',
	'db487f9a3f738dc5', '324fb206612ce4f7', '64a71fbe6ef75b80',
	'e40aa3f8f6141a86', 'd0007ea2f834da58', 'e15680e4c4a89420',
	'effdfd2386aef629', '99a130858b3d8ff1',
}
assert(#OFFICER_HIDE == 20)
for _, id in ipairs(OFFICER_HIDE) do assert(id ~= CAPE and id ~= MARKER) end

local function rule_line(name, radius, all, none, hide)
	return 'rule=name:' .. name .. ';radius:' .. radius .. ';all:' .. table.concat(all, ',')
		.. ';none:' .. table.concat(none, ',') .. ';hide:' .. table.concat(hide, ',')
end
local function rules_text(revision, ...)
	local lines = { 'schema=1', 'revision=' .. revision }
	for _, l in ipairs({ ... }) do lines[#lines + 1] = l end
	return table.concat(lines, '\n') .. '\n'
end
local OFFICER_RULE = rule_line('aemeath_democracy_officer', '0.30', { MARKER }, {}, OFFICER_HIDE)

-- 纯函数 -------------------------------------------------------------------
local obj = setmetatable({}, { __tostring = function() return '[IdString64 "#ID[82595B5D94C57343]" 0x82595b5d94c57343]' end })
assert(M.decode_resource(obj) == MARKER)
assert(M.decode_resource('nope') == nil)
assert(M.decode_resource(nil) == nil)

local unpack = table.unpack or unpack

-- 解析：正常 ----------------------------------------------------------------
local parsed = assert(M.parse_rules(rules_text(7, OFFICER_RULE)))
assert(parsed.revision == 7 and #parsed.rules == 1)
local r1 = parsed.rules[1]
assert(r1.name == 'aemeath_democracy_officer' and r1.radius == 0.3)
assert(#r1.all == 1 and r1.all[1] == MARKER and #r1.none == 0 and #r1.hide == 20)
-- 空规则、注释、空行、CRLF、未知键、未知规则字段
local empty = assert(M.parse_rules('schema=1\r\nrevision=0\r\n'))
assert(empty.revision == 0 and #empty.rules == 0)
local noisy = assert(M.parse_rules('# 注释\n\nschema=1\nfuture=whatever\nrevision=3\r\n'
	.. 'rule=name:a;radius:1;all:' .. MARKER .. ';hide:' .. LEG .. ';extra:zzz\n'))
assert(noisy.revision == 3 and #noisy.rules == 1 and noisy.rules[1].radius == 1)
-- 边界：8 个 all、8 个 none、256 个 hide、1024 条规则（P6 S3 扩容）
local function ids(n, offset)
	local list = {}
	for i = 1, n do list[i] = string.format('%016x', offset + i) end
	return list
end
assert(M.parse_rules(rules_text(1, rule_line('x', '0.5', ids(8, 0), ids(8, 100), ids(256, 200)))))
local many = {}
for i = 1, 1024 do many[i] = rule_line('r' .. i, '0.3', { MARKER }, {}, { LEG }) end
assert(M.parse_rules(rules_text(1, unpack(many))))

-- 解析：非法一律整份无效 ----------------------------------------------------
local function bad(text, why)
	local result, err = M.parse_rules(text)
	assert(result == nil, 'should be invalid: ' .. why)
	assert(type(err) == 'string' and #err > 0, 'reason expected: ' .. why)
end
bad('revision=1\n', 'schema missing')
bad('schema=1\n', 'revision missing')
bad('schema=2\nrevision=1\n', 'schema 2')
bad('schema=1\nrevision=-1\n', 'negative revision')
bad('schema=1\nrevision=abc\n', 'revision not a number')
bad('schema=1\nrevision=1\nrevision=2\n', 'duplicate revision')
bad('schema=1\nschema=1\nrevision=1\n', 'duplicate schema')
bad('schema=1\nrevision=1\ngarbage line\n', 'line without =')
bad(rules_text(1, rule_line('Bad Name', '0.3', { MARKER }, {}, { LEG })), 'name with uppercase/space')
bad(rules_text(1, rule_line(string.rep('a', 65), '0.3', { MARKER }, {}, { LEG })), 'name too long')
bad(rules_text(1, rule_line('a', '0', { MARKER }, {}, { LEG })), 'radius 0')
bad(rules_text(1, rule_line('a', '1.01', { MARKER }, {}, { LEG })), 'radius > 1')
bad(rules_text(1, rule_line('a', '0x1', { MARKER }, {}, { LEG })), 'radius hex')
bad(rules_text(1, rule_line('a', 'abc', { MARKER }, {}, { LEG })), 'radius text')
bad(rules_text(1, rule_line('a', '0.3', {}, {}, { LEG })), 'all empty')
bad(rules_text(1, rule_line('a', '0.3', ids(9, 0), {}, { LEG })), 'all > 8')
bad(rules_text(1, rule_line('a', '0.3', { MARKER }, ids(9, 100), { LEG })), 'none > 8')
bad(rules_text(1, rule_line('a', '0.3', { MARKER }, {}, {})), 'hide empty')
bad(rules_text(1, rule_line('a', '0.3', { MARKER }, {}, ids(257, 200))), 'hide > 256')
bad(rules_text(1, rule_line('a', '0.3', { 'ABCDEF0123456789' }, {}, { LEG })), 'uppercase id')
bad(rules_text(1, rule_line('a', '0.3', { '1234' }, {}, { LEG })), 'short id')
bad(rules_text(1, rule_line('a', '0.3', { 'zzzzzzzzzzzzzzzz' }, {}, { LEG })), 'non-hex id')
bad(rules_text(1, rule_line('a', '0.3', { MARKER }, {}, { LEG }), rule_line('a', '0.3', { MARKER }, {}, { LEG })),
	'duplicate name')
bad(rules_text(1, 'rule=name:a;radius:0.3;all:' .. MARKER), 'missing hide')
bad(rules_text(1, 'rule=name:a;radius:0.3;radius:0.4;all:' .. MARKER .. ';hide:' .. LEG), 'duplicate field')
local tooMany = {}
for i = 1, 1025 do tooMany[i] = rule_line('r' .. i, '0.3', { MARKER }, {}, { LEG }) end
bad(rules_text(1, unpack(tooMany)), '1025 rules')
bad('schema=1\nrevision=1\n# ' .. string.rep('x', 2 * 1024 * 1024) .. '\n', 'over 2 MiB')
assert(M.parse_rules('schema=1\nrevision=1\n# ' .. string.rep('x', 70000) .. '\n'), 'a 64 KiB+ file is fine now')

-- 假引擎 --------------------------------------------------------------------
local function unit(id, x, y, z, opts)
	return { id = id, x = x, y = y, z = z, alive = not (opts and opts.dead), visible = true }
end
local function make_world_state()
	-- world1 = 飞船：玩家在原点穿神射手（与民主官共用腿和手臂），民主官在 14 米外
	local player = { unit(LEG, 0, 0, 0), unit(ARM, 0, 0, 0), unit(PLAYER_SHOULDER, 0, 0, 0), unit(CAPE, 0, 0, 0) }
	-- 民主官：腿排在标记前面
	local officer = { unit(LEG, 1.47, 13.87, 19.57), unit(MARKER, 1.47, 13.87, 19.57),
		unit(ARM, 1.48, 13.87, 19.57), unit(CAPE, 1.47, 13.87, 19.57) }
	local deadLeg = unit(LEG, 1.47, 13.87, 19.57, { dead = true })
	local w1 = {}
	for _, u in ipairs(player) do w1[#w1 + 1] = u end
	for _, u in ipairs(officer) do w1[#w1 + 1] = u end
	w1[#w1 + 1] = deadLeg
	-- world2 = 军械库预览：只有玩家的神射手，没有标记
	local preview = { unit(LEG, 0, 0, -0.93), unit(ARM, 0, 0, -0.93) }
	return { player = player, officer = officer, w1 = w1, preview = preview,
		worlds = { { units = w1 }, { units = preview } } }
end

local state = make_world_state()
local failVisibility = false
local visibilityCalls = 0
local function make_stingray(getWorlds)
	local S = {
		Application = { worlds = function() return getWorlds() end,
			time_since_launch = function() return 0 end },
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
	-- 允许清单之外的调用一律报错（包括 IdString64）
	for _, ns in ipairs({ 'Unit', 'World', 'Application', 'Vector3' }) do
		setmetatable(S[ns], { __index = function(_, k) error('unexpected ' .. ns .. '.' .. k) end })
	end
	setmetatable(S, { __index = function(_, k) error('unexpected stingray.' .. k) end })
	return S
end
local S = make_stingray(function() return state.worlds end)

local clock, lines = 0, {}
local rulesText, rulesStatus = nil, nil   -- 假文件：rulesText=nil + rulesStatus='missing'/'error'
local reads = 0
local hider = M.new({
	stingray = S,
	now = function() return clock end,
	cpu = function() return clock end,
	write = function(line) lines[#lines + 1] = line end,
	read_rules = function()
		reads = reads + 1
		return rulesText, rulesStatus
	end,
})

local function find_lines(needle)
	local n = 0
	for _, l in ipairs(lines) do if l:find(needle, 1, true) then n = n + 1 end end
	return n
end
-- 每轮时钟走 2 秒（>= 规则文件的 2 秒读取间隔，也 > 0.5 秒扫描间隔）：每轮都会先重读规则。
local function run_sweep()
	clock = clock + 2
	for _ = 1, 10 do hider.tick() end
end

-- 文件不存在 = 没有规则：什么都不藏，只记一行
rulesText, rulesStatus = nil, 'missing'
run_sweep(); run_sweep(); run_sweep()
for _, u in ipairs(state.officer) do assert(u.visible == true) end
assert(find_lines('"kind":"rules"') == 1, 'missing file logged once')

-- 民主官规则出现 -------------------------------------------------------------
rulesText, rulesStatus = rules_text(7, OFFICER_RULE), nil
run_sweep()   -- 读到规则；本轮记录签名部件（锚点要等轮末才确认）
for _, u in ipairs(state.officer) do assert(u.visible == true, 'nothing hidden before an anchor is confirmed') end
run_sweep()   -- 上一轮确认了锚点：腿和手臂这轮才藏
assert(state.officer[1].visible == false, 'leg hidden on second sweep')
assert(state.officer[3].visible == false, 'arm hidden on second sweep')
assert(state.officer[2].visible == true, 'marker (Aemeath) must stay visible')
assert(state.officer[4].visible == true, 'cape must stay visible')
for _, u in ipairs(state.player) do assert(u.visible == true, 'player piece hidden: ' .. u.id) end
for _, u in ipairs(state.preview) do assert(u.visible == true, 'preview piece hidden: ' .. u.id) end

-- 日志：规则行 + sweep 行（markers=1、hidden=2、revision=7）
local found = false
for _, l in ipairs(lines) do
	if l:find('"kind":"sweep"', 1, true) and l:find('"hidden":2', 1, true)
		and l:find('"markers":1', 1, true) and l:find('"revision":7', 1, true)
		and l:find('"left_visible":4', 1, true) then found = true end
end
assert(found, table.concat(lines))
assert(find_lines('"kind":"rules"') == 2 and find_lines('"revision":7') >= 1)

-- 结果不变不重复写
local n = #lines
run_sweep(); run_sweep(); run_sweep()
assert(#lines == n, 'unchanged sweeps must not log: ' .. table.concat(lines, '', n + 1))
-- left_visible 抖动（别处多一个同款部件）也不写
state.preview[#state.preview + 1] = unit(LEG, 0, 0, -0.93)
state.worlds[2].units = state.preview
run_sweep(); run_sweep()
assert(#lines == n, 'left_visible change must not log')
state.preview[#state.preview] = nil

-- 坏文件：保留上一版，同一份坏文本只记一次 -----------------------------------
rulesText = 'schema=1\nrevision=8\nrule=name:BAD;radius:0.3\n'
local respawn = unit(LEG, 1.47, 13.87, 19.57)
state.w1[#state.w1 + 1] = respawn
run_sweep(); run_sweep(); run_sweep()
assert(find_lines('"kind":"rules_error"') == 1, 'bad text logged once, got ' .. find_lines('"kind":"rules_error"'))
assert(respawn.visible == false, 'previous version stays in force after a bad file')
run_sweep(); run_sweep(); run_sweep(); run_sweep()
assert(find_lines('"kind":"rules_error"') == 1, 'same bad text must not be logged again')
-- 另一份不同的坏文本再记一次
rulesText = 'schema=2\nrevision=9\n'
run_sweep(); run_sweep(); run_sweep()
assert(find_lines('"kind":"rules_error"') == 2)
-- 读失败（被占用等）：同样保留上一版，且不记坏文件
rulesText, rulesStatus = nil, 'error'
respawn.visible = true
run_sweep(); run_sweep(); run_sweep()
assert(find_lines('"kind":"rules_error"') == 2)
assert(respawn.visible == false, 'read error keeps previous rules in force')

-- revision 变化：重新载入并写日志 ---------------------------------------------
rulesText, rulesStatus = rules_text(9, OFFICER_RULE), nil
run_sweep(); run_sweep(); run_sweep()
local sawRev9 = false
for _, l in ipairs(lines) do
	if l:find('"kind":"sweep"', 1, true) and l:find('"revision":9', 1, true) then sawRev9 = true end
end
assert(sawRev9, 'revision change must be logged')

-- 空规则：之后不再隐藏；已经藏掉的不恢复 --------------------------------------
rulesText = rules_text(10)
run_sweep(); run_sweep()
local fresh = unit(LEG, 1.47, 13.87, 19.57)
state.w1[#state.w1 + 1] = fresh
run_sweep(); run_sweep(); run_sweep()
assert(fresh.visible == true, 'empty rules must not hide anything')
assert(state.officer[1].visible == false, 'already hidden units are not restored')
local callsBefore = visibilityCalls
run_sweep(); run_sweep()
assert(visibilityCalls == callsBefore, 'no rules ⇒ no engine calls')

-- 规则文件消失：同样没有规则 -------------------------------------------------
rulesText = rules_text(11, OFFICER_RULE)
run_sweep(); run_sweep(); run_sweep()
local fresh2 = unit(LEG, 1.47, 13.87, 19.57)
state.w1[#state.w1 + 1] = fresh2
run_sweep(); run_sweep()
assert(fresh2.visible == false, 'rules back in force')
rulesText, rulesStatus = nil, 'missing'
run_sweep(); run_sweep()
local fresh3 = unit(LEG, 1.47, 13.87, 19.57)
state.w1[#state.w1 + 1] = fresh3
run_sweep(); run_sweep(); run_sweep()
assert(fresh3.visible == true, 'missing file ⇒ no rules')
rulesText, rulesStatus = nil, nil

-- 标记消失 ⇒ 之后新生成的腿不再被藏 --------------------------------------------
rulesText = rules_text(12, OFFICER_RULE)
run_sweep(); run_sweep()
state.officer[2].alive = false
run_sweep()   -- 这一轮仍用上一轮确认的锚点
local late = unit(LEG, 1.47, 13.87, 19.57)
state.w1[#state.w1 + 1] = late
run_sweep(); run_sweep()
assert(late.visible == true, 'no marker ⇒ no hiding')
state.officer[2].alive = true

-- 多件 all + none -------------------------------------------------------------
do
	local A, B, C = 'aaaaaaaaaaaaaaaa', 'bbbbbbbbbbbbbbbb', 'cccccccccccccccc'
	local H1, H2 = '1111111111111111', '2222222222222222'
	local u = {
		-- 角色一：A + B 都在 ⇒ 签名成立
		unit(A, 0, 0, 0), unit(B, 0.1, 0, 0), unit(H1, 0.05, 0, 0),
		-- 角色二：只有 A ⇒ 不成立
		unit(A, 10, 0, 0), unit(H1, 10, 0, 0),
		-- 角色三：A + B + C ⇒ none 命中，不成立
		unit(A, 20, 0, 0), unit(B, 20, 0, 0), unit(C, 20.1, 0, 0), unit(H1, 20, 0, 0),
		-- 角色四：A 在，B 在半径外 ⇒ 不成立
		unit(A, 30, 0, 0), unit(B, 31, 0, 0), unit(H1, 30, 0, 0),
		-- 角色五：A + B，且 C 在半径外（不算 none 命中）⇒ 成立
		unit(A, 40, 0, 0), unit(B, 40, 0, 0), unit(C, 45, 0, 0), unit(H1, 40, 0, 0), unit(H2, 40.1, 0, 0),
	}
	local localState = { worlds = { { units = u } } }
	local Slocal = make_stingray(function() return localState.worlds end)
	local text = rules_text(1, rule_line('multi', '0.5', { A, B }, { C }, { H1, H2 }))
	local h = M.new({ stingray = Slocal, now = function() return clock end, cpu = function() return clock end,
		write = function() end, read_rules = function() return text end })
	clock = 100
	for _ = 1, 3 do
		clock = clock + 1
		for _ = 1, 10 do h.tick() end
	end
	local function vis(i) return u[i].visible end
	assert(vis(3) == false, 'cluster 1 hide piece')
	assert(vis(5) == true and vis(9) == true and vis(12) == true, 'clusters 2-4 must stay visible')
	assert(vis(16) == false and vis(17) == false, 'cluster 5 hides both')
	assert(vis(1) and vis(2) and vis(4), 'signature pieces stay visible')
end

-- set_unit_visibility 连续失败 ⇒ 停手并记一行 ----------------------------------
rulesText = rules_text(13, OFFICER_RULE)
state.officer[2].alive = true
failVisibility = true
for _ = 1, 8 do run_sweep() end
assert(find_lines('hide_disabled') >= 1, 'hide_disabled not logged')
local before = visibilityCalls
run_sweep()
assert(visibilityCalls == before, 'must stop calling after repeated failures')
failVisibility = false

-- 日志上限 48 KiB ---------------------------------------------------------------
do
	local logged, count = 0, 0
	local h = M.new({ stingray = make_stingray(function() return {} end), now = function() return 0 end,
		cpu = function() return 0 end,
		write = function(line) logged = logged + #line; count = count + 1 end,
		read_rules = function() return nil, 'missing' end })
	for i = 1, 5000 do h.emit('spam', { i = i, text = string.rep('x', 40) }) end
	assert(logged <= 48 * 1024 and count > 0, 'log cap: ' .. logged)
end

-- 游戏内安装：无加载器时用 io 写日志；有加载器时用 open_log ---------------------
do
	local function fake_io(files)
		local writes = {}
		return { open = function(p, mode)
			if mode == 'ab' then
				writes[#writes + 1] = { path = p, mode = mode, lines = {} }
				local entry = writes[#writes]
				return { write = function(_, s) entry.lines[#entry.lines + 1] = s; return true end,
					close = function() entry.closed = true end }
			end
			local text = files[p]
			if text == nil then return nil, p .. ': No such file or directory', 2 end
			return { read = function(_, count) assert(count == 2 * 1024 * 1024 + 1, 'read limit'); return text end,
				close = function() end }
		end }, writes
	end
	local t = 0
	local previousCalls = 0
	local worlds = make_world_state()
	local Sg = make_stingray(function() return worlds.worlds end)
	Sg.Application.time_since_launch = function() return t end

	-- A. 没有加载器：LOCALAPPDATA 求 base
	local rulesPath = 'C:/L/CowboyBingus/Helldivers2/ArmorRegistry/kit-instance.rules'
	local ioFake, writes = fake_io({ [rulesPath] = rules_text(5, OFFICER_RULE) })
	local g = { stingray = Sg, io = ioFake, os = { getenv = function(k) assert(k == 'LOCALAPPDATA'); return 'C:/L' end,
		clock = function() return t end },
		update = function() previousCalls = previousCalls + 1 end }
	setmetatable(g, { __index = function(_, k) if k == 'CowboyBingusModLoader' then return nil end end })
	assert(M.install(g).status == 'active')
	for step = 1, 12 do t = t + 1; g.update(0.016) end
	assert(previousCalls == 12, 'previous update must still run')
	assert(#writes > 0, 'io log fallback must write')
	for _, w in ipairs(writes) do
		assert(w.path == 'C:/L/CowboyBingus/Helldivers2/Logs/KitInstanceHider.log', w.path)
		assert(w.closed, 'log handle closed')
	end
	assert(worlds.officer[1].visible == false, 'rules read through io drive the hiding')

	-- B. 有加载器：base 取 log_directory 上一级，日志走 open_log
	local opened, logLines = {}, {}
	local loader = { api = 1, log_directory = 'D:/X/Logs',
		open_log = function(name) opened[#opened + 1] = name
			return { write = function(_, s) logLines[#logLines + 1] = s end, flush = function() end } end }
	local ioFake2, writes2 = fake_io({ ['D:/X/ArmorRegistry/kit-instance.rules'] = rules_text(1) })
	local g2 = { stingray = Sg, io = ioFake2, os = { clock = function() return t end }, CowboyBingusModLoader = loader }
	setmetatable(g2, { __index = function() return nil end })
	assert(M.install(g2).status == 'active')
	for _ = 1, 4 do t = t + 1; g2.update(0.016) end
	assert(opened[1] == 'KitInstanceHider.log' and #logLines > 0, 'loader open_log used')
	assert(#writes2 == 0, 'no io log when the loader provides one')

	-- C. 缺条件：不安装、不报错
	local g3 = { stingray = Sg, os = { getenv = function() return 'C:/L' end } }
	assert(M.install(g3).status == 'io_unavailable')
	local g4 = { io = ioFake, os = {} }
	assert(M.install(g4).status == 'engine_unavailable')
	local g5 = { stingray = Sg, io = ioFake, os = { getenv = function() return nil end } }
	assert(M.install(g5).status == 'no_base_directory')
end

-- 两个规则文件（主文件 + 软映射文件）：并集、互相独立、一份坏不影响另一份 ------------------------
do
	local A, B = 'aaaaaaaaaaaaaaaa', 'bbbbbbbbbbbbbbbb'
	local H1, H2, H3 = '1111111111111111', '2222222222222222', '3333333333333333'
	local t0 = 1000
	local function build()
		-- 角色一：签名 A；角色二：签名 B。两个角色身边都有 H1、H2、H3。
		local u = {
			unit(A, 0, 0, 0), unit(H1, 0.05, 0, 0), unit(H2, 0.05, 0, 0), unit(H3, 0.05, 0, 0),
			unit(B, 50, 0, 0), unit(H1, 50.05, 0, 0), unit(H2, 50.05, 0, 0), unit(H3, 50.05, 0, 0),
		}
		return u, make_stingray(function() return { { units = u } } end)
	end
	local function drive(u, S, main, mapping, ticks)
		local logLines = {}
		local files = { main = main, mapping = mapping }
		local h = M.new({ stingray = S, now = function() return t0 end, cpu = function() return t0 end,
			write = function(l) logLines[#logLines + 1] = l end,
			read_rules = function() return files.main, files.main == nil and 'missing' or nil end,
			read_mapping_rules = function() return files.mapping, files.mapping == nil and 'missing' or nil end })
		for _ = 1, ticks or 4 do
			t0 = t0 + 2
			for _ = 1, 10 do h.tick() end
		end
		return h, logLines, files
	end
	local function more(h, n)
		for _ = 1, n or 3 do t0 = t0 + 2; for _ = 1, 10 do h.tick() end end
	end

	-- 只有映射文件：照样生效，且只在自己的锚点旁
	do
		local u, S = build()
		local h = drive(u, S, nil, rules_text(1, rule_line('lp-1', '0.3', { A }, {}, { H1 })))
		assert(u[2].visible == false and u[6].visible == true, 'mapping-only file hides near its own anchor only')
		assert(#h.rules() == 1 and h.rules()[1].source == 'mapping')
	end

	-- 两份规则取并集；同签名也不去重、不互相覆盖：每条各藏各的
	do
		local u, S = build()
		local _, logLines = drive(u, S,
			rules_text(2, rule_line('main-a', '0.3', { A }, {}, { H1 })),
			rules_text(9, rule_line('lp-1', '0.3', { A }, {}, { H2 }), rule_line('lp-1b', '0.3', { B }, {}, { H3 })))
		assert(u[2].visible == false, 'main file rule fires')
		assert(u[3].visible == false, 'mapping rule with the SAME signature also fires (union, no priority)')
		assert(u[4].visible == true and u[8].visible == false, 'B-signature rule hides H3 near B only')
		assert(u[7].visible == true and u[6].visible == true, 'H1/H2 near B are untouched')
		local joined = table.concat(logLines)
		assert(joined:find('"mapping_rules":2', 1, true) and joined:find('"mapping_revision":9', 1, true), joined)
		assert(joined:find('"rules_hit":"lp-1,lp-1b,main-a"', 1, true),
			'sweep log names the rules whose signature held: ' .. joined)
	end

	-- 同名规则分处两个文件：各自独立，不当成重复
	do
		local u, S = build()
		local h = drive(u, S, rules_text(1, rule_line('same', '0.3', { A }, {}, { H1 })),
			rules_text(1, rule_line('same', '0.3', { A }, {}, { H2 })))
		assert(#h.rules() == 2 and u[2].visible == false and u[3].visible == false)
	end

	-- 一份坏、一份好：坏的保留它自己的上一版，好的照常更新
	do
		local u, S = build()
		local h, logLines, files = drive(u, S,
			rules_text(1, rule_line('main-a', '0.3', { A }, {}, { H1 })),
			rules_text(1, rule_line('lp-1', '0.3', { A }, {}, { H2 })), 3)
		files.mapping = 'schema=1\nrevision=2\nrule=name:BAD;radius:0.3\n'
		files.main = rules_text(2, rule_line('main-a', '0.3', { A }, {}, { H1, H3 }))
		more(h)
		assert(u[4].visible == false, 'main file update applied')
		assert(#h.rules() == 2 and h.rules()[2].name == 'lp-1', 'bad mapping file keeps its previous version')
		assert(table.concat(logLines):find('"file":"mapping"', 1, true), 'bad file is attributed to its file')
	end

	-- 映射文件被清空（撤销映射 / 关开关）：这份规则没了，主文件的还在；已藏掉的不恢复
	do
		local u, S = build()
		local h, _, files = drive(u, S,
			rules_text(1, rule_line('main-a', '0.3', { B }, {}, { H1 })),
			rules_text(1, rule_line('lp-1', '0.3', { A }, {}, { H2 })), 3)
		assert(u[3].visible == false)
		files.mapping = rules_text(2)
		more(h)
		assert(#h.rules() == 1 and h.rules()[1].name == 'main-a', 'emptied mapping file contributes no rules')
		assert(u[3].visible == false, 'already hidden stays hidden')
	end

	-- 映射文件不存在：与 0.2.0 一致，只用主文件；"file missing" 只在两份都没有时才写
	do
		local u, S = build()
		local h, logLines = drive(u, S, rules_text(1, rule_line('main-a', '0.3', { A }, {}, { H1 })), nil, 3)
		assert(#h.rules() == 1 and u[2].visible == false)
		assert(not table.concat(logLines):find('file missing', 1, true))
		local u2, S2 = build()
		local _, lines2 = drive(u2, S2, nil, nil, 3)
		assert(table.concat(lines2):find('file missing', 1, true))
	end
end

-- 规则文件每 2 秒最多读一次 -------------------------------------------------------
do
	local before = reads
	clock = clock + 10
	hider.tick()                       -- 读一次
	for _ = 1, 3 do clock = clock + 0.4; hider.tick() end   -- 1.2 秒内不再读
	assert(reads == before + 1, 'rules file read at most every 2s, reads=' .. (reads - before))
	clock = clock + 1
	hider.tick()                       -- 距上次 1.6 秒，仍不读
	clock = clock + 1
	hider.tick()                       -- 2.6 秒 ⇒ 读
	assert(reads == before + 2)
end

print('test_kit_instance_hider: ok (' .. #lines .. ' log lines)')
