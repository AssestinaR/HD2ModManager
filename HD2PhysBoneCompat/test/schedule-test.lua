-- 调度器（谁拿最高物理权）的回归。
--
-- 这套逻辑只有两个判据，而且都能离线钉住，不该靠进游戏观察：
--   ① 重排：相机附近的那个人排第一；每个会话仍归属"离它最近的 avatar"。
--   ② 兜底：最高权单位离开相机超过阈值并持续 N 秒 ⇒ 重置一次；限流生效。
--
-- 最关键的一条是"**只重排、不重挂**"：归属变了但会话不能重建，
-- 否则每次纠正都会闪一下、掉一次物理（那正是老版本多人局的毛病）。
--
-- 用法：luajit HD2PhysBoneCompat/test/schedule-test.lua [runtime]

local runtimePath = arg[1] or "HD2PhysBoneCompat/src/physbone_runtime.lua"
local Runtime = assert(assert(loadfile(runtimePath))())

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-52s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

-- ---------------------------------------------------------------------------
-- 假引擎：位置表驱动，相机位置可控
-- ---------------------------------------------------------------------------
local engine = { positions = {}, camera = { 0, 0, 0 } }

local S = {
	Vector3 = { to_elements = function(v) return v[1], v[2], v[3] end },
	Matrix4x4 = { translation = function() return engine.camera end },
	World = { debug_camera_pose = function() return { kind = "pose" } end },
	Unit = {
		world_position = function(unit) return engine.positions[unit] or { 0, 0, 0 } end,
		debug_name = function(unit) return "#ID[" .. tostring(unit) .. "]" end,
	},
}

local function place(unit, x, y, z)
	engine.positions[unit] = { x, y, z }
end

local TEMP = os.getenv("TEMP") or os.getenv("TMP") or "."
local tunePath = TEMP .. "\\physbone-schedule-no-such-tune.txt"

local function make_runtime()
	local runtime = {
		avatar = nil,
		world = {},
		cache = {},
		frame = 100,
		options = { tunePath = tunePath },
		logger = function() end,
		sessions = {},
		avatars = {},
	}
	return runtime
end

-- 两名玩家：A 在相机旁，B 在 10 m 外；两件护甲各自贴着自己的玩家。
local function scene()
	engine.camera = { 0, 0, 0 }
	place("avatar_A", 0.5, 0, 0)
	place("avatar_B", 10, 0, 0)
	place("armor_A", 0.5, 0, 0.1)
	place("armor_B", 10, 0, 0.1)
	local runtime = make_runtime()
	runtime.avatars = {
		{ unit = "avatar_A", tag = "A", nodes = 200 },
		{ unit = "avatar_B", tag = "B", nodes = 200 },
	}
	runtime.sessions = {
		{ unit = "armor_A", unitTag = "armor_A", owner = "avatar_A", ownerOrder = 1 },
		{ unit = "armor_B", unitTag = "armor_B", owner = "avatar_B", ownerOrder = 2 },
	}
	return runtime
end

-- ① 基本排序：相机旁的那个排第一，两件护甲各归其主，没有人"变化"
print("T1 初次重排")
do
	local runtime = scene()
	local changed = Runtime.redistribute_(S, runtime, "测试")
	check("自己的 avatar 排第一", runtime.avatars[1].unit == "avatar_A",
		tostring(runtime.avatars[1].unit))
	check("会话归属不变（A 的护甲仍属 A）", runtime.sessions[1].owner == "avatar_A")
	check("没有发生归属变化", changed == 0, "changed=" .. tostring(changed))
end

-- ② 位置互换：物理上"A 的护甲跟着 A 走"，所以归属不该变，但**顺序**必须变
print("T2 两个玩家位置互换")
do
	local runtime = scene()
	-- 重排会**重排 runtime.sessions 本身**，所以必须先把表引用存下来再断言，
	-- 不能事后用下标取（第一版测试就栽在这里，代码是对的、断言是错的）。
	local armorA, armorB = runtime.sessions[1], runtime.sessions[2]
	place("avatar_A", 10, 0, 0)
	place("avatar_B", 0.5, 0, 0)
	place("armor_A", 10, 0, 0.1)
	place("armor_B", 0.5, 0, 0.1)
	local changed = Runtime.redistribute_(S, runtime, "测试")
	check("现在排第一的是 B", runtime.avatars[1].unit == "avatar_B",
		tostring(runtime.avatars[1].unit))
	check("A 的护甲仍归 A（跟着身体走）", armorA.owner == "avatar_A", tostring(armorA.owner))
	check("B 的护甲仍归 B", armorB.owner == "avatar_B", tostring(armorB.owner))
	check("会话顺序按新排序（B 在前）", armorB.ownerOrder == 1,
		"ownerOrder=" .. tostring(armorB.ownerOrder))
	check("归属本身没有变化", changed == 0, "changed=" .. tostring(changed))
