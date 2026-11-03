-- attach 去重（多人穿同一套护甲）回归
--
-- 场景：同屏 4 个玩家穿同一套 mod 护甲 ⇒ 同一个资源实例化出 4 组 Unit，
-- 场景图节点下标**完全一样**。attach 最贵的一段是"每根骨 has_node + node"，
-- 如果不缓存，就是 4 倍的无意义重复（每套 231 根骨 ≈ 460 次引擎调用）。
--
-- 这个测试断言的是**引擎调用次数**，不是"没崩"：
--   第 1 个实例：正常解析（> 0 次 has_node）
--   第 2 个实例：只做抽查（每链 1 次），调用数应当降一个数量级
--
-- 用法：luajit tmp/lua-check/attach-cache-test.lua [模块路径]

local path = arg[1] or "HD2PhysBoneCompat/src/physbone_compat.lua"
local M = assert(assert(loadfile(path)))()

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-52s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

local function quiet() return function() end end

-- ---------------------------------------------------------------------------
-- 假引擎：counts.hasNode / counts.node 记录调用次数。
-- `resource` 是 Unit.name_hash 的返回值 —— 同一个 resource 表示"同款护甲"。
-- ---------------------------------------------------------------------------
local function make_engine()
	local counts = { hasNode = 0, node = 0, localPose = 0 }
	local S = { Application = {}, Unit = {}, World = {}, Vector3 = {}, Quaternion = {}, Matrix4x4 = {} }
	local pose, vector, quat = { kind = "pose" }, { kind = "vector" }, { kind = "quat" }

	S.Application.string_to_id32 = function(text)
		local sum = 0
		for index = 1, #text do sum = sum + text:byte(index) * index end
		return sum * 1000 + #text
	end
	S.Unit.alive = function(unit) return unit.alive ~= false end
	S.Unit.name_hash = function(unit) return unit.resource end
	S.Unit.num_scene_graph_items = function() return 100 end
	S.Unit.has_node = function(unit, id)
		counts.hasNode = counts.hasNode + 1
		if unit.alive == false then return false end
		return unit.ids[id] == true
	end
	S.Unit.node = function(unit, id)
		counts.node = counts.node + 1
		return unit.nodeOf[id]
	end
	S.Unit.local_pose = function() counts.localPose = counts.localPose + 1 return pose end
	S.Unit.world_position = function() return vector end
	S.Unit.world_rotation = function() return quat end
	S.Unit.set_local_pose = function() end
	S.World.update_unit = function() end

	S.Vector3.to_elements = function() return 0, 0, 0 end
	S.Quaternion.to_elements = function() return 0, 0, 0, 1 end
	S.Matrix4x4.to_elements = function() return 1, 0, 0, 0, 1, 0, 0, 0, 1, 0.1, 0.2, 0.3 end
	S.Matrix4x4.is_valid = function() return true end
	S.Matrix4x4.from_quaternion_position = function() return pose end
	S.Quaternion.axis_angle = function() return quat end
	setmetatable(S.Vector3, { __call = function() return vector end })
	setmetatable(S.Quaternion, { __call = function() return quat end })
	return S, counts
end

-- 一套护甲：两条链（一条 3 节挂在 head，一条 1 节挂在 head），外加一根锚点骨
local RIG = (function()
	local params = { tipMass = 200, yawStiffness = 50, yawDamping = 7,
		pitchStiffness = 50, pitchDamping = 7, angleLimit = 0.611, length = 30 }
	local bones = {
		{ index = 1, name = "jA/source_extra_1", parent = 0, parentName = "head", pos = { 0, 0, 0 }, params = params, flags = 0x31 },
		{ index = 2, name = "jA/source_extra_2", parent = 1, parentName = "jA/source_extra_1", pos = { 0, -0.1, 0 }, params = params, flags = 0x31 },
		{ index = 3, name = "jA/source_extra_3", parent = 2, parentName = "jA/source_extra_2", pos = { 0, -0.2, 0 }, params = params, flags = 0x31 },
		{ index = 4, name = "jA/source_extra_4", parent = 0, parentName = "head", pos = { 0.1, 0, 0 }, params = params, flags = 0x31 },
	}
	return { bones = bones }
end)()

local function newUnit(S, tag, resource)
	local unit = { tag = tag, resource = resource, ids = {}, nodeOf = {} }
	local nextNode = 10
	for _, bone in ipairs(RIG.bones) do
		local id = S.Application.string_to_id32(bone.name)
		unit.ids[id] = true
		unit.nodeOf[id] = nextNode
		nextNode = nextNode + 1
	end
	local headId = S.Application.string_to_id32("head")
	unit.ids[headId] = true
	unit.nodeOf[headId] = 9
	return unit
end

