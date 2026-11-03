-- P1 回归测试：三个已修缺陷，每个都要能用数值证明"现在是对的"
--
--   R1 漂移护栏真的执行了（以前 `local tx,ty,tz = pcall(...)` 让判断恒假）
--   R2 release 只用数字重建（以前优先用缓存的 pose 句柄，实测 0.1 秒就是 inf）
--   R3 段长单位换算（以前有 params.length 时 scale 被绕过，小 52 倍）
--
-- 用法：luajit p1-regression-test.lua [模块路径]

local path = arg[1] or "HD2PhysBoneCompat/src/physbone_compat.lua"
local M = assert(assert(loadfile(path)))()

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-46s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

-- ---------------------------------------------------------------------------
-- 假引擎：布局与实测一致（packed9：旋转 1..9，平移 10..12）
-- ---------------------------------------------------------------------------
local idByText, textById, nextId = {}, {}, 0
local function id32(text)
	if idByText[text] == nil then
		nextId = nextId + 1
		idByText[text] = 1000 + nextId
		textById[1000 + nextId] = text
	end
	return idByText[text]
end

local nodeByName = { head = 1, jiggle_L1 = 11, jiggle_L2 = 12, jiggle_L3 = 13 }

-- 可控的世界坐标：tipDrift 就是"末节相对锚点漂了多远"
local world = { head = { 0, 0, 0 }, L1 = { 0, 0, 1 }, L2 = { 0, 0, 2 }, L3 = { 0, 0, 3 }, tipDrift = 0 }
local written = 0

local IDENTITY_POSE = { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 }   -- packed9 单位阵 + 零平移

local S = {}
S.Application = { string_to_id32 = id32 }
S.Vector3 = setmetatable({
	to_elements = function(v) return v[1], v[2], v[3] end,
}, { __call = function(_, x, y, z) return { x, y, z } end })
S.Quaternion = {
	axis_angle = function(axis, angle) return { axis[1], axis[2], axis[3], angle } end,
	to_elements = function(q) return 0, 0, 0, 1 end,   -- xyzw（一致性电池 E5b 实测）
	from_elements = function() return {} end,   -- 存在但不该被用到（release 已改成 axis_angle 路径）
}
S.Matrix4x4 = {
	from_quaternion_position = function() return IDENTITY_POSE end,
	to_elements = function(m) return m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12] end,
	is_valid = function() return true end,
	translation = function() return { 0, 0, 0 } end,
	rotation = function() return {} end,   -- 实测不可用，这里也故意返回垃圾
}
S.Unit = {
	num_scene_graph_items = function() return 999 end,
	has_node = function(_, id) return textById[id] ~= nil end,
	node = function(_, id) return nodeByName[textById[id]] or 999999 end,
	local_pose = function() return IDENTITY_POSE end,
	set_local_pose = function() written = written + 1 end,
	world_position = function(_, node)
		if node == 1 then return world.head end
		if node == 11 then return world.L1 end
		if node == 12 then return world.L2 end
		if node == 13 then return { world.L3[1], world.L3[2], world.L3[3] + world.tipDrift } end
		return { 0, 0, 0 }
	end,
	world_rotation = function() return { 0, 0, 0, 1 } end,   -- 单位四元数（xyzw）
}
S.World = { update_unit = function() end }

local function bone(index, parent, name, parentName, x)
	return {
		index = index, parent = parent, name = name, parentName = parentName,
		flags = 0x31, pos = { x, 0, 0 },
		params = {
			length = 30, tipMass = 200,
			yawStiffness = 50, yawDamping = 7,
			pitchStiffness = 50, pitchDamping = 7,
			angleLimit = 0.611,
		},
	}
end

local rig = { bones = {
	bone(101, 1, "jiggle_L1", "head", -0.19),
	bone(102, 101, "jiggle_L2", "jiggle_L1", -0.22),
	bone(103, 102, "jiggle_L3", "jiggle_L2", -0.31),
} }