end

-- ③ 误判纠正：把某件护甲错记成别人的 ⇒ 重排必须改回来，且**只改归属**
print("T3 纠正错归属")
do
	local runtime = scene()
	local session = runtime.sessions[1]      -- 先存引用：重排会改变下标
	local identity = session
	session.owner = "avatar_B"          -- 模拟"错误命中队友"
	session.ownerOrder = 2
	local changed = Runtime.redistribute_(S, runtime, "测试")
	check("错误的归属被改回来", session.owner == "avatar_A", tostring(session.owner))
	check("报告了 1 处变化", changed == 1, "changed=" .. tostring(changed))
	check("会话没被重建（同一张表）", identity == session)
	check("没有新增/丢失会话", #runtime.sessions == 2)
end

-- ④ 兜底：最高权单位离相机 10 m，累积到 1 秒才触发一次
print("T4 兜底触发与限流")
do
	local runtime = scene()
	-- 两个人都搬到远处：否则"最近的那个"仍然在相机旁，判据不该成立
	place("avatar_A", 10, 0, 0)
	place("avatar_B", 12, 0, 0)
	place("armor_A", 10, 0, 0.1)
	place("armor_B", 12, 0, 0.1)
	local dt = 1 / 120
	local fired = nil
	for step = 1, 200 do
		runtime.frame = runtime.frame + 1
		Runtime.scheduler_watch_(S, runtime, dt)
		if runtime.schedResets ~= nil and fired == nil then fired = step end
	end
	check("1 秒后触发一次", runtime.schedResets == 1, "resets=" .. tostring(runtime.schedResets))
	check("触发时刻约在 120 帧", fired ~= nil and fired >= 115 and fired <= 125,
		"第 " .. tostring(fired) .. " 帧")
end

-- ⑤ 回到相机旁：累积值清零，不再触发
print("T5 回到相机旁")
do
	local runtime = scene()
	place("avatar_A", 20, 0, 0)
	place("armor_A", 20, 0, 0.1)
	for step = 1, 60 do
		runtime.frame = runtime.frame + 1
		Runtime.scheduler_watch_(S, runtime, 1 / 120)
	end
	check("还没到 1 秒，暂时不触发", (runtime.schedResets or 0) == 0)
	-- 注意：兜底量的是**当前排第一的那个**（avatars[1]），所以必须把它搬回来，
	-- 而不是把另一个球员搬近（第一版测试就写错了这一点）。
	place("avatar_A", 0.4, 0, 0)
	runtime.frame = runtime.frame + 1
	Runtime.scheduler_watch_(S, runtime, 1 / 120)
	check("判据不成立时累积值清零", (runtime.schedFar or 0) == 0,
		"schedFar=" .. tostring(runtime.schedFar))
	check("依然没有触发", (runtime.schedResets or 0) == 0)
end

-- ⑥ 调参通道（产品路径的"手动重设"）：值变化才触发，重复值不重复触发
print("T6 调参 schedReset 通道")
do
	local path = TEMP .. "\\physbone-schedule-tune.txt"
	local function writeTune(text)
		local handle = assert(io.open(path, "wb"))
		handle:write(text)
		handle:close()
	end
	writeTune("schedReset = 1\n")
	local runtime = scene()
	runtime.options.tunePath = path
	runtime.tune = nil
	runtime.frame = 60
	Runtime.sched_poll_(S, runtime)
	check("首次读到只记录、不触发", (runtime.schedResets or 0) == 0,
		"resets=" .. tostring(runtime.schedResets))
	writeTune("schedReset = 2\n")
	runtime.tune = nil                       -- 丢掉缓存，逼它重读
	runtime.frame = 61
	Runtime.sched_poll_(S, runtime)
	check("值变了触发一次", (runtime.schedResets or 0) == 1,
		"resets=" .. tostring(runtime.schedResets))
	runtime.tune = nil
	runtime.frame = 62
	Runtime.sched_poll_(S, runtime)
	check("值没变不重复触发", (runtime.schedResets or 0) == 1,
		"resets=" .. tostring(runtime.schedResets))
	os.remove(path)
end

-- ⑦ 原生键盘（如果这个版本暴露了 Keyboard API）：按下沿触发一次，长按不连发
print("T7 原生热键（假 Keyboard）")
do
	local runtime = scene()
	local logs = {}
	runtime.logger = function(text, ...)
		local args = { ... }
		if #args > 0 then text = string.format(text, unpack(args)) end
		logs[#logs + 1] = text
	end
	local keyDown = false
	S.Keyboard = {
		button_index = function(name) return name == "f8" and 79 or nil end,
		button = function(code) return code == 79 and keyDown or false end,
	}
	Runtime.sched_poll_(S, runtime)            -- 未按下
	check("未按下不触发", (runtime.schedResets or 0) == 0)
	check("绑定时打了一行（能看出调用约定）", (function()
		for _, line in ipairs(logs) do
			if line:find("已绑定 f8", 1, true) and line:find("约定=code", 1, true) then return true end
		end
		return false
	end)())
	keyDown = true
	Runtime.sched_poll_(S, runtime)            -- 按下沿
	check("按下触发一次", (runtime.schedResets or 0) == 1,
		"resets=" .. tostring(runtime.schedResets))
	check("按下留了一行日志（联调时能确认键读到了）", (function()
		for _, line in ipairs(logs) do
			if line:find("f8 按下", 1, true) then return true end
		end
		return false
	end)())
	Runtime.sched_poll_(S, runtime)            -- 仍按着
	check("长按不连发", (runtime.schedResets or 0) == 1,
		"resets=" .. tostring(runtime.schedResets))
	keyDown = false
	Runtime.sched_poll_(S, runtime)
	check("松开也留一行", (function()
		for _, line in ipairs(logs) do
			if line:find("f8 松开", 1, true) then return true end
		end
		return false
	end)())
	keyDown = true
	Runtime.sched_poll_(S, runtime)            -- 再按一次
	check("再按一次再触发", (runtime.schedResets or 0) == 2,
		"resets=" .. tostring(runtime.schedResets))
	S.Keyboard = nil
end

-- ⑦b 另一种调用约定：只有 button，且直接收键名（没有 button_index）
print("T7b 只有 button(名字) 的约定")
do
	local runtime = scene()
	local keyDown = false
	S.Keyboard = {
		button = function(name) if name == "f8" then return keyDown end return false end,
	}
	Runtime.sched_poll_(S, runtime)
	keyDown = true
	Runtime.sched_poll_(S, runtime)
	check("按名字读取也能工作", (runtime.schedResets or 0) == 1,
		"resets=" .. tostring(runtime.schedResets))
	S.Keyboard = nil
end

-- ⑧ 没有 Keyboard API 时：不崩、不阻止其它通道
print("T8 没有 Keyboard API")
do
	local runtime = scene()
	S.Keyboard = nil
	local ok = pcall(function() Runtime.sched_poll_(S, runtime) end)
	check("缺 Keyboard 时不抛错", ok)
	check("也不会误触发", (runtime.schedResets or 0) == 0)
end

-- ⑦ 没有相机 / 读不到位置时不能崩，也不能乱改归属
print("T9 相机不可用")
do
	local runtime = scene()
	local savedPose = S.World.debug_camera_pose
	S.World.debug_camera_pose = function() return nil end
	runtime.cache = {}
	local ok = pcall(function() Runtime.redistribute_(S, runtime, "测试") end)
	check("相机读不到时不抛错", ok)
	check("归属仍然正确（退回按位置最近）", runtime.sessions[1].owner == "avatar_A")
	S.World.debug_camera_pose = savedPose
end

-- ⑩ 预算分档：自己永远每帧；6 m 内尽快；6 m 外按需退让
print("T10 预算分档")
do
	local budget = 160
	-- 链数在预算内 ⇒ 谁都不用节流
	local ns, fs = Runtime.schedule_budget_(40, 60, 50, budget)
	check("总量在预算内 ⇒ 全每帧", ns == 1 and fs == 1,
		string.format("near=%d far=%d", ns, fs))
	-- 近处链很少、远处很多 ⇒ 近处必须每帧，远处退让
	ns, fs = Runtime.schedule_budget_(10, 30, 400, budget)
	check("近处链少 ⇒ 近处每帧", ns == 1, "near=" .. tostring(ns))
	check("远处退让到 4 帧", fs == 4, "far=" .. tostring(fs))
	-- 四人局：自己 400 链（已超预算）+ 队友各 400
	ns, fs = Runtime.schedule_budget_(400, 400, 800, budget)
	check("近处最多退到 3 帧", ns <= 3, "near=" .. tostring(ns))
	check("远处最多退到 8 帧", fs <= 8, "far=" .. tostring(fs))
	check("远处一定比近处更稀", fs >= ns, string.format("near=%d far=%d", ns, fs))
	-- 单调性：链越多，步长不减
	local nsA, fsA = Runtime.schedule_budget_(50, 100, 100, budget)
	local nsB, fsB = Runtime.schedule_budget_(50, 100, 900, budget)
	check("远处链更多 ⇒ 远处步长不减", fsB >= fsA,
		string.format("far %d→%d", fsA, fsB))
end

print(string.format("\n失败 %d 项", failures))
os.exit(failures == 0 and 0 or 1)
