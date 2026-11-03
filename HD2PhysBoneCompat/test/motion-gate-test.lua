-- 运动门限（chain-level motion gate）回归
--
-- 这是 2026-11-01 性能优化里收益最大的一条：原来只有"锚点**逐位相同**"才整条链跳过，
-- 而人台/待机角色有极小的动画抖动（远小于 1 mm），逐位相等永远不成立 ⇒ 它们每帧都在跑
-- 完整求解 + 脏比较。改成"与**上次真正求解时**的锚点比，位移/转角小于门限且尖端静止 ⇒ 跳过"。
--
-- 关键语义（本测试要钉住的）：
--   ① 微小抖动 ⇒ 跳过（省下来的就是这部分）
--   ② 移动超过门限 ⇒ 必须求解（不能漏）
--   ③ 比较基准是"上次求解时"的锚点 ⇒ 缓慢持续漂移会累积到门限，不会永久漏掉
--
-- 用法：luajit tmp/lua-check/motion-gate-test.lua [模块路径]

local path = arg[1] or "HD2PhysBoneCompat/src/physbone_compat.lua"
local M = assert(assert(loadfile(path)))()

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-52s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

local function quiet() return function() end end

-- ---------------------------------------------------------------------------
-- 假引擎：锚点位置由测试直接控制（`engine.anchor`）。
-- ---------------------------------------------------------------------------
local function make_engine()
	local engine = { anchor = { 0, 0, 0 }, writes = 0 }
	local S = { Application = {}, Unit = {}, World = {}, Vector3 = {}, Quaternion = {}, Matrix4x4 = {} }
	local pose = { kind = "pose" }
	local quat = { kind = "quat" }

	S.Application.string_to_id32 = function(text)
		local sum = 0
		for index = 1, #text do sum = sum + text:byte(index) * index end
		return sum * 1000 + #text
	end
	S.Unit.alive = function() return true end
	S.Unit.num_scene_graph_items = function() return 100 end
	S.Unit.has_node = function(_, id) return type(id) == "number" and id > 0 end
	S.Unit.node = function(_, id) return (id % 100) + 1 end
	S.Unit.local_pose = function() return pose end
	S.Unit.world_position = function()
		local a = engine.anchor
		return { x = a[1], y = a[2], z = a[3] }
	end
	S.Unit.world_rotation = function() return { x = 0, y = 0, z = 0, w = 1 } end
	S.Unit.set_local_pose = function() engine.writes = engine.writes + 1 end
	S.Unit.set_local_rotation = function() engine.writes = engine.writes + 1 end
	S.World.update_unit = function() end

	S.Vector3.to_elements = function(v) return v.x, v.y, v.z end
	S.Quaternion.to_elements = function(q) return q.x, q.y, q.z, q.w end
	S.Quaternion.axis_angle = function(axis, angle)
		local s = math.sin(angle / 2)
		return { x = axis.x * s, y = axis.y * s, z = axis.z * s, w = math.cos(angle / 2) }
	end
	S.Quaternion.from_elements = function(x, y, z, w) return { x = x, y = y, z = z, w = w } end
	S.Matrix4x4.to_elements = function() return 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 end
	S.Matrix4x4.is_valid = function() return true end
	S.Matrix4x4.from_quaternion_position = function() return pose end
	setmetatable(S.Vector3, { __call = function(_, x, y, z) return { x = x, y = y, z = z } end })
	return S, engine
end

local RIG = (function()
	local params = { tipMass = 200, yawStiffness = 50, yawDamping = 7,
		pitchStiffness = 50, pitchDamping = 7, angleLimit = 0.611, length = 30 }
	return { bones = {
		{ index = 1, name = "a/source_extra_1", parent = 0, parentName = "head",
			pos = { 0, 0, 0 }, params = params, flags = 0x31 },
		{ index = 2, name = "a/source_extra_2", parent = 1, parentName = "a/source_extra_1",
			pos = { 0, -0.1, 0 }, params = params, flags = 0x31 },
	} }
end)()

local function attach(S)
	return M.attach(S, {}, RIG, { logger = quiet(), world = {} })
end

-- 让尖端**先静止**再测运动门限：门限的判据是"锚点没动 **且** 尖端已静止"，
-- 弹簧还在摆的时候不该跳过（否则会冻结在半空）。这里驱动到 maxSpeed2 收敛。
local function settle(session, S, engine)
	for _ = 1, 240 do M.update(session, 1 / 60) end
end

-- ---------------------------------------------------------------------------
print("T1 锚点有亚毫米抖动 ⇒ 整条链跳过（原来逐位相等抓不到）")
do
	local S, engine = make_engine()
	local session = attach(S)
	settle(session, S, engine)                -- 先让尖端静止
	M.update(session, 1 / 60)                 -- 首帧建立基准
	local baseline = engine.writes
	check("首帧有写入", baseline > 0, string.format("写入=%d", baseline))

	for frame = 1, 30 do
		-- 在 0 附近**来回抖** ±0.1 mm（真实待机动画就是这样：抖动不累积）
		engine.anchor[1] = math.sin(frame) * 0.0001
		M.update(session, 1 / 60)
	end
	local extra = engine.writes - baseline
	check("亚毫米抖动不再触发写入", extra == 0, string.format("30 帧新增写入=%d", extra))
	check("跳过数被记录（handled 不为 0）", (session.skippedSame or 0) > 0,
		"skippedSame=" .. tostring(session.skippedSame))
end

-- ---------------------------------------------------------------------------
print("T2 移动超过门限 ⇒ 必须重新求解")
do
	local S, engine = make_engine()
	local session = attach(S)
	settle(session, S, engine)
	M.update(session, 1 / 60)
	local baseline = engine.writes
	engine.anchor[2] = 0.05                    -- 5 cm，远超 1 mm
	M.update(session, 1 / 60)
	check("超过门限后恢复写入", engine.writes > baseline,
		string.format("%d → %d", baseline, engine.writes))
end

-- ---------------------------------------------------------------------------
print("T3 缓慢持续漂移会累积到门限（比较基准是「上次求解时」，不会永久漏掉）")
do
	local S, engine = make_engine()
	local session = attach(S)
	settle(session, S, engine)
	M.update(session, 1 / 60)
	local baseline = engine.writes
	-- 每帧 0.05 mm（单帧远小于门限），累积 100 帧 = 5 mm > 1 mm
	local solved = 0
	local last = engine.writes
	for _ = 1, 100 do
		engine.anchor[3] = engine.anchor[3] + 0.00005
		M.update(session, 1 / 60)
		if engine.writes > last then solved = solved + 1 end
		last = engine.writes
	end
	check("缓慢漂移期间仍会周期性求解", solved > 0,
		string.format("100 帧里触发了 %d 次求解", solved))
	check("但远少于每帧都求解", solved < 100, string.format("触发 %d / 100 帧", solved))
end

-- ---------------------------------------------------------------------------
print("T4 门限可配置：调大到 1 米后 5 cm 位移也算静止")
do
	local S, engine = make_engine()
	local session = attach(S)
	session.motionEpsilon = 1.0
	settle(session, S, engine)
	M.update(session, 1 / 60)
	local baseline = engine.writes
	engine.anchor[2] = 0.05
	M.update(session, 1 / 60)
	check("大范围门限下不再写入", engine.writes == baseline,
		string.format("%d → %d", baseline, engine.writes))
end

print("")
if failures == 0 then
	print("=== 全部通过 ===")
else
	print(string.format("=== %d 项失败 ===", failures))
	os.exit(1)
end
