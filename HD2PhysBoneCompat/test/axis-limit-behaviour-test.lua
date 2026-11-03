-- 分轴限位的**行为**测试：跑真实求解器，验"相对自己绑定方向的偏转"不超限。
--
-- 为什么参数级测试不够：上一轮（1.4.0）的参数映射是对的（yaw/pitch 都读出来了），
-- 但夹取用的是**相对父节**的锥，而锥会沿链累加 —— 3 节各 30° 就能累到 90°，
-- 实机表现就是"左右两侧各一根头发翘起来"。这一轮把基准换成了绑定方向。
-- 参数测试看不出这个差别，必须跑求解器。
--
-- 用法：luajit tmp/lua-check/axis-limit-behaviour-test.lua

local Compat = assert(assert(loadfile("HD2PhysBoneCompat/src/physbone_compat.lua"))())
local DEG = 180 / math.pi

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-52s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

-- ---------------------------------------------------------------------------
-- 假引擎：锚点位置/旋转由 test 每帧写入
-- ---------------------------------------------------------------------------
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

	local unit = { ids = {}, nodeOf = {}, writes = 0 }
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
	S.Unit.set_local_pose = function() unit.writes = unit.writes + 1 end
	S.Unit.set_local_rotation = function() unit.writes = unit.writes + 1 end
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

--- 造一条竖直下垂的 jiggle 链（HD2 世界 +Z 朝上 ⇒ 下垂是 -Z）。
local function makeRig(segmentCount, yawDeg, pitchDeg, flags)
	local bones = {}
	for index = 1, segmentCount do
		bones[index] = {
			index = index,
			name = string.format("jtest%08x/bone_%d", 0, index),
			parent = (index == 1) and 0 or (index - 1),
			parentName = (index == 1) and "head" or string.format("jtest%08x/bone_%d", 0, index - 1),
			flags = flags or 0x2d,   -- flexible|yawCon|pitchCon|lengthCon（没有 angleCon）
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

--- 跑一段锚点摆动，返回**每节**相对自己绑定方向的最大偏离（度）。
local function run(rig, swayDeg, frames)
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
	if item == nil or item.tip == nil then return nil, nil end

	local dt = 1 / 100
	local peakPerSegment = {}
	for _ = 1, frames do
		local t = _ * dt
		local a = math.rad(swayDeg) * math.sin(2 * math.pi * 1.0 * t)
		anchor.qx, anchor.qy, anchor.qz, anchor.qw = 0, 0, math.sin(a / 2), math.cos(a / 2)
		Compat.begin_frame(_)
		Compat.update(session, dt)
		for k = 1, #item.tip.bones do
			local d, b = item.tip.dirs[k], item.tip.segDirLocal[k]
			if d ~= nil and b ~= nil then
				-- 静止方向跟着锚点转（这类骨不是世界锚定的），所以先把 b 转到当前朝向再比。
				local bx, by, bz = Compat._q.rot(anchor.qx, anchor.qy, anchor.qz, anchor.qw, b[1], b[2], b[3])
				local dot = d[1] * bx + d[2] * by + d[3] * bz
				if dot > 1 then dot = 1 elseif dot < -1 then dot = -1 end
				local dev = math.acos(dot) * DEG
				if dev > (peakPerSegment[k] or 0) then peakPerSegment[k] = dev end
			end
		end
	end
	return peakPerSegment, item
end

print("T1 单节：分轴限位后偏离绑定方向不超过给定角")
do
	local peak = run(makeRig(1, 15, 30), 40, 600)
	check("单节偏离 ≤ 30°（pitch 上限）", peak[1] <= 30 + 2,
		string.format("%.1f°", peak[1]))
end

print("T2 三节各 30°：不能沿链累加（这正是「翘起来」的成因）")
do
	-- 每节单独 ≤30°，但 3 节串联后整条链的末节如果被允许累加，能偏离绑定 90°。
	local peak = run(makeRig(3, 30, 30), 40, 600)
	local worst = math.max(peak[1] or 0, peak[2] or 0, peak[3] or 0)
	check("每节都不超过 30°+容差", worst <= 32,
		string.format("各节 %.1f/%.1f/%.1f°", peak[1] or 0, peak[2] or 0, peak[3] or 0))
	check("不会累加到 90°", worst < 60, string.format("最大 %.1f°", worst))
end

print("T3 五节各 45°：仍然逐节受限")
do
	local peak = run(makeRig(5, 30, 45), 40, 600)
	local worst = 0
	for index = 1, 5 do
		if (peak[index] or 0) > worst then worst = peak[index] end
	end
	check("每节不超过 45°+容差", worst <= 47, string.format("最大 %.1f°", worst))
end

print("T4 带 angleCon 的骨仍然走锥（行为不变）")
do
	local rig = makeRig(2, 15, 30, 0x31)
	for _, bone in ipairs(rig.bones) do bone.params.angleLimit = 20 / DEG end
	local peak = run(rig, 40, 600)
	check("有结果", peak ~= nil and peak[1] ~= nil)
	check("照样受锥约束（不会甩到 90°）", (peak[1] or 0) < 60,
		string.format("%.1f°", peak[1] or 0))
end

print("")
if failures == 0 then
	print("=== 全部通过 ===")
else
	print(string.format("=== %d 项失败 ===", failures))
	os.exit(1)
end
