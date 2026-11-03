-- 分轴限位的**参考系一致性**：锚点大幅旋转时，偏离静止方向必须仍然受控。
--
-- 钉的是 2026-11-01 第三次实机那个 bug：绑定方向用 goalQ 旋转、left/up 用当前 q 旋转，
-- 两个参考系错开之后 `l·n` / `u·n` 不再是"偏离多少"，而是随机数 ⇒ 每帧都被夹
-- ⇒ 身体一动（转向/走位）头发就被钉住：实机表现"跑起来向前翘起、特别僵硬"。
--
-- 判据：世界朝向锚定的链（flags 同时含 yawCon+pitchCon）静止方向**固定在世界里**，
-- 所以锚点怎么转、平移，各节相对它自己静止方向的偏离都不该超过 limit。
--
-- 用法：luajit tmp/lua-check/axis-limit-frame-test.lua

local Compat = assert(assert(loadfile("HD2PhysBoneCompat/src/physbone_compat.lua"))())
local DEG = 180 / math.pi

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-56s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

local function makeEngine()
	local S = { Application = {}, Unit = {}, World = {}, Vector3 = {}, Quaternion = {}, Matrix4x4 = {} }
	local vector, pose = {}, {}
	local anchor = { x = 0, y = 0, z = 0, qx = 0, qy = 0, qz = 0, qw = 1 }
	S.Application.time_since_launch = function() return 0 end
	S.Application.worlds = function() return { {} } end
	S.Application.string_to_id32 = function(t)
		local s = 0
		for i = 1, #t do s = s + t:byte(i) * i end
		return s * 1000 + #t
	end
	S.World.units = function() return {} end
	S.World.update_unit = function() end
	local unit = { ids = {}, nodeOf = {} }
	S.Unit.alive = function() return true end
	S.Unit.num_scene_graph_items = function() return 10000 end
	S.Unit.has_animation_state_machine = function() return false end
	S.Unit.has_node = function(_, id) return unit.ids[id] == true end
	S.Unit.node = function(_, id) return unit.nodeOf[id] end
	S.Unit.debug_name = function() return "x" end
	S.Unit.name_hash = function() return "x" end
	S.Unit.local_pose = function() return pose end
	S.Unit.local_rotation = function() return { x = 0, y = 0, z = 0, w = 1 } end
	S.Unit.world_position = function() return vector end
	S.Unit.world_rotation = function() return { x = anchor.qx, y = anchor.qy, z = anchor.qz, w = anchor.qw } end
	S.Unit.set_local_pose = function() end
	S.Unit.set_local_rotation = function() end
	S.Vector3.to_elements = function() return anchor.x, anchor.y, anchor.z end
	S.Quaternion.to_elements = function(q) return q.x, q.y, q.z, q.w end
	S.Matrix4x4.to_elements = function() return 1, 0, 0, 0, 1, 0, 0, 0, 1, anchor.x, anchor.y, anchor.z end
	S.Matrix4x4.is_valid = function() return true end
	S.Matrix4x4.from_quaternion_position = function() return pose end
	S.Quaternion.axis_angle = function(a, ang)
		local s = math.sin(ang / 2)
		return { x = 0, y = 0, z = s, w = math.cos(ang / 2) }
	end
	S.Quaternion.from_elements = function(x, y, z, w) return { x = x, y = y, z = z, w = w } end
	setmetatable(S.Vector3, { __call = function() return vector end })
	return S, unit, anchor
end

--- 竖直下垂的 jiggle 链；flags 0x2d = flexible|yawCon|pitchCon|lengthCon（无 angleCon）
local function makeRig(segments, yawDeg, pitchDeg, flags)
	local bones = {}
	for index = 1, segments do
		bones[index] = {
			index = index,
			name = string.format("jframe%07x/bone_%d", 0, index),
			parent = (index == 1) and 0 or (index - 1),
			parentName = (index == 1) and "head" or string.format("jframe%07x/bone_%d", 0, index - 1),
			flags = flags or 0x2d,
			pos = { 0, 0, -0.1 * index },
			params = {
				length = 30, tipMass = 1,
				yawStiffness = 50, yawDamping = 7,
				pitchStiffness = 50, pitchDamping = 7,
				alongStiffness = 0, alongDamping = 0,
				angleLimit = 0,
				minYaw = -yawDeg / DEG, maxYaw = yawDeg / DEG,
				yawFriction = 0, yawBounce = 0,
				minPitch = -pitchDeg / DEG, maxPitch = pitchDeg / DEG,
				pitchFriction = 0, pitchBounce = 0,
			},
		}
	end
	return { bones = bones }
