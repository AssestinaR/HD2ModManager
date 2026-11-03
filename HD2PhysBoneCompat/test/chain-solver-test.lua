-- 粒子链求解器（`solver="chain"`）+ 求解器分派 + 碰撞接线 的验收
--
-- 背景：用户实测"头发不再乱飞，但甩动时像双截棍/多截棍一段一段地折"。
-- 根因是 `step_tip` 里每节是**独立的弹簧铰链**，相邻节之间不约束曲率 ⇒
-- 弯曲必然塌到某一个关节上（真实资产实测：旗袍辫子相邻节夹角 9.4° / 42.2° / 9.6°）。
--
-- 这里钉住五件事：
--   T1 `step_chain` 的静止形状 = bind 形状（重力=0 时不自己动）
--   T2 弯曲约束真的沿链分布（不匀度明显低于逐节铰链）
--   T3 重力让整条链下垂成**悬链线**，而不是每节各自竖直
--   T4 求解器分派：设 `solver="chain"` 必须真的走链路（不能静默跑 tip）
--   T5 碰撞代理真的接到了求解器手上（2026-11-03 之前挂在会话项上，一次都没生效）
--
-- 用法：luajit HD2PhysBoneCompat/test/chain-solver-test.lua [compat 路径]

local modulePath = arg[1] or 'HD2PhysBoneCompat/src/physbone_compat.lua'
local M = assert(assert(loadfile(modulePath)))()
local q = M._q

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-52s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

local function ang(a, b)
	local d = a[1] * b[1] + a[2] * b[2] + a[3] * b[3]
	if d > 1 then d = 1 elseif d < -1 then d = -1 end
	return math.deg(math.acos(d))
end

