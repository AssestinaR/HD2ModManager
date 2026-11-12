-- 多 mod 选 rig 回归（select_rig）
--
-- 场景：装了多套带物理的护甲 mod，每个 patch 尾部各带一份容器。
-- 运行时要选出**属于当前这套护甲**的那一份 —— 选错就是用别的模型的骨名去驱动，
-- 轻则 0 条链，重则骨名撞车（source_extra_* 按源模型骨骼下标生成，不同模型会重名）写到错的骨上。
--
-- 用法：luajit tmp/lua-check/rig-select-test.lua [模块路径]

local path = arg[1] or "HD2PhysBoneCompat/src/physbone_compat.lua"
local M = assert(assert(loadfile(path)))()

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-52s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

-- ---------------------------------------------------------------------------
-- 假引擎：has_node **认名字**（只有真正在这件护甲上的骨名才返回 true）。
-- 这正是实机行为，也是本测试要验证的判据。
--
-- ⚠️ `strictNames` 打开时，传数字（`string_to_id32` 得到的 id）**直接抛错**。
-- 实机就是这个行为：`IdString32` 会失效，拿它做 `has_node` 一律 false/抛错
-- （2026-11-12 的"舰船正常、进任务全没物理"就是缓存了 id 造成的）。
-- ---------------------------------------------------------------------------
local function make_engine(opts)
	opts = opts or {}
	local units = {}
	local S = { Application = {}, Unit = {} }

	S.Application.string_to_id32 = function(text)
		-- 稳定可读的假 id
		local sum = 0
		for index = 1, #text do sum = sum + text:byte(index) * index end
		return sum * 1000 + #text
	end
	S.Unit.alive = function(unit) return unit.alive ~= false end
	S.Unit.has_node = function(unit, key)
		if unit.alive == false then return false end
		if opts.strictNames == true and type(key) ~= "string" then
			error("[Stale IdString32] 只能按骨名问")
		end
		return unit.ids[key] == true
	end
	-- 故意把 node 做成"什么名字都给一个数字"，用来验证 select_rig 没有误用 Unit.node
	S.Unit.node = function() return 7 end

	local function newUnit(tag, boneNames)
		local unit = { tag = tag, ids = {} }
		for _, name in ipairs(boneNames) do
			unit.ids[name] = true
		end
		units[#units + 1] = unit
		return unit
	end
	return S, newUnit
end

local function makeRig(names)
	local bones = {}
	for index, name in ipairs(names) do
		bones[index] = { index = index, name = name, parent = index - 1,
			parentName = index == 1 and "head" or names[index - 1],
			pos = { 0, 0, 0 }, params = {}, flags = 0x31 }
	end
	return { bones = bones }
end

-- ---------------------------------------------------------------------------
print("T1 单一 mod：正确的那份全命中")
do
	local S, newUnit = make_engine()
	local armorA = newUnit("armorA", { "source_extra_1", "source_extra_2", "source_extra_3" })
	local rigA = makeRig({ "source_extra_1", "source_extra_2", "source_extra_3" })
	local rigB = makeRig({ "source_extra_9", "source_extra_10" })
	local best, scored = M.select_rig(S, { armorA },
		{ { rig = rigB, path = "B" }, { rig = rigA, path = "A" } })
	check("选中 A", best ~= nil and best.path == "A", tostring(best and best.path))
	check("全命中 3/3", best ~= nil and best.full and best.score == 3,
		best and string.format("%d/%d", best.score, best.total))
	check("两份都被打分", #scored == 2, tostring(#scored))
end

-- ---------------------------------------------------------------------------
print("T2 错 rig 命中多、对 rig 命中少 ⇒ 仍然选对（全命中优先）")
do
	local S, newUnit = make_engine()
	-- 这件护甲上有 4 根骨属于 B 的命名空间，但只有 2 根属于 A；A 的 rig 只有这 2 根
	local armor = newUnit("armor", { "b1", "b2", "b3", "b4", "a1", "a2" })
	local rigA = makeRig({ "a1", "a2" })                       -- 2/2 全命中
	local rigB = makeRig({ "b1", "b2", "b3", "b4", "b5" })     -- 4/5 部分命中
	local best = M.select_rig(S, { armor }, { { rig = rigB, path = "B" }, { rig = rigA, path = "A" } })
	check("选中 A（全命中优先于命中数）", best ~= nil and best.path == "A", tostring(best and best.path))
	check("A 是 2/2", best ~= nil and best.score == 2 and best.total == 2,
		best and string.format("%d/%d", best.score, best.total))
end

-- ---------------------------------------------------------------------------
print("T3 打分对象是多件护甲：任意一件命中就算数")
do
	local S, newUnit = make_engine()
	-- 主护甲件（armorList[1]）属于别的模型，真正的那件在第二个
	local foreign = newUnit("foreign", { "x1", "x2", "x3" })
	local ours = newUnit("ours", { "a1", "a2" })
	local rigA = makeRig({ "a1", "a2" })
	local rigX = makeRig({ "x1", "x2", "x3" })
	local best = M.select_rig(S, { foreign, ours }, { { rig = rigX, path = "X" }, { rig = rigA, path = "A" } })
	-- X 在 foreign 上 3/3 全命中，A 在 ours 上 2/2 全命中 ⇒ 同为全命中，骨更多的 X 胜出
	check("同为全命中时取骨更多的", best ~= nil and best.path == "X", tostring(best and best.path))

	local best2 = M.select_rig(S, { foreign, ours }, { { rig = rigA, path = "A" } })
	check("只给 A 时能命中第二个 unit", best2 ~= nil and best2.full and best2.path == "A",
		best2 and string.format("%s %d/%d", tostring(best2.path), best2.score, best2.total))
end

-- ---------------------------------------------------------------------------
print("T4 一件都不匹配 ⇒ 返回最高分（0 分）且 full=false，调用方可据此告警")
do
	local S, newUnit = make_engine()
	local armor = newUnit("armor", { "zzz" })
	local rigA = makeRig({ "a1", "a2" })
	local best = M.select_rig(S, { armor }, { { rig = rigA, path = "A" } })
	check("返回了候选", best ~= nil and best.path == "A")
	check("score=0 且 full=false", best ~= nil and best.score == 0 and best.full == false,
		best and string.format("%d/%d", best.score, best.total))
end

-- ---------------------------------------------------------------------------
print("T5 空输入不炸")
do
	local S, newUnit = make_engine()
	local armor = newUnit("armor", { "a1" })
	check("空候选返回 nil", M.select_rig(S, { armor }, {}) == nil)
	check("nil 候选返回 nil", M.select_rig(S, { armor }, nil) == nil)
	check("空 bones 的 rig 被跳过",
		M.select_rig(S, { armor }, { { rig = { bones = {} }, path = "E" } }) == nil)
end

-- ---------------------------------------------------------------------------
print("T6 已销毁的 unit 不参与打分（不会被判成全命中）")
do
	local S, newUnit = make_engine()
	local dead = newUnit("dead", { "a1", "a2" })
	dead.alive = false
	local rigA = makeRig({ "a1", "a2" })
	local best = M.select_rig(S, { dead }, { { rig = rigA, path = "A" } })
	check("死 unit 得 0 分", best ~= nil and best.score == 0,
		best and string.format("%d/%d", best.score, best.total))
end

-- ---------------------------------------------------------------------------
print("T7 命名空间兼容：新容器 + 旧 patch（容器名带前缀，节点名不带）")
do
	local S, newUnit = make_engine()
	-- 旧 patch：节点名是裸的 source_extra_N
	local oldUnit = newUnit("old", { "source_extra_64", "source_extra_65" })
	-- 新容器：骨名带命名空间前缀
	local newRig = makeRig({ "j1a2b3c4d5e6f/source_extra_64", "j1a2b3c4d5e6f/source_extra_65" })
	local best = M.select_rig(S, { oldUnit }, { { rig = newRig, path = "new" } })
	check("仍判为全命中", best ~= nil and best.full and best.score == 2,
		best and string.format("%d/%d", best.score, best.total))
end

-- ---------------------------------------------------------------------------
print("T8 命名空间隔离：两个模型的占位名不再互相命中")
do
	local S, newUnit = make_engine()
	-- 模型 A 的护甲：节点名带 A 的命名空间
	local armorA = newUnit("A", { "jAAAAAAAAAAAA/source_extra_64", "jAAAAAAAAAAAA/source_extra_65" })
	local rigA = makeRig({ "jAAAAAAAAAAAA/source_extra_64", "jAAAAAAAAAAAA/source_extra_65" })
	local rigB = makeRig({ "jBBBBBBBBBBBB/source_extra_64", "jBBBBBBBBBBBB/source_extra_65" })
	local best = M.select_rig(S, { armorA }, { { rig = rigB, path = "B" }, { rig = rigA, path = "A" } })
	check("选中自己那份 A", best ~= nil and best.path == "A", tostring(best and best.path))
	check("A 全命中", best ~= nil and best.full, best and string.format("%d/%d", best.score, best.total))

	-- 关键：B 的骨名拆掉前缀后是 source_extra_64/65，而 A 的节点名带前缀，所以 B 应当 **0 命中**
	local onlyB = M.select_rig(S, { armorA }, { { rig = rigB, path = "B" } })
	check("B 对 A 的护甲 0 命中（命名空间真的隔离了）", onlyB ~= nil and onlyB.score == 0,
		onlyB and string.format("%d/%d", onlyB.score, onlyB.total))
end

-- ---------------------------------------------------------------------------
print("T9 锚点名兼容：带前缀的父骨名也能查到映射")
do
	check("裸名照旧", M.anchor_name("ValveBiped.Bip01_Head1") == "head",
		tostring(M.anchor_name("ValveBiped.Bip01_Head1")))
	check("带前缀时拆掉再查（占位父骨 → nil，由调用方回退成节点名）",
		M.anchor_name("j1a2b3c4d5e6f/source_extra_72") == nil)
	check("有真名的骨加前缀也能查到", M.anchor_name("j1a2b3c4d5e6f/ValveBiped.Bip01_Head1") == "head",
		tostring(M.anchor_name("j1a2b3c4d5e6f/ValveBiped.Bip01_Head1")))
end

-- ---------------------------------------------------------------------------
print("T10 匹配必须**按骨名问**，不能按 string_to_id32 的 id 问")
--
-- 实机现场（2026-11-12，2.1.25~2.1.30）：`Application.string_to_id32` 返回的
-- `IdString32` 会失效。为了省调用，2.1.25 把它的结果按 rig 缓存起来，于是
--   · 进程刚启动、id 还是新算的时候 ⇒ 舰船上一次挂满（看起来正常）；
--   · 之后全部失效 ⇒ 换场景后再也挂不上，所有候选都是 0/68，日志还像"骨头不在"。
-- 这个用例把"按 id 匹配"钉死成**失败**：`strictNames` 下传数字直接抛错。
do
	local S, newUnit = make_engine({ strictNames = true })
	local armor = newUnit("armor", { "source_extra_1", "source_extra_2" })
	local rig = makeRig({ "source_extra_1", "source_extra_2" })
	local best = M.select_rig(S, { armor }, { { rig = rig, path = "A" } })
	check("按名字问能全命中", best ~= nil and best.full and best.score == 2,
		best and string.format("%d/%d", best.score, best.total))
	check("按名字问时不会抛错（id 路径已不再使用）", best ~= nil)

	-- 反向证据：如果调用方只给 id（模拟旧实现），必须 0 命中而不是崩
	local idsOnly = { tag = "idsOnly", ids = {} }
	idsOnly.ids[S.Application.string_to_id32("source_extra_1")] = true
	local wasted = M.select_rig(S, { idsOnly }, { { rig = rig, path = "A" } })
	check("只按 id 存的 unit 拿不到命中（正是实机的坏结果）",
		wasted ~= nil and wasted.score == 0,
		wasted and string.format("%d/%d", wasted.score, wasted.total))
end

print("")
if failures == 0 then
	print("=== 全部通过 ===")
else
	print(string.format("=== %d 项失败 ===", failures))
	os.exit(1)
end
