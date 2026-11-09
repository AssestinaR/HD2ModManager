-- 多人局调度层离线测试（luajit 跑，不需要游戏）。
--
-- 为什么值得写：这一层改的是"谁该有物理"，而它的失败现象全都**没有报错**——
-- 只有一个人有头发飘动、或者所有人闪一下没有物理。等实机发现，代价是一轮"重启游戏试试"。
-- 这个壳把 S（引擎命名空间）和 Compat（求解器）都换成假的，于是"4 个人各自挂上"
-- "中途有人加入""有人退出"这些只能在多人局里出现的情形，可以在这台机器上逐条断言。
--
-- 跑法：luajit HD2PhysBoneCompat/test/mp_harness.lua

local here = arg and arg[0] and arg[0]:gsub("[^/\\]*$", "") or ""
package.path = here .. "../src/?.lua;" .. package.path

local runtimeModule = dofile(here .. "../src/physbone_runtime.lua")

local failures, checks = 0, 0
local function check(condition, message, detail)
	checks = checks + 1
	-- 输出统一成别的 harness 那套 `PASS` / `FAIL` 行：跑套件时要能被
	-- tools/physbone-harness/run_physbone_regressions.py 数到，否则汇总里这一整
	-- 文件看起来是 0 项检查。
	if not condition then
		failures = failures + 1
		print(string.format("  %-4s %s%s", "FAIL", message, detail ~= nil and ("  [" .. tostring(detail) .. "]") or ""))
	else
		print(string.format("  %-4s %s", "PASS", message))
	end
end

-- ---------------------------------------------------------------- 假引擎
local function vector(x, y, z) return { x = x, y = y, z = z } end

local Fake = {}
Fake.__index = Fake

local function newFake()
	local self = setmetatable({ units = {}, nextIndex = 1, clock = 0 }, Fake)
	-- 噪声 unit：节点数不够，必须被判"不是目标"，用来确认扫描没有把所有人都当候选。
	for _ = 1, 40 do self:addUnit({ nodes = 10 }) end
	return self
end

