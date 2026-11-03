-- 生命周期闸回归（K1/K2：军械库 / 空投仓换甲闪退）
--
-- 这个测试的价值在于：它把"Unit 被销毁之后还在被碰"这件事变成**可离线复现**的断言。
-- 游戏里这种错误的表现是原生 AV（进程直接没，pcall 拦不住），
-- 所以唯一能自动化的判据是：**Unit 死掉之后，一个引擎调用都不许再发**。
--
-- 用法：luajit tmp/lua-check/lifecycle-test.lua [模块路径]

local path = arg[1] or "HD2PhysBoneCompat/src/physbone_compat.lua"
local M = assert(assert(loadfile(path)))()

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-52s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

local function quiet() return function() end end

-- ---------------------------------------------------------------------------
-- 假引擎：可以随时把 unit 标记为"已销毁"，并统计各类调用。
-- 关键：一旦 alive=false，**任何**非 alive 的调用都会被记进 calls.violation。
-- ---------------------------------------------------------------------------
local function make_engine(nodeCount)
	local calls = { alive = 0, other = 0, writes = 0, updateUnit = 0, violation = nil }
	local state = { alive = true, nodeCount = nodeCount }

	local unit = { name = "fake-armor" }
	local pose, vector, quat = { kind = "pose" }, { kind = "vector" }, { kind = "quat" }

	local function guard(name)
		calls.other = calls.other + 1
		if not state.alive then
			calls.violation = calls.violation or {}
			calls.violation[#calls.violation + 1] = name
		end
	end

	local S = {
		Application = {
			-- 稳定、可读的假 id：长度 × 1000 + 首字节，够区分骨名
			string_to_id32 = function(text) return #text * 1000 + text:byte(1) end,
		},
		Unit = {}, World = {}, Vector3 = {}, Quaternion = {}, Matrix4x4 = {},
	}

	S.Unit.alive = function() calls.alive = calls.alive + 1 return state.alive end
	S.Unit.num_scene_graph_items = function() guard("num_scene_graph_items") return state.nodeCount end
	S.Unit.has_node = function(_, id)
		guard("has_node")
		if not state.alive then return false end
		return type(id) == "number" and id > 0
	end
	S.Unit.node = function(_, id)
		guard("node")
		if not state.alive then return nil end
		return (id % state.nodeCount) + 1
	end
	S.Unit.local_pose = function() guard("local_pose") return pose end
	S.Unit.world_position = function() guard("world_position") return vector end
	S.Unit.world_rotation = function() guard("world_rotation") return quat end
	S.Unit.set_local_pose = function()
		guard("set_local_pose")
		calls.writes = calls.writes + 1
	end
	S.World.update_unit = function()
		guard("update_unit")
		calls.updateUnit = calls.updateUnit + 1
	end

	S.Vector3.to_elements = function() guard("Vector3.to_elements") return 0, 0, 0 end
	S.Quaternion.to_elements = function() guard("Quaternion.to_elements") return 0, 0, 0, 1 end
	S.Matrix4x4.to_elements = function()
		guard("Matrix4x4.to_elements")
		return 1, 0, 0, 0, 1, 0, 0, 0, 1, 0.1, 0.2, 0.3   -- 单位旋转 + 平移（同实机样本）
	end
	S.Matrix4x4.is_valid = function() guard("Matrix4x4.is_valid") return true end
	S.Matrix4x4.from_quaternion_position = function() guard("from_quaternion_position") return pose end
	S.Quaternion.axis_angle = function() guard("axis_angle") return quat end
	-- 引擎里 Vector3 / Quaternion 是**可调用的表**（S.Vector3(x,y,z)），不是 .new
	setmetatable(S.Vector3, { __call = function() guard("Vector3()") return vector end })
	setmetatable(S.Quaternion, { __call = function() guard("Quaternion()") return quat end })

	return S, unit, state, calls
end

-- 一条 2 节链（第一节挂 head，第二节挂在第一节下面）
local function make_rig()
	local params = {
		tipMass = 200, yawStiffness = 50, yawDamping = 7,
		pitchStiffness = 50, pitchDamping = 7, angleLimit = 0.611, length = 30,
	}
	return {
		bones = {
			{ index = 1, name = "jig_tail_a", parent = 0, parentName = "head",
				pos = { 0, 0, 0 }, params = params, flags = 0x31 },
			{ index = 2, name = "jig_tail_b", parent = 1, parentName = "jig_tail_a",
				pos = { 0, -0.1, 0 }, params = params, flags = 0x31 },
		},
	}
end

-- 两条**互相独立**的链（各自挂 head），这样 attach 的循环会走两轮
local function make_rig_two_chains()
	local params = {
		tipMass = 200, yawStiffness = 50, yawDamping = 7,
		pitchStiffness = 50, pitchDamping = 7, angleLimit = 0.611, length = 30,
	}
	return {
		bones = {
			{ index = 1, name = "jig_tail_a", parent = 0, parentName = "head",
				pos = { 0, 0, 0 }, params = params, flags = 0x31 },
			{ index = 2, name = "jig_tail_c", parent = 0, parentName = "head",
				pos = { 0.1, 0, 0 }, params = params, flags = 0x31 },
		},
	}
end

local function violationText(calls)
	return calls.violation and table.concat(calls.violation, ",") or "none"
end

-- ---------------------------------------------------------------------------
print("T1 unit_alive 的语义")
do
	local S, unit, engineState = make_engine(100)
	check("alive=true 判定为活", M.unit_alive(S, unit) == true)
	engineState.alive = false
	check("alive=false 判定为死", M.unit_alive(S, unit) == false)
	check("nil unit 判定为死", M.unit_alive(S, nil) == false)
	check("没有 Unit.alive 时乐观为真", M.unit_alive({ Unit = {} }, unit) == true)
	check("Unit.alive 抛错时按死处理",
		M.unit_alive({ Unit = { alive = function() error("boom") end } }, unit) == false)
end

-- ---------------------------------------------------------------------------
print("T2 attach 在 Unit 已销毁时一个引擎调用都不发")
do
	local S, unit, engineState, calls = make_engine(100)
	engineState.alive = false
	local session = M.attach(S, unit, make_rig(), { logger = quiet() })
	check("返回空会话", #session.sessions == 0)
	check("标记 dead", session.dead == "dead", tostring(session.dead))
	check("除 alive 外零引擎调用", calls.other == 0, string.format("other=%d", calls.other))
	check("零违规", calls.violation == nil, violationText(calls))
end

-- ---------------------------------------------------------------------------
print("T3 正常 attach 之后，Unit 一死就停手（核心判据）")
do
	local S, unit, engineState, calls = make_engine(100)
	local session = M.attach(S, unit, make_rig(), { logger = quiet(), world = {} })
	check("attach 成功（至少一条链）", #session.sessions >= 1,
		string.format("链数=%d", #session.sessions))
	check("记录了节点数指纹", session.nodeCount == 100, tostring(session.nodeCount))

	local before = calls.other
	local written = M.update(session, 1 / 60)
	check("活着时正常驱动", written > 0 and calls.other > before,
		string.format("写入=%d 引擎调用=%d", written, calls.other))

	engineState.alive = false
	calls.violation = nil
	local aliveBefore = calls.alive
	local w2 = M.update(session, 1 / 60)
	local w3 = M.update(session, 1 / 60)
	check("销毁后 update 返回 0", w2 == 0 and w3 == 0)
	check("销毁后仍会查 alive", calls.alive > aliveBefore,
		string.format("alive 调用 +%d", calls.alive - aliveBefore))
	check("销毁后零违规引擎调用", calls.violation == nil, violationText(calls))
	check("会话被标记 dead", session.dead == "dead", tostring(session.dead))
end

-- ---------------------------------------------------------------------------
print("T4 release 在 Unit 已销毁时不写姿态")
do
	local S, unit, engineState, calls = make_engine(100)
	local session = M.attach(S, unit, make_rig(), { logger = quiet(), world = {} })
	engineState.alive = false
	calls.violation = nil
	local restored = M.release(session)
	check("release 返回 0", restored == 0)
	check("零违规引擎调用", calls.violation == nil, violationText(calls))
end

-- ---------------------------------------------------------------------------
print("T5 节点数变化 ⇒ 判定 rebuilt 并停手（唯一可靠的重建判据）")
do
	local S, unit, engineState, calls = make_engine(100)
	local session = M.attach(S, unit, make_rig(), { logger = quiet(), world = {} })
	session.identityEvery = 1
	engineState.nodeCount = 123      -- 换装/切 LOD：场景图节点数变了 ⇒ 缓存的节点下标可能越界
	calls.violation = nil
	local written = M.update(session, 1 / 60)
	check("节点数变化后不再驱动", written == 0)
	check("判定为 rebuilt", session.dead == "rebuilt", tostring(session.dead))
	check("零违规引擎调用", calls.violation == nil, violationText(calls))
end

-- ---------------------------------------------------------------------------
print("T6 strictIdentity=true 时才按漂移停手（A/B 开关）")
do
	local S, unit, engineState, calls = make_engine(100)
	local session = M.attach(S, unit, make_rig(),
		{ logger = quiet(), world = {}, strictIdentity = true })
	session.identityEvery = 1
	engineState.nodeCount = 123
	calls.violation = nil
	local written = M.update(session, 1 / 60)
	check("节点数变化后不再驱动", written == 0)
	check("判定为 rebuilt", session.dead == "rebuilt", tostring(session.dead))
	check("零违规引擎调用", calls.violation == nil, violationText(calls))
end

-- ---------------------------------------------------------------------------
print("T7 死会话不会被反复重试")
do
	local S, unit, engineState, calls = make_engine(100)
	local session = M.attach(S, unit, make_rig(), { logger = quiet(), world = {} })
	engineState.alive = false
	M.update(session, 1 / 60)
	local mark = calls.alive
	for _ = 1, 10 do M.update(session, 1 / 60) end
	check("dead 被记住 ⇒ 后续零 alive 调用", calls.alive == mark,
		string.format("额外 alive 调用 %d 次", calls.alive - mark))
end

-- ---------------------------------------------------------------------------
print("T8 节点数没变但抽样骨名解析不到 ⇒ 默认也只观察")
do
	local S, unit, engineState, calls = make_engine(100)
	local session = M.attach(S, unit, make_rig(), { logger = quiet(), world = {} })
	session.identityEvery = 1
	check("有抽样骨名 id", type(session.identityNode) == "number", tostring(session.identityNode))
	local originalHasNode = S.Unit.has_node
	S.Unit.has_node = function(_, id)
		if id == session.identityNode then return false end
		return originalHasNode(_, id)
	end
	calls.violation = nil
	local written = M.update(session, 1 / 60)
	check("抽样骨消失后仍继续驱动", written > 0, string.format("写入=%d", written))
	check("漂移被记录（has=false）", type(session.identityNow) == "table"
		and session.identityNow.has == false, tostring(session.identityNow and session.identityNow.has))
	check("零违规引擎调用", calls.violation == nil, violationText(calls))
end

-- ---------------------------------------------------------------------------
print("T9 attach 途中 Unit 失效 ⇒ 中断，且之后 update 立刻停手")
do
	local S, unit, engineState, calls = make_engine(100)
	-- 让第二次 has_node 之后 Unit 就失效（模拟引擎调用内部把它弄没了）
	local originalHasNode = S.Unit.has_node
	local seen = 0
	S.Unit.has_node = function(_, id)
		seen = seen + 1
		if seen >= 2 then engineState.alive = false end
		return originalHasNode(_, id)
	end
	local session = M.attach(S, unit, make_rig_two_chains(), { logger = quiet(), world = {} })
	check("第二条链开始前就中断", session.dead == "dead", tostring(session.dead))
	calls.violation = nil
	local written = M.update(session, 1 / 60)
	check("之后的 update 零写入", written == 0)
	check("零违规引擎调用", calls.violation == nil, violationText(calls))
end

print("")
if failures == 0 then
	print("=== 全部通过 ===")
else
	print(string.format("=== %d 项失败 ===", failures))
	os.exit(1)
end
