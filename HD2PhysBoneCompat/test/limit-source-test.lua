-- 限位来源：源模型的三种约束形式，求解器必须都认。
--
-- 背景（2026-11-01 实机）：一份裙摆/缎带资产 55 根骨里有 21 根用的是 `yawCon|pitchCon`
-- （没有 `angleCon`），它们的 `angleLimit` 字段是 0。旧实现只读 `angleLimit`，于是把这 21 根
-- 当成**完全不限位**，实机单节转角冲到 174°——画面上就是头发/缎带乱飞。
-- 同一批的另外三套资产全带 `angleCon`，只有 34~39°，所以问题表现为"有的模型正常、有的乱飘"。
--
-- 用法：luajit tmp/lua-check/limit-source-test.lua

local Compat = assert(assert(loadfile("HD2PhysBoneCompat/src/physbone_compat.lua"))())
local DEG = 180 / math.pi

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-52s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

local function near(a, b) return math.abs(a - b) < 1e-6 end

local function params(overrides)
	local base = {
		length = 30, tipMass = 1,
		yawStiffness = 50, yawDamping = 7,
		pitchStiffness = 50, pitchDamping = 7,
		alongStiffness = 0, alongDamping = 0,
		angleLimit = 0,
		minYaw = 0, maxYaw = 0, yawFriction = 0, yawBounce = 0,
		minPitch = 0, maxPitch = 0, pitchFriction = 0, pitchBounce = 0,
	}
	for key, value in pairs(overrides or {}) do base[key] = value end
	return base
end

print("T1 带 angleCon ⇒ 用锥（行为与以前完全一致）")
do
	local gains = Compat.gains_of(
		params({ angleLimit = 0.611 }),
		nil, nil, { flags = 0x31, normalize = false })
	check("limitSource = cone", gains.limitSource == "cone", gains.limitSource)
	check("limit 取 angleLimit", near(gains.limit, 0.611), string.format("%.3f", gains.limit))
end

print("T2 只有 yawCon/pitchCon ⇒ 按轴分别夹（以前是无限位）")
do
	-- 实测样本：Side.L  yaw ±15°、pitch 0..30°；flags=0x2d 没有 angleCon
	local gains = Compat.gains_of(
		params({ minYaw = -15 / DEG, maxYaw = 15 / DEG, minPitch = 0, maxPitch = 30 / DEG }),
		nil, nil, { flags = 0x2d, normalize = false })
	check("limitSource = yawPitchAxes", gains.limitSource == "yawPitchAxes", gains.limitSource)
	check("axisLimits 打开", gains.axisLimits == true)
	check("yaw 上限 = sin(15°)", near(gains.limitYaw, math.sin(15 / DEG)),
		string.format("%.4f", gains.limitYaw))
	check("pitch 上限 = sin(30°)", near(gains.limitPitch, math.sin(30 / DEG)),
		string.format("%.4f", gains.limitPitch))
	check("不再是 0（旧实现就是 0 = 无限位）", gains.limitYaw > 0 and gains.limitPitch > 0)
end

print("T3 负向范围也算进去")
do
	-- Back_S.L：yaw ±30°、pitch −45..0 ⇒ 外包 45°
	local gains = Compat.gains_of(
		params({ minYaw = -30 / DEG, maxYaw = 30 / DEG, minPitch = -45 / DEG, maxPitch = 0 }),
		nil, nil, { flags = 0x2d, normalize = false })
	check("pitch 上限 = sin(45°)（负向也算）", near(gains.limitPitch, math.sin(45 / DEG)),
		string.format("%.4f", gains.limitPitch))
	check("yaw 上限 = sin(30°)", near(gains.limitYaw, math.sin(30 / DEG)),
		string.format("%.4f", gains.limitYaw))
end

print("T4 三种约束都没有 ⇒ 才真的不限位")
do
	local gains = Compat.gains_of(params({}), nil, nil, { flags = 0x01, normalize = false })
	check("limit = 0", gains.limit == 0)
	check("axisLimits 关闭", gains.axisLimits == false)
	check("limitSource = none", gains.limitSource == "none", gains.limitSource)
end

print("T5 不给 flags ⇒ 保持旧行为（只看 angleLimit），纯数学测试不受影响")
do
	local gains = Compat.gains_of(
		params({ angleLimit = 0, minYaw = -20 / DEG, maxYaw = 20 / DEG }),
		nil, nil, { normalize = false })
	check("没有 flags 时不猜分轴范围", gains.limit == 0)
end

print("T6 angleCon 但限位写成 0 ⇒ 标成 coneZero，暂不强制（冻住比多晃两下更糟）")
do
	local gains = Compat.gains_of(
		params({ angleLimit = 0 }), nil, nil, { flags = 0x11, normalize = false })
	check("limitSource = coneZero", gains.limitSource == "coneZero", gains.limitSource)
	check("limit 仍为 0（不冻住）", gains.limit == 0)
end

print("T7 锥限位 ≥180° 仍然归一成不限位（袖子的 360°）")
do
	local gains = Compat.gains_of(
		params({ angleLimit = 2 * math.pi }), nil, nil, { flags = 0x31, normalize = false })
	check("limit = 0", gains.limit == 0)
end

print("")
if failures == 0 then
	print("=== 全部通过 ===")
else
	print(string.format("=== %d 项失败 ===", failures))
	os.exit(1)
end
