-- 快路径（每骨 2 次引擎调用）自检门回归
--
-- 背景：`Quaternion.from_elements` 在 HD2 里存在，但**参数顺序从未验证**。
-- 用错的顺序不会崩，只会得到错误旋转 —— 最难查的一类问题。
-- 所以快路径必须**先自检**：用已验证的 `axis_angle` 造参考四元数，再按 to_elements 的顺序
-- 用 from_elements 造一个，比两者。一致才启用，否则退回矩阵路径（每骨 5 次调用）。
--
-- 这个测试断言的是"**不满足条件时绝不启用**"，比"启用了更快"更重要。
--
-- 用法：luajit tmp/lua-check/fast-write-test.lua [模块路径]

local path = arg[1] or "HD2PhysBoneCompat/src/physbone_compat.lua"
local M = assert(assert(loadfile(path)))()

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-52s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

local function quiet() return function() end end

-- ---------------------------------------------------------------------------
-- 假引擎。`scramble` 用来模拟"from_elements 的参数顺序不是 xyzw"。
-- ---------------------------------------------------------------------------
local function make_engine(opts)
	opts = opts or {}
	local calls = { setLocalPose = 0, setLocalRotation = 0, fromElements = 0, axisAngle = 0 }
	local S = { Application = {}, Unit = {}, World = {}, Vector3 = {}, Quaternion = {}, Matrix4x4 = {} }
	local pose, vector = { kind = "pose" }, { kind = "vector" }

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
	-- 快路径的**写入语义校验**会读 local_rotation 再原样写回，所以这里必须有它。
	S.Unit.local_rotation = function() return { x = 0, y = 0, z = 0, w = 1 } end
	S.Unit.world_position = function() return vector end
	S.Unit.world_rotation = function() return { x = 0, y = 0, z = 0, w = 1 } end
	S.Unit.set_local_pose = function() calls.setLocalPose = calls.setLocalPose + 1 end
	if opts.withSetLocalRotation ~= false then
		S.Unit.set_local_rotation = function() calls.setLocalRotation = calls.setLocalRotation + 1 end
	end
	S.World.update_unit = function() end

	S.Vector3.to_elements = function() return 0, 0, 0 end
	S.Quaternion.to_elements = function(q) return q.x, q.y, q.z, q.w end
	-- 真四元数：q = (axis*sin(θ/2), cos(θ/2))，轴按 (1,2,3) 归一化后传入
	S.Quaternion.axis_angle = function(axis, angle)
		calls.axisAngle = calls.axisAngle + 1
		local ax, ay, az = axis[1], axis[2], axis[3]
		local s = math.sin(angle / 2)
		return { x = ax * s, y = ay * s, z = az * s, w = math.cos(angle / 2) }
	end
	S.Quaternion.from_elements = function(a, b, c, d)
		calls.fromElements = calls.fromElements + 1
		if opts.scramble then
			-- 模拟"参数顺序不是 xyzw"：把 w 放到第一位
			return { x = d, y = a, z = b, w = c }
		end
		return { x = a, y = b, z = c, w = d }
	end
	S.Matrix4x4.to_elements = function() return 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 end
	S.Matrix4x4.is_valid = function() return true end
	S.Matrix4x4.from_quaternion_position = function() return pose end
	-- Vector3 既可调用也可索引
	setmetatable(S.Vector3, {
		__call = function(_, x, y, z) return { x, y, z } end,
		__index = function(_, key)
			if key == "to_elements" then return S.Vector3.to_elements end
			return nil
		end,
	})
	return S, calls
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

local function drive(S, frames)
	local session = M.attach(S, {}, RIG, { logger = quiet(), world = {} })
	for _ = 1, frames do M.update(session, 1 / 60) end
	return session
end

-- ---------------------------------------------------------------------------
print("T1 没有 set_local_rotation ⇒ 不启用快路径，走矩阵路径")
do
	M._fastWrite = nil
	local S, calls = make_engine({ withSetLocalRotation = false })
	drive(S, 5)
	check("_fastWrite 为 false", M._fastWrite == false, tostring(M._fastWrite))
	check("走的是 set_local_pose", calls.setLocalPose > 0,
		string.format("set_local_pose=%d", calls.setLocalPose))
	check("没有调 from_elements", calls.fromElements == 0,
		string.format("from_elements=%d", calls.fromElements))
end

-- ---------------------------------------------------------------------------
print("T2 from_elements 顺序正确 ⇒ 启用快路径")
do
	M._fastWrite = nil
	local S, calls = make_engine({})
	drive(S, 5)
	check("_fastWrite 为 true", M._fastWrite == true, tostring(M._fastWrite))
	check("走的是 set_local_rotation", calls.setLocalRotation > 0,
		string.format("set_local_rotation=%d", calls.setLocalRotation))
	check("不再调 set_local_pose", calls.setLocalPose == 0,
		string.format("set_local_pose=%d", calls.setLocalPose))
	-- 自检本身用掉 1 次 axis_angle + 1 次 from_elements，之后每骨只多 1 次 from_elements
	check("每骨只多一次 from_elements", calls.fromElements >= calls.setLocalRotation,
		string.format("from_elements=%d set_local_rotation=%d", calls.fromElements, calls.setLocalRotation))
end

-- ---------------------------------------------------------------------------
print("T3 from_elements 顺序错误（自检不过）⇒ 必须退回矩阵路径")
do
	M._fastWrite = nil
	local S, calls = make_engine({ scramble = true })
	drive(S, 5)
	check("_fastWrite 为 false（顺序不对就不启用）", M._fastWrite == false, tostring(M._fastWrite))
	check("退回 set_local_pose", calls.setLocalPose > 0,
		string.format("set_local_pose=%d", calls.setLocalPose))
	check("没有调 set_local_rotation", calls.setLocalRotation == 0,
		string.format("set_local_rotation=%d", calls.setLocalRotation))
end

-- ---------------------------------------------------------------------------
print("T4 自检只跑一次（不会每帧重建参考四元数）")
do
	M._fastWrite = nil
	local S, calls = make_engine({})
	M.probe_fast_write(S, quiet())
	local afterFirst = calls.axisAngle
	for _ = 1, 20 do M.probe_fast_write(S, quiet()) end
	check("后续调用不再触发 axis_angle", calls.axisAngle == afterFirst,
		string.format("axis_angle %d → %d", afterFirst, calls.axisAngle))
end

print("T5 set_local_rotation 会把平移清零（语义不对）⇒ 写入校验必须关掉快路径")
do
	M._fastWrite = nil
	M._fastWriteVerified = nil
	local S, calls = make_engine({})
	-- 模拟"签名/语义不对"：写旋转时把平移也清零了
	local pose = { kind = "pose" }
	S.Matrix4x4.to_elements = function() return 1, 0, 0, 0, 1, 0, 0, 0, 1, 0.1, 0.2, 0.3 end
	S.Unit.local_pose = function() return pose end
	local originalSet = S.Unit.set_local_rotation
	S.Unit.set_local_rotation = function()
		calls.setLocalRotation = calls.setLocalRotation + 1
		-- 之后读到的位姿平移变成 0
		S.Matrix4x4.to_elements = function() return 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 end
	end
	drive(S, 5)
	check("写入校验把快路径关掉了", M._fastWrite == false, tostring(M._fastWrite))
	check("退回 set_local_pose", calls.setLocalPose > 0,
		string.format("set_local_pose=%d", calls.setLocalPose))
	S.Unit.set_local_rotation = originalSet
end

print("")
if failures == 0 then
	print("=== 全部通过 ===")
else
	print(string.format("=== %d 项失败 ===", failures))
	os.exit(1)
end
