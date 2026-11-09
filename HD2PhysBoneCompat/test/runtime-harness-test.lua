-- 生产运行时（physbone_runtime.lua）集成自检
--
-- 这是**要部署给玩家**的那一份，所以必须验证它自己能把整条链路跑起来：
-- install → 发现 → 就绪 → 逐个 unit 选容器 → attach → 每帧驱动 → 换甲重扫 → uninstall 还回去。
--
-- 用法：luajit tmp/lua-check/runtime-harness-test.lua [runtime] [compat]

local runtimePath = arg[1] or "HD2PhysBoneCompat/src/physbone_runtime.lua"
local compatPath = arg[2] or "HD2PhysBoneCompat/src/physbone_compat.lua"

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-52s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

local Compat = assert(assert(loadfile(compatPath))())

-- ---------------------------------------------------------------------------
-- 假引擎
-- ---------------------------------------------------------------------------
local AVATAR_NODES, ARMOR_NODES = 107, { 171, 148, 132, 121, 108, 96 }
local FILLER = 2400

local engine = { alive = {}, nodes = {}, anim = {}, pos = {}, time = 10.0, writes = 0 }
local world = { name = "w" }

local function newUnit(tag, nodeCount, animated, resource)
	local unit = { tag = tag, resource = resource or ("#ID[" .. tag .. "]"), ids = {}, nodeOf = {} }
	engine.alive[unit] = true
	engine.nodes[unit] = nodeCount
	engine.anim[unit] = animated
	return unit
end

-- 骨名（带命名空间，模拟新转换器产物）
local BONE_NAMES = {}
for index = 1, 4 do BONE_NAMES[index] = string.format("jabcdef012345/source_extra_%d", index) end

local RIG = (function()
	local params = { tipMass = 200, yawStiffness = 50, yawDamping = 7,
		pitchStiffness = 50, pitchDamping = 7, angleLimit = 0.611, length = 30 }
	local bones = {}
	for index = 1, 4 do
		bones[index] = { index = index, name = BONE_NAMES[index],
			parent = (index <= 2) and (index - 1) or 0,
			parentName = (index == 1 or index == 3) and "head" or BONE_NAMES[index - 1],
			pos = { 0, -0.1 * (index - 1), 0 }, params = params, flags = 0x31 }
	end
	return { bones = bones }
end)()

local S = { Application = {}, Unit = {}, World = {}, Vector3 = {}, Quaternion = {}, Matrix4x4 = {} }
local pose, vector, quat = { kind = "pose" }, { kind = "vector" }, { kind = "quat" }

S.Application.time_since_launch = function() return engine.time end
S.Application.worlds = function() return { world } end
S.Application.string_to_id32 = function(text)
	local sum = 0
	for index = 1, #text do sum = sum + text:byte(index) * index end
	return sum * 1000 + #text
end
S.World.units = function() return engine.unitsArray() end
S.World.update_unit = function() end
S.Unit.alive = function(unit) return engine.alive[unit] == true end
S.Unit.num_scene_graph_items = function(unit) return engine.nodes[unit] or 0 end
S.Unit.has_animation_state_machine = function(unit) return engine.anim[unit] == true end
S.Unit.has_node = function(unit, id)
	if engine.alive[unit] ~= true then return false end
	return unit.ids[id] == true
end
S.Unit.node = function(unit, id) return unit.nodeOf[id] end
S.Unit.debug_name = function(unit) return unit.resource end
S.Unit.name_hash = function(unit) return unit.resource end
S.Unit.local_pose = function() return pose end
S.Unit.local_rotation = function() return { x = 0, y = 0, z = 0, w = 1 } end
S.Unit.world_position = function(u) return { unit = u } end
S.Unit.world_rotation = function() return { x = 0, y = 0, z = 0, w = 1 } end
S.Unit.set_local_pose = function() engine.writes = engine.writes + 1 end
S.Unit.set_local_rotation = function() engine.writes = engine.writes + 1 end
-- 位置按 unit 分别给出（"离玩家多远"要真的能量出来，否则没法验尸体排除）
S.Vector3.to_elements = function(v)
	local p = engine.pos[v and v.unit] or { 0, 0, 0 }
	return p[1], p[2], p[3]