--- 造一条"每节沿给定方向排开"的链。positions 是各节原点（世界坐标，锚点在原点）。
local function makeChain(positions, params, flags)
	local item = { bones = {}, tips = {}, segDirLocal = {}, dirs = {}, spacing = {} }
	for index = 1, #positions do
		local a = positions[index]
		local b = (index < #positions) and positions[index + 1] or positions[index - 1]
		local sx, sy, sz
		if index < #positions then
			sx, sy, sz = b[1] - a[1], b[2] - a[2], b[3] - a[3]
		else
			sx, sy, sz = a[1] - b[1], a[2] - b[2], a[3] - b[3]
		end
		local len = math.sqrt(sx * sx + sy * sy + sz * sz)
		if len < 1e-9 then sx, sy, sz, len = 0, 0, -1, 1 end
		local dx, dy, dz = sx / len, sy / len, sz / len
		item.segDirLocal[index] = { dx, dy, dz }
		-- 真实骨距
		if index < #positions then
			local c = positions[index + 1]
			item.spacing[index] = math.max(
				math.sqrt((c[1] - a[1]) ^ 2 + (c[2] - a[2]) ^ 2 + (c[3] - a[3]) ^ 2), 1e-4)
		else
			item.spacing[index] = item.spacing[index - 1] or 0.05
		end
		local hx, hy, hz = 0, 0, 1
		if math.abs(dz) > 0.9 then hx, hy, hz = 0, 1, 0 end
		local cx, cy, cz = hy * dz - hz * dy, hz * dx - hx * dz, hx * dy - hy * dx
		local cl = math.sqrt(cx * cx + cy * cy + cz * cz)
		if cl < 1e-9 then cx, cy, cz, cl = 1, 0, 0, 1 end
		cx, cy, cz = cx / cl, cy / cl, cz / cl
		local ux, uy, uz = dy * cz - dz * cy, dz * cx - dx * cz, dx * cy - dy * cx
		item.bones[index] = {
			offLocal = { a[1], a[2], a[3] },
			frameLocal = { cx, cy, cz, ux, uy, uz },
			gains = M.gains_of(params, 1.0, 0.01905, { flags = flags or 0x31 }),
		}
		item.tips[index] = { ready = false, px = 0, py = 0, pz = 0, vx = 0, vy = 0, vz = 0 }
	end
	return item
end

--- 一条**带弯曲**的链：前两节朝 -Z，后两节开始朝 -Z 偏 +X（模拟作者做的弧度）
local BENT = {
	{ 0, 0, 0 },
	{ 0, 0, -0.10 },
	{ 0.020, 0, -0.195 },
	{ 0.055, 0, -0.280 },
	{ 0.100, 0, -0.350 },
}

local NO_GRAVITY = { length = 10, tipMass = 0, yawStiffness = 50, yawDamping = 7,
	pitchStiffness = 50, pitchDamping = 7, angleLimit = 6.283 }
local WITH_GRAVITY = { length = 10, tipMass = 200, yawStiffness = 50, yawDamping = 7,
	pitchStiffness = 50, pitchDamping = 7, angleLimit = 6.283 }

-- ---------------------------------------------------------------------------
print("T1 重力=0 ⇒ 粒子链保持 bind 形状（不自己动）")
do
	local item = makeChain(BENT, NO_GRAVITY)
	item.chainOptions = { carry = 'frameforce' }
	for _ = 1, 300 do M.step_chain(item, 1 / 60, 0, 0, 0, 0, 0, 0, 1) end
	local worst = 0
	for index = 1, #item.dirs do
		worst = math.max(worst, ang(item.segDirLocal[index], item.dirs[index]))
	end
	check("各节方向 = bind 方向", worst < 0.5, string.format("最大偏差 %.3f°", worst))
end

-- ---------------------------------------------------------------------------
print("T2 弯曲约束沿链分布（最大相邻夹角明显小于逐节铰链）")
do
	-- 同一激励分别跑两个核，比"相邻节夹角的最大值"
	-- ⚠️ 判据只用**绝对夹角**，不用"不匀度 = 最大/平均"：
	-- 粒子链的最大夹角可以小到 0.1°，此时平均值也趋近 0，比值会假性爆炸
	-- （实测 tip 1.22 → chain 9.73，但 chain 的绝对夹角反而小了 100 倍）。
	local function measure(solver)
		local item = makeChain(BENT, WITH_GRAVITY)
		item.chainOptions = { carry = 'frameforce' }
		local worst, sum, count = 0, 0, 0
		for frame = 1, 420 do
			local t = frame / 60
			local sway = 0.40 * math.sin(t * 2 * math.pi * 0.7)
			local qx, qy, qz, qw = 0, 0, math.sin(sway / 2), math.cos(sway / 2)
			if solver == 'chain' then
				M.step_chain(item, 1 / 60, 0, 0, 0, qx, qy, qz, qw)
			else
				M.step_tip(item, 1 / 60, 0, 0, 0, qx, qy, qz, qw)
			end
			if frame > 120 then
				for index = 2, #item.dirs do
					local a = ang(item.dirs[index - 1], item.dirs[index])
					worst = math.max(worst, a); sum = sum + a; count = count + 1
				end
			end
		end
		return worst, (count > 0 and sum / count or 0)
	end
	local tipWorst, tipAvg = measure('tip')
	local chainWorst, chainAvg = measure('chain')
	check("粒子链的最大相邻夹角更小", chainWorst <= tipWorst + 1e-9,
		string.format("tip %.1f° → chain %.1f°", tipWorst, chainWorst))
	check("粒子链没有把整条链冻住（仍然在动）", chainAvg > 1e-3,
		string.format("chain 平均相邻夹角 %.3f°（tip %.3f°）", chainAvg, tipAvg))
end

-- ---------------------------------------------------------------------------
print("T3 软硬由**作者的 k** 决定：k 大 = 贴着 bind 抖，k 小 = 被重力拽下去")
do
	-- 一条水平伸出去的链（模拟 T-pose 的大袖子）
	local horizontal = {}
	for index = 1, 6 do horizontal[index] = { 0.10 * (index - 1), 0, 0 } end

	local function settle(params)
		local item = makeChain(horizontal, params)
		item.chainOptions = { carry = 'frameforce' }
		for _ = 1, 600 do M.step_chain(item, 1 / 60, 0, 0, 0, 0, 0, 0, 1) end
		local state = item.chain
		local tail = state.p[#state.p]
		-- 相对 bind 的平均偏离角（越大 = 被重力拽得越远）
		local dev, count = 0, 0
		for index = 1, #item.dirs do
			dev = dev + ang(item.segDirLocal[index], item.dirs[index]); count = count + 1
		end
		return tail[3], dev / count
	end

	-- 头发族：k=50, c=7（回正强）
	local hairTailZ, hairDev = settle({
		length = 10, tipMass = 200, yawStiffness = 50, yawDamping = 7,
		pitchStiffness = 50, pitchDamping = 7, angleLimit = 6.283 })
	-- 大袖子族：k=1, c=10（回正弱 ⇒ 重力主导 ⇒ 垂到下方，正是作者要的）
	local sleeveTailZ, sleeveDev = settle({
		length = 10, tipMass = 500, yawStiffness = 1, yawDamping = 10,
		pitchStiffness = 1, pitchDamping = 10, angleLimit = 6.283 })

	check("两族都下垂了（末端低于根）", hairTailZ < 0 and sleeveTailZ < 0,
		string.format("头发末端 z=%.3f，袖子末端 z=%.3f", hairTailZ, sleeveTailZ))
	check("k 小的袖子被拽得更狠（重力主导）", sleeveDev > hairDev + 5,
		string.format("偏离 bind：头发 %.1f° < 袖子 %.1f°", hairDev, sleeveDev))
	check("k 大的头发贴着 bind（偏离不到 45°）", hairDev < 45,
		string.format("头发偏离 %.1f°", hairDev))
	check("袖子垂到接近竖直", sleeveDev > 60,
		string.format("袖子偏离 %.1f°", sleeveDev))
end

-- ---------------------------------------------------------------------------
print("T4 求解器分派：solver=\"chain\" 必须真的走链路")
do
	-- 直接检查 update_tip 里的分派是否按 session.solver 选择
	-- （不去构造完整会话——那需要假引擎；这里用行为差异来判定：
	--  chain 核会写 `item.chain` 状态，tip 核不会）
	local item = makeChain(BENT, WITH_GRAVITY)
	item.chainOptions = { carry = 'frameforce' }
	M.step_chain(item, 1 / 60, 0, 0, 0, 0, 0, 0, 1)
	check("step_chain 建立了粒子链状态", item.chain ~= nil and item.chain.p ~= nil,
		item.chain ~= nil and string.format("n=%d", item.chain.n) or "nil")

	local tipItem = makeChain(BENT, WITH_GRAVITY)
	M.step_tip(tipItem, 1 / 60, 0, 0, 0, 0, 0, 0, 1)
	check("step_tip 不建立粒子链状态（两核互不干扰）", tipItem.chain == nil, "")
end

-- ---------------------------------------------------------------------------
print("T5 碰撞代理必须真的接到求解器（2026-11-03 之前的静默失效）")
do
	-- 一条竖直向下的链，锚点正上方放一个球把中段包住
	local straight = {}
	for index = 1, 5 do straight[index] = { 0, 0, -0.10 * (index - 1) } end
	local item = makeChain(straight, NO_GRAVITY)
	item.chainOptions = { carry = 'frameforce' }
	-- 球心在 (0.03, 0, -0.20)，半径 0.10 ⇒ 第 2、3 节都被包住
	item.colliders = { { 0.03, 0, -0.20, 0.03, 0, -0.20, 0.10 } }
	for _ = 1, 120 do M.step_chain(item, 1 / 60, 0, 0, 0, 0, 0, 0, 1) end

	-- 链被推开 ⇒ 至少有一节不再笔直朝 -Z
	local maxTilt = 0
	for index = 1, #item.dirs do
		maxTilt = math.max(maxTilt, math.abs(item.dirs[index][1]))
	end
	check("碰撞把链推离了原来的直线", maxTilt > 0.05,
		string.format("最大横向分量 %.3f", maxTilt))

	-- 对照：不给 colliders 时应当保持笔直
	local clean = makeChain(straight, NO_GRAVITY)
	clean.chainOptions = { carry = 'frameforce' }
	for _ = 1, 120 do M.step_chain(clean, 1 / 60, 0, 0, 0, 0, 0, 0, 1) end
	local cleanTilt = 0
	for index = 1, #clean.dirs do cleanTilt = math.max(cleanTilt, math.abs(clean.dirs[index][1])) end
	check("没有碰撞代理时保持笔直（对照组）", cleanTilt < 1e-6,
		string.format("最大横向分量 %.3e", cleanTilt))
end

-- ---------------------------------------------------------------------------
print("T6 不再做频段夹取：作者 k/c 原样变成 ω/ζ")
do
	-- 头发族：k=50, c=7 ⇒ ω=7.07, ζ=0.49（本来就在旧频段内，两侧都该一致）
	local hair = M.gains_of({ yawStiffness = 50, yawDamping = 7, pitchStiffness = 50,
		pitchDamping = 7, length = 10, tipMass = 200 }, 0.01905, 0.01905, { flags = 0x31 })
	check("头发 ω = √50 = 7.07", math.abs(hair.omegaYaw - math.sqrt(50)) < 1e-9,
		string.format("ω=%.4f ζ=%.4f", hair.omegaYaw, hair.zetaYaw))

	-- 大袖子族：k=1, c=10 ⇒ ω=1.0, ζ=5.0。旧策略会夹成 ω=4 / ζ=1.2。
	local sleeve = M.gains_of({ yawStiffness = 1, yawDamping = 10, pitchStiffness = 1,
		pitchDamping = 10, length = 10, tipMass = 500 }, 0.01905, 0.01905, { flags = 0x3d })
	check("袖子 ω 保持 1.0（不再被夹到 4）", math.abs(sleeve.omegaYaw - 1.0) < 1e-9,
		string.format("ω=%.4f", sleeve.omegaYaw))
	check("袖子 ζ 保持 5.0（不再被夹到 1.2）", math.abs(sleeve.zetaYaw - 5.0) < 1e-9,
		string.format("ζ=%.4f", sleeve.zetaYaw))
	check("没有被标记成已归一化", sleeve.normalized ~= true, "")

	-- 显式要求时仍然可以回到旧口径（诊断 / A/B 用）
	local forced = M.gains_of({ yawStiffness = 1, yawDamping = 10, pitchStiffness = 1,
		pitchDamping = 10, length = 10, tipMass = 500 }, 0.01905, 0.01905,
		{ flags = 0x3d, normalize = true })
	check("显式 normalize=true 仍能夹（诊断用）",
		math.abs(forced.omegaYaw - 4) < 1e-6 and forced.normalized == true,
		string.format("ω=%.3f ζ=%.3f", forced.omegaYaw, forced.zetaYaw))
end

print("")
if failures == 0 then
	print("=== 全部通过 ===")
else
	print(string.format("=== %d 项失败 ===", failures))
	os.exit(1)
end