-- ---------------------------------------------------------------------------
print("T1 同款护甲的第 2 个实例复用节点解析缓存")
do
	M.clear_attach_cache()
	local S, counts = make_engine()
	local player1 = newUnit(S, "p1", "#ID[aaaaaaaaaaaaaaaa]")
	local player2 = newUnit(S, "p2", "#ID[aaaaaaaaaaaaaaaa]")

	local first = M.attach(S, player1, RIG, { logger = quiet(), world = {} })
	local firstCalls = counts.hasNode + counts.node
	check("第 1 个实例挂上了", #first.sessions == 2, string.format("链数=%d", #first.sessions))
	check("第 1 个实例正常解析（有 has_node 调用）", firstCalls > 0, string.format("引擎调用=%d", firstCalls))

	counts.hasNode, counts.node = 0, 0
	local second = M.attach(S, player2, RIG, { logger = quiet(), world = {} })
	local secondCalls = counts.hasNode + counts.node
	check("第 2 个实例也挂上了", #second.sessions == 2, string.format("链数=%d", #second.sessions))
	check("第 2 个实例的解析调用大幅减少", secondCalls < firstCalls,
		string.format("%d → %d 次（应为每链 1 次抽查）", firstCalls, secondCalls))
	check("省掉的量级 ≥ 一半", secondCalls * 2 <= firstCalls,
		string.format("省了 %.0f%%", 100 * (1 - secondCalls / math.max(firstCalls, 1))))
	check("两条链的节点下标与第 1 个实例一致",
		second.sessions[1].nodes[1] == first.sessions[1].nodes[1]
		and second.sessions[1].anchorNode == first.sessions[1].anchorNode,
		string.format("node=%s anchor=%s", tostring(second.sessions[1].nodes[1]),
			tostring(second.sessions[1].anchorNode)))
end

-- ---------------------------------------------------------------------------
print("T2 不同护甲（不同资源）不会互相污染缓存")
do
	M.clear_attach_cache()
	local S, counts = make_engine()
	local armorA = newUnit(S, "A", "#ID[aaaaaaaaaaaaaaaa]")
	local armorB = newUnit(S, "B", "#ID[bbbbbbbbbbbbbbbb]")
	-- B 的下标整体偏移，用来证明它没有被 A 的缓存覆盖
	for id, node in pairs(armorB.nodeOf) do armorB.nodeOf[id] = node + 50 end

	local a = M.attach(S, armorA, RIG, { logger = quiet(), world = {} })
	local b = M.attach(S, armorB, RIG, { logger = quiet(), world = {} })
	check("两套都挂上了", #a.sessions == 2 and #b.sessions == 2,
		string.format("A=%d B=%d", #a.sessions, #b.sessions))
	check("B 用的是自己的下标（没被 A 的缓存污染）",
		b.sessions[1].nodes[1] == a.sessions[1].nodes[1] + 50,
		string.format("A=%s B=%s", tostring(a.sessions[1].nodes[1]), tostring(b.sessions[1].nodes[1])))
end

-- ---------------------------------------------------------------------------
print("T3 拿不到资源哈希时退回不缓存（行为与优化前一致）")
do
	M.clear_attach_cache()
	local S, counts = make_engine()
	S.Unit.name_hash = function() return nil end   -- 引擎不提供
	local u1 = newUnit(S, "u1", nil)
	local u2 = newUnit(S, "u2", nil)

	local a = M.attach(S, u1, RIG, { logger = quiet(), world = {} })
	counts.hasNode, counts.node = 0, 0
	local b = M.attach(S, u2, RIG, { logger = quiet(), world = {} })
	check("两个都挂上了", #a.sessions == 2 and #b.sessions == 2)
	check("没有缓存 ⇒ 第 2 个仍完整解析", counts.hasNode > 0,
		string.format("has_node=%d", counts.hasNode))
end

-- ---------------------------------------------------------------------------
print("T4 抽查失败（节点布局变了）时退回逐根解析")
do
	M.clear_attach_cache()
	local S, counts = make_engine()
	local u1 = newUnit(S, "u1", "#ID[aaaaaaaaaaaaaaaa]")
	local u2 = newUnit(S, "u2", "#ID[aaaaaaaaaaaaaaaa]")
	M.attach(S, u1, RIG, { logger = quiet(), world = {} })

	-- u2 的节点整体偏移：缓存里第一根骨的 has_node 仍然为真（名字在），
	-- 但为了模拟"布局变了"，这里把 u2 的节点下标整体 +50 并清掉缓存里的第一根骨名字
	local firstBoneId = S.Application.string_to_id32(RIG.bones[1].name)
	u2.ids[firstBoneId] = nil
	counts.hasNode, counts.node = 0, 0
	local b = M.attach(S, u2, RIG, { logger = quiet(), world = {} })
	-- 抽查用的是 has_node：命中缓存时每链只有 1 次；走完整解析时每根骨 1 次。
	check("抽查失败后走了完整解析", counts.hasNode > #RIG.bones / 2,
		string.format("has_node=%d（缓存命中时应当只有每链 1 次）", counts.hasNode))
	check("结果仍然合理（缺骨的那条链被跳过，另一条照常挂上）",
		type(b.sessions) == "table" and #b.sessions == 1, string.format("链数=%d", #b.sessions))
end

print("")
if failures == 0 then
	print("=== 全部通过 ===")
else
	print(string.format("=== %d 项失败 ===", failures))
	os.exit(1)
end