function Fake:addUnit(spec)
	local unit = {
		id = self.nextIndex,
		nodes = spec.nodes or 110,
		alive = true,
		avatarResource = spec.avatar or false,
		anim = spec.anim ~= false,
		mg = spec.mg ~= false,
		position = spec.position or vector(0, 0, 0),
		chains = spec.chains or 6,
		name = spec.name or ("unit" .. self.nextIndex),
	}
	self.nextIndex = self.nextIndex + 1
	self.units[#self.units + 1] = unit
	return unit
end

function Fake:removeUnit(unit)
	for index = #self.units, 1, -1 do
		if self.units[index] == unit then table.remove(self.units, index) end
	end
	unit.alive = false
end

function Fake:playerAt(name, x, chains)
	local avatar = self:addUnit({ name = name .. "-avatar", avatar = true, position = vector(x, 0, 0) })
	local armor = self:addUnit({ name = name .. "-armor", anim = false, mg = true, position = vector(x + 0.1, 0, 0), chains = chains })
	return avatar, armor
end

function Fake:stingray()
	local self_ = self
	local id32 = function(text) return text end
	local Unit = {
		alive = function(unit) return unit ~= nil and unit.alive == true end,
		num_scene_graph_items = function(unit) return unit.nodes end,
		has_animation_state_machine = function(unit) return unit.anim end,
		has_node = function(unit, _) return unit.mg end,
		world_position = function(unit, _) return unit.position end,
		debug_name = function(unit) return "#ID[" .. tostring(unit.id) .. "]" end,
	}
	local World = {
		units = function(_) return self_.units end,
		num_units = function(_) return #self_.units end,
		units_by_resource = function(_, resource)
			local found = {}
			for index = 1, #self_.units do
				local unit = self_.units[index]
				if unit.alive and unit.avatarResource and resource == "content/fac_helldivers/cha_avatar/avatar_helldiver" then
					found[#found + 1] = unit
				end
			end
			return found
		end,
		-- 相机固定在原点：于是 (0,0,0) 那个玩家就是"自己"（第一位）。
		debug_camera_pose = function(_) return { pose = true } end,
		update_unit = function() end,
	}
	return {
		Application = {
			worlds = function() return { 1 } end,
			string_to_id32 = id32,
			time_since_launch = function() return self_.clock end,
		},
		World = World,
		Unit = Unit,
		Vector3 = { to_elements = function(v) return v.x, v.y, v.z end },
		Matrix4x4 = { translation = function(_) return vector(0, 0, 0) end },
	}
end

function Fake:advance(frames, step)
	step = step or 0.01
	for _ = 1, frames do
		self.clock = self.clock + step
		rawget(_G, "render")()
	end
end

-- ---------------------------------------------------------------- 假求解器
local function newCompat()
	local compat = { attached = {}, released = {}, dtByUnit = {}, calls = {} }
	compat.unit_alive = function(_, unit) return unit ~= nil and unit.alive == true end
	compat.begin_frame = function() end
	compat.select_rig = function(_, units, _)
		local unit = units[1]
		return { rig = { bones = { { name = "hair" } } }, path = "data/fake.gpu_resources", full = true, score = 1, total = #unit.name }
	end
	compat.attach = function(_, unit, _, _)
		local session = {
			unit = unit,
			S = nil,
			sessions = {},
			logger = function() end,
			engineCalls = 0,
			skippedSame = 0,
		}
		for index = 1, unit.chains do
			session.sessions[#session.sessions + 1] = { chain = { { name = "c" .. index } }, staticFrames = 0 }
		end
		compat.attached[#compat.attached + 1] = unit.name
		return session
	end
	compat.update = function(session, dt, _)
		compat.dtByUnit[#compat.dtByUnit + 1] = { unit = session.unit.name, dt = dt }
		session.engineCalls = 2 * #session.sessions
		session.skippedSame = 1
		return 1
	end
	compat.release = function(session)
		compat.released[#compat.released + 1] = session.unit.name
		return 1
	end
	compat.session_health = function(session)
		if session.unit == nil or session.unit.alive ~= true then return "dead" end
		return "ok"
	end
	return compat
end

-- ---------------------------------------------------------------- 场景
local function freshRuntime(fake, compat, options)
	rawset(_G, "__hd2physbone_runtime", nil)
	rawset(_G, "render", function() end)
	local opts = {
		compat = compat,
		log = false,
		carriers = { { rig = { bones = { { name = "hair" } } }, path = "fake" } },
		settleFrames = 1,
		pollFrames = 1,
		validateFrames = 3,
		heartbeatFrames = 1000000,
		minNodes = 90,
	}
	for key, value in pairs(options or {}) do opts[key] = value end
	runtimeModule.install(fake:stingray(), opts)
	return rawget(_G, "__hd2physbone_runtime")
end

local function sessionsOf(runtime)
	local list = {}
	for _, session in ipairs(runtime.sessions or {}) do
		list[#list + 1] = { unit = session.unit.name, owner = session.owner ~= nil and session.owner.name or nil, order = session.ownerOrder }
	end
	table.sort(list, function(a, b) return a.unit < b.unit end)
	return list
end

local function sessionUnits(runtime)
	local set = {}
	for _, session in ipairs(runtime.sessions or {}) do set[session.unit] = session end
	return set
end

-- ================================================================ 场景 1：四人局
io.stdout:write("\n[场景 1] 四人局：每个人都该拿到自己的物理\n")
do
	local fake = newFake()
	local compat = newCompat()
	local avatars, armors = {}, {}
	for index = 1, 4 do
		local avatar, armor = fake:playerAt("p" .. index, (index - 1) * 10)
		avatars[index], armors[index] = avatar, armor
	end
	local runtime = freshRuntime(fake, compat)
	fake:advance(60)

	local sessions = sessionsOf(runtime)
	check(#sessions == 4, "4 个玩家各挂上一件护甲", #sessions)
	local owners = {}
	for _, item in ipairs(sessions) do owners[item.order] = (owners[item.order] or 0) + 1 end
	check(owners[1] == 1 and owners[2] == 1 and owners[3] == 1 and owners[4] == 1,
		"四个会话分别归属于四个不同的玩家", "顺序=" .. tostring(owners[1]) .. tostring(owners[2]) .. tostring(owners[3]) .. tostring(owners[4]))

	-- 相机在原点 ⇒ p1 是"自己"，必须排第一（分帧时它每帧都算）。
	check(runtime.avatars ~= nil and runtime.avatars[1] ~= nil and runtime.avatars[1].unit == avatars[1],
		"离相机最近的那个玩家被认成第一位（自己）",
		runtime.avatars ~= nil and runtime.avatars[1] ~= nil and runtime.avatars[1].unit.name or "nil")
end

-- ================================================================ 场景 2：中途有人加入
io.stdout:write("\n[场景 2] 两人局中途又进来一个人：老人一个会话都不该被动到\n")
do
	local fake = newFake()
	local compat = newCompat()
	fake:playerAt("p1", 0)
	fake:playerAt("p2", 10)
	local runtime = freshRuntime(fake, compat)
	fake:advance(60)
	local before = sessionUnits(runtime)
	check(#runtime.sessions == 2, "先挂上两个人", #runtime.sessions)

	local _, armor3 = fake:playerAt("p3", 20)
	fake:advance(40)
	local after = sessionUnits(runtime)

	check(#runtime.sessions == 3, "新玩家补挂上第三件", #runtime.sessions)
	local untouched = true
	for unit in pairs(before) do
		if after[unit] ~= before[unit] then untouched = false end
	end
	check(untouched, "原有会话对象没有被重建（只是增量补挂）")
	check(#compat.released == 0, "整个过程没有释放任何会话（老人不闪断）", table.concat(compat.released, ","))
	check(sessionUnits(runtime)[armor3] ~= nil, "新玩家的护甲确实被挂上了")
end

-- ================================================================ 场景 3：有人退出
io.stdout:write("\n[场景 3] 有人退出：只收他的会话，别人的继续跑\n")
do
	local fake = newFake()
	local compat = newCompat()
	fake:playerAt("p1", 0)
	local avatar2, armor2 = fake:playerAt("p2", 10)
	local runtime = freshRuntime(fake, compat)
	fake:advance(60)
	check(#runtime.sessions == 2, "先挂上两个人", #runtime.sessions)

	-- (a) 退出的玩家 unit 被销毁：会话随 `pruneSessions` 走，不需要（也不能）交还姿态。
	fake:removeUnit(armor2)
	fake:removeUnit(avatar2)
	fake:advance(40)
	check(#runtime.sessions == 1, "退出的那个人只剩 1 个会话", #runtime.sessions)
	check(runtime.sessions[1] ~= nil and runtime.sessions[1].ownerOrder == 1, "留下的是第一位玩家的会话")
end

do
	-- (b) 只有 avatar 没了、护甲 unit 还活着（阵亡留尸）：必须**交还基准姿态**，
	--     否则头发会停在最后写入的那一帧，看着像被冻住。
	local fake = newFake()
	local compat = newCompat()
	fake:playerAt("p1", 0)
	local avatar2 = fake:playerAt("p2", 10)
	local runtime = freshRuntime(fake, compat)
	fake:advance(60)
	check(#runtime.sessions == 2, "先挂上两个人（留尸场景）", #runtime.sessions)

	fake:removeUnit(avatar2)
	fake:advance(40)
	check(#compat.released >= 1, "主人不在了但 unit 还活着 ⇒ 释放并交还姿态", table.concat(compat.released, ","))
	check(#runtime.sessions == 1, "他自己的会话被收掉，别人不受影响", #runtime.sessions)
end

-- ================================================================ 场景 4：预算与分帧
io.stdout:write("\n[场景 4] 链数超预算：非首位玩家分帧驱动，但物理时间不能丢\n")
do
	local fake = newFake()
	local compat = newCompat()
	-- 每人 60 条链 ⇒ 四人 240 链，超过 160 的预算。
	for index = 1, 4 do fake:playerAt("p" .. index, (index - 1) * 10, 60) end
	local runtime = freshRuntime(fake, compat, { maxChainsPerFrame = 160, otherPlayerStride = 2 })
	fake:advance(80)
	-- 2026-11-03：分帧步长从"固定 2"改成**按总量自适应**（四人局固定 2 会把预算打穿）。
	-- 本场景：自己 60 链 + 队友 180 链；队友预算 = 160/2 = 80
	-- ⇒ stride = ceil(180/80) = 3
	check(runtime.driveStride == 3, "超预算后按总量自适应分帧（队友 180 链 / 预算 80 ⇒ stride=3）",
		runtime.driveStride)

	compat.dtByUnit = {}
	fake:advance(20, 0.01)
	local frames = 20
	local byUnit = {}
	for _, entry in ipairs(compat.dtByUnit) do byUnit[entry.unit] = (byUnit[entry.unit] or 0) + entry.dt end
	-- 20 帧 × 0.01 = 0.2 秒的时间，每个会话拿到的 dt 总和都必须≈0.2。
	-- 容差 = **一个 stride 周期**的时间：分帧的语义是"攒够再算"，所以窗口末尾
	-- 必然有不足一个周期的 dt 还挂在账上（stride=3 时最多 2 帧 = 0.02 秒）。
	local worst, worstName = 0, nil
	for unit, total in pairs(byUnit) do
		local error = math.abs(total - frames * 0.01)
		if error > worst then worst, worstName = error, unit end
	end
	check(worst <= (runtime.driveStride - 1) * 0.01 + 0.001, "每个被分帧的会话都拿到了完整的时间（dt 累积补偿）",
		string.format("最差 %s 误差 %.4f 秒", tostring(worstName), worst))

	-- 第一位（自己）每帧都要算，不能被分帧。
	local selfCount = 0
	for _, entry in ipairs(compat.dtByUnit) do
		if entry.unit == "p1-armor" then selfCount = selfCount + 1 end
	end
	check(selfCount == frames, "自己（离相机最近）每帧都在算", selfCount .. "/" .. frames)
end

-- ================================================================ 场景 5：分帧不能被当成驱动失效
io.stdout:write("\n[场景 5] 分帧期间不触发「驱动失效」重挂\n")
do
	local fake = newFake()
	local compat = newCompat()
	for index = 1, 4 do fake:playerAt("p" .. index, (index - 1) * 10, 60) end
	local runtime = freshRuntime(fake, compat, { maxChainsPerFrame = 160, otherPlayerStride = 2, zeroLimit = 5 })
	fake:advance(80)
	local attachCount = #compat.attached
	fake:advance(60)
	check(#compat.attached == attachCount, "分帧跑了 60 帧，一次都没有重挂", #compat.attached - attachCount)
	check(runtime.sessions ~= nil and #runtime.sessions == 4, "四个会话仍在", runtime.sessions ~= nil and #runtime.sessions or 0)
end

-- ================================================================ 场景 6：没有 units_by_resource 的兜底
io.stdout:write("\n[场景 6] 老引擎没有 units_by_resource：退回全量扫描，照样要找到所有玩家\n")
do
	local fake = newFake()
	local compat = newCompat()
	for index = 1, 3 do fake:playerAt("p" .. index, (index - 1) * 10) end
	local runtime = freshRuntime(fake, compat)
	-- 模拟"这个版本的引擎没有这个函数"：直接抽掉它。
	local stingray = fake:stingray()
	stingray.World.units_by_resource = nil
	rawset(_G, "__hd2physbone_runtime", nil)
	rawset(_G, "render", function() end)
	runtimeModule.install(stingray, {
		compat = compat, log = false,
		carriers = { { rig = { bones = { { name = "hair" } } }, path = "fake" } },
		settleFrames = 1, pollFrames = 1, validateFrames = 3, heartbeatFrames = 1000000, minNodes = 90,
	})
	runtime = rawget(_G, "__hd2physbone_runtime")
	fake:advance(60)
	check(runtime.cache.avatarSource == "scan", "确实走了全量扫描那条路", runtime.cache.avatarSource)
	check(#runtime.sessions == 3, "三个人都挂上了（不再只认一个）", #runtime.sessions)
end

-- ================================================================ 场景 7：单人局不回归
io.stdout:write("\n[场景 7] 单人局：一个玩家、两件护甲，两件都要挂上\n")
do
	local fake = newFake()
	local compat = newCompat()
	fake:playerAt("p1", 0)
	fake:addUnit({ name = "p1-armor-b", anim = false, mg = true, position = vector(0.2, 0, 0), chains = 4 })
	local runtime = freshRuntime(fake, compat)
	fake:advance(60)
	check(#runtime.sessions == 2, "两件护甲都挂上", #runtime.sessions)
	check(runtime.driveStride == 1, "链数在预算内 ⇒ 不分帧", runtime.driveStride)
	local owners = {}
	for _, session in ipairs(runtime.sessions) do owners[session.ownerOrder] = true end
	check(owners[1] == true and next(owners, 2) == nil, "两件都属于同一位玩家")
end

-- ================================================================ 场景 8：日志闸门在真实驱动路径上生效
-- 单测（log-budget-test）验的是 logger 本身；这一条走 install + render 的真实链路，
-- 验的是"装进去的默认值对不对"和"预算在驱动路径上也拦得住"。默认心跳如果哪天被改回
-- 120 帧，这里会立刻红：1201 帧出十行，而不是一两行。
io.stdout:write("\n[场景 8] 日志闸门：默认心跳 600 帧、预算在真实驱动路径上生效\n")
do
	local TEMP = os.getenv("TEMP") or os.getenv("TMP") or "."
	local stamp = tostring(os.time())

	local function tempLog(tag)
		local path = string.format("%s\\physbone-mp-log-%s-%s.log", TEMP, stamp, tag)
		os.remove(path)
		os.remove(path .. ".1")
		return path
	end

	local function readLog(path)
		local handle = io.open(path, "rb")
		if handle == nil then return "" end
		local text = handle:read("*a") or ""
		handle:close()
		return text
	end

	local function countIn(text, needle)
		local count, from = 0, 1
		while true do
			local found = string.find(text, needle, from, true)
			if found == nil then return count end
			count = count + 1
			from = found + #needle
		end
	end

	-- 刻意不传 heartbeatFrames：这条测的就是默认值。
	local function installWithLog(fake, compat, path, extra)
		rawset(_G, "__hd2physbone_runtime", nil)
		rawset(_G, "render", function() end)
		local opts = {
			compat = compat,
			logPath = path,
			carriers = { { rig = { bones = { { name = "hair" } } }, path = "fake" } },
			settleFrames = 1,
			pollFrames = 1,
			validateFrames = 3,
			minNodes = 90,
		}
		for key, value in pairs(extra or {}) do opts[key] = value end
		runtimeModule.install(fake:stingray(), opts)
		return rawget(_G, "__hd2physbone_runtime")
	end

	local path = tempLog("default")
	local fake = newFake()
	local compat = newCompat()
	fake:playerAt("p1", 0)
	installWithLog(fake, compat, path)
	fake:advance(601)
	local first = countIn(readLog(path), "驱动心跳")
	fake:advance(600)
	local text = readLog(path)
	local second = countIn(text, "驱动心跳")
	check(second >= 1, "心跳确实落盘（证明驱动块进去了）", second .. " 条")
	check(second <= 4, "1201 帧只有一两行心跳（默认 600 帧，不是 120）", second .. " 条")
	check(second - first <= 2, "每 600 帧才多一行", (second - first) .. " 条")
	check(string.find(text, "累计/帧", 1, true) ~= nil, "心跳行仍带关键事实（累计/帧）")
	check(#text < 128 * 1024, "空跑 1201 帧远没到预算", #text .. " 字节")

	-- 预算：把心跳拉到每帧一行，900 字节预算必须把它截住。
	local budgetPath = tempLog("budget")
	local fake2 = newFake()
	local compat2 = newCompat()
	fake2:playerAt("p1", 0)
	installWithLog(fake2, compat2, budgetPath, { heartbeatFrames = 1, logMaxBytes = 900 })
	fake2:advance(400)
	local budgetText = readLog(budgetPath)
	local beats = countIn(budgetText, "驱动心跳")
	check(beats > 0, "预算之内仍写了心跳", beats .. " 条")
	check(beats < 40, "预算把心跳截住了（无预算时该是 400）", beats .. " 条")
	check(string.find(budgetText, "日志已达上限", 1, true) ~= nil, "落了一行「已达上限」")
	check(#budgetText < 900 + 600, "总字节仍在上限附近", #budgetText .. " 字节")

	os.remove(path)
	os.remove(path .. ".1")
	os.remove(budgetPath)
	os.remove(budgetPath .. ".1")
	pcall(runtimeModule.uninstall)
end

io.stdout:write(string.format("\n%s：%d 项检查，%d 项失败\n", failures == 0 and "全部通过" or "有失败", checks, failures))
os.exit(failures == 0 and 0 or 1)

