-- 裙摆外推的核心数学自检：只验"叠一个旋转"这件事本身对不对。
--
-- 为什么值得单独钉：这段是纯数字运算（轴角 → 四元数 → 乘 → 转回轴角），
-- 写错一个符号的表现是"裙子朝反方向甩出去"或者"整体歪掉"，在游戏里看起来像
-- 物理坏了，很容易误判成求解器的问题。离线钉住就不会浪费实机时间。
--
-- 用法：luajit HD2PhysBoneCompat/test/push-out-test.lua [compat]

local compatPath = arg[1] or "HD2PhysBoneCompat/src/physbone_compat.lua"
local Compat = assert(assert(loadfile(compatPath))())

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-52s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

local function near(a, b, eps)
	return math.abs(a - b) <= (eps or 1e-4)
end

local function sameAxisAngle(got, axisX, axisY, axisZ, angle, eps)
	if got == nil then return false end
	local angleOk = near(got[4], angle, eps or 1e-3)
	-- 轴方向：与期望轴同向或反向都可以（反向时角度取负，等价）
	local dot = got[1] * axisX + got[2] * axisY + got[3] * axisZ
	return angleOk and near(math.abs(dot), 1, eps or 1e-3)
end

-- ① 角度为 0 必须原样返回（热路径上大量走到这里，不能有额外开销/漂移）
do
	local rotation = { 0.6, 0.8, 0, 0.3 }
	local out = Compat.push_out_rotation(rotation, 0, 0, 1, 0)
	check("角度 0 时原样返回", out == rotation)
end

-- ② 单位旋转叠 90° 绕 +Y ⇒ 结果就是 90° 绕 +Y
do
	local identity = { 0, 0, 1, 0 }
	local out = Compat.push_out_rotation(identity, 0, 1, 0, math.pi / 2)
	check("单位旋转 + 90°绕Y = 90°绕Y", sameAxisAngle(out, 0, 1, 0, math.pi / 2),
		out and string.format("axis=(%.3f,%.3f,%.3f) angle=%.1f°",
			out[1], out[2], out[3], out[4] * 180 / math.pi))
end

-- ③ 同一轴连续叠两次 90° ⇒ 180°
do
	local out = Compat.push_out_rotation({ 0, 1, 0, math.pi / 2 }, 0, 1, 0, math.pi / 2)
	check("绕同一轴两次 90° = 180°", out ~= nil and near(out[4], math.pi, 1e-3),
		out and string.format("%.1f°", out[4] * 180 / math.pi))
end

-- ④ 叠一个"反向"的旋转应该抵消回去（这里用 ±5° 的小角，模拟实际调参范围）
do
	local base = { 0.0, 0.0, 1.0, 5 * math.pi / 180 }
	local out = Compat.push_out_rotation(base, 0, 0, 1, -5 * math.pi / 180)
	check("叠反向小角能抵消", out ~= nil and near(out[4], 0, 1e-3),
		out and string.format("%.3f°", out[4] * 180 / math.pi))
end

-- ⑤ 轴必须是归一化的输入也能得出单位轴（调用方传的轴可能没归一）
do
	local out = Compat.push_out_rotation({ 0, 0, 1, 0 }, 0, 3, 0, math.pi / 6)
	local length = out and math.sqrt(out[1] ^ 2 + out[2] ^ 2 + out[3] ^ 2) or 0
	check("未归一化的轴也能算出单位轴", near(length, 1, 1e-3), string.format("|axis|=%.4f", length))
end

-- ⑥ 极端：180° 边界不能出 NaN
do
	local out = Compat.push_out_rotation({ 0, 0, 1, math.pi }, 0, 0, 1, 0.1)
	local ok = out ~= nil
	for index = 1, 4 do
		if out == nil or out[index] ~= out[index] then ok = false end
	end
	check("180° 附近不出 NaN", ok, out and string.format("%.3f", out[4]))
end

print(string.format("\n失败 %d 项", failures))
os.exit(failures == 0 and 0 or 1)
