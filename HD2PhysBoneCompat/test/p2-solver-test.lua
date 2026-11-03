-- P2 求解器数值回归（纯数字，不进游戏）
--
-- 判据全部对标解析解，依据见 docs/physbone-compat-design-consolidated.md §3/§6：
--   ωn = √k           （Valve 方程 a = k·e − c·v，**没有质量除法**）
--   ζ  = c/(2√k)
--   静垂角 θ = atan(g / (k·L))，g = tipMass × unitScale，L = length × unitScale
--
-- 用法：luajit p2-solver-test.lua [模块路径]

local path = arg[1] or "HD2PhysBoneCompat/src/physbone_compat.lua"
local M = assert(assert(loadfile(path)))()
local q = M._q

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-44s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

local DEG = 180 / math.pi

-- ---------------------------------------------------------------------------
-- 造一条"从锚点竖直向下"的三节链，长度各自独立（与真实资产同形）
-- ---------------------------------------------------------------------------
local function make_chain(count, params, unitScale)
	unitScale = unitScale or (0.75 * 0.0254)
	local item = { bones = {}, tips = {}, segDirLocal = {}, dirs = {} }
	for k = 1, count do
		item.bones[k] = {
			gains = M.gains_of(params, unitScale, nil, { normalize = false }),
			offLocal = { 0, 0, -0.2 * (k - 1) },   -- 每节沿 -Z 排开
			frameLocal = { 1, 0, 0, 0, 1, 0 },      -- 下面覆盖
		}
		item.tips[k] = { ready = false, px = 0, py = 0, pz = 0, vx = 0, vy = 0, vz = 0 }
		item.segDirLocal[k] = { 0, 0, -1 }         -- 竖直向下
	end
	-- forward = -Z 时：left = -X，up = +Y（与 attach 里的叉乘一致）
	for k = 1, count do item.bones[k].frameLocal = { -1, 0, 0, 0, 1, 0 } end
	return item
end

local function dir_of(item, k)
	local d = item.dirs[k]
	return d[1], d[2], d[3]
end

local function angle_between(ax, ay, az, bx, by, bz)
	local dot = math.max(-1, math.min(1, ax * bx + ay * by + az * bz))
	return math.acos(dot)
end

-- 让锚点带一个"把 -Z 转到 +X"的旋转（绕 Y 轴 -90°），于是链朝 +X 伸展，重力 -Z 会产生下垂
local function anchor_sideways()
	-- 绕 +Y 转 -90°：-Z → +X
	return q.axis_angle(0, 1, 0, -math.pi / 2)
end

local function step_n(item, n, dt, ax, ay, az, qx, qy, qz, qw, hook)
	for i = 1, n do
		M.step_tip(item, dt, ax, ay, az, qx, qy, qz, qw)
		if hook then hook(i) end
	end
end

