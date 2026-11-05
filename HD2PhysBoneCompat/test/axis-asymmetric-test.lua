-- 分轴限位：**带符号** + 与锥**叠加**
--
-- 2026-11-05 实机（L4D2-3831123094 的头发）：作者给的是单边 yaw 区间 ——
--   `Bn_r_cefa01/02`  yaw [0, +10°]（只能往外）
--   `Bn_liuhaia01…`  yaw [−10°, 0]（只能往另一边）
-- 旧实现做两件事把它抹平了：
--   ① flags 带 angleCon（0x10）时**根本不看分轴**，只用锥；
--   ② 就算看，也取 `max(|min|,|max|)` 变成对称 ±10° ⇒ "只能往外"变成"里外都行"，
--      实机就是头发朝脸那一侧甩。
--
-- 现在按 Valve 的语义：yawCon / pitchCon / angleCon 各自独立叠加，区间保留符号。
--
-- 用法：luajit HD2PhysBoneCompat/test/axis-asymmetric-test.lua [模块路径]

local path = arg[1] or "HD2PhysBoneCompat/src/physbone_compat.lua"
local M = assert(assert(loadfile(path)))()

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-56s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

local DEG = math.pi / 180
local FLAGS_HAIR = 0x3D   -- flexible|yawCon|pitchCon|angleCon|lengthCon（实测这批头发）
local FLAGS_SLEEVE = 0x3D -- 同一组 flag，区别只在范围

local function params(t)
	local p = {
		length = 10, tipMass = 0,
		yawStiffness = 50, yawDamping = 7,
		pitchStiffness = 50, pitchDamping = 7,
		angleLimit = 0,
		minYaw = 0, maxYaw = 0, minPitch = 0, maxPitch = 0,
	}
	for k, v in pairs(t or {}) do p[k] = v end
	return p
end

local function near(a, b) return type(a) == "number" and math.abs(a - b) < 1e-9 end

print("① 单边 yaw：作者写 [0, +10°] 就必须是 [0, +sin10°]")
do
	local g = M.gains_of(params({ angleLimit = 10 * DEG, maxYaw = 10 * DEG, minPitch = -10 * DEG, maxPitch = 10 * DEG }),
		1.0, nil, { flags = FLAGS_HAIR })
	check("axisLimits 打开（即使有锥）", g.axisLimits == true, tostring(g.limitSource))
	check("yaw 下界 = 0（不许往内）", near(g.limitYawMin, 0), tostring(g.limitYawMin))
	check("yaw 上界 = sin(10°)", near(g.limitYawMax, math.sin(10 * DEG)), tostring(g.limitYawMax))
	check("pitch 仍然对称 ±sin(10°)",
		near(g.limitPitchMin, -math.sin(10 * DEG)) and near(g.limitPitchMax, math.sin(10 * DEG)),
		string.format("%+.4f..%+.4f", g.limitPitchMin, g.limitPitchMax))
	check("总偏角上限 = sin(10°)", near(g.sinTotal, math.sin(10 * DEG)), tostring(g.sinTotal))
end

print()
print("② 另一边：作者写 [−10°, 0]")
do
	local g = M.gains_of(params({ angleLimit = 10 * DEG, minYaw = -10 * DEG }),
		1.0, nil, { flags = FLAGS_HAIR })
	check("yaw 上界 = 0", near(g.limitYawMax, 0), tostring(g.limitYawMax))
	check("yaw 下界 = −sin(10°)", near(g.limitYawMin, -math.sin(10 * DEG)), tostring(g.limitYawMin))
end

print()
print("③ 对称范围：行为必须和旧实现完全一样")
do
	local g = M.gains_of(params({ angleLimit = 0, minYaw = -15 * DEG, maxYaw = 15 * DEG,
		minPitch = -30 * DEG, maxPitch = 30 * DEG }), 1.0, nil, { flags = 0x2D })
	check("yaw = ±sin(15°)", near(g.limitYawMin, -math.sin(15 * DEG)) and near(g.limitYawMax, math.sin(15 * DEG)),
		string.format("%+.4f..%+.4f", g.limitYawMin, g.limitYawMax))
	check("pitch = ±sin(30°)", near(g.limitPitchMin, -math.sin(30 * DEG)) and near(g.limitPitchMax, math.sin(30 * DEG)),
		string.format("%+.4f..%+.4f", g.limitPitchMin, g.limitPitchMax))
	check("没有锥时 sinTotal = 0（不加额外限制）", near(g.sinTotal, 0), tostring(g.sinTotal))
end

print()
print("④ 袖子的 360°：锥归一成不限位，分轴照旧是 ±120°")
do
	local g = M.gains_of(params({ angleLimit = math.pi * 2,
		minYaw = -120 * DEG, maxYaw = 120 * DEG,
		minPitch = -120 * DEG, maxPitch = 120 * DEG }), 1.0, nil, { flags = FLAGS_SLEEVE })
	check("锥 = 0（不限位）", near(g.limit, 0), tostring(g.limit))
	check("sinTotal = 0", near(g.sinTotal, 0), tostring(g.sinTotal))
	check("yaw = ±sin(120°)（与旧实现一致）",
		near(g.limitYawMin, -math.sin(120 * DEG)) and near(g.limitYawMax, math.sin(120 * DEG)),
		string.format("%+.4f..%+.4f", g.limitYawMin, g.limitYawMax))
end

print()
print("⑤ 没有分轴范围 ⇒ 不算分轴限位（T4 的老规矩不变）")
do
	local g = M.gains_of(params({ angleLimit = 0 }), 1.0, nil, { flags = 0x2D })
	check("axisLimits 关闭", g.axisLimits == false, tostring(g.limitSource))
	check("limitSource = none", g.limitSource == "none", tostring(g.limitSource))
end

print()
print("⑥ limitRelax 仍然按倍数放宽（含单边）")
do
	local g = M.gains_of(params({ angleLimit = 10 * DEG, maxYaw = 10 * DEG }),
		1.0, nil, { flags = FLAGS_HAIR, limitRelax = 2 })
	check("上界放宽到 sin(20°)", near(g.limitYawMax, math.sin(20 * DEG)), tostring(g.limitYawMax))
	check("下界仍是 0", near(g.limitYawMin, 0), tostring(g.limitYawMin))
end

print()
if failures == 0 then
	print("axis-asymmetric: 全部通过")
	os.exit(0)
else
	print(string.format("axis-asymmetric: %d 条失败", failures))
	os.exit(1)
end