end
S.Quaternion.to_elements = function(q) return q.x, q.y, q.z, q.w end
S.Matrix4x4.to_elements = function() return 1, 0, 0, 0, 1, 0, 0, 0, 1, 0.1, 0.2, 0.3 end
S.Matrix4x4.is_valid = function() return true end
S.Matrix4x4.from_quaternion_position = function() return pose end
S.Quaternion.axis_angle = function(axis, angle)
	local s = math.sin(angle / 2)
	return { x = 0, y = 0, z = s, w = math.cos(angle / 2) }
end
S.Quaternion.from_elements = function(x, y, z, w) return { x = x, y = y, z = z, w = w } end
setmetatable(S.Vector3, { __call = function() return vector end })

-- 护甲 unit：装上骨名，节点下标从 10 起
local function dress(unit)
	local nextNode = 10
	for _, name in ipairs(BONE_NAMES) do
		local id = S.Application.string_to_id32(name)
		unit.ids[id] = true
		unit.nodeOf[id] = nextNode
		nextNode = nextNode + 1
	end
	local headId = S.Application.string_to_id32("head")
	unit.ids[headId] = true
	unit.nodeOf[headId] = 9
end

local avatar = newUnit("avatar", AVATAR_NODES, true, "#ID[avatar]")
-- avatar 的指纹：带 support_mg 挂点（发现逻辑靠"有动画状态机 + 有 support_mg"认它）
do
	local mgId = S.Application.string_to_id32("support_mg")
	avatar.ids[mgId] = true
	avatar.nodeOf[mgId] = 5
end
local armors = {}
for index, count in ipairs(ARMOR_NODES) do
	armors[index] = newUnit("armor" .. index, count, false, "#ID[armor" .. index .. "]")
	dress(armors[index])
end
local fillers = {}
for index = 1, FILLER do fillers[index] = newUnit("f" .. index, 5 + (index % 20), false) end