end

--- 锚点一边走一边转（模拟"向前移动"），返回各节相对**静止世界方向**的最大偏离。
local function walkAndMeasure(rig, seconds)
	local S, unit, anchor = makeEngine()
	local nextNode = 1
	for _, bone in ipairs(rig.bones) do
		for _, name in ipairs(Compat.bone_name_aliases(bone.name)) do
			local id = S.Application.string_to_id32(name)
			unit.ids[id] = true
			unit.nodeOf[id] = nextNode
		end
		nextNode = nextNode + 1
	end
	local headId = S.Application.string_to_id32("head")
	unit.ids[headId] = true
	unit.nodeOf[headId] = nextNode

	local session = Compat.attach(S, unit, rig, { world = {}, fastWrite = true, logger = function() end })
	local item = session.sessions[1]
	if item == nil or item.tip == nil then return nil end

	-- 静止参考方向：**跟着头转**。这类骨（0x2d 纯 yaw/pitch 头发）不是世界锚定的，
	-- 头转多少，它的静止方向就转多少 —— 所以每帧用当前锚点朝向把 segDirLocal 转出来。
	-- （曾经这里用世界固定方向，那是对"袖子 0x3d"才成立的假设。）

	local dt = 1 / 100
	local peak = {}
	for frame = 1, math.floor(seconds / dt) do
		local t = frame * dt
		-- 平移：+Y 方向 3 m/s（相当于小跑）
		anchor.x, anchor.y, anchor.z = 0, 3.0 * t, 0
		-- 朝向：绕 Z 轴（世界 up）匀速转，2 秒转一圈
		local yaw = 2 * math.pi * 0.5 * t
		anchor.qx, anchor.qy, anchor.qz, anchor.qw = 0, 0, math.sin(yaw / 2), math.cos(yaw / 2)
		Compat.begin_frame(frame)
		Compat.update(session, dt)
		for k = 1, #item.tip.bones do
			local d = item.tip.dirs[k]
			local b = item.tip.segDirLocal[k]
			if d ~= nil and b ~= nil then
				local bx, by, bz = Compat._q.rot(anchor.qx, anchor.qy, anchor.qz, anchor.qw, b[1], b[2], b[3])
				local dot = d[1] * bx + d[2] * by + d[3] * bz
				if dot > 1 then dot = 1 elseif dot < -1 then dot = -1 end
				local dev = math.acos(dot) * DEG
				if dev > (peak[k] or 0) then peak[k] = dev end
			end
		end
	end
	return peak
end

print("T1 边走边转（3 m/s + 2 秒一圈）：各节偏离静止方向仍受限位约束")
do
	-- 极限情况：yaw 上限 15°、pitch 上限 30° ⇒ 单节合成上界 atan(√(sin²15+sin²30)) ≈ 31.4°
	local peak = walkAndMeasure(makeRig(3, 15, 30), 4)
	local worst = 0
	for k = 1, 3 do if (peak[k] or 0) > worst then worst = peak[k] end end
	check("各节偏离 ≤ 35°（单节合成上界 31.4° + 容差）", worst <= 35,
		string.format("各节 %.1f/%.1f/%.1f°", peak[1] or 0, peak[2] or 0, peak[3] or 0))
	check("没有被钉死（仍有摆动余量 ≥3°）", worst >= 3,
		string.format("%.1f°", worst))
end

print("T2 五节链：静止方向是逐节独立的，不沿链累加")
do
	local peak = walkAndMeasure(makeRig(5, 30, 30), 4)
	local worst = 0
	for k = 1, 5 do if (peak[k] or 0) > worst then worst = peak[k] end end
	-- 30/30 的单节合成上界 atan(√(0.25+0.25)) ≈ 35.3°
	check("各节偏离 ≤ 40°", worst <= 40, string.format("最大 %.1f°", worst))
	check("不会累加到 90°+", worst < 60, string.format("最大 %.1f°", worst))
end

print("")
if failures == 0 then
	print("=== 全部通过 ===")
else
	print(string.format("=== %d 项失败 ===", failures))
	os.exit(1)
end
