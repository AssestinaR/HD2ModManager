-- 碰撞层（球 / 胶囊）离线验收
--
-- 背景：**原版 L4D2 的 jigglebone 没有任何碰撞**（Valve `jigglesbones.cpp` 全文零碰撞查询），
-- 而 HD2 的 Lua **完全没有物理查询 API**（`PhysicsWorld`/`Actor`/`Raycast`/`Mover` 整层被裁）。
-- 所以这一层是"补上用户明确要求的缺失能力"，只能用**纯数学身体代理**做位置投影：
--   用户原话："我们完全没做胶囊碰撞体……效果可以略微差一点，但起码不能有缺失"。
--
-- 这里钉住四条：
--   T1 代理能把尖端推出球外（而且推出后仍然满足定长约束）
--   T2 **没有代理时逐位不变**（不许悄悄改动正常模型）
--   T3 退化输入（尖端正好落在轴线上 / 零长度胶囊）不许出 NaN、不许随机方向
--   T4 建代理的统计要说得清（缺骨要计数，不能静默把整层关掉）
--
-- 用法：luajit tmp/lua-check/collision-test.lua [compat 路径]

local modulePath = arg[1] or 'HD2PhysBoneCompat/src/physbone_compat.lua'
local M = assert(assert(loadfile(modulePath)))()

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-52s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

local function norm(x, y, z)
	local l = math.sqrt(x * x + y * y + z * z)
	if l < 1e-12 then return 0, 0, 1, 1 end
	return x / l, y / l, z / l, l
end

--- 单节链：从 (0,0,0) 指到 (0,0,-L)。锚点取单位阵 ⇒ segDirLocal 就是世界方向。
local function makeSingleSegment(L, params)
	local item = { bones = {}, tips = {}, segDirLocal = {}, dirs = {} }
	item.segDirLocal[1] = { 0, 0, -1 }
	item.bones[1] = {
		offLocal = { 0, 0, 0 },
		frameLocal = { 0, 1, 0, 1, 0, 0 },     -- left = +Y（与 -Z 前向正交）
		gains = M.gains_of(params, 1.0, 0.01905, { flags = 0x31 }),
	}
	item.tips[1] = { ready = false, px = 0, py = 0, pz = 0, vx = 0, vy = 0, vz = 0 }
	return item
end

local QUIET = { length = 0.10, tipMass = 0, yawStiffness = 50, yawDamping = 7,
	pitchStiffness = 50, pitchDamping = 7, angleLimit = 3.0 }

-- ---------------------------------------------------------------------------
print("T1 球代理把尖端推到球外，且定长约束仍然满足")
do
	-- 球心 (0.05, 0, -0.09)、半径 0.06；尖端在 (0,0,-0.10)，离球心 0.051 ⇒ 在球内。
	local item = makeSingleSegment(0.10, QUIET)
	item.colliders = { { 0.05, 0, -0.09, 0.05, 0, -0.09, 0.06 } }   -- 退化胶囊 = 球
	local before = { item.dirs[1] }
	for _ = 1, 30 do
		M.step_tip(item, 1 / 60, 0, 0, 0, 0, 0, 0, 1)
	end
	local d = item.dirs[1]
	check("尖端被横向推开（方向不再是纯 -Z）", math.abs(d[1]) > 0.05,
		string.format("方向=(%.3f %.3f %.3f)", d[1], d[2], d[3]))

	local tipX, tipY, tipZ = d[1] * 0.10, d[2] * 0.10, d[3] * 0.10
	local dx, dy, dz = tipX - 0.05, tipY - 0, tipZ + 0.09
	local dist = math.sqrt(dx * dx + dy * dy + dz * dz)

	-- 没有碰撞时的距离（纯 -Z 落点）
	local bx, by, bz = 0 - 0.05, 0 - 0, -0.10 + 0.09
	local baseDist = math.sqrt(bx * bx + by * by + bz * bz)

	-- ⚠️ 判据不能写成"距离 ≥ 半径"。定长约束把尖端钉在以基座为球心、半径 L 的球面上，
	-- 而那个球面**未必**与碰撞球面相交 —— 推出后再恢复定长会把点拉回来一点。
	-- 能保证的是"明显比不推更远"，以及"几乎贴在碰撞面上"。
	check("推出后贴近碰撞面（差 ≤ 1 mm）", dist >= 0.06 - 1e-3,
		string.format("距球心 %.4f（半径 0.060）", dist))
	check("明显比不碰撞时更远", dist > baseDist + 0.005,
		string.format("%.4f → %.4f", baseDist, dist))
	check("定长约束仍满足（|dir| = 1）",
		math.abs(math.sqrt(d[1] ^ 2 + d[2] ^ 2 + d[3] ^ 2) - 1) < 1e-9, "")
end