-- ---------------------------------------------------------------------------
-- T1 固有频率：无重力、给一个初始偏角，数零穿越
-- ---------------------------------------------------------------------------
print("T1 固有频率（对标 f0 = √k / 2π）")
do
	local params = { yawStiffness = 50, yawDamping = 0, pitchStiffness = 50, pitchDamping = 0,
		tipMass = 0, length = 30, angleLimit = 1.0 }
	local item = make_chain(1, params)
	local dt = 1 / 240
	-- 先让尖端就位，再用"把锚点整体转过一小角"的方式给出初始偏角
	local qx, qy, qz, qw = anchor_sideways()
	M.step_tip(item, dt, 0, 0, 0, qx, qy, qz, qw)      -- ready
	-- 直接把尖端推到偏离 5° 的位置（沿 up 轴 = +Y）
	local L = item.bones[1].gains.L
	local tilt = 5 / DEG
	local d = item.dirs[1]
	local target = { d[1] * math.cos(tilt), d[2] + math.sin(tilt), d[3] * math.cos(tilt) }
	local len = math.sqrt(target[1] ^ 2 + target[2] ^ 2 + target[3] ^ 2)
	item.tips[1].px = L * target[1] / len
	item.tips[1].py = L * target[2] / len
	item.tips[1].pz = L * target[3] / len

	local crossingTimes, prevSign = {}, nil
	local duration = 6.0
	local steps = math.floor(duration / dt)
	for i = 1, steps do
		M.step_tip(item, dt, 0, 0, 0, qx, qy, qz, qw)
		local ax, ay, az = dir_of(item, 1)
		local ux, uy, uz = q.rot(qx, qy, qz, qw, 0, 1, 0)   -- up 轴
		local value = ax * ux + ay * uy + az * uz
		local sign = value > 0
		if prevSign ~= nil and sign ~= prevSign then crossingTimes[#crossingTimes + 1] = i * dt end
		prevSign = sign
	end
	-- 用首末零穿越的时间差算频率（比"数个数 / 总时长"精确得多：后者把端点的半个周期算丢了）
	local f0 = -1
	if #crossingTimes >= 3 then
		f0 = (#crossingTimes - 1) / (2 * (crossingTimes[#crossingTimes] - crossingTimes[1]))
	end
	local expected = math.sqrt(50) / (2 * math.pi)
	check("f0 与 √k/2π 一致（±3%）", f0 > 0 and math.abs(f0 - expected) / expected < 0.03,
		string.format("实测 %.3f Hz / 解析 %.3f Hz（%d 次零穿越）", f0, expected, #crossingTimes))
end

-- ---------------------------------------------------------------------------
-- T2 阻尼比：量对数衰减率
-- 取 c=2（ζ≈0.14）而不是样本的 c=7（ζ≈0.50）：
-- 后者一个周期就衰减到 4.5%，峰根本不够数 —— 那是**测量方法**的限制，不是求解器的问题。
-- 高阻尼那档另用"更硬更快停"的定性判据。
-- ---------------------------------------------------------------------------
print("T2 阻尼比（对标 ζ = c / (2√k)）")
do
	local k, c = 50, 2
	local params = { yawStiffness = k, yawDamping = c, pitchStiffness = k, pitchDamping = c,
		tipMass = 0, length = 30, angleLimit = 1.0 }
	local item = make_chain(1, params)
	local dt = 1 / 240
	local qx, qy, qz, qw = anchor_sideways()
	M.step_tip(item, dt, 0, 0, 0, qx, qy, qz, qw)
	local L = item.bones[1].gains.L
	local tilt = 5 / DEG
	local d = item.dirs[1]
	local target = { d[1] * math.cos(tilt), d[2] + math.sin(tilt), d[3] * math.cos(tilt) }
	local len = math.sqrt(target[1] ^ 2 + target[2] ^ 2 + target[3] ^ 2)
	item.tips[1].px, item.tips[1].py, item.tips[1].pz = L * target[1] / len, L * target[2] / len, L * target[3] / len

	local values, times = {}, {}
	for i = 1, math.floor(10 / dt) do
		M.step_tip(item, dt, 0, 0, 0, qx, qy, qz, qw)
		local ax, ay, az = dir_of(item, 1)
		local ux, uy, uz = q.rot(qx, qy, qz, qw, 0, 1, 0)
		-- ⚠️ 必须用**带符号**的偏角找峰。用 |偏角| 的话一个周期会出现两个峰，
		-- 周期被算成一半、ζ 也就被算成一半（第一次就是栽在这）。
		values[i] = math.asin(math.max(-1, math.min(1, ax * ux + ay * uy + az * uz)))
		times[i] = i * dt
	end
	local peaks = {}
	for i = 2, #values - 1 do
		if values[i] > values[i - 1] and values[i] > values[i + 1] and values[i] > 1e-3 then
			peaks[#peaks + 1] = { t = times[i], v = values[i] }
		end
	end
	local zeta = -1
	if #peaks >= 3 then
		local first, last = peaks[1], peaks[#peaks]
		-- 对数衰减率：相邻同号峰比值 = exp(-2πζ)，n 个周期就是 n 次
		zeta = math.log(first.v / last.v) / (2 * math.pi * (#peaks - 1))
	end
	local expected = c / (2 * math.sqrt(k))
	check("ζ 与 c/(2√k) 一致（±0.03）", zeta > 0 and math.abs(zeta - expected) < 0.03,
		string.format("实测 %.3f / 解析 %.3f（%d 个同号峰，周期 %.4f s）",
			zeta, expected, #peaks, #peaks >= 2 and (peaks[#peaks].t - peaks[1].t) / (#peaks - 1) or -1))

	-- 高阻尼那档（样本真实参数 c=7 → ζ≈0.50）：应"回弹一两下、1 秒多就停"
	-- 判据用**稳定时间**而不是"零穿越次数"：线性振子的穿越次数是无限的，
	-- 只是幅度越来越小 —— 拿穿越次数当判据是测量方法错，不是求解器错。
	local item2 = make_chain(1, { yawStiffness = 50, yawDamping = 7, pitchStiffness = 50, pitchDamping = 7,
		tipMass = 0, length = 30, angleLimit = 1.0 })
	M.step_tip(item2, dt, 0, 0, 0, qx, qy, qz, qw)
	local d2 = item2.dirs[1]
	local target2 = { d2[1] * math.cos(tilt), d2[2] + math.sin(tilt), d2[3] * math.cos(tilt) }
	item2.tips[1].px, item2.tips[1].py, item2.tips[1].pz = L * target2[1] / len, L * target2[2] / len, L * target2[3] / len
	local overshoots, settleAt = 0, nil
	local last, lastPrev = nil, nil
	for i = 1, math.floor(4 / dt) do
		M.step_tip(item2, dt, 0, 0, 0, qx, qy, qz, qw)
		local ax, ay, az = dir_of(item2, 1)
		local ux, uy, uz = q.rot(qx, qy, qz, qw, 0, 1, 0)
		local signed = math.asin(math.max(-1, math.min(1, ax * ux + ay * uy + az * uz)))
		-- 只数**看得见**的回弹：幅度 > 0.5° 的极值。
		-- 线性振子的过零次数是无限的（幅度越来越小而已），数过零是测量方法错。
		if lastPrev ~= nil and last ~= nil then
			if (last > lastPrev and last > signed) or (last < lastPrev and last < signed) then
				if math.abs(last) * DEG > 0.5 then overshoots = overshoots + 1 end
			end
		end
		lastPrev, last = last, signed
		local t = i * dt
		if math.abs(signed) * DEG < 0.5 then
			if settleAt == nil then settleAt = t end
		else
			settleAt = nil
		end
	end
	check("高阻尼（ζ≈0.50）1.7 秒内稳定到 0.5° 以内", settleAt ~= nil and settleAt < 1.7,
		string.format("稳定于 %.2f s（解析估计 ln(100)/(ζωn) ≈ %.2f s）", settleAt or -1,
			math.log(100) / (0.495 * math.sqrt(50))))
	check("可见回弹 1~3 次（不像筛子也不像钢板）", overshoots >= 1 and overshoots <= 3,
		string.format("幅度 >0.5° 的极值 %d 个", overshoots))
end

-- ---------------------------------------------------------------------------
-- T3 静垂角：只开重力，跑到稳态
-- ---------------------------------------------------------------------------
print("T3 静垂角（对标 θ = atan(g/(k·L))，样本应 ≈ 7.6°）")
do
	local params = { yawStiffness = 50, yawDamping = 7, pitchStiffness = 50, pitchDamping = 7,
		tipMass = 200, length = 30, angleLimit = 1.0 }
	local item = make_chain(1, params)
	local dt = 1 / 240
	local qx, qy, qz, qw = anchor_sideways()   -- 链朝 +X，重力 -Z 让它下垂
	step_n(item, 2000, dt, 0, 0, 0, qx, qy, qz, qw)
	local g = item.bones[1].gains
	local expected = math.atan(g.g / (g.kYaw * g.L))
	-- 稳态方向 vs 动画方向（= 锚点旋转后的 segDir）
	local ax, ay, az = dir_of(item, 1)
	local bx, by, bz = q.rot(qx, qy, qz, qw, 0, 0, -1)
	local angle = angle_between(ax, ay, az, bx, by, bz)
	check("静垂角与 atan(g/(kL)) 一致（±1°）", math.abs(angle - expected) * DEG < 1.0,
		string.format("实测 %.2f° / 解析 %.2f°（g=%.3f m/s², L=%.4f m）",
			angle * DEG, expected * DEG, g.g, g.L))
end

-- ---------------------------------------------------------------------------
-- T4 限位：重力开到很大时，角度必须被 angC 夹住
-- ---------------------------------------------------------------------------
print("T4 角度限位")
do
	local params = { yawStiffness = 50, yawDamping = 7, pitchStiffness = 50, pitchDamping = 7,
		tipMass = 3000, length = 30, angleLimit = 0.35 }
	local item = make_chain(1, params)
	local dt = 1 / 240
	local qx, qy, qz, qw = anchor_sideways()
	step_n(item, 3000, dt, 0, 0, 0, qx, qy, qz, qw)
	local ax, ay, az = dir_of(item, 1)
	local bx, by, bz = q.rot(qx, qy, qz, qw, 0, 0, -1)
	local angle = angle_between(ax, ay, az, bx, by, bz)
	check("不超过 limit（+0.5° 容差）", angle <= 0.35 + 0.5 / DEG,
		string.format("实测 %.2f° / limit %.2f°", angle * DEG, 0.35 * DEG))
end

-- ---------------------------------------------------------------------------
-- T5 链式弯曲：三节链在重力下每节都要转，且累积方向单调变弯
-- ---------------------------------------------------------------------------
print("T5 链式弯曲（三节）")
do
	local params = { yawStiffness = 50, yawDamping = 7, pitchStiffness = 50, pitchDamping = 7,
		tipMass = 200, length = 30, angleLimit = 0.611 }
	local item = make_chain(3, params)
	local dt = 1 / 240
	local qx, qy, qz, qw = anchor_sideways()
	step_n(item, 3000, dt, 0, 0, 0, qx, qy, qz, qw)
	local rotations = M.local_rotations(item, qx, qy, qz, qw)
	local a1, a2, a3 = rotations[1][4], rotations[2][4], rotations[3][4]
	check("每节都转了", a1 > 1e-4 and a2 > 1e-4 and a3 > 1e-4,
		string.format("各节 local 转角 = %.2f° / %.2f° / %.2f°", a1 * DEG, a2 * DEG, a3 * DEG))
	-- 累积世界方向：末节应该比首节更"下垂"
	local d1, d3 = item.dirs[1], item.dirs[3]
	check("末节比首节更下垂（累积弯曲成立）", d3[3] < d1[3] - 1e-4,
		string.format("首节 dir.z=%.4f 末节 dir.z=%.4f", d1[3], d3[3]))
end

-- ---------------------------------------------------------------------------
-- T6 net 旋转必须复现目标方向（这是我先前把合成顺序写错时的抓错断言）
-- ---------------------------------------------------------------------------
print("T6 local_rotations 的净旋转复现目标方向")
do
	local params = { yawStiffness = 50, yawDamping = 7, pitchStiffness = 50, pitchDamping = 7,
		tipMass = 200, length = 30, angleLimit = 0.611 }
	local item = make_chain(3, params)
	local dt = 1 / 240
	local qx, qy, qz, qw = anchor_sideways()
	step_n(item, 1500, dt, 0, 0, 0, qx, qy, qz, qw)
	local rotations = M.local_rotations(item, qx, qy, qz, qw)
	local cqx, cqy, cqz, cqw = q.conj(qx, qy, qz, qw)
	local ax, ay, az, aw = 0, 0, 0, 1
	local worst = 0
	for k = 1, 3 do
		local r = rotations[k]
		local rq = { q.axis_angle(r[1], r[2], r[3], r[4]) }
		ax, ay, az, aw = q.mul(ax, ay, az, aw, rq[1], rq[2], rq[3], rq[4])
		local seg = item.segDirLocal[k]
		local tx, ty, tz = q.rot(ax, ay, az, aw, seg[1], seg[2], seg[3])
		local dx, dy, dz = q.rot(cqx, cqy, cqz, cqw, item.dirs[k][1], item.dirs[k][2], item.dirs[k][3])
		local err = math.sqrt((tx - dx) ^ 2 + (ty - dy) ^ 2 + (tz - dz) ^ 2)
		if err > worst then worst = err end
	end
	check("每节净方向误差 < 1e-6", worst < 1e-6, string.format("最大误差 %.2e", worst))
end

-- ---------------------------------------------------------------------------
-- T7 转动惯性：锚点转 180°，尖端必须滞后（旧核完全做不到这一点）
-- ---------------------------------------------------------------------------
print("T7 转身惯性（旧核缺失的能力）")
do
	local params = { yawStiffness = 50, yawDamping = 7, pitchStiffness = 50, pitchDamping = 7,
		tipMass = 200, length = 30, angleLimit = 1.2 }
	local item = make_chain(3, params)
	local dt = 1 / 240
	-- 先在静止状态收敛
	local q0 = { q.axis_angle(0, 1, 0, -math.pi / 2) }
	step_n(item, 1500, dt, 0, 0, 0, q0[1], q0[2], q0[3], q0[4])
	-- 然后在 0.5 秒内把锚点绕 +Y 转 180°
	local maxLag = 0
	local frames = math.floor(0.5 / dt)
	for i = 1, frames do
		local angle = -math.pi / 2 + (i / frames) * math.pi
		local qq = { q.axis_angle(0, 1, 0, angle) }
		M.step_tip(item, dt, 0, 0, 0, qq[1], qq[2], qq[3], qq[4])
		local ax, ay, az = dir_of(item, 3)
		local bx, by, bz = q.rot(qq[1], qq[2], qq[3], qq[4], item.segDirLocal[3][1], item.segDirLocal[3][2], item.segDirLocal[3][3])
		local lag = angle_between(ax, ay, az, bx, by, bz)
		if lag > maxLag then maxLag = lag end
	end
	check("转身过程中出现明显滞后（>10°）", maxLag * DEG > 10,
		string.format("峰值滞后 %.1f°", maxLag * DEG))
end

-- ---------------------------------------------------------------------------
-- T8 稳定性：12000 步不产生 NaN，且 dt 在大范围内都稳定
-- ---------------------------------------------------------------------------
print("T8 数值稳定性")
do
	local params = { yawStiffness = 50, yawDamping = 7, pitchStiffness = 50, pitchDamping = 7,
		tipMass = 200, length = 30, angleLimit = 0.611 }
	for _, dt in ipairs({ 1 / 240, 1 / 120, 1 / 60, 1 / 30 }) do
		local item = make_chain(3, params)
		local qx, qy, qz, qw = anchor_sideways()
		local bad = 0
		for i = 1, 12000 do
			M.step_tip(item, dt, 0, 0, 0, qx, qy, qz, qw)
			local t = item.tips[1]
			if t.px ~= t.px or t.py ~= t.py or t.pz ~= t.pz then bad = bad + 1 end
		end
		check(string.format("dt=1/%d 无 NaN（12000 步）", math.floor(1 / dt + 0.5)), bad == 0,
			bad == 0 and "ok" or (bad .. " 步出现 NaN"))
	end
end

-- ---------------------------------------------------------------------------
-- T9 单位双比例：长度已经是米、tipMass 仍是源单位时的静垂角
-- 这一条专门盯 2026-10-31 实机踩到的 bug：把同一个 scale 同时用于 length 与 tipMass，
-- 得到 g=200（应为 3.81），静垂角 81.9°，头发被重力顶死在 35° 限位上、总是指向正下方。
-- ---------------------------------------------------------------------------
print("T9 长度/重力双比例（米制 rig）")
do
	local U = 0.75 * 0.0254
	local g = M.gains_of({ yawStiffness = 50, yawDamping = 7, pitchStiffness = 50, pitchDamping = 7,
		tipMass = 200, length = 30 * U, angleLimit = 0.611 }, 1.0, U, { normalize = false })
	check("length 已按米读入（scale=1.0）", math.abs(g.L - 0.5715) < 1e-3, string.format("L=%.4f m", g.L))
	check("重力按源单位换算（≠200）", math.abs(g.g - 3.810) < 0.01, string.format("g=%.3f m/s²（错版本会是 200）", g.g))

	local item = make_chain(1, { yawStiffness = 50, yawDamping = 7, pitchStiffness = 50, pitchDamping = 7,
		tipMass = 200, length = 30 * U, angleLimit = 0.611 })
	-- make_chain 内部用默认双比例，这里改成"米制长度"的量等价格：
	for k = 1, #item.bones do item.bones[k].gains = g end
	local dt = 1 / 240
	local qx, qy, qz, qw = anchor_sideways()
	step_n(item, 2000, dt, 0, 0, 0, qx, qy, qz, qw)
	local ax, ay, az = dir_of(item, 1)
	local bx, by, bz = q.rot(qx, qy, qz, qw, 0, 0, -1)
	local angle = angle_between(ax, ay, az, bx, by, bz)
	check("静垂角 ≈ 7.6°（不是 81.9°）", math.abs(angle * DEG - 7.6) < 1.0,
		string.format("实测 %.2f°", angle * DEG))
end

print("")
if failures == 0 then
	print("=== 全部通过 ===")
else
	print(string.format("=== %d 项失败 ===", failures))
	os.exit(1)
end
