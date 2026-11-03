-- 绑定形状（bind shape）与分叉拓扑的离线验收
--
-- 这两条是 2026-11-03 一波调研共同的结论，也是用户三个症状里的两个的直接根因：
--
-- ① **bind 形状必须是零力平衡态。**
--    旧实现里"本节的父段前向"对 k>1 直接抄上一节的**模拟**方向 ⇒ 零力平衡态是一条直线。
--    而 `tip.ready == false` 时首帧把尖端直接放到目标上 ⇒ **第一帧就把整条链掰直**。
--    作者在 bind 姿态里做出的下垂与弯曲被当成"要纠正的偏差"，于是
--    表现就是"头发不会自然下垂、反而指向一个错误的方向"。
--    现在的判据：重力=0、锚点静止 ⇒ 每节写出去的 local 旋转必须是 **0°**（形状 = bind 形状）。
--
-- ② **分叉树不能拍平成一条链。**
--    旧 `build_chains` 用 DFS 把所有后代串成一条，兄弟分支变成父子。
--    真实资产 `silent_code_shinano` 骨 96→{97,98,100} 摊平后造出 **154.3°** 人造折角，
--    而生产日志里数千帧恒定的"偏离绑定峰值"恰好是 154.3° —— 就是用户说的"闪电状折痕"。
--    现在的判据：每个骨只属于一条路径；分叉处不产生人造折角。
--
-- ③ 顺带钉住 `swing_axis` 的反平行：180° 必须真的是 180°，不能退化成 0°。
--
-- 用法：luajit tmp/lua-check/bind-shape-test.lua [compat 路径]

local modulePath = arg[1] or 'HD2PhysBoneCompat/src/physbone_compat.lua'
local M = assert(assert(loadfile(modulePath)))()

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-54s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

local function norm(x, y, z)
	local l = math.sqrt(x * x + y * y + z * z)
	if l < 1e-12 then return 0, 0, 1, 1 end
	return x / l, y / l, z / l, l
end

local function dot(ax, ay, az, bx, by, bz) return ax * bx + ay * by + az * bz end

local function angleBetween(ax, ay, az, bx, by, bz)
	local d = dot(ax, ay, az, bx, by, bz)
	if d > 1 then d = 1 elseif d < -1 then d = -1 end
	return math.acos(d) * 180 / math.pi
end

--- 按 `M.attach` 的口径把"骨位置序列"变成一个可交给 step_tip 的 item。
--- 锚点旋转取单位阵（q = identity）⇒ segDirLocal 就等于世界方向，测试能直接读。
local function makeItem(positions, params, flags)
	local item = { bones = {}, tips = {}, segDirLocal = {}, dirs = {} }
	local anchor = positions[1]
	for index = 1, #positions do
		local a = positions[index]
		local sx, sy, sz
		if index < #positions then
			local b = positions[index + 1]
			sx, sy, sz = b[1] - a[1], b[2] - a[2], b[3] - a[3]
		elseif index > 1 then
			local p = positions[index - 1]
			sx, sy, sz = a[1] - p[1], a[2] - p[2], a[3] - p[3]
		else
			sx, sy, sz = a[1] - anchor[1], a[2] - anchor[2], a[3] - anchor[3]
		end
		local dx, dy, dz = norm(sx, sy, sz)
		item.segDirLocal[index] = { dx, dy, dz }
		-- attach 里的正交基：forward = 本节方向；left = hint × forward；up = forward × left
		local hx, hy, hz = 0, 0, 1
		if math.abs(dz) > 0.9 then hx, hy, hz = 0, 1, 0 end
		local cx, cy, cz = hy * dz - hz * dy, hz * dx - hx * dz, hx * dy - hy * dx
		local lx, ly, lz = norm(cx, cy, cz)
		local ux = dy * lz - dz * ly
		local uy = dz * lx - dx * lz
		local uz = dx * ly - dy * lx
		item.bones[index] = {
			-- 锚点在原点、q=identity ⇒ 锚点局部系里的偏移就是世界位置本身
			offLocal = { a[1] - anchor[1], a[2] - anchor[2], a[3] - anchor[3] },
			frameLocal = { lx, ly, lz, ux, uy, uz },
			gains = M.gains_of(params, 1.0, 0.01905, { flags = flags }),
		}
		item.tips[index] = { ready = false, px = 0, py = 0, pz = 0, vx = 0, vy = 0, vz = 0 }
	end
	return item