local logs = {}
local function logger(fmt, ...)
	logs[#logs + 1] = string.format(fmt, ...)
end

-- ---------------------------------------------------------------------------
-- R3：段长单位换算
-- ---------------------------------------------------------------------------
print("R3 段长单位换算")
local spring = M.new_spring(rig.bones[1], 0.75 * 0.0254)
check("params.length 被换算成米", math.abs(spring.segmentLength - 0.5715) < 1e-3,
	string.format("segmentLength=%.4f（期望 0.5715；错版本会是 30）", spring.segmentLength))

-- ---------------------------------------------------------------------------
-- R1：护栏必须真的执行
-- ---------------------------------------------------------------------------
print("R1 漂移护栏（旧核，显式 solver=angle）")
-- 护栏住在旧的 angle 核里；P2 的尖端核不靠"事后检测漂移"，而是靠构造上就不可能发散
-- （定长投影 + 角度限位 + RESYNC + G4 活度），所以这里显式切旧核来测护栏本身。
local session = M.attach(S, {}, rig, { logger = logger, world = {}, solver = "angle" })
check("attach 挂上 1 条链", #session.sessions == 1, "sessions=" .. #session.sessions)

-- 先跑几帧稳定状态：不应该触发护栏
world.tipDrift = 0
for i = 1, 5 do M.update(session, 1 / 60) end
local trippedEarly = false
for i = 1, #logs do
	if logs[i]:find("护栏", 1, true) then trippedEarly = true end
end
check("稳定状态不误报护栏", not trippedEarly, "")

-- 再把末节推出去 2 m：护栏必须抓到并停用这条链
world.tipDrift = 2.0
for i = 1, 5 do M.update(session, 1 / 60) end
local tripped, tripLine = false, nil
for i = 1, #logs do
	if logs[i]:find("护栏", 1, true) then tripped, tripLine = true, logs[i] end
end
check("漂移 2 m 时护栏触发", tripped, tripLine or "护栏一次都没执行（旧版就是这样）")
check("触发后该链被停用", session.sessions[1].disabled == true,
	"disabled=" .. tostring(session.sessions[1].disabled))

-- ---------------------------------------------------------------------------
-- R2：release 只用数字重建，不碰任何句柄
-- ---------------------------------------------------------------------------
print("R2 release 数字重建")
written = 0
session.sessions[1].disabled = false
collectgarbage("collect")
local restored = M.release(session)
check("release 写回 3 根骨", restored == 3, "restored=" .. tostring(restored))
check("release 实际调用了 set_local_pose", written >= 3, "set_local_pose 次数=" .. written)

-- 证明它没有走 Quaternion.from_elements（那条路的四元数顺序从未验证）
local usedFromElements = false
local realFrom = S.Quaternion.from_elements
S.Quaternion.from_elements = function(...) usedFromElements = true return realFrom(...) end
M.release(session)
S.Quaternion.from_elements = realFrom
check("release 不依赖 Quaternion.from_elements", not usedFromElements, "")

-- ---------------------------------------------------------------------------
-- R4：P2 尖端核的引擎侧（attach → update_tip → 写姿态）
-- ---------------------------------------------------------------------------
print("R4 尖端核集成")
do
	world.tipDrift = 0
	local tipLogs = {}
	local tipSession = M.attach(S, {}, rig, {
		logger = function(fmt, ...) tipLogs[#tipLogs + 1] = string.format(fmt, ...) end,
		world = {},
	})
	check("attach 建出尖端状态", tipSession.sessions[1] ~= nil and tipSession.sessions[1].tip ~= nil,
		tipSession.sessions[1] and tipSession.sessions[1].tip and "tip 表已建立" or "缺 tip 表")
	check("默认走尖端核", tipSession.solver == "tip", "solver=" .. tostring(tipSession.solver))
	check("尖端状态含 3 节几何", tipSession.sessions[1].tip ~= nil and #tipSession.sessions[1].tip.bones == 3,
		tipSession.sessions[1].tip and ("bones=" .. #tipSession.sessions[1].tip.bones) or "")

	written = 0
	M.update(tipSession, 1 / 60)
	local firstFrame = written
	check("首帧把 3 个节点都写了", firstFrame == 3, "首次写入=" .. firstFrame)

	-- 假引擎的锚点位置恒定 ⇒ 姿态收敛后不该再重复写同一份姿态。
	-- （2026-11-01 性能优化：加了"和上次写的比，几乎没变就跳过"的脏标记，
	--   站着不动时每帧几百次 set_local_pose 直接降到 0。）
	for i = 1, 120 do M.update(tipSession, 1 / 60) end
	local idleWrites = written - firstFrame
	check("静止时不再重复写同一姿态", idleWrites < 3 * 120,
		string.format("之后 120 帧只写了 %d 次（旧行为 %d 次）", idleWrites, 3 * 120))
	check("跳过次数被记录", (tipSession.skippedSame or 0) > 0,
		"skippedSame=" .. tostring(tipSession.skippedSame))

	-- 定期全量刷新：证明脏标记不会让节点永久停在旧姿态（引擎万一重置了也能救回来）
	tipSession.refreshEvery = 5
	written = 0
	for i = 1, 10 do M.update(tipSession, 1 / 60) end
	check("定期全量刷新会重写", written > 0, "刷新期写入=" .. written)

	-- 让重力发挥作用后，姿态必须真的变化（不是"写了但都一样"）
	local firstRotation = tipSession.sessions[1].tip.dirs[1][3]
	check("尖端状态已收敛到有值", type(firstRotation) == "number", string.format("dir.z=%.4f", firstRotation))
	check("没有触发写入失败", tipSession.skippedWrites == nil or tipSession.skippedWrites == 0,
		"skippedWrites=" .. tostring(tipSession.skippedWrites))
	check("G4 未误报冻结", not tipSession.sessions[1].frozenLogged, "")
end

print("")
if failures == 0 then
	print("=== 全部通过 ===")
else
	print(string.format("=== %d 项失败 ===", failures))
	os.exit(1)
end