function engine.unitsArray()
	local list = { avatar }
	for _, unit in ipairs(armors) do list[#list + 1] = unit end
	for _, unit in ipairs(fillers) do list[#list + 1] = unit end
	return list
end

rawset(_G, "stingray", S)

-- ---------------------------------------------------------------------------
local Runtime = assert(assert(loadfile(runtimePath))())
local logPath = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime.txt"

print("T1 install 后能自己发现并挂上")
do
	local originalRender = function() end
	rawset(_G, "render", originalRender)

	local runtime = Runtime.install(S, {
		compat = Compat,
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath,
		settleFrames = 2, snapshot = false, tunePath = "nonexistent-hd2pbx-tune.txt",
		pollFrames = 1,
	})
	check("install 返回运行时状态", type(runtime) == "table" and runtime.installed == true)
	check("install 是幂等的", Runtime.install(S, { compat = Compat }) == runtime)
	check("挂上了 render 钩子", rawget(_G, "render") ~= originalRender)

	local probeRender = rawget(_G, "render")
	local writesBefore = engine.writes
	for _ = 1, 60 do
		engine.time = engine.time + 1 / 72
		probeRender()
	end
	check("自动完成了 attach 并写了姿态", engine.writes > writesBefore,
		string.format("写入 %d → %d", writesBefore, engine.writes))
	check("会话数 = 候选护甲件数", runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES,
		string.format("会话=%s 期望=%d", tostring(runtime.sessions and #runtime.sessions), #ARMOR_NODES))
	-- dt 必须跟随引擎时间（这里每帧推进 1/72），不能停在写死的 1/60：
	-- 回调实测约 70 次/秒，写死 1/60 会让物理比真实时间慢 15~30%。
	check("dt 跟随引擎时间", math.abs((runtime.dt or 0) - 1 / 72) < 1e-9,
		string.format("dt=%.6f 期望=%.6f", runtime.dt or 0, 1 / 72))

	print("T2 换甲（护甲 unit 全部销毁）⇒ 自动重扫重挂")
	for _, unit in ipairs(armors) do engine.alive[unit] = false end
	local writesAtKill = engine.writes
	for _ = 1, 30 do engine.time = engine.time + 1 / 72; probeRender() end
	check("停手后不再写死 Unit", engine.writes == writesAtKill,
		string.format("新增写入 %d", engine.writes - writesAtKill))

	local armors2 = {}
	for index, count in ipairs(ARMOR_NODES) do
		armors2[index] = newUnit("b" .. index, count, false, "#ID[b" .. index .. "]")
		dress(armors2[index])
	end
	armors = armors2
	for _ = 1, 120 do engine.time = engine.time + 1 / 72; probeRender() end
	check("重挂成功", runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))
	check("重挂后继续驱动", engine.writes > writesAtKill, string.format("总写入 %d", engine.writes))

	print("T3 uninstall 把 render 还回去")
	check("uninstall 成功", Runtime.uninstall() == true)
	check("render 已还原", rawget(_G, "render") == originalRender)
	check("安装标记已清除", rawget(_G, "__hd2physbone_runtime") == nil)
end

-- ---------------------------------------------------------------------------
print("T4 缺少 compat 模块时明确报错（不静默）")
do
	local ok, err = pcall(Runtime.install, S, {})
	check("install 抛错而不是静默", ok == false and tostring(err):find("physbone_compat") ~= nil,
		tostring(err))
end

-- ---------------------------------------------------------------------------
-- T5：观测。这一组全是"2026-11-01 排查两轮都拿不到事实"的直接产物。
-- 每一条都对应一个曾经的盲区：没进驱动块 / 进了没写 / 写了没生效 / 根本没挂上。
print("T5 观测：日志可读、失败有原因、静止不误判")

local function countOf(text, needle)
	local count, pos = 0, 1
	while true do
		local found = string.find(text, needle, pos, true)
		if found == nil then break end
		count = count + 1
		pos = found + #needle
	end
	return count
end

do
	local logPath2 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t5.txt"
	os.remove(logPath2)
	local function readLog()
		local handle = io.open(logPath2, "r")
		if handle == nil then return "" end
		local text = handle:read("*a")
		handle:close()
		return text or ""
	end

	-- 一份"骨名完全对不上"的容器：用来逼出 `#plan == 0` 那条以前完全静默的路径。
	local wrongRig = { bones = { {
		index = 1, name = "nope_1", parent = 0, parentName = "head",
		pos = { 0, 0, 0 }, params = RIG.bones[1].params, flags = 0x31,
	} } }

	local armorsA = {}
	for index, count in ipairs(ARMOR_NODES) do
		armorsA[index] = newUnit("t5a" .. index, count, false, "#ID[t5a" .. index .. "]")
		dress(armorsA[index])
	end
	armors = armorsA

	local runtime = Runtime.install(S, {
		compat = Compat,
		carriers = { { rig = wrongRig, path = "wrong.patch_1.gpu_resources" } },
		logPath = logPath2,
		settleFrames = 2, snapshot = false, tunePath = "nonexistent-hd2pbx-tune.txt",
		pollFrames = 1,
		heartbeatFrames = 10,
	})
	local probeRender = rawget(_G, "render")

	for _ = 1, 40 do engine.time = engine.time + 1 / 72; probeRender() end
	local text = readLog()
	check("挂不上时给出原因", text:find("没有 unit 命中任何物理容器", 1, true) ~= nil)
	check("原因里带最高命中分数", text:find("最高命中 0/1", 1, true) ~= nil)
	check("同一原因只落一次（不刷屏）",
		countOf(text, "没有 unit 命中任何物理容器") == 1,
		string.format("出现 %d 次", countOf(text, "没有 unit 命中任何物理容器")))

	-- 换成正确容器：下一拍应该挂上，并把实例名记进日志。
	runtime.carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } }
	for _ = 1, 40 do engine.time = engine.time + 1 / 72; probeRender() end
	text = readLog()
	check("挂上后会话数正确", runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))
	check("挂载日志带实例名", text:find("#ID[t5a1]", 1, true) ~= nil)
	check("会话记住了 unitTag", runtime.sessions[1].unitTag == "#ID[t5a1]",
		tostring(runtime.sessions[1].unitTag))

	-- 心跳：证明驱动块被进入过，并给出当帧四个事实。
	check("有心跳行", text:find("驱动心跳", 1, true) ~= nil)
	check("心跳带写入/跳过/引擎调用",
		text:find("写入=", 1, true) ~= nil and text:find("跳过=", 1, true) ~= nil
			and text:find("引擎调用=", 1, true) ~= nil)

	-- 每行前缀必须是"时间戳 + 帧号"：没有帧号就看不出"30 帧后全灭"这类时序规律。
	local firstLine = text:match("([^\n]*)") or ""
	check("日志行带时间戳与帧号", firstLine:match("^%d%d:%d%d:%d%d f%d+") ~= nil, firstLine)

	-- 静止 200 帧：written 会掉到 0，但 handled（写入+跳过）不为 0 ⇒ 绝不能判"驱动失效"。
	-- 这条是整个看门狗的存在理由：2026-11-01 用 `written == 0` 判失效，结果静止的角色
	-- 每 45 帧被重挂一次，画面是"头发在垂直和原样之间来回切"。
	local reArmBefore = runtime.reArm
	for _ = 1, 200 do engine.time = engine.time + 1 / 72; probeRender() end
	check("静止时不误判驱动失效", runtime.reArm == reArmBefore,
		string.format("reArm %d → %d", reArmBefore, runtime.reArm))
	text = readLog()
	check("静止时没报驱动失效", text:find("驱动失效", 1, true) == nil)
	-- 抓一帧"写入=0 但跳过>0"作为实证：这正是 written 单独看会误判、handled 不会的那种帧。
	check("出现过 写入=0 但 跳过>0 的帧", text:find("写入=0 跳过=[1-9]", 1) ~= nil)

	-- 全部销毁：prune 必须把 dead / rebuilt 分开报，并带存活帧数（P0.5 的判据）。
	for _, unit in ipairs(armorsA) do engine.alive[unit] = false end
	for _ = 1, 30 do engine.time = engine.time + 1 / 72; probeRender() end
	text = readLog()
	check("prune 分开报已销毁/已重建",
		text:find("丢弃 6 个已销毁 / 0 个已重建", 1, true) ~= nil)
	check("失效会话带存活帧数与实例名",
		text:find("会话失效（dead）：unit=#ID%[t5a%d+%] 存活 %d+ 帧", 1) ~= nil)

	check("uninstall 成功", Runtime.uninstall() == true)
	-- 再装一次：日志必须是**追加**的，否则上一次启动的证据会被抹掉。
	Runtime.install(S, { compat = Compat, logPath = logPath2 })
	Runtime.uninstall()
	text = readLog()
	check("日志是追加而非覆盖", countOf(text, "已安装") == 2,
		string.format("已安装 出现 %d 次", countOf(text, "已安装")))
end


-- ---------------------------------------------------------------------------
-- T6：同一时间步内被调用多次，绝不能重复积分。
--
-- `Application.time_since_launch` **每帧只更新一次**，而 render 钩子实测每帧被调用多次。
-- 如果后面那几次沿用上一次的 dt 再积一遍，一帧就积了 N 倍的时间：物理快 N 倍、
-- 永远停不下来。实机表现就是"头发飘逸过头"——所以这条必须钉死。
print("T6 同一时间步内的重复调用不重复积分")
do
	local logPath3 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t6.txt"
	os.remove(logPath3)
	local armorsB = {}
	for index, count in ipairs(ARMOR_NODES) do
		armorsB[index] = newUnit("t6" .. index, count, false, "#ID[t6" .. index .. "]")
		dress(armorsB[index])
	end
	armors = armorsB
	local runtime = Runtime.install(S, {
		compat = Compat,
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath3,
		settleFrames = 2, snapshot = false, tunePath = "nonexistent-hd2pbx-tune.txt",
		pollFrames = 1,
		heartbeatFrames = 10,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 40 do engine.time = engine.time + 1 / 120; probeRender() end
	check("先挂上", runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))

	local driveBefore = runtime.driveFrames
	local repeatBefore = runtime.repeatCalls or 0
	for _ = 1, 5 do probeRender() end
	check("同拍重复调用不推进驱动", runtime.driveFrames == driveBefore,
		string.format("driveFrames %d → %d", driveBefore, runtime.driveFrames))
	check("同拍重复调用被计数", (runtime.repeatCalls or 0) == repeatBefore + 5,
		string.format("repeatCalls %d → %d", repeatBefore, runtime.repeatCalls or 0))

	engine.time = engine.time + 1 / 120
	probeRender()
	check("时钟前进才推进驱动", runtime.driveFrames == driveBefore + 1,
		string.format("driveFrames=%d", runtime.driveFrames))

	-- 时钟永久卡住时不能静默冻住：兜底恢复驱动，并大声说一次。
	runtime.options.stuckClockLimit = 5
	for _ = 1, 5 do probeRender() end
	check("时钟卡死后兜底恢复驱动", runtime.driveFrames == driveBefore + 2,
		string.format("driveFrames=%d", runtime.driveFrames))
	local handle = io.open(logPath3, "r")
	local log = handle and handle:read("*a") or ""
	if handle then handle:close() end
	check("兜底时明确告警", log:find("引擎时钟连续", 1, true) ~= nil)

	Runtime.uninstall()
end


-- ---------------------------------------------------------------------------
-- T7：阵亡 → 尸体留在原地 → 新玩家实体出现。
--
-- 这是实机反馈的原始问题：玩家阵亡后尸体**不会被销毁**，`pruneSessions` 只看
-- `Unit.alive` / 节点数，判不出"人已经换了"，于是我们一直驱动着尸体，
-- 而新玩家的护甲 unit 从来没被挂过 —— 表现就是"复活之后没有物理"。
--
-- ⚠️ **2.0.0 之后这个场景的模型变了，断言也跟着变。**
-- 1.9.x 只有一个"玩家"概念，靠"出现新的 avatar 候选"判换人；多人局里那条判据是**错的**
-- （队友一进房间就把我的会话全丢、重挂到队友身上）。2.0.0 改成：
--   每套护甲归属**离它最近的那个玩家**，会话记住主人；主人不在才释放。
-- 所以"换人"不再是一个判定事件，而是两件独立的事：
--   ① 尸体不再算玩家（HD2 里尸体是 ragdoll，没有动画状态机）⇒ 它那套护甲**失去主人**；
--   ② 失去主人的护甲离"最近的玩家"很远 ⇒ 被 `maxArmorDistance` 过滤掉，不会被挂回去。
-- 这里验的仍然是最初那三件事，只是换成新模型的等价形式：
--   ① 尸体那套护甲失去主人并被释放（而不是"检测到换人"）；
--   ② 尸体被**交还基准姿态**（不留在最后写入的姿势，像被冻住）；
--   ③ 新玩家的护甲被挂上，而尸体那套（离新玩家 50 m）不再被驱动。
print("T7 阵亡→复活：释放尸体、重挂新玩家")
do
	local logPath7 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t7.txt"
	os.remove(logPath7)
	local function readLog7()
		local handle = io.open(logPath7, "r")
		if not handle then return "" end
		local text = handle:read("*a")
		handle:close()
		return text or ""
	end

	-- 玩家 A 在原点，带 6 件护甲（都贴着玩家）
	local avatarA = newUnit("t7avatarA", AVATAR_NODES, true, "#ID[t7avatarA]")
	do
		local mgId = S.Application.string_to_id32("support_mg")
		avatarA.ids[mgId] = true
		avatarA.nodeOf[mgId] = 5
	end
	local corpseArmors = {}
	for index, count in ipairs(ARMOR_NODES) do
		corpseArmors[index] = newUnit("t7c" .. index, count, false, "#ID[t7c" .. index .. "]")
		dress(corpseArmors[index])
		engine.pos[corpseArmors[index]] = { 0, 0, 0 }
	end
	avatar = avatarA
	armors = corpseArmors

	local runtime = Runtime.install(S, {
		compat = Compat,
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath7,
		settleFrames = 2, snapshot = false, tunePath = "nonexistent-hd2pbx-tune.txt",
		pollFrames = 1,
		validateFrames = 1,      -- 每帧都校验，测试才跑得快
		maxArmorDistance = 3.0,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 60 do engine.time = engine.time + 1 / 120; probeRender() end
	check("先挂上尸体那套", runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))
	local writesBeforeDeath = engine.writes

	-- 阵亡：玩家 A 的 unit **仍然活着**（尸体就是它，这是 HD2 的实际情况），护甲也留着。
	-- 但尸体是 ragdoll ⇒ **不再有动画状态机** ⇒ `findAvatars` 不再把它算作玩家。
	-- 这一步是 2.0.0 判"主人不在了"的**唯一**依据（旧版靠"出现新候选"判换人，多人局会误判）。
	engine.anim[avatarA] = false

	-- 新玩家 B 在 50 米外出现。死掉那套护甲留在原点，离 B 有 50 m，
	-- 应当被 `maxArmorDistance` 挡在门外，绝不重新驱动。
	local avatarB = newUnit("t7avatarB", AVATAR_NODES, true, "#ID[t7avatarB]")
	do
		local mgId = S.Application.string_to_id32("support_mg")
		avatarB.ids[mgId] = true
		avatarB.nodeOf[mgId] = 5
	end
	engine.pos[avatarB] = { 50, 0, 0 }
	local newArmors = {}
	for index, count in ipairs(ARMOR_NODES) do
		newArmors[index] = newUnit("t7n" .. index, count, false, "#ID[t7n" .. index .. "]")
		dress(newArmors[index])
		engine.pos[newArmors[index]] = { 50, 0, 0 }
	end
	avatar = avatarB
	local combined = {}
	for _, unit in ipairs(corpseArmors) do combined[#combined + 1] = unit end
	for _, unit in ipairs(newArmors) do combined[#combined + 1] = unit end
	armors = combined

	for _ = 1, 120 do engine.time = engine.time + 1 / 120; probeRender() end
	local text = readLog7()
	check("尸体那套护甲失去主人并被释放", text:find("释放了", 1, true) ~= nil,
		"主人（尸体）不再算玩家时应当整组释放")
	check("尸体被交还基准姿态", text:find("已交还", 1, true) ~= nil)
	check("重挂到了新玩家身上",
		runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))
	local allNew = true
	for _, session in ipairs(runtime.sessions or {}) do
		if not tostring(session.unitTag):find("t7n", 1, true) then allNew = false end
	end
	check("挂的是新玩家那套，不是尸体", allNew,
		tostring(runtime.sessions and runtime.sessions[1] and runtime.sessions[1].unitTag))
	check("尸体那套护甲没有被重新挂上",
		(text:find("离它所属玩家超过", 1, true) ~= nil), "应记录被距离过滤掉的尸体件")

	-- 死亡之后没再往尸体写姿态（只在新玩家身上写）。
	check("新玩家有物理写入", engine.writes > writesBeforeDeath,
		string.format("%d → %d", writesBeforeDeath, engine.writes))

	-- 关键：尸体一直活着、护甲也一直在 ⇒ 不能每 1.2 秒又判一次"主人不在"再释放一遍。
	-- 反复判 = 反复 release + 重挂（几百次引擎调用 + 姿态跳变），实机就是"特别卡顿 + 没物理"。
	local function countOf(text, needle)
		local count, pos = 0, 1
		while true do
			local found = string.find(text, needle, pos, true)
			if not found then break end
			count = count + 1
			pos = found + #needle
		end
		return count
	end
	for _ = 1, 600 do engine.time = engine.time + 1 / 120; probeRender() end
	text = readLog7()
	-- 数的是"整组释放"那一条：它每轮只该出现一次，之后再多的帧都不该重复触发。
	check("释放只发生一次（不会反复重挂）", countOf(text, "释放了") == 1,
		string.format("释放 %d 次", countOf(text, "释放了")))
	check("之后仍挂在新玩家身上",
		runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES
			and tostring(runtime.sessions[1].unitTag):find("t7n", 1, true) ~= nil,
		tostring(runtime.sessions and runtime.sessions[1] and runtime.sessions[1].unitTag))

	Runtime.uninstall()
end
print("")
if failures == 0 then
	print("=== 全部通过 ===")
else
	print(string.format("=== %d 项失败 ===", failures))
	os.exit(1)
end