end

--- 跑 frames 帧，返回每节 local 旋转角（度）与最终方向。
local function run(item, frames, dt)
	dt = dt or 1 / 60
	for _ = 1, frames do
		M.step_tip(item, dt, 0, 0, 0, 0, 0, 0, 1)
	end
	local rotations = M.local_rotations(item, 0, 0, 0, 1)
	local angles = {}
	for index = 1, #item.bones do
		angles[index] = rotations[index][4] * 180 / math.pi
	end
	return angles, item.dirs
end

-- ---------------------------------------------------------------------------
print("T1 重力=0、锚点静止 ⇒ 写出去的 local 旋转必须是 0°（形状 = bind 形状）")
do
	-- 一条**明显弯曲**的链（每节偏 25~30°），朝下并向前弯：模拟"作者的头发束"。
	local positions = {
		{ 0, 0, 0 },
		{ 0.020, 0, -0.100 },
		{ 0.075, 0, -0.180 },
		{ 0.160, 0, -0.235 },
		{ 0.265, 0, -0.262 },
	}
	local params = { length = 0.10, tipMass = 0, yawStiffness = 50, yawDamping = 7,
		pitchStiffness = 50, pitchDamping = 7, angleLimit = 0.611 }
	local item = makeItem(positions, params, 0x31)
	local angles, dirs = run(item, 240)

	local worst = 0
	for index = 1, #angles do worst = math.max(worst, math.abs(angles[index])) end
	check("每节 local 旋转 ≈ 0°", worst < 1.0,
		string.format("最大 %.3f°（各节 %s）", worst, table.concat(angles, " / ")))

	-- 方向必须回到 bind 方向本身（没有重力时不该有任何偏转）
	local maxDev = 0
	for index = 1, #dirs do
		local s = item.segDirLocal[index]
		maxDev = math.max(maxDev, angleBetween(s[1], s[2], s[3], dirs[index][1], dirs[index][2], dirs[index][3]))
	end
	check("各节方向 = bind 方向", maxDev < 1.0, string.format("最大偏差 %.3f°", maxDev))
end

