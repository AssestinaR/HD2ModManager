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

-- ---------------------------------------------------------------------------
print("T8 会话死亡后的停写窗口（多人局换场景不再把骨写到已释放对象上）")
do
	rawset(_G, "render", function() end)

	local t8Avatar = newUnit("t8avatar", AVATAR_NODES, true, "#ID[t8avatar]")
	do
		local mgId = S.Application.string_to_id32("support_mg")
		t8Avatar.ids[mgId] = true
		t8Avatar.nodeOf[mgId] = 5
	end
	local t8Armors = {}
	for index, count in ipairs(ARMOR_NODES) do
		t8Armors[index] = newUnit("t8a" .. index, count, false, "#ID[t8a" .. index .. "]")
		dress(t8Armors[index])
		engine.pos[t8Armors[index]] = { 0, 0, 0 }
	end
	avatar = t8Avatar
	armors = t8Armors
	engine.pos[t8Avatar] = { 0, 0, 0 }

	local logPath8 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t8death.txt"
	pcall(os.remove, logPath8)

	local runtime = Runtime.install(S, {
		compat = Compat,
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath8,
		settleFrames = 2, snapshot = false, tunePath = "nonexistent-hd2pbx-tune.txt",
		pollFrames = 1, validateFrames = 0,   -- 关掉名单校验，隔离出"死亡停写"这一条
		maxArmorDistance = 10.0,
		deathGraceFrames = 20,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 60 do engine.time = engine.time + 1 / 120; probeRender() end
	check("T8 先全部挂上", runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))

	-- ① 正常帧确实在写骨（否则下面的"停写"断言是空转）
	local baseline = engine.writes
	for _ = 1, 5 do engine.time = engine.time + 1 / 120; probeRender() end
	check("T8 正常帧会写骨", engine.writes > baseline,
		string.format("写入=%d", engine.writes - baseline))

	-- ② 停写窗口内**一个骨都不写**。直接设窗口，把"是谁设的"和"设了之后怎样"分开测。
	local heldFrom = runtime.frame
	runtime.driveHoldUntil = heldFrom + 20
	local writesAtHold = engine.writes
	for _ = 1, 15 do engine.time = engine.time + 1 / 120; probeRender() end
	check("T8 窗口内不写骨", engine.writes == writesAtHold,
		string.format("窗口内写入=%d", engine.writes - writesAtHold))
	check("T8 窗口内确实过了帧", runtime.frame > heldFrom,
		string.format("frame=%d→%d", heldFrom, runtime.frame))

	-- ③ 过了窗口必须恢复写入：停写不等于永久停手
	for _ = 1, 40 do engine.time = engine.time + 1 / 120; probeRender() end
	check("T8 窗口过后恢复写骨", engine.writes > writesAtHold,
		string.format("恢复后写入=%d", engine.writes - writesAtHold))

	-- ④ 真让一个会话死掉：pruneSessions 必须自己把窗口设上。
	--    这是"多人局换场景"的入口条件——单人局是整组一起死，走的是另一条分支。
	local beforeDeath = runtime.frame
	engine.alive[t8Armors[1]] = false
	engine.time = engine.time + 1 / 120; probeRender()
	check("T8 会话死亡会设置停写窗口", (runtime.driveHoldUntil or 0) > beforeDeath,
		string.format("holdUntil=%s frame=%s",
			tostring(runtime.driveHoldUntil), tostring(runtime.frame)))

	Runtime.uninstall()
end

-- ---------------------------------------------------------------------------
print("T9 加载空档：不逐条查存活，直接整组重挂")
--
-- 加载界面期间 render/update 不跑，而引擎正在销毁上一场景的 unit。
-- 回来后的第一帧如果逐条 `Unit.alive`（session_health 内部就是它），
-- 就是在"句柄可能已被释放"的窗口里发引擎调用 —— 2026-11-11 实机崩在 0x3e564b 读空指针。
do
	rawset(_G, "render", function() end)

	local t9Avatar = newUnit("t9avatar", AVATAR_NODES, true, "#ID[t9avatar]")
	do
		local mgId = S.Application.string_to_id32("support_mg")
		t9Avatar.ids[mgId] = true
		t9Avatar.nodeOf[mgId] = 5
	end
	local t9Armors = {}
	for index, count in ipairs(ARMOR_NODES) do
		t9Armors[index] = newUnit("t9a" .. index, count, false, "#ID[t9a" .. index .. "]")
		dress(t9Armors[index])
		engine.pos[t9Armors[index]] = { 0, 0, 0 }
	end
	avatar = t9Avatar
	armors = t9Armors
	engine.pos[t9Avatar] = { 0, 0, 0 }

	local logPath9 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t9gap.txt"
	pcall(os.remove, logPath9)

	local runtime = Runtime.install(S, {
		compat = Compat,
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath9,
		settleFrames = 2, snapshot = false, tunePath = "nonexistent-hd2pbx-tune.txt",
		pollFrames = 1, validateFrames = 0,
		maxArmorDistance = 10.0,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 60 do engine.time = engine.time + 1 / 120; probeRender() end
	check("T9 先全部挂上", runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))

	-- 统计这一帧会发多少次 Unit.alive
	local originalAlive = S.Unit.alive
	local aliveCalls = 0
	S.Unit.alive = function(unit) aliveCalls = aliveCalls + 1 return originalAlive(unit) end
	engine.time = engine.time + 5.0          -- 模拟加载：时钟一次跳 5 秒
	probeRender()
	S.Unit.alive = originalAlive

	check("T9 空档后整组重挂（会话清空）", runtime.sessions == nil,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))
	check("T9 空档帧没有再查存活", aliveCalls == 0, "Unit.alive 调用=" .. tostring(aliveCalls))
	local text9 = ""
	do
		local handle = io.open(logPath9, "r")
		if handle then text9 = handle:read("*a") or ""; handle:close() end
	end
	check("T9 留下空档日志", text9:find("检测到加载空档", 1, true) ~= nil)
	check("T9 崩场景那套「会话失效」没有出现", text9:find("会话失效", 1, true) == nil,
		"不应逐条判活")

	Runtime.uninstall()
end

-- ---------------------------------------------------------------------------
print("T10 诊断开关 writeOff：只挂不写")
--
-- 用途：把"崩溃是否由写骨触发"与"仅仅持有句柄就够"分开。
-- 这个开关**不能**影响别的行为：发现/挂载/调度/判活全都照旧，只是不写骨。
do
	rawset(_G, "render", function() end)

	local t10Avatar = newUnit("t10avatar", AVATAR_NODES, true, "#ID[t10avatar]")
	do
		local mgId = S.Application.string_to_id32("support_mg")
		t10Avatar.ids[mgId] = true
		t10Avatar.nodeOf[mgId] = 5
	end
	local t10Armors = {}
	for index, count in ipairs(ARMOR_NODES) do
		t10Armors[index] = newUnit("t10a" .. index, count, false, "#ID[t10a" .. index .. "]")
		dress(t10Armors[index])
		engine.pos[t10Armors[index]] = { 0, 0, 0 }
	end
	avatar = t10Avatar
	armors = t10Armors
	engine.pos[t10Avatar] = { 0, 0, 0 }

	local tunePath10 = (os.getenv("TEMP") or ".") .. "\\physbone-t10-writeoff-tune.txt"
	do
		local handle = io.open(tunePath10, "w")
		handle:write("writeOff = 1\n")
		handle:close()
	end
	local logPath10 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t10writeoff.txt"
	pcall(os.remove, logPath10)

	local runtime = Runtime.install(S, {
		compat = Compat,
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath10,
		settleFrames = 2, snapshot = false, tunePath = tunePath10,
		pollFrames = 1, validateFrames = 0,
		maxArmorDistance = 10.0,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 60 do engine.time = engine.time + 1 / 120; probeRender() end
	check("T10 照样挂上（开关不影响挂载）",
		runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))

	local writesBefore = engine.writes
	for _ = 1, 60 do engine.time = engine.time + 1 / 120; probeRender() end
	check("T10 一帧都不写", engine.writes == writesBefore,
		string.format("写入=%d", engine.writes - writesBefore))
	check("T10 会话仍在（不是把会话丢了）",
		runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES)

	local text10 = ""
	do
		local handle = io.open(logPath10, "r")
		if handle then text10 = handle:read("*a") or ""; handle:close() end
	end
	check("T10 明确说出生效", text10:find("writeOff=1", 1, true) ~= nil)
	pcall(os.remove, tunePath10)

	Runtime.uninstall()
end

-- ---------------------------------------------------------------------------
print("T11 细分开关 discoverOnly / hardOff")
--
-- 实机已达成的结论：写骨不是触发点（writeOff=1 仍崩）。
-- 于是继续往上游劈：① 只扫描不挂载；② 连扫描都不做。
do
	local function scenario(tag, tuneLine, expectLog)
		rawset(_G, "render", function() end)
		local av = newUnit(tag .. "avatar", AVATAR_NODES, true, "#ID[" .. tag .. "avatar]")
		do
			local mgId = S.Application.string_to_id32("support_mg")
			av.ids[mgId] = true
			av.nodeOf[mgId] = 5
		end
		local gear = {}
		for index, count in ipairs(ARMOR_NODES) do
			gear[index] = newUnit(tag .. "a" .. index, count, false, "#ID[" .. tag .. "a" .. index .. "]")
			dress(gear[index])
			engine.pos[gear[index]] = { 0, 0, 0 }
		end
		avatar = av
		armors = gear
		engine.pos[av] = { 0, 0, 0 }

		local tunePath = (os.getenv("TEMP") or ".") .. "\\physbone-" .. tag .. "-tune.txt"
		do
			local handle = io.open(tunePath, "w")
			handle:write(tuneLine .. "\n")
			handle:close()
		end
		local logPath = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_" .. tag .. ".txt"
		pcall(os.remove, logPath)

		local runtime = Runtime.install(S, {
			compat = Compat,
			carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
			logPath = logPath,
			settleFrames = 2, snapshot = false, tunePath = tunePath,
			pollFrames = 1, validateFrames = 0,
			maxArmorDistance = 10.0,
		})
		local probeRender = rawget(_G, "render")
		for _ = 1, 90 do engine.time = engine.time + 1 / 120; probeRender() end

		local text = ""
		do
			local handle = io.open(logPath, "r")
			if handle then text = handle:read("*a") or ""; handle:close() end
		end
		check(tag .. " 不挂载（会话保持 nil）", runtime.sessions == nil,
			string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))
		check(tag .. " 说出生效", text:find(expectLog, 1, true) ~= nil)
		pcall(os.remove, tunePath)
		Runtime.uninstall()
	end

	scenario("discoverOnly", "discoverOnly = 1", "discoverOnly=1")
	scenario("hardOff", "hardOff = 1", "hardOff=1")
end

-- ---------------------------------------------------------------------------
print("T12 dropAllOnDeath：一见死亡就整组释放（不留句柄）")
--
-- 依据：discoverOnly（不持有句柄）不崩 ⇒ 崩因是攥着 unit 句柄跨过场景销毁。
-- 默认行为是"只掉一部分就留着剩下的"（多人局不闪），但那批句柄正是悬空来源。
do
	rawset(_G, "render", function() end)

	local t12Avatar = newUnit("t12avatar", AVATAR_NODES, true, "#ID[t12avatar]")
	do
		local mgId = S.Application.string_to_id32("support_mg")
		t12Avatar.ids[mgId] = true
		t12Avatar.nodeOf[mgId] = 5
	end
	local t12Armors = {}
	for index, count in ipairs(ARMOR_NODES) do
		t12Armors[index] = newUnit("t12a" .. index, count, false, "#ID[t12a" .. index .. "]")
		dress(t12Armors[index])
		engine.pos[t12Armors[index]] = { 0, 0, 0 }
	end
	avatar = t12Avatar
	armors = t12Armors
	engine.pos[t12Avatar] = { 0, 0, 0 }

	local tunePath12 = (os.getenv("TEMP") or ".") .. "\\physbone-t12-dropall-tune.txt"
	do
		local handle = io.open(tunePath12, "w")
		handle:write("dropAllOnDeath = 1\n")
		handle:close()
	end
	local logPath12 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t12dropall.txt"
	pcall(os.remove, logPath12)

	local runtime = Runtime.install(S, {
		compat = Compat,
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath12,
		settleFrames = 2, snapshot = false, tunePath = tunePath12,
		pollFrames = 1, validateFrames = 0,
		maxArmorDistance = 10.0,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 60 do engine.time = engine.time + 1 / 120; probeRender() end
	check("T12 先全部挂上", runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))

	-- 只死一个（多人局里队友那套先被释放）
	engine.alive[t12Armors[1]] = false
	engine.time = engine.time + 1 / 120; probeRender()

	check("T12 一个死亡就整组释放（不留剩的）", runtime.sessions == nil,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))
	local text12 = ""
	do
		local handle = io.open(logPath12, "r")
		if handle then text12 = handle:read("*a") or ""; handle:close() end
	end
	check("T12 日志说明整组释放", text12:find("整组释放", 1, true) ~= nil)
	pcall(os.remove, tunePath12)

	Runtime.uninstall()
end

-- ---------------------------------------------------------------------------
print("T13 dumpApi：导出引擎命名空间清单（找批量写骨 API 的依据）")
do
	rawset(_G, "render", function() end)

	local tunePath13 = (os.getenv("TEMP") or ".") .. "\\physbone-t13-dumpapi-tune.txt"
	do
		local handle = io.open(tunePath13, "w")
		handle:write("dumpApi = 1\n")
		handle:close()
	end
	local dumpPath13 = (os.getenv("TEMP") or ".") .. "\\physbone-t13-api.txt"
	pcall(os.remove, dumpPath13)
	local logPath13 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t13dumpapi.txt"
	pcall(os.remove, logPath13)

	local runtime = Runtime.install(S, {
		compat = Compat,
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath13,
		settleFrames = 2, snapshot = false, tunePath = tunePath13,
		apiDumpPath = dumpPath13,
		pollFrames = 1, validateFrames = 0,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 10 do engine.time = engine.time + 1 / 120; probeRender() end

	local dump = ""
	do
		local handle = io.open(dumpPath13, "r")
		if handle then dump = handle:read("*a") or ""; handle:close() end
	end
	check("T13 写出了清单", #dump > 0, string.format("长度=%d", #dump))
	check("T13 含 Unit 命名空间", dump:find("[Unit]", 1, true) ~= nil)
	-- 假引擎里 Unit.alive / Unit.world_position 都是表成员，必须被列出来
	check("T13 列出了 Unit 的真实成员", dump:find("alive = function", 1, true) ~= nil)
	check("T13 带运行时版本", dump:find("physbone runtime", 1, true) ~= nil)
	pcall(os.remove, tunePath13)
	pcall(os.remove, dumpPath13)

	Runtime.uninstall()
end

-- ---------------------------------------------------------------------------
print("T14 装填：每帧只打 1 件也要把两个玩家的件都挂满（不再被配额饿死）")
--
-- 这就是用户报的"只有自己有物理、队友全没有"的回归：旧实现按 owner.order 排序 + 固定件数额度，
-- 自己的件先占满额度（实测 2 人局 12 个名额里自己 9 件、队友 3 件）。
-- 现在改成"按 unit 缓存 + 每帧新打分 N 件"，进度单调，所以队友迟早会被轮到。
do
	rawset(_G, "render", function() end)

	local a14 = newUnit("t14avatarA", AVATAR_NODES, true, "#ID[t14avatarA]")
	local b14 = newUnit("t14avatarB", AVATAR_NODES, true, "#ID[t14avatarB]")
	for _, av in ipairs({ a14, b14 }) do
		local mgId = S.Application.string_to_id32("support_mg")
		av.ids[mgId] = true
		av.nodeOf[mgId] = 5
	end
	engine.pos[a14] = { 0, 0, 0 }
	engine.pos[b14] = { 10, 0, 0 }

	-- 每人 7 件：**总数 14 件 > 旧实现的配额度**（`max(12, 4×2) = 12`）。
	-- 旧实现按 owner.order 排序 + 固定额度 ⇒ 自己的 7 件先吃满 12 个名额，队友只拿到 5 件。
	-- 这一条就是用来把那种"饿死队友"钉死的。
	local PIECES_PER_PLAYER = 7
	local gear = {}
	local counts = { { 171, 148, 132 }, { 121, 108, 96 } }
	local owners = { a14, b14 }
	for who = 1, 2 do
		for slot = 1, PIECES_PER_PLAYER do
			local unit = newUnit(("t14%s%d"):format(who == 1 and "a" or "b", slot),
				counts[who][((slot - 1) % 3) + 1], false, "#ID[t14p" .. who .. slot .. "]")
			dress(unit)
			engine.pos[unit] = engine.pos[owners[who]]
			gear[#gear + 1] = unit
		end
	end
	-- 假引擎的 unitsArray() 只吐一个 `avatar` 全局；第二个玩家必须混在 armors 里才能被扫到
	-- （findAvatars 是在整份 units 里找"有动画状态机 + support_mg"的，找到后会被排除出候选）。
	avatar = a14
	local unitsList = { b14 }
	for _, unit in ipairs(gear) do unitsList[#unitsList + 1] = unit end
	armors = unitsList

	local tunePath14 = (os.getenv("TEMP") or ".") .. "\\physbone-t14-fill-tune.txt"
	do
		local handle = io.open(tunePath14, "w")
		handle:write("writeOff = 0\n")
		handle:close()
	end

	local runtime = Runtime.install(S, {
		compat = Compat,
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t14fill.txt",
		settleFrames = 2, snapshot = false, tunePath = tunePath14,
		pollFrames = 1, validateFrames = 0,
		maxArmorDistance = 15.0,
		scoreUnitsPerFrame = 1,      -- 故意压到每帧只打 1 件：最容易饿死队友的设置
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 400 do engine.time = engine.time + 1 / 120; probeRender() end

	check("T14 十四件全部挂上", runtime.sessions ~= nil and #runtime.sessions == 14,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))
	local perOwner = {}
	for _, session in ipairs(runtime.sessions or {}) do
		local key = session.ownerOrder or 99
		perOwner[key] = (perOwner[key] or 0) + 1
	end
	check("T14 自己名下 7 件全在", (perOwner[1] or 0) == PIECES_PER_PLAYER, "玩家1=" .. tostring(perOwner[1]))
	check("T14 队友名下 7 件也全在（旧实现的典型值是被配额截到 5）",
		(perOwner[2] or 0) == PIECES_PER_PLAYER, "玩家2=" .. tostring(perOwner[2]))
	pcall(os.remove, tunePath14)

	Runtime.uninstall()
end

-- ---------------------------------------------------------------------------
print("T15 负结果会过期：骨骼晚就绪的件必须能被重新打分挂上")
--
-- 实机踩到（2026-11-12）：unit 刚生成时骨骼还没就绪，第一拍打分 0 命中；把这条当永久结论，
-- 这个 unit 就再也不会被重试 —— 现象是"只有先就绪的头盔有物理，护甲的件永远没有"，
-- 换护甲后连头盔也没了（新 unit 又被提前判死）。所以负结论必须带过期 + 退避。
do
	rawset(_G, "render", function() end)

	local av15 = newUnit("t15avatar", AVATAR_NODES, true, "#ID[t15avatar]")
	do
		local mgId = S.Application.string_to_id32("support_mg")
		av15.ids[mgId] = true
		av15.nodeOf[mgId] = 5
	end
	engine.pos[av15] = { 0, 0, 0 }

	local late = newUnit("t15late", 148, false, "#ID[t15late]")
	dress(late)
	engine.pos[late] = { 0, 0, 0 }
	local readyIds = late.ids
	late.ids = {}          -- 先装"骨骼还没就绪"

	avatar = av15
	armors = { late }

	local tunePath15 = (os.getenv("TEMP") or ".") .. "\\physbone-t15-neg-tune.txt"
	do
		local handle = io.open(tunePath15, "w")
		handle:write("writeOff = 0\n")
		handle:close()
	end

	local runtime = Runtime.install(S, {
		compat = Compat,
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t15neg.txt",
		settleFrames = 2, snapshot = false, tunePath = tunePath15,
		pollFrames = 1, validateFrames = 0,
		maxArmorDistance = 10.0,
		scoreUnitsPerFrame = 8,        -- 不设小额度的干扰，专测负结果过期
		negativeRetryFrames = 5,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 12 do engine.time = engine.time + 1 / 120; probeRender() end
	check("T15 骨骼没就绪时挂不上", runtime.sessions == nil,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))

	late.ids = readyIds             -- 骨骼就绪
	for _ = 1, 60 do engine.time = engine.time + 1 / 120; probeRender() end
	local entry = runtime.rigMatches and runtime.rigMatches[late]
	local entryText = entry == nil and "nil"
		or ("none=" .. tostring(entry.none) .. " retryAt=" .. tostring(entry.retryAt)
			.. " tries=" .. tostring(entry.tries))
	check("T15 负结果过期后能重新打分并挂上",
		runtime.sessions ~= nil and #runtime.sessions == 1,
		string.format("会话=%s frame=%s 缓存=%s",
			tostring(runtime.sessions and #runtime.sessions), tostring(runtime.frame), entryText))
	pcall(os.remove, tunePath15)

	Runtime.uninstall()
end

print("")
if failures == 0 then
	print("=== 全部通过 ===")
else
	print(string.format("=== %d 项失败 ===", failures))
	os.exit(1)
end