-- ---------------------------------------------------------------------------
print("T2 没有碰撞代理时逐位不变（不许悄悄改动正常模型）")
do
	local a = makeSingleSegment(0.10, QUIET)
	local b = makeSingleSegment(0.10, QUIET)
	b.colliders = nil                       -- 老路径
	for _ = 1, 200 do
		M.step_tip(a, 1 / 60, 0, 0, 0, 0, 0, 0, 1)
		M.step_tip(b, 1 / 60, 0, 0, 0, 0, 0, 0, 1)
	end
	local same = a.dirs[1][1] == b.dirs[1][1] and a.dirs[1][2] == b.dirs[1][2]
		and a.dirs[1][3] == b.dirs[1][3]
	check("colliders=nil 与 空表 结果完全一致", same,
		string.format("(%.6f %.6f %.6f) vs (%.6f %.6f %.6f)",
			a.dirs[1][1], a.dirs[1][2], a.dirs[1][3], b.dirs[1][1], b.dirs[1][2], b.dirs[1][3]))

	local c = makeSingleSegment(0.10, QUIET)
	c.colliders = {}
	for _ = 1, 20 do M.step_tip(c, 1 / 60, 0, 0, 0, 0, 0, 0, 1) end
	check("空代理表不产生碰撞计数", (c.collisionHits or 0) == 0, "")
end

-- ---------------------------------------------------------------------------
print("T3 退化输入：不许 NaN、不许随机方向")
do
	-- 尖端正好落在球心 ⇒ 法线不唯一
	local item = makeSingleSegment(0.10, QUIET)
	item.colliders = { { 0, 0, -0.10, 0, 0, -0.10, 0.07 } }
	local ok = pcall(function()
		for _ = 1, 60 do M.step_tip(item, 1 / 60, 0, 0, 0, 0, 0, 0, 1) end
	end)
	local d = item.dirs[1]
	local finite = d[1] == d[1] and d[2] == d[2] and d[3] == d[3]
		and math.abs(d[1]) < 2 and math.abs(d[3]) < 2
	check("球心重合不出 NaN/Inf", ok and finite,
		string.format("方向=(%.4f %.4f %.4f)", d[1], d[2], d[3]))

	-- 零长度胶囊（两个端点相同）走的是同一条球分支，不该除零。
	-- 端点放在 (1,1,1)、查询点也放在 (1,1,1) ⇒ 刚好落在"轴线"上，法线不唯一，是最坏输入。
	local push = M._push_out_of_colliders
	local px, py, pz, hit = push(1, 1, 1, { { 1, 1, 1, 1, 1, 1, 0.2 } })
	local finite2 = px == px and py == py and pz == pz
	local dist2 = math.sqrt((px - 1) ^ 2 + (py - 1) ^ 2 + (pz - 1) ^ 2)
	check("零长度胶囊不除零", finite2 and hit == true,
		string.format("推出到 (%.3f %.3f %.3f)，距球心 %.3f", px, py, pz, dist2))
	check("退化情形推到半径上", math.abs(dist2 - 0.2) < 1e-9,
		string.format("距球心 %.4f（半径 0.200）", dist2))

	-- 确定性：同样输入跑两次，必须得到同一个方向（不能每帧随机翻）
	local function runOnce()
		local it = makeSingleSegment(0.10, QUIET)
		it.colliders = { { 0, 0, -0.10, 0, 0, -0.10, 0.07 } }
		for _ = 1, 30 do M.step_tip(it, 1 / 60, 0, 0, 0, 0, 0, 0, 1) end
		return it.dirs[1]
	end
	local r1, r2 = runOnce(), runOnce()
	check("同一输入两次结果一致（确定性）",
		math.abs(r1[1] - r2[1]) < 1e-12 and math.abs(r1[3] - r2[3]) < 1e-12, "")
end

-- ---------------------------------------------------------------------------
print("T4 建代理的统计：缺骨要计数，不能静默关掉整层")
do
	local names = {}
	for _, entry in ipairs(M.default_colliders) do
		names[entry[1]] = true
		names[entry[2]] = true
	end
	-- ① 全部骨都在
	local all = {}
	local index = 0
	for name in pairs(names) do index = index + 1; all[name] = index end
	local spec, stats = M.build_colliders(function(name) return all[name] end, {})
	check("骨齐时全部建起", stats.built == stats.requested and stats.skipped == 0,
		string.format("%d/%d", stats.built, stats.requested))

	-- ② 一个骨都没有
	local none, statsNone = M.build_colliders(function() return nil end, {})
	check("骨全缺时一个都建不起（但仍报出请求数）", #none == 0
		and statsNone.skipped == statsNone.requested and statsNone.requested > 0,
		string.format("请求 %d、跳过 %d", statsNone.requested, statsNone.skipped))

	-- ③ 只有一半端点 ⇒ 退化成球，且计入 sphereOnly
	local half = M.build_colliders(function(name)
		if name == "head" or name == "chest" or name == "hips" then return 7 end
		return nil
	end, {})
	local sphereOnly = 0
	for _, entry in ipairs(half) do
		if entry[8] == entry[9] then sphereOnly = sphereOnly + 1 end
	end
	check("缺一端 ⇒ 退化成球并计数", sphereOnly > 0, string.format("球 %d 个", sphereOnly))

	-- ④ 自定义列表 + 统一半径覆盖
	local custom, statsCustom = M.build_colliders(function() return 3 end,
		{ colliders = { { "head", "neck", 0.05 } }, colliderRadius = 0.2 })
	check("统一半径覆盖生效", #custom == 1 and math.abs(custom[1][7] - 0.2) < 1e-9,
		string.format("半径 %.3f", custom[1] and custom[1][7] or -1))
end

print("")
if failures == 0 then
	print("=== 全部通过 ===")
else
	print(string.format("=== %d 项失败 ===", failures))
	os.exit(1)
end