-- ---------------------------------------------------------------------------
print("T2 弯曲链 + 重力：必须保留 bind 弯曲，而不是整体掰直")
do
	-- 同一条链，但开启重力（tipMass=200 ⇒ g = 200×0.01905 = 3.81 m/s²）
	local positions = {
		{ 0, 0, 0 },
		{ 0.020, 0, -0.100 },
		{ 0.075, 0, -0.180 },
		{ 0.160, 0, -0.235 },
		{ 0.265, 0, -0.262 },
	}
	local params = { length = 0.10, tipMass = 200, yawStiffness = 50, yawDamping = 7,
		pitchStiffness = 50, pitchDamping = 7, angleLimit = 1.0 }
	local item = makeItem(positions, params, 0x11)
	local angles, dirs = run(item, 600)

	-- 判据：链整体**仍然朝下**（bind 末节方向 z ≈ -0.19），而不是被拉成水平或朝上。
	local tail = dirs[#dirs]
	check("末节仍然朝下（没有被掰直/翻上去）", tail[3] < -0.10,
		string.format("末节方向=(%.2f %.2f %.2f)", tail[1], tail[2], tail[3]))

	-- 相邻节之间仍然有可见夹角（弯曲保留了；全掰直会接近 0）
	local bend = 0
	for index = 2, #dirs do
		local a, b = dirs[index - 1], dirs[index]
		bend = math.max(bend, angleBetween(a[1], a[2], a[3], b[1], b[2], b[3]))
	end
	check("相邻节之间仍有弯曲（不是一条直线）", bend > 3.0,
		string.format("最大节间夹角 %.1f°", bend))
end

-- ---------------------------------------------------------------------------
print("T3 分叉树：每个骨只属于一条路径，且不造假折角")
do
	-- 96 → {97, 98, 100}，98 → 99（真实资产的形状，曾被摊平成一条线性链）
	local bones = {
		{ index = 96, parent = 80, pos = { 0, 0, 0 } },        -- 80 不在 rig 里 ⇒ 96 是链根
		{ index = 97, parent = 96, pos = { 0.10, 0, -0.02 } },
		{ index = 98, parent = 96, pos = { -0.10, 0, -0.02 } },
		{ index = 99, parent = 98, pos = { -0.20, 0, -0.04 } },
		{ index = 100, parent = 96, pos = { 0, 0, -0.12 } },
	}
	local chains = M.build_chains(bones)

	-- 切法：96 自己一条（它有两个以上子骨 ⇒ 收尾）；97/98/100 各自开新路径；98 带 99。
	local seen, duplicated = {}, 0
	local sizes = {}
	for _, chain in ipairs(chains) do
		sizes[#sizes + 1] = #chain
		for _, bone in ipairs(chain) do
			if seen[bone.index] then duplicated = duplicated + 1 end
			seen[bone.index] = true
		end
	end
	check("没有骨被两条路径重复驱动", duplicated == 0,
		string.format("重复 %d 个；链=%s", duplicated, table.concat(sizes, "+")))
	check("5 根骨全部覆盖", (function()
		local n = 0
		for _ in pairs(seen) do n = n + 1 end
		return n == 5
	end)(), string.format("覆盖 %d 根", (function()
		local n = 0
		for _ in pairs(seen) do n = n + 1 end
		return n
	end)()))

	-- 关键：分叉节点必须**收尾**在自己那条路径里，不能把兄弟当续节。
	local worstLen = 0
	for _, chain in ipairs(chains) do worstLen = math.max(worstLen, #chain) end
	check("没有一条路径吞掉兄弟分支", worstLen <= 2,
		string.format("最长路径 %d 节（摊平会是 5 节）", worstLen))

	-- 每条路径单独跑求解器，重力=0 ⇒ 形状 = bind 形状 ⇒ 节点旋转 0°（无假折角）
	local params = { length = 0.02, tipMass = 0, yawStiffness = 50, yawDamping = 7,
		pitchStiffness = 50, pitchDamping = 7, angleLimit = 1.0 }
	local worstAngle = 0
	for _, chain in ipairs(chains) do
		if #chain >= 2 then
			local positions = {}
			for _, bone in ipairs(chain) do
				positions[#positions + 1] = bone.pos
			end
			local item = makeItem(positions, params, 0x31)
			local angles = run(item, 240)
			for _, angle in ipairs(angles) do worstAngle = math.max(worstAngle, math.abs(angle)) end
		end
	end
	check("分叉处没有人造折角（各节旋转 ≈ 0°）", worstAngle < 1.0,
		string.format("最大 %.3f°", worstAngle))
end

-- ---------------------------------------------------------------------------
print("T4 swing_axis / local_rotations：180° 必须真的是 180°")
do
	local swing = M._q.swing
	local sx, sy, sz, angle = swing(0, 0, 1, 0, 0, -1)
	check("反平行返回非 nil 且角度 = π", sx ~= nil and math.abs(angle - math.pi) < 1e-9,
		string.format("轴=(%s) 角=%s", tostring(sx), tostring(angle)))

	local nx, ny, nz = swing(0.3, 0.5, 0.81, -0.3, -0.5, -0.81)
	check("非轴对齐反平行也返回轴", nx ~= nil and nx == nx, "NaN 检查")

	-- 端到端：目标方向与 bind 方向反向 ⇒ 复现出来必须是反向
	local item = makeItem({ { 0, 0, 0 }, { 0, 0, 0.1 } }, { length = 0.1, tipMass = 0,
		yawStiffness = 50, yawDamping = 7, pitchStiffness = 50, pitchDamping = 7, angleLimit = 3.0 }, 0x31)
	-- `local_rotations` 读的是模拟出来的方向表（`item.dirs`），手工塞一个反向进去
	item.dirs[1] = { 0, 0, -1 }
	local rotations = M.local_rotations(item, 0, 0, 0, 1)
	local r = rotations[1]
	check("180° 目标没有被写成零旋转", r[4] > math.pi - 1e-6,
		string.format("输出角 %.4f°", r[4] * 180 / math.pi))
end

print("")
if failures == 0 then
	print("=== 全部通过 ===")
else
	print(string.format("=== %d 项失败 ===", failures))
	os.exit(1)
end
