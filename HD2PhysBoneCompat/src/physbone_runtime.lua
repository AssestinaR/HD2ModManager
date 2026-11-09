-- HD2 物理骨骼运行时（生产入口）
--
-- 这是**真正部署给玩家**的那一份。调试探针（tmp/lua-boot/probe.lua）保留在开发侧，
-- 两者共用同一个求解器库 `physbone_compat.lua`，区别只在"发现目标 + 调度"这一层。
--
-- 两种部署形态，同一个文件：
--
--   A. 内置 lua（独占 boot）
--      管理器的 boot 文档 `require` 本模块并调用 `install()`。此时我们**拥有** boot，
--      init/update/render 都是我们的。
--
--   B. 外置 lua 兼容（独立 patch 模块）
--      本文件作为一个普通 Lua 资源投放，由第三方加载器 `require` 或直接执行。
--      此时我们**不拥有**任何东西：`update`/`render` 可能是别人包装过的，
--      也可能被调用多次。所以：
--        · 绝不假设 `_G` 是我们的（不写全局，除一个安装标记）；
--        · 挂钩子一律"接在已有函数后面"，并把已有函数还回去；
--        · 所有状态挂在模块表上，重复 install 直接返回同一个实例。
--
-- 设计约束（都是实机踩出来的，改之前先看 docs/physbone-status.md）：
--   · 引擎调用是主要成本 ⇒ 每帧只做必要的事，姿态没变就不写；
--   · 不能缓存引擎返回的句柄（0.1 秒就变 inf）；
--   · 对已销毁 Unit 调任何方法 = 原生 AV ⇒ 每帧先 `Unit.alive`；
--   · 场景图重建只看 `num_scene_graph_items`（`has_node` 会假翻）。
--
-- 观测（v1.1.0 起）：这一层必须能自证"我到底做了什么"。
-- 2026-11-01 那次"物理完全没生效"排查了整整两轮，卡点全在"日志里没有事实"：
-- 分不清是没进驱动块、进了但没写、写了但写错骨、还是根本没挂上。
-- 所以下面每一处"静默 return"都配了一条原因日志，并且**只在原因变化时**落盘
-- （每 20 帧一条同样的等待原因是刷屏，不是信息）。

local M = {}

-- 2.0.4：快照**探针**。按骨名采回来 13 个全"缺"，而同一套 API 在求解器里是好的 ——
--        光看一个"缺 13"分不清是 has_node 假、node 假还是读位置假。请求加 `probe = 1`：
--        前 4 个 id 逐步把 类型/返回值 打进日志（`[快照][探针]`）；另外数字 id 没命中时
--        退回字符串名字再试一次（id32 只是名字的哈希，未必等价）。
--
-- 2.0.3：快照的**取证能力**补强（实机第一次按 F9 就把游戏读崩了，而日志里没有现场）。
--        · `[快照]` 归入"永远落盘"的那几类，预算用尽也留得下
--        · 每帧**先**打一行"开始第 k/M 帧、N 个节点"再采 —— 崩了就知道崩在第几帧
--        · 每帧单独落盘（snapshot-<seq>-s<k>.json），崩了前面的数据还在
--        · 新增请求项 `limit=N`：只采前 N 个节点，先探路再放量
--        一次按键读 685 个节点 ≈ 2700 次引擎调用，这个量本身就是风险。
--
-- 2.0.2：**姿态快照**。按一下热键，把当前玩家那具骨架的世界变换写到
--        `%LOCALAPPDATA%\hd2physbone-snap\snapshot-*.json`（外部工具写请求文件触发，
--        因为游戏的输入走 Raw Input，Lua 侧读不到键）。为什么需要：穿模只在动起来时
--        看得见，而离线只有 bind 姿态；实测绑定时链根离髋部表面 +4.6~+9.9 cm（全在外面），
--        真正插进去的是皮面，所以必须拿到"那一帧的骨变换"，在 PC 侧蒙皮还原网格再判。
--
-- 2.0.1：**日志瘦身**（只动日志，不动求解）。心跳 120 帧 → 600 帧；打开时旧文件超
--        512 KiB 就轮转成 `.1`；一次启动最多写 128 KiB，用满之后只留错误/失败/警告/驱动失效
--        四类。目的一句话：日志是事后唯一的证据，但它不该成为磁盘上的负担，也不该因为
--        "玩家这局玩了多久"而决定自己有多大。
--
-- 2.0.0：**多人局**。以前整条调度只认"一个玩家 avatar"，多出来的候选一律当成尸体；
--        于是队友一出现就被判成"玩家换人了"，会话全丢、重新挂到队友身上，自己身上反而没物理。
--        现在按玩家分组：每个活动玩家各自持有自己的会话，谁死了只收谁。
--        附：`World.units_by_resource` 直接列出所有玩家 avatar（一次调用，替代扫 2290 个 unit）；
--        `World.debug_camera_pose` 给出相机位置，用来排序（最近的那个就是自己）；
--        链数超预算时非首位玩家按 2 帧一次驱动（dt 累积补偿，不丢时间）。
--
-- 1.9.1：容器扫描窗口改成"按实际部署的最大 patch 序号 + 5"（以前写死 0..63，
--        装一堆 mod 时排在 63 之后的物理 mod 会被漏掉，日志还误报成"没做 embed"）。
M.VERSION = "2.1.12"

--- 安装标记用的全局键。只写这一个全局，且值就是模块本身（便于外部检查是否已装）。
local INSTALL_KEY = "__hd2physbone_runtime"

local Compat          -- physbone_compat 模块（require 或注入）
local S               -- stingray 命名空间
local state = nil     -- 运行状态；nil = 未安装
local originalRender  -- 挂载前的 render（链式调用，不吞掉别人）

-- ---------------------------------------------------------------------------
-- 小工具
-- ---------------------------------------------------------------------------

--- 默认日志路径。`options.logPath` 可覆盖，`options.log == false` 可彻底静默。
local function defaultLogPath(options)
	local path = options and options.logPath
	if path ~= nil then return path end
	if options and options.log == false then return nil end
	local base = os.getenv("LOCALAPPDATA")
	if type(base) == "string" and #base > 0 then return base .. "\\hd2physbone.log" end
	return nil
end

--- 一次启动最多往盘上写多少字节（默认 128 KiB）；用满之后只留"出事了"的行。
local DEFAULT_LOG_MAX_BYTES = 128 * 1024

--- 打开日志时如果已有文件超过这个大小就轮转成 `.1`（默认 512 KiB）。
local DEFAULT_LOG_ROTATE_BYTES = 512 * 1024

--- 预算用尽之后**仍然**要落盘的标记。
--- <para>
--- 只留这几类，是因为它们本身就是"出事了"的证据；心跳、等待、预算、逐链明细
--- 这类常规行才是吃字节的大头，也是最没必要的长期留存。
--- </para>
local ALWAYS_LOG_TAGS = { "[错误]", "[失败]", "[警告]", "[驱动失效]", "[快照]", "[裙摆]" }

--- 造一个 logger。返回 `log, context`。
--- <para>
--- 两个刻意的选择：
---   ① **追加**而不是覆盖。覆盖式会把上一次启动的证据抹掉，于是"这次和上次哪里不一样"
---      永远无从对比——2026-11-01 排查就卡在这里（日志只剩最后 30 秒）。
---   ② 每行带**时间戳 + 帧号**。没有帧号，跨会话的"30 帧后全灭"这种时序规律看不出来。
--- 帧号由 `drive` 每帧写进 `context.frame`：compat 会直接调 `session.logger`，
--- 只能靠这个共享表把上下文带进去。
--- </para>
--- <para>
--- 光"追加"会有两个副作用，所以这里还加了两道闸：
---   * **跨会话轮转**：打开时文件已经超过 <c>logRotateBytes</c> 就改名成 `.1`。
---     否则这个文件会随每次启动单调长大，几百 MB 之后连打开都费劲。
---   * **单次启动预算**：写满 <c>logMaxBytes</c> 就停常规行，并落一行说明。
---     心跳这类周期行的总量因此有了硬上限，不再取决于玩家玩了多久。
--- 刻意**没有**加"开局 N 秒后停止录制"的时间窗：玩到第 40 分钟才出现的毛病
--- 恰好会落在窗外，而这正是最需要日志的时候。要省字节，砍周期行比砍时间窗安全。
--- </para>
local function makeLogger(options)
	local handle = nil
	local path = defaultLogPath(options)
	local maxBytes = (options and options.logMaxBytes) or DEFAULT_LOG_MAX_BYTES
	-- **开发模式**：把预算整个关掉，全量落盘。
	-- 为什么需要：预算用尽后只留错误类，于是"我加的那行诊断"被静默丢掉——
	-- 2026-11-09 就因为这个白测了一轮（以为没效果，其实是没有观测数据）。
	-- 开关走调参文件（`devLog = 1`）或环境变量 `HD2_PHYSBONE_DEV=1`：两者都在启动时读一次，
	-- 不影响运行期性能。产品环境不设它 ⇒ 行为与之前完全一致。
	if (options and options.devLog == true) or os.getenv("HD2_PHYSBONE_DEV") == "1" then
		maxBytes = math.huge
	else
		local tunePath = (options and options.tunePath) or ((os.getenv("LOCALAPPDATA") or "") .. "\\hd2pbx_tune.txt")
		local ok, handle = pcall(io.open, tunePath, "r")
		if ok and handle ~= nil then
			local okRead, text = pcall(handle.read, handle, "*a")
			pcall(handle.close, handle)
			if okRead and type(text) == "string" and text:find("devLog%s*=%s*1") ~= nil then
				maxBytes = math.huge
			end
		end
	end
	local rotateBytes = (options and options.logRotateBytes) or DEFAULT_LOG_ROTATE_BYTES
	if path then
		-- 轮转：先量旧文件，超阈值就挪到 `.1`（覆盖上一次的 `.1`）。
		pcall(function()
			local probe = io.open(path, "r")
			if probe == nil then return end
			local size = probe:seek("end") or 0
			probe:close()
			if size <= rotateBytes then return end
			os.remove(path .. ".1")
			os.rename(path, path .. ".1")
		end)
		local ok, h = pcall(io.open, path, "a")
		if ok then handle = h end
	end
	local debugEnabled = options ~= nil and options.debug == true
	local context = { frame = 0 }
	local used, lines, exhausted = 0, 0, false
	local function writeLine(text)
		local ok = pcall(function() handle:write(text, "\n") handle:flush() end)
		if ok then lines = lines + 1 end
		return ok
	end
	local function alwaysKept(text)
		for index = 1, #ALWAYS_LOG_TAGS do
			if string.find(text, ALWAYS_LOG_TAGS[index], 1, true) ~= nil then return true end
		end
		return false
	end
	local function emit(text)
		if handle ~= nil then
			local size = #text + 1
			if exhausted then
				-- 预算用尽：常规行丢掉，出事的行照写。
				if alwaysKept(text) then used = used + size; writeLine(text) end
			elseif used + size > maxBytes then
				exhausted = true
				used = used + size
				writeLine(string.format(
					"[physbone] 日志已达上限 %d KiB（此前 %d 行）⇒ 本次启动后续只保留错误/失败/警告类",
					math.floor(maxBytes / 1024), lines))
			else
				used = used + size
				writeLine(text)
			end
		end
		if debugEnabled then
			local real = rawget(_G, "print")
			if type(real) == "function" then pcall(real, text) end
		end
	end
	local function log(fmt, ...)
		if handle == nil and not debugEnabled then return end
		local body = select("#", ...) > 0 and string.format(fmt, ...) or fmt
		emit(string.format("%s f%-7d %s", os.date("%H:%M:%S"), context.frame, body))
	end
	return log, context
end

local function try(...)
	local ok, a, b, c = pcall(...)
	if ok then return a, b, c end
	return nil, tostring(a)
end

local function readVector(S_, vector)
	if vector == nil then return nil end
	local ok, x, y, z = pcall(function() return S_.Vector3.to_elements(vector) end)
	if ok and type(x) == "number" and x == x then return x, y, z end
	return nil
end

local function id32(S_, text)
	local ok, value = pcall(function() return S_.Application.string_to_id32(text) end)
	if ok then return value end
	return nil
end

--- `Unit.debug_name` 返回形如 `#ID[a0cc3d2318d9e237]` 的**实例**标识。
--- 这是运行时唯一可靠的"是不是同一个 unit"判据（`Unit.id` 对大量 unit 返回 nil）。
--- ⚠️ 只能对活着的 unit 调：销毁之后再读就是原生 AV，所以挂载时就要记下来。
local function unit_tag(S_, unit)
	local text = try(S_.Unit.debug_name, unit)
	if type(text) ~= "string" then return "?" end
	return text
end

--- 记一条"还没挂上"的原因。
--- 只在**原因变化**时落盘，重复次数留给心跳报——这样"一直卡在同一步"和"来回变"能区分开。
--- `detail` 里的数字会变（分数、节点数…），所以只在变化时打一次，不参与去重键。
local function note_wait(runtime, reason, detail)
	if runtime.waitReason == reason then
		runtime.waitRepeat = (runtime.waitRepeat or 0) + 1
		return
	end
	runtime.logger("[physbone][等待] %s%s", reason, detail ~= nil and ("：" .. detail) or "")
	runtime.waitReason = reason
	runtime.waitRepeat = 0
end

-- ---------------------------------------------------------------------------
-- 世界发现
-- ---------------------------------------------------------------------------

--- 主世界 + 它的 unit 列表。优先用上次记住的下标（省掉遍历所有世界）。
local function pickWorld(S_, cache)
	local worlds = try(S_.Application.worlds)
	if type(worlds) ~= "table" or #worlds == 0 then return nil, nil end
	local cached = cache.worldIndex
	if cached ~= nil and worlds[cached] ~= nil then
		local units = try(S_.World.units, worlds[cached])
		if type(units) == "table" and #units >= 64 then return worlds[cached], units end
	end
	local bestWorld, bestUnits, bestCount, bestIndex = nil, nil, -1, nil
	for index = 1, #worlds do
		local units = try(S_.World.units, worlds[index])
		local count = type(units) == "table" and #units or -1
		if count > bestCount then
			bestWorld, bestUnits, bestCount, bestIndex = worlds[index], units, count, index
		end
	end
	cache.worldIndex = bestIndex
	return bestWorld, bestUnits
end

--- 玩家 avatar 的资源路径。同一个角色的所有玩家共用这一个资源，所以
--- `World.units_by_resource(world, 路径)` **一次调用**就能把场上所有玩家列出来，
--- 不必再"扫 2290 个 unit 猜谁有动画状态机"。
---
--- 这也是"多人局只给一个人挂物理"的正解：我们从来不需要知道**哪个是我**，
--- 需要的是**哪些是玩家**——这个资源名就是那个答案。谁是第一位（自己）交给相机距离。
local DEFAULT_AVATAR_RESOURCE = "content/fac_helldivers/cha_avatar/avatar_helldiver"

--- 相机位置。只用于两件事：给玩家排序（最近的那个＝自己）、给驱动预算定优先级。
--- 读不到**不影响功能**（只是"谁是第一位"退回按 unit 顺序），所以任何一步失败都安静返回 nil，
--- 并记住别再试——每帧问一次注定失败的引擎调用纯属浪费。
local function cameraPosition(S_, world, cache)
	if cache.cameraUsable == false then return nil end
	local matrix = S_.Matrix4x4
	if type(matrix) ~= "table" or type(matrix.translation) ~= "function" then
		cache.cameraUsable = false
		return nil
	end
	local pose = try(S_.World.debug_camera_pose, world)
	if pose == nil then
		cache.cameraUsable = false
		return nil
	end
	local ok, value = pcall(matrix.translation, pose)
	if not ok then
		cache.cameraUsable = false
		return nil
	end
	local x, y, z = readVector(S_, value)
	if x == nil then
		cache.cameraUsable = false
		return nil
	end
	cache.cameraUsable = true
	return x, y, z
end

--- 场上**所有**玩家 avatar。判据两级：
---   ① `World.units_by_resource(world, avatar 资源路径)` —— 精确、一次调用。
---   ② 拿不到（老版本没这个函数 / 资源路径变了）才退回全量扫描"有动画状态机 + 带 support_mg"。
--- 两种都要求"带 support_mg"：那是玩家 avatar 的挂点骨，人台/敌人没有。
local function findAvatars(S_, world, units, count, supportMg, minNodes, options, cache)
	local resource = options.avatarResource or DEFAULT_AVATAR_RESOURCE
	local byResource = nil
	if type(S_.World) == "table" and type(S_.World.units_by_resource) == "function" then
		byResource = try(S_.World.units_by_resource, world, resource)
	end

	local function collect(candidates, requireAnim)
		local list, seen, noAnim = {}, {}, 0
		for index = 1, #candidates do
			local unit = candidates[index]
			if unit ~= nil and seen[unit] == nil then
				seen[unit] = true
				if Compat.unit_alive(S_, unit) then
					local nodes = try(S_.Unit.num_scene_graph_items, unit)
					local animated = try(S_.Unit.has_animation_state_machine, unit) == true
					if type(nodes) == "number" and nodes >= minNodes
						and (supportMg == nil or try(S_.Unit.has_node, unit, supportMg) == true) then
						if requireAnim and not animated then
							noAnim = noAnim + 1
						else
							list[#list + 1] = { unit = unit, nodes = nodes }
						end
					end
				end
			end
		end
		return list, noAnim
	end

	local list, dropped = nil, 0
	if type(byResource) == "table" and #byResource > 0 then
		cache.avatarSource = "units_by_resource"
		-- 先要求"有动画状态机"：载具/人台这类同资源的东西通常没有状态机，这一条把它们挡在外面。
		list, dropped = collect(byResource, true)
		-- 但绝不因为这一条把所有人都挡掉：退回"只要有 support_mg 就算"。
		if #list == 0 then list, dropped = collect(byResource, false) end
	else
		cache.avatarSource = "scan"
		local scanned = {}
		for index = 1, count do
			local unit = units[index]
			if try(S_.Unit.has_animation_state_machine, unit) == true then
				scanned[#scanned + 1] = unit
			end
		end
		list = collect(scanned, true)
	end
	-- 把"同资源但没有动画状态机"的数量报出来：实机里"某个队友没挂上"到底是没找到、
	-- 还是被这一条挡掉了，只有这个数字能分辨。
	cache.avatarDroppedNoAnim = dropped
	return list
end

--- 一趟扫完：**所有**玩家 avatar + 候选护甲件（存活、节点数够），
--- 每个护甲件归属到"离它最近的那个玩家"，按玩家顺序排序。
--- 返回 nil + 原因 表示"还没准备好"（场景在变 / 找不到目标），调用方下一拍再来。
local function discover(S_, cache, options, logger)
	local world, units = pickWorld(S_, cache)
	if world == nil or units == nil then return nil, "读不到世界或 unit 列表" end
	local count = #units
	local previous = cache.lastUnitCount
	cache.lastUnitCount = count
	-- 场景还在加载/切换（unit 总数在变）⇒ 不做全量扫描，下一拍再来。
	if previous ~= count then
		return nil, "unit 总数在变（加载/切场景中）"
	end

	local supportMg = id32(S_, "support_mg")
	if supportMg == nil then return nil, "support_mg 的 id32 拿不到" end
	local minNodes = options.minNodes or 90
	local maxUnits = options.maxUnits or 64
	local maxAvatars = options.maxAvatars or 4

	-- ① 找全所有玩家。
	local avatars = findAvatars(S_, world, units, count, supportMg, minNodes, options, cache)
	if #avatars == 0 then
		cache.worldIndex = nil   -- 可能选错了世界，下一拍重新挑
		return nil, string.format("没找到玩家 avatar（%d 个 unit，判据=%s%s）",
			count, tostring(cache.avatarSource),
			(cache.avatarDroppedNoAnim or 0) > 0
				and string.format("，另有 %d 个同资源 unit 因没有动画状态机被跳过", cache.avatarDroppedNoAnim)
				or "")
	end
	if (cache.avatarDroppedNoAnim or 0) > 0 and cache.avatarDroppedLogged ~= true then
		cache.avatarDroppedLogged = true
		logger(string.format(
			"[physbone][玩家] 找到 %d 个玩家；另有 %d 个同资源 unit 没有动画状态机，按载具/人台处理（跳过）",
			#avatars, cache.avatarDroppedNoAnim))
	end

	-- ② 位置 + 排序。相机距离最近的那个＝自己（第一人称时相机就在自己头上）。
	local camX, camY, camZ = cameraPosition(S_, world, cache)
	if cache.cameraLogged ~= true then
		cache.cameraLogged = true
		-- 这条只打一次：实机排查时"第一个到底是不是我"完全取决于它，
		-- 而 debug_camera_pose 在某些版本上返回的是开发自由相机，那种情况下排序会退化。
		logger(string.format("[physbone] 玩家排序依据：%s",
			camX ~= nil and "相机位置（最近的那个＝自己）" or "读不到相机 ⇒ 只按节点数排（功能不受影响）"))
	end
	for index = 1, #avatars do
		local avatar = avatars[index]
		local ax, ay, az = readVector(S_, try(S_.Unit.world_position, avatar.unit, 1))
		avatar.x, avatar.y, avatar.z = ax, ay, az
		if camX ~= nil and ax ~= nil then
			local dx, dy, dz = ax - camX, ay - camY, az - camZ
			avatar.cameraDistance = dx * dx + dy * dy + dz * dz
		end
	end
	table.sort(avatars, function(a, b)
		local da, db = a.cameraDistance, b.cameraDistance
		if da ~= nil and db ~= nil then return da < db end
		if da ~= nil then return true end
		if db ~= nil then return false end
		return (a.nodes or 0) > (b.nodes or 0)
	end)
	if #avatars > maxAvatars then
		-- 上限只防"某个场景里有几十个 avatar 资源实例"把预算吃光；正常对局是 1~4。
		for index = #avatars, maxAvatars + 1, -1 do table.remove(avatars, index) end
	end
	for index = 1, #avatars do
		avatars[index].order = index
		avatars[index].tag = nil   -- 实例名等真要挂载时再读（每个 unit 一次引擎调用）
	end

	-- ③ 护甲候选：扫一遍 unit。avatar 自己不进候选（它们是骨架，不是护甲件）。
	local avatarUnits = {}
	for index = 1, #avatars do avatarUnits[avatars[index].unit] = avatars[index] end

	local withMg, withoutMg = {}, {}
	for index = 1, count do
		local unit = units[index]
		if avatarUnits[unit] == nil and Compat.unit_alive(S_, unit) then
			local nodes = try(S_.Unit.num_scene_graph_items, unit)
			if type(nodes) == "number" and nodes >= minNodes then
				if try(S_.Unit.has_node, unit, supportMg) == true then
					if #withMg < maxUnits then
						withMg[#withMg + 1] = { unit = unit, nodes = nodes, mg = true }
					end
				elseif #withoutMg < maxUnits then
					withoutMg[#withoutMg + 1] = { unit = unit, nodes = nodes, mg = false }
				end
			end
		end
	end
	local armorList = withMg
	for _, candidate in ipairs(withoutMg) do
		if #armorList >= maxUnits then break end
		armorList[#armorList + 1] = candidate
	end
	if #armorList == 0 then
		return nil, string.format("没有候选护甲件（%d 个 unit，节点数门槛 %d）", count, minNodes)
	end

	-- ④ 归属：每个候选件属于**离它最近的那个玩家**。
	--    玩家那套护甲挂在 avatar 骨架上，位置几乎重合；队友的在队友身上；尸体/人台离谁都远。
	--    这一个不等式就是全部事实来源——不需要问"谁是主件"，也不会因为"多出来一个候选"就误判换人。
	for _, candidate in ipairs(armorList) do
		local ux, uy, uz = readVector(S_, try(S_.Unit.world_position, candidate.unit, 1))
		if ux ~= nil then
			local best, bestDistance = avatars[1], math.huge
			for index = 1, #avatars do
				local avatar = avatars[index]
				if avatar.x ~= nil then
					local dx, dy, dz = ux - avatar.x, uy - avatar.y, uz - avatar.z
					local distance = dx * dx + dy * dy + dz * dz
					if distance < bestDistance then best, bestDistance = avatar, distance end
				end
			end
			candidate.owner = best
			candidate.distance = bestDistance
		else
			candidate.owner = avatars[1]
			candidate.distance = math.huge
		end
	end
	table.sort(armorList, function(a, b)
		local oa = a.owner ~= nil and a.owner.order or 99
		local ob = b.owner ~= nil and b.owner.order or 99
		if oa ~= ob then return oa < ob end
		if a.mg ~= b.mg then return a.mg end
		return (a.distance or math.huge) < (b.distance or math.huge)
	end)
	return world, units, avatars, armorList
end

--- 从部署后的 patch 尾部读物理容器。返回 { { rig = ..., path = ... }, ... }。
--- 路径是"猜"的（运行时不知道 data 的绝对路径），所以多前缀都试一遍，成功后按相对名去重。
---
--- **扫描窗口按实际部署算，不写死。** patch 序号 = 这个 mod 在配置里的载入顺序：
--- 单独装一个物理 mod 时它是 `patch_0`，装了一大堆之后它可能是 `patch_87`。
--- 以前这里写死 `0..63`，于是"装一堆 mod 就找不到任何容器"——排在 63 之后的容器全被漏掉，
--- 日志还写着"来源模型可能没做 embed"，把问题指到了完全错误的方向。
---
--- 现在：先定出能打开的前缀 → 用倍增+二分问出最大序号（O(log N) 次探测）→ 扫 `0..最大+5`。
--- 那 +5 是余量：万一部署序列中间出现空洞（理论上不会，管理器每次从 0 连续重编），
--- 二分可能低估，多探五格兜住。
local function loadCarriers(S_, cache, options, logger)
	local archive = options.archiveId or "9ba626afa44a3aa3"
	local carriers = {}
	local seen = {}

	-- 老版本 compat 没有探测函数（外置包里两个文件总是一起生成，但玩家可能只更新了其中一个）。
	-- 缺了它就退回原来的固定窗口，至少不至于整条物理链路报错。
	local canProbe = type(Compat.detect_prefix) == "function"
		and type(Compat.find_max_patch_index) == "function"

	local prefixes = { "data/", "data\\", "./data/", "../data/", "..\\data\\" }
	local poked = {}
	local scannedTo = -1
	for _, prefix in ipairs(prefixes) do
		if not canProbe or Compat.detect_prefix(archive, { prefix }) ~= nil then
			local maxIndex = options.maxPatchIndex
			if maxIndex == nil then
				local last = canProbe and Compat.find_max_patch_index(prefix, archive) or -1
				if last < 0 then
					maxIndex = canProbe and 5 or 63     -- 探不到就用老的固定窗口
				else
					maxIndex = last + 5
				end
			end
			if maxIndex > scannedTo then scannedTo = maxIndex end

			for index = 0, maxIndex do
				local relative = archive .. ".patch_" .. index .. ".gpu_resources"
				if seen[relative] == nil then
					local handle = io.open(prefix .. relative, "rb")
					if handle ~= nil then
						handle:close()
						seen[relative] = true
						poked[#poked + 1] = prefix .. relative
						local rig = Compat.load_rig({ prefix .. relative })
						if rig ~= nil and type(rig.bones) == "table" and #rig.bones > 0 then
							carriers[#carriers + 1] = { rig = rig, path = prefix .. relative }
						end
					end
				end
			end
		end
	end

	if #carriers > 0 then
		logger(string.format("[physbone] 读到 %d 份物理容器（扫了 patch 0..%d）", #carriers, scannedTo))
	elseif scannedTo >= 0 then
		-- **把"扫了多大范围"写进日志。** 以前只有一句"可能没做 embed"，而真正的原因
		-- 往往在"扫的窗口不够大"：说了范围，下一次一眼就能分辨是哪种。
		logger(string.format(
			"[physbone] 没找到任何物理容器（扫了 patch 0..%d，%d 个 .gpu_resources 存在但都不含容器）",
			scannedTo, #poked))
	else
		logger("[physbone] 没找到任何物理容器（data 目录下没有这个归档的 patch 文件——游戏是否还没部署过？）")
	end
	return carriers
end

-- ---------------------------------------------------------------------------
-- 挂载 / 卸载
-- ---------------------------------------------------------------------------

--- 就绪采样 + 增量挂载。
---
--- "增量"是这一版的核心：**只补新 unit、只释放主人已经不在的会话**。
--- 以前是"一有变化就把全部会话丢掉重挂"，多人局里那等于每次有人加入/复活都让所有人
--- 短暂失去物理（而且会把会话挂到错误的玩家身上）。
---
--- 返回 true 表示这一趟真的跑完了（不论有没有新挂上的），false 表示还在等场景稳定。
local function tryAttach(runtime)
	local options, cache = runtime.options, runtime.cache
	local frame = runtime.frame
	local world, units, avatars, armorList, reason = discover(S, cache, options, runtime.logger)
	if world == nil or armorList == nil or avatars == nil then
		note_wait(runtime, reason or "发现目标失败（未给出原因）")
		return false
	end
	local armor = armorList[1]

	local nodeCount = try(S.Unit.num_scene_graph_items, armor.unit)
	if type(nodeCount) ~= "number" or nodeCount < 10 then
		note_wait(runtime, "主件节点数异常", tostring(nodeCount))
		return false
	end
	local sample = readVector(S, try(S.Unit.world_position, armor.unit, 1))
	if sample == nil then
		note_wait(runtime, "主件世界坐标读不到")
		return false
	end

	-- 两次采样一致 ⇒ 加载/传送/换装已经结束。间隔太短容易在动画还没落位时就 attach。
	-- ⚠️ 采样一旦记下就**不要每帧覆盖**，否则 (frame - previous.frame) 永远是 1，永远等不到。
	-- 这一步是正常路径（10 帧内必然过去），所以不记等待原因，免得盖掉真正的卡点。
	local settleFrames = options.settleFrames or 10
	local previous = cache.settle
	if previous == nil or previous.nodes ~= nodeCount then
		cache.settle = { x = sample, frame = frame, nodes = nodeCount }
		return false
	end
	if (frame - previous.frame) < settleFrames then return false end
	cache.settle = nil

	-- 逐 unit 选容器：每个 unit 只认"全命中"的那一份，没有就跳过（宁可不驱动也不写错参数）。
	-- `options.carriers` 可以直接注入（测试 / 由管理器把容器内容传进来时用），省掉扫盘。
	local carriers = runtime.carriers or {}
	if #carriers == 0 and options.carriers ~= nil then
		carriers = options.carriers
		runtime.carriers = carriers
	end
	if #carriers == 0 then carriers = loadCarriers(S, cache, options, runtime.logger); runtime.carriers = carriers end
	local allowPartial = options.allowPartial == true
	-- 只挂**离它那个玩家**近的护甲件。为什么必须过滤：阵亡后尸体留在原地、unit 一直活着，
	-- 而复活点常在别处 ⇒ 不过滤的话会把尸体也一起挂上（既浪费，也让"给尸体关物理"落空）。
	-- 注意距离是"到**它自己那个玩家**"的：队友的护甲离队友近，不该因为离我远就被丢掉。
	local maxDist = options.maxArmorDistance == nil and 5.0 or options.maxArmorDistance
	local maxDist2 = maxDist > 0 and (maxDist * maxDist) or nil
	local nearCandidates, farCount = {}, 0
	for _, candidate in ipairs(armorList) do
		if maxDist2 == nil or candidate.distance == nil or candidate.distance <= maxDist2 then
			nearCandidates[#nearCandidates + 1] = candidate
		else
			farCount = farCount + 1
		end
	end
	if farCount > 0 then
		runtime.logger("[physbone] 跳过 %d 个离它所属玩家超过 %.1f m 的护甲件（尸体/人台）", farCount, maxDist)
	end
	-- 给候选打分是这里最贵的一步（每份容器 × 每根骨一次 has_node，一件 ~460 次调用）。
	-- 候选多时（军械库里几十个人台 / 四人同屏）不设上限就是几万次调用 ⇒ 明显卡顿。
	-- 只测最近的一批：真正属于玩家的护甲一定是最近的那几件。玩家越多，允许测的越多。
	local maxScored = options.maxScoredCandidates or math.max(12, 4 * #avatars)
	local plan, bestScored = {}, nil
	for index = 1, #nearCandidates do
		if index > maxScored then
			runtime.logger("[physbone] 只给最近的 %d 件护甲打分（候选共 %d 件）", maxScored, #nearCandidates)
			break
		end
		local candidate = nearCandidates[index]
		local best, scored = Compat.select_rig(S, { candidate.unit }, carriers)
		if best ~= nil and (best.full or allowPartial) then
			plan[#plan + 1] = {
				unit = candidate.unit, rig = best.rig, path = best.path, mg = candidate.mg,
				tag = unit_tag(S, candidate.unit),
				owner = candidate.owner,
			}
		elseif scored ~= nil and scored[1] ~= nil then
			-- 记下"最接近的那一份"，没挂上时这条是唯一能指出"差几根骨"的线索。
			if bestScored == nil or scored[1].score > bestScored.score then bestScored = scored[1] end
		end
	end
	if #plan == 0 then
		local detail
		if #carriers == 0 then
			detail = "没有物理容器可匹配"
		elseif bestScored ~= nil then
			detail = string.format("%d 个候选 unit × %d 份容器，最高命中 %d/%d 根骨（%s）",
				#nearCandidates, #carriers, bestScored.score, bestScored.total, tostring(bestScored.path))
		else
			detail = string.format("%d 个候选 unit × %d 份容器，全部 0 命中", #nearCandidates, #carriers)
		end
		note_wait(runtime, "没有 unit 命中任何物理容器", detail)
		return false
	end
	table.sort(plan, function(a, b)
		local oa = a.owner ~= nil and a.owner.order or 99
		local ob = b.owner ~= nil and b.owner.order or 99
		if oa ~= ob then return oa < ob end
		if a.mg ~= b.mg then return a.mg end
		return false
	end)

	-- ① 先收掉"主人已经不在了"的会话（玩家退出 / 阵亡后尸体不再算玩家）。
	--
	-- 这一步取代了老版本那个 `playerChanged`：老版本把"出现一个新的 avatar 候选"当成
	-- "玩家换人了"，于是**队友一进房间就把我的会话全丢掉、重挂到队友身上**。
	-- 现在每个会话都记着自己的主人，主人还在就一律不动。
	local aliveOwners = {}
	for index = 1, #avatars do aliveOwners[avatars[index].unit] = true end
	local kept, released = {}, 0
	for _, session in ipairs(runtime.sessions or {}) do
		if session.owner ~= nil and aliveOwners[session.owner] == nil then
			released = released + 1
			if Compat.unit_alive(S, session.unit) then
				local ok, restored = pcall(Compat.release, session)
				runtime.logger("[physbone][玩家] 主人已不在，释放 unit=%s（%s）%s",
					tostring(session.unitTag or "?"), tostring(session.ownerTag or "?"),
					ok and ("已交还 " .. tostring(restored) .. " 节基准姿态") or "交还失败")
			end
		else
			kept[#kept + 1] = session
		end
	end
	if released > 0 then
		runtime.sessions = kept
		runtime.logger("[physbone][玩家] 释放了 %d 个会话（主人已离开/阵亡），剩 %d 个", released, #kept)
	end

	-- ② 再补上还没挂的 unit。已有会话的 unit 直接跳过 —— 这是增量的关键：
	--    队友加入时，我自己的会话一个字节都不该被碰。
	local attachedUnits = {}
	for _, session in ipairs(runtime.sessions or {}) do attachedUnits[session.unit] = true end

	local attachOptions = {
		logger = runtime.logger,
		world = world,
		strictIdentity = options.strictIdentity == true,
		fastWrite = options.fastWrite ~= false,
		-- `solver` 现在只用于那条**已废弃的角度振子核**（`"angle"`，仅 A/B）。
		-- 粒子链（`chain`）与碰撞层已在 2026-11-03 移除，不再往下传。
		solver = options.solver,
		bendBlend = options.bendBlend,
	}
	local attached, emptyUnits, already = 0, 0, 0
	for index = 1, #plan do
		local entry = plan[index]
		if attachedUnits[entry.unit] then
			already = already + 1
		elseif Compat.unit_alive(S, entry.unit) then
			local session = Compat.attach(S, entry.unit, entry.rig, attachOptions)
			session.rigSource = entry.path
			session.unitTag = entry.tag
			session.attachFrame = frame
			session.owner = entry.owner ~= nil and entry.owner.unit or nil
			session.ownerOrder = entry.owner ~= nil and entry.owner.order or 99
			if entry.owner ~= nil then
				-- 实例名只在这里读一次：unit 一旦销毁再读就是 AV，而日志里全是 `?` 等于没有信息。
				entry.owner.tag = entry.owner.tag or unit_tag(S, entry.owner.unit)
			end
			session.ownerTag = entry.owner ~= nil and entry.owner.tag or nil
			if #session.sessions > 0 then
				if runtime.sessions == nil then runtime.sessions = {} end
				runtime.sessions[#runtime.sessions + 1] = session
				attached = attached + 1
			else
				-- attach 走完了却一条链都没产出：容器骨名和这个 unit 的节点名对不上，
				-- 或者锚点全缺。以前这里完全静默，等于"挂上了"和"挂出 0 条"从日志上看一样。
				emptyUnits = emptyUnits + 1
				runtime.logger("[physbone][失败] unit=%s 挂载完成但产出 0 条链（容器 %s）",
					entry.tag, tostring(entry.path))
			end
		else
			runtime.logger("[physbone][失败] unit=%s 在挂载前一刻已销毁（跳过）", tostring(entry.tag))
		end
	end
	if (runtime.sessions == nil or #runtime.sessions == 0) then
		note_wait(runtime, "attach 没有产出任何链", string.format("%d 个 unit 通过容器匹配、%d 个产出 0 条链", #plan, emptyUnits))
		return false
	end

	-- 记下"这次场上有哪些玩家"。下一拍比对它的**实例同一性**（userdata，不是 debug_name：
	-- 实测那个 id 是按资源算的，拿它比"是不是同一个人"永远为真）。
	runtime.avatars = avatars
	runtime.world = world
	local first = avatars[1]
	runtime.avatar = first.unit
	runtime.avatarTag = first.tag
	runtime.attachedFrame = frame
	runtime.reArm = 0
	runtime.rearmAt = nil
	runtime.waitReason, runtime.waitRepeat = nil, nil
	runtime.zeroFrames = 0
	runtime.driveFrames = 0
	runtime.callsAccum = 0
	runtime.peakAngle = 0
	runtime.repeatCalls = 0

	local chains, tags = 0, {}
	local perOwner = {}
	for _, session in ipairs(runtime.sessions) do
		chains = chains + #session.sessions
		tags[#tags + 1] = tostring(session.unitTag or "?")
		local key = session.ownerOrder or 99
		perOwner[key] = (perOwner[key] or 0) + 1
	end
	local ownerText = {}
	for _, order in ipairs({ 1, 2, 3, 4, 99 }) do
		if perOwner[order] ~= nil then ownerText[#ownerText + 1] = string.format("玩家%d=%d件", order, perOwner[order]) end
	end
	runtime.logger("[physbone] 已挂载 %d 个 unit / %d 条链（%d 份容器，玩家 %d 个%s）；本次新挂 %d、已挂过 %d；unit=%s",
		#runtime.sessions, chains, #carriers, #avatars,
		#ownerText > 0 and ("：" .. table.concat(ownerText, " ")) or "",
		attached, already, table.concat(tags, ","))
	return true
end

--- 丢掉失效会话。任何一个失效都意味着场景变了 ⇒ 必须重扫（不能"还剩几个就继续开"）。
--- 返回丢弃总数；dead / rebuilt 分开计数并各自落日志。
local function pruneSessions(runtime)
	local kept, dead, rebuilt = {}, 0, 0
	for _, session in ipairs(runtime.sessions or {}) do
		local health = Compat.session_health(session, runtime.frame)
		if health == "ok" then
			kept[#kept + 1] = session
		else
			session.dead = health
			if health == "rebuilt" then rebuilt = rebuilt + 1 else dead = dead + 1 end
			-- `unitTag` 是**挂载时**记下的实例名：unit 已经死了，现在再读 debug_name 就是 AV。
			-- 这条名字是 P0.5 判"换实例（军械库 churn）还是同实例被销毁"的唯一依据。
			runtime.logger("[physbone][LIFECYCLE] 会话失效（%s）：unit=%s 存活 %d 帧 链=%d%s",
				health, tostring(session.unitTag or "?"),
				runtime.frame - (session.attachFrame or runtime.frame),
				#(session.sessions or {}),
				session.deadReason ~= nil and (" 判死原因=" .. session.deadReason) or "")
		end
	end
	runtime.sessions = kept
	if dead > 0 or rebuilt > 0 then
		runtime.logger("[physbone] 丢弃 %d 个已销毁 / %d 个已重建，剩 %d 个会话",
			dead, rebuilt, #kept)
	end
	return dead + rebuilt
end

--- 便宜的"场上有哪些玩家"扫描，供驱动中定期比对。
--- 返回 avatar unit 列表；`nil` 表示"这次扫不了"（世界/单位列表取不到）。
---
--- 现在几乎免费：`World.units_by_resource` 一次调用就列出所有玩家 avatar，
--- 之后每个候选只要 1 次 `Unit.alive`。以前这里要遍历 2000+ unit、每个两次引擎调用，
--- 所以只能 1.2 秒跑一次 —— 队友中途加入要多等一拍才接得上。
local function scanAvatars(runtime)
	local world, units = pickWorld(S, runtime.cache)
	if world == nil or units == nil then return nil end
	local supportMg = id32(S, "support_mg")
	if supportMg == nil then return nil end
	local options = runtime.options or {}
	local list = findAvatars(S, world, units, #units, supportMg,
		options.minNodes or 90, options, runtime.cache)
	local found = {}
	for index = 1, #list do
		if Compat.unit_alive(S, list[index].unit) then found[#found + 1] = list[index].unit end
	end
	return found
end

--- 玩家人数有没有变化（有人加入 / 有人退出 / 阵亡后尸体不再算玩家）。
---
--- 取代了旧版的 `playerChanged`。旧版把"出现了一个新的 avatar 候选"当成"玩家换人了"，
--- 在多人局里这是致命的：队友一进房间，我们就把**自己的**会话全丢掉、重新挂到队友身上，
--- 而自己身上从此没有物理。现在"多出一个玩家"只是一个**新玩家**，谁的会话都不该动。
---
--- ⚠️ 判据用 **userdata 同一性**（`rawequal`），不是 `debug_name`：
--- 2026-11-01 实测：丢掉 7 个 unit 之后重新挂上的还是**同一批 `#ID[...]`**，
--- 说明那个 id 是按资源/描述符算的，不是按实例 —— 拿它比"是不是同一个人"永远为真。
---@return boolean changed 需要补挂/收尾
local function avatarSetChanged(runtime)
	local live = scanAvatars(runtime)
	if live == nil then return false end
	local known = runtime.avatars or {}
	local knownSet, liveSet = {}, {}
	for index = 1, #known do knownSet[known[index].unit] = true end
	local maxAvatars = (runtime.options or {}).maxAvatars or 4
	for index = 1, #live do
		local unit = live[index]
		liveSet[unit] = true
		if knownSet[unit] == nil then
			if #known >= maxAvatars then
				-- 已经到上限了（正常对局 4 人；观战/人台可能更多）。再补会一直"发现新玩家"→
				-- 每 60 帧白跑一趟 discover，所以这里明确放弃，并留一条日志。
				if runtime.extraAvatarLogged ~= true then
					runtime.extraAvatarLogged = true
					runtime.logger("[physbone][玩家] 场上还有第 %d 个 avatar（%s），超过上限 %d，不挂它的物理",
						#known + 1, tostring(unit_tag(S, unit)), maxAvatars)
				end
			else
				runtime.logger("[physbone][玩家] 有新玩家（%s）⇒ 补挂它的护甲",
					tostring(unit_tag(S, unit)))
				return true
			end
		end
	end
	for index = 1, #known do
		if liveSet[known[index].unit] == nil then
			runtime.logger("[physbone][玩家] 有玩家离开/阵亡（%s）⇒ 收掉它的会话",
				tostring(known[index].tag or "?"))
			return true
		end
	end
	return false
end


-- ---------------------------------------------------------------------------
-- 抬升诊断（默认关闭，靠一个外部文本文件打开）
-- ---------------------------------------------------------------------------
--
-- 只回答一个问题：**把 avatar 整体抬高，摄像机会不会跟着走。**
--
-- 为什么这条决定一切：护甲网格是按骨名从 avatar 取世界变换渲染的（本仓库实测：往护甲
-- 自己的 `chest` 写 pose 能留住 450 帧，子节点世界坐标一点都不动），枪挂在 avatar 的
-- `attach_hand_r` / `r_hand` 上。所以抬 avatar ⇒ 网格和枪**一定**一起走，相对关系不变。
-- 唯一的未知量是摄像机：它若从 avatar 取，视线跟着抬 ⇒ 脚落地、手和枪对齐、瞄准不变，
-- 这条路就是完整的解；它若不跟，网格和枪升上去而视线留在原地 ⇒ 瞄准对不上，
-- 那这条路直接作废（那时只剩"只压腿"一条）。
--
-- 开关刻意放在**外部文本文件**里（`%LOCALAPPDATA%\hd2pbx_tune.txt`）：
--   lift=0.09        抬多少米（正 = 向上）；不给 / 给 0 / 没有文件 ⇒ 这一段完全不执行
--   lift_bone=chest  改抬 **avatar 上某一根骨**（不给 = 抬 avatar 根节点）
--   lift_armor=1     对照实验：连护甲件的根节点也一起抬
--   lift_log_frames  每多少帧打一行（默认 60）
-- 不放进打包配置，是因为诊断要能**就地改数值、就地关掉**，不该逼着重新打包或重建管理器。
--
-- **实机结论（2026-11-05，用户测试）**：
--   · `lift_armor=1` 与 `=0` **完全一样** ⇒ 护甲件的根节点不参与渲染
--     （它的网格只认被重定向的骨，这条和 §B2.3 的实测一致）；
--   · 写 avatar 的 node 1 ⇒ **只有摄像机跟着动，网格一点不动**。
--     也就是说 unit 根节点对相机有效、对网格的变换链无效（网格的蒙皮来自动画烘焙，不吃 node 1）。
-- ⇒ 下一个要试的杠杆是**骨**：`lift_bone` 写 avatar 某根骨的 local position，然后读回
--    ① 这根骨自己的世界 z（杠杆存不存在）、② 头骨的世界 z（子节点跟不跟）、
--    ③ 护甲上同名骨的世界 z（网格会不会跟）。三个数在同一行里。
--
-- ⚠️ 边界：这一段挂在 `drive()` 里，所以**只有挂着物理会话时才会执行**。诊断足够用；
-- 若哪天要把它做成正式功能（不只是诊断），必须挪到独立的 render 钩子，否则
-- "没物理骨的模型 / 关掉物理的玩家"都不会被抬。

--- 读调参文件（缓存，默认 2 秒重读一次）。
--- 数字进表本身（`lift` 等），字符串原文进 `values.text`（`lift_bone` 这类骨名）。
local function tune_values(runtime)
	local frame = runtime.frame or 0
	local cached = runtime.tune
	local refresh = (runtime.options and runtime.options.tuneRefreshFrames) or 30
	if cached ~= nil and (frame - cached.frame) < refresh then return cached.values end

	local values = { text = {} }
	local path = runtime.options and runtime.options.tunePath
	if path == nil then
		local base = os.getenv("LOCALAPPDATA")
		if type(base) == "string" and #base > 0 then path = base .. "\\hd2pbx_tune.txt" end
	end
	if path ~= nil then
		local ok, handle = pcall(io.open, path, "r")
		if ok and handle ~= nil then
			local okRead, text = pcall(handle.read, handle, "*a")
			pcall(handle.close, handle)
			if okRead and type(text) == "string" then
				for line in text:gmatch("[^\r\n]+") do
					-- `#` 开头的行整行跳过；值只取到空白或 `#` 为止（行尾可以写说明）
					local key, raw = line:match("^%s*([%w_]+)%s*=%s*([^%s#]+)")
					if key ~= nil then
						local number = tonumber(raw)
						if number ~= nil then values[key] = number end
						values.text[key] = raw
					end
				end
			end
		end
	end
	runtime.tune = { frame = frame, values = values }
	return values
end

--- 给一个 unit 的根节点（node 1）加一个垂直偏移。返回 `wrote, kept`。
--- <para>
--- **绝不能累加**。规则是"先读、再判断"：
---   · 读到的值等于我们上次写进去的 ⇒ 引擎这一帧没重设 ⇒ 不再加（否则每帧 +9 cm，会飞）；
---   · 否则说明引擎把它设回了基线 ⇒ 在**当前值**上重加一次。
--- 根 unit 的 local == world，玩家一动引擎就重设 ⇒ 正常路径是"每帧重写一次"；
--- 被父节点带着走的 unit（local 恒定）⇒ 路径是"写一次后一直跳过"。两种情况都稳。
--- </para>
local function lift_unit(S_, world, unit, state, metres)
	local current = try(S_.Unit.local_position, unit, 1)
	if current == nil then return false, false end
	local cx, cy, cz = readVector(S_, current)
	if cx == nil then return false, false end
	local written = state.written
	if written ~= nil
		and math.abs(cx - written[1]) < 1e-4
		and math.abs(cy - written[2]) < 1e-4
		and math.abs(cz - written[3]) < 1e-4 then
		return false, true
	end
	local ok = pcall(function()
		S_.Unit.set_local_position(unit, 1, S_.Vector3(cx, cy, cz + metres))
	end)
	if not ok then return false, false end
	if world ~= nil then pcall(function() S_.World.update_unit(world, unit) end) end
	state.written = { cx, cy, cz + metres }
	state.base = { cx, cy, cz }
	return true, false
end

--- unit 还活着吗。诊断这条路上不能依赖 `Compat` 已注入（单测会直接调它），
--- 所以没有 Compat 时退回裸 `Unit.alive`。
local function lift_unit_ok(api, unit)
	if unit == nil then return false end
	if Compat ~= nil and type(Compat.unit_alive) == "function" then
		return Compat.unit_alive(api, unit) and true or false
	end
	return try(api.Unit.alive, unit) == true
end

--- 读某个 unit 上某根骨的世界 z（不存在的骨返回 nil）。
local function bone_world_z(S_, unit, id)
	if unit == nil or id == nil or not lift_unit_ok(S_, unit) then return nil end
	if try(S_.Unit.has_node, unit, id) ~= true then return nil end
	local node = try(S_.Unit.node, unit, id)
	if type(node) ~= "number" then return nil end
	local _, _, z = readVector(S_, try(S_.Unit.world_position, unit, node))
	return z
end

--- 抬 **avatar 上的一根骨**（`lift_bone`）。
--- <para>
--- 为什么必须直接量三个数才能下结论：
---   ① 这根骨自己的世界 z —— 写进去之后**变了**，才说明"写骨"这个杠杆存在；
---   ② 头骨的世界 z —— 子节点跟没跟。跟了才说明整条骨链都能被抬；
---   ③ 护甲上**同名骨**的世界 z —— 网格渲不渲染就看它（渲染读的是这个值）。
--- 根节点那次的教训就是只看了"写了没报错"：结果相机动了、网格没动。
--- </para>
local function lift_bone(S_, runtime, name, metres)
	local avatar = runtime.avatar
	if avatar == nil or not lift_unit_ok(S_, avatar) then return nil end
	local id = id32(S_, name)
	if id == nil then return nil end
	local node = try(S_.Unit.node, avatar, id)
	if type(node) ~= "number" or try(S_.Unit.has_node, avatar, id) ~= true then return nil end

	local state = runtime.boneLift or {}
	runtime.boneLift = state
	local entry = state[name] or {}

	local before = bone_world_z(S_, avatar, id)
	local cx, cy, cz = readVector(S_, try(S_.Unit.local_position, avatar, node))
	local wrote, kept = false, false
	if cx ~= nil then
		local written = entry.written
		if written ~= nil
			and math.abs(cx - written[1]) < 1e-4
			and math.abs(cy - written[2]) < 1e-4
			and math.abs(cz - written[3]) < 1e-4 then
			kept = true
		else
			local ok = pcall(function()
				S_.Unit.set_local_position(avatar, node, S_.Vector3(cx, cy, cz + metres))
			end)
			if ok then
				if runtime.world ~= nil then
					pcall(function() S_.World.update_unit(runtime.world, avatar) end)
				end
				entry.written = { cx, cy, cz + metres }
				entry.base = entry.base or { cx, cy, cz }
				wrote = true
			end
		end
	end
	state[name] = entry

	local after = bone_world_z(S_, avatar, id)
	local headZ = bone_world_z(S_, avatar, id32(S_, "head"))
	-- 相机也得读：它的成败和网格是两件事（根节点那次就是"相机动、网格不动"）。
	local cameraZ = nil
	if runtime.world ~= nil and runtime.cache ~= nil then
		local cx, _, cz = cameraPosition(S_, runtime.world, runtime.cache)
		if cx ~= nil then cameraZ = cz end
	end
	local armorZ = nil
	for index = 1, #(runtime.sessions or {}) do
		local armor = runtime.sessions[index].unit
		if armor ~= nil then
			armorZ = bone_world_z(S_, armor, id)
			if armorZ ~= nil then break end
		end
	end
	return { before = before, after = after, head = headZ, camera = cameraZ, armor = armorZ,
		wrote = wrote, kept = kept }
end

--- 每帧调一次。没有调参文件 / `lift` 为 0 时什么都不做（**连读都不读**，只多一次缓存判断）。
--- <param name="S_">引擎命名空间；省略时用安装时注入的那份（单测会显式传）。</param>
local function apply_lift(runtime, S_)
	local api = S_ or S
	if api == nil or runtime == nil then return end
	local values = tune_values(runtime)
	local metres = values.lift
	local every = values.lift_log_frames or (runtime.options and runtime.options.liftLogFrames) or 60
	local boneName = values.text and values.text.lift_bone
	local boneMode = boneName ~= nil and boneName ~= "" and boneName ~= "0" and boneName ~= "none"
		and metres ~= nil and metres ~= 0

	-- 关掉时把写过的值还回去一次。根 unit 会被引擎每帧重设，所以只有"写得住"的
	-- （被父节点带着走的）那类需要显式还原——但两种都还一次更省心，代价是一次写。
	if metres == nil or metres == 0 then
		local boneStates = runtime.boneLift
		if boneStates ~= nil then
			local avatar = runtime.avatar
			if avatar ~= nil and lift_unit_ok(api, avatar) then
				for name, entry in pairs(boneStates) do
					if entry.written ~= nil and entry.base ~= nil then
						local id = id32(api, name)
						local node = id ~= nil and try(api.Unit.node, avatar, id) or nil
						if type(node) == "number" then
							pcall(function()
								api.Unit.set_local_position(avatar, node,
									api.Vector3(entry.base[1], entry.base[2], entry.base[3]))
							end)
						end
					end
				end
				if runtime.world ~= nil then
					pcall(function() api.World.update_unit(runtime.world, avatar) end)
				end
			end
			runtime.boneLift = nil
			runtime.logger("[physbone][抬升] 已关闭，写过的骨位置还回去了")
		end
		local states = runtime.liftState
		if states ~= nil then
			for index = 1, #states do
				local state = states[index]
				if state ~= nil and state.written ~= nil and state.base ~= nil then
					local unit = state.unit
					if unit ~= nil and lift_unit_ok(api, unit) then
						pcall(function()
							api.Unit.set_local_position(unit, 1,
								api.Vector3(state.base[1], state.base[2], state.base[3]))
						end)
						if runtime.world ~= nil then
							pcall(function() api.World.update_unit(runtime.world, unit) end)
						end
					end
				end
			end
			runtime.liftState = nil
			runtime.logger("[physbone][抬升] 已关闭，写过位置的目标都还原了一次")
		end
		return
	end

	-- ── 骨模式：只写 avatar 的一根骨，并把三个读数打在同一行 ────────────────
	if boneMode then
		if runtime.liftMode ~= ("bone:" .. boneName) then
			runtime.liftMode = "bone:" .. boneName
			runtime.logger("[physbone][抬升] 模式=写骨 %s（+%.3f m；unit 根节点不再动）", boneName, metres)
		end
		local result = lift_bone(api, runtime, boneName, metres)
		if ((runtime.driveFrames or 0) % every) == 0 then
			if result == nil then
				runtime.logger("[physbone][抬升-骨] %s：avatar 上找不到这根骨（或读写失败）", boneName)
			else
				runtime.logger(
					"[physbone][抬升-骨] %s +%.3f 写入=%s 保持=%s | 该骨世界 z %s→%s | 头骨 z %s | 相机 z %s | 护甲同名骨 z %s",
					boneName, metres,
					result.wrote and "是" or "否", result.kept and "是" or "否",
					result.before ~= nil and string.format("%.3f", result.before) or "?",
					result.after ~= nil and string.format("%.3f", result.after) or "?",
					result.head ~= nil and string.format("%.3f", result.head) or "?",
					result.camera ~= nil and string.format("%.3f", result.camera) or "?",
					result.armor ~= nil and string.format("%.3f", result.armor) or "?")
			end
		end
		return
	end

	local world = runtime.world
	local states = runtime.liftState or {}
	runtime.liftState = states
	local wrote, kept, failed = 0, 0, 0

	local function apply(unit, index, tag)
		local state = states[index]
		if state == nil then
			state = { unit = unit }
			states[index] = state
		end
		state.unit = unit
		state.tag = tag
		local didWrite, didKeep = lift_unit(api, world, unit, state, metres)
		if didWrite then
			wrote = wrote + 1
		elseif didKeep then
			kept = kept + 1
		else
			failed = failed + 1
		end
	end

	for index = 1, #(runtime.avatars or {}) do
		local avatar = runtime.avatars[index]
		if avatar ~= nil and avatar.unit ~= nil and lift_unit_ok(api, avatar.unit) then
			apply(avatar.unit, index, "avatar")
		end
	end
	if values.lift_armor ~= nil and values.lift_armor ~= 0 then
		for index = 1, #(runtime.sessions or {}) do
			local session = runtime.sessions[index]
			if session ~= nil and session.unit ~= nil and lift_unit_ok(api, session.unit) then
				apply(session.unit, #(runtime.avatars or {}) + index, "armor")
			end
		end
	end

	-- 首次生效时说一次，免得"日志里没动静"分不清是没打开还是没生效。
	if runtime.liftActive ~= metres then
		runtime.liftActive = metres
		runtime.logger("[physbone][抬升] 已打开：每个目标根节点 +%.3f m（护甲件%s）；"
			.. "看三件事——视线有没有跟着抬、枪和手有没有错位、脚下还陷不陷",
			metres, values.lift_armor ~= nil and values.lift_armor ~= 0 and "也抬" or "不动")
	end

	local every = values.lift_log_frames or (runtime.options and runtime.options.liftLogFrames) or 60
	if ((runtime.driveFrames or 0) % every) ~= 0 then return end

	-- 关键那一行：相机 z 与 avatar 根 / 头骨的 z 放在一起。
	-- 相机跟着走 ⇒ 「相机−头骨」恒为常数；不跟 ⇒ 相机 z 原地不动、差值随抬升变大。
	local cameraZ = nil
	if runtime.world ~= nil and runtime.cache ~= nil then
		local cx, _, cz = cameraPosition(api, runtime.world, runtime.cache)
		if cx ~= nil then cameraZ = cz end
	end
	local rootZ, headZ = nil, nil
	local avatarUnit = runtime.avatar
	if avatarUnit ~= nil and lift_unit_ok(api, avatarUnit) then
		local _, _, rz = readVector(api, try(api.Unit.world_position, avatarUnit, 1))
		rootZ = rz
		local headId = id32(api, "head")
		if headId ~= nil and try(api.Unit.has_node, avatarUnit, headId) == true then
			local node = try(api.Unit.node, avatarUnit, headId)
			if type(node) == "number" then
				local _, _, hz = readVector(api, try(api.Unit.world_position, avatarUnit, node))
				headZ = hz
			end
		end
	end
	runtime.logger(
		"[physbone][抬升] +%.3f m 写入=%d 保持=%d 失败=%d | avatar 根 z=%s 头骨 z=%s 相机 z=%s 相机−头骨=%s",
		metres, wrote, kept, failed,
		rootZ ~= nil and string.format("%.3f", rootZ) or "?",
		headZ ~= nil and string.format("%.3f", headZ) or "?",
		cameraZ ~= nil and string.format("%.3f", cameraZ) or "?",
		(cameraZ ~= nil and headZ ~= nil) and string.format("%+.3f", cameraZ - headZ) or "?")
end

-- ---------------------------------------------------------------------------
-- 姿态快照：按一下键，把当前玩家这一具骨架的世界变换写到盘上
--
-- 为什么需要它：穿模这件事**只在动起来时看得见**，而离线只有 bind 姿态。
-- 2026-11-09 的实测把这一点钉死了：绑定时 7 个挂在 hips 上的链根离髋部表面
-- +4.6~+9.9 cm（全在外面），真正插进身体的是**皮面**（裙子上沿，最深 −1.5 cm）。
-- 也就是说"看骨点有没有越界"这个判据永远不成立，必须拿到那一帧的骨变换，
-- 在 PC 侧把网格蒙皮还原出来再判。
--
-- 为什么是"外部写请求、Lua 轮询"而不是在 Lua 里读键盘：
-- 这个游戏的输入走 Raw Input（见 tools/game-input.py 的注释），Lua 侧没有可信的按键 API；
-- 而"请求文件 → 轮询 → 应答文件"这条路在本仓已经有成熟先例
-- （HD2RuntimeArmorOverride/src/armor_refresh_hotkey.lua 就是这套）。
--
-- 请求文件是**按行的纯文本**，不是 JSON：解析器只需要认得 `key=value`，
-- 少一层转义就少一类静默失败。节点用 32 位哈希给（引擎的 IdString32），
-- 因为转换产物里没有 Bone 资源、节点名根本不存在，只有哈希。
-- ---------------------------------------------------------------------------
local SNAPSHOT_MAX_NODES = 1024
local SNAPSHOT_MAX_FRAMES = 32
local SNAPSHOT_POLL_FRAMES = 30

--- 快照目录。默认 `%LOCALAPPDATA%\hd2physbone-snap`，`options.snapshotDir` 可覆盖，
--- `options.snapshot == false` 可彻底关掉。
local function snapshot_dir(options)
	local dir = options and options.snapshotDir
	if type(dir) == "string" and #dir > 0 then return dir end
	if options and options.snapshot == false then return nil end
	local base = os.getenv("LOCALAPPDATA")
	if type(base) == "string" and #base > 0 then return base .. "\\hd2physbone-snap" end
	return nil
end

--- 解析请求文本。任何一行看不懂就跳过，不因此整份作废——
--- 拿到半份数据也比"按一下什么都没发生"强。
local function parse_snapshot_request(text)
	if type(text) ~= "string" or #text == 0 then return nil end
	local request = { frames = 8, stride = 2, limit = 0, nodes = {}, names = {}, seq = nil }
	for line in text:gmatch("[^\r\n]+") do
		line = line:gsub("^%s+", ""):gsub("%s+$", "")
		if #line > 0 and line:sub(1, 1) ~= "#" then
			local key, value = line:match("^([%w_]+)%s*=%s*(.-)$")
			if key ~= nil then
				key = key:lower()
				if key == "frames" then
					local count = tonumber(value)
					if count ~= nil then
						request.frames = math.max(1, math.min(SNAPSHOT_MAX_FRAMES, math.floor(count)))
					end
				elseif key == "stride" then
					local stride = tonumber(value)
					if stride ~= nil then
						request.stride = math.max(1, math.min(30, math.floor(stride)))
					end
				elseif key == "auto" then
					-- 自动清单：用运行时自己正在驱动的链骨名 + 固定关节名。换护甲也不用改请求。
					request.auto = (value == "1" or value == "true")
				elseif key == "probe" then
					-- 探针模式：把前几个 id 的每一步结果（类型 + 返回值）打出来。
					-- 为什么需要：按骨名采回来 13 个全"缺"，而同一套 API 在求解器里是好的，
					-- 光看"缺 13"根本分不清是 has_node 假、node 假还是读位置假。
					request.probe = (value == "1" or value == "true")
				elseif key == "limit" then
					-- 只采前 N 个节点。为什么需要：一次按键里读 685 个节点 ≈ 2700 次引擎调用，
					-- 2026-11-09 实测直接把游戏读崩了。先用小的探路，再放量。
					local count = tonumber(value)
					if count ~= nil then request.limit = math.max(0, math.min(SNAPSHOT_MAX_NODES, math.floor(count))) end
				elseif key == "seq" then
					request.seq = value
				elseif key == "node" then
					local hash = tonumber((value:gsub("^0[xX]", "")), 16)
					if hash ~= nil and #request.nodes < SNAPSHOT_MAX_NODES then
						request.nodes[#request.nodes + 1] = hash
					end
				elseif key == "name" then
					if #value > 0 and #request.names < 128 then
						request.names[#request.names + 1] = value
					end
				end
			end
		end
	end
	if #request.nodes == 0 and #request.names == 0 and request.auto ~= true then return nil end
	return request
end

--- 把请求里的"哈希 + 名字"并成一张 id 表（名字按引擎的 IdString32 现算）。
local function snapshot_ids(S_, request)
	local ids, seen = {}, {}
	local limit = request.limit or 0
	local function push(id, label, name)
		if id == nil and name == nil then return end
		local key = id or ("name:" .. name)
		if seen[key] then return end
		if limit > 0 and #ids >= limit then return end
		seen[key] = true
		ids[#ids + 1] = { id = id, label = label, name = name }
	end
	for _, hash in ipairs(request.nodes or {}) do push(hash, string.format("0x%08x", hash)) end
	-- 名字同时留着原文：万一某些引擎版本 has_node 只认字符串，还能退回去再试一次。
	for _, name in ipairs(request.names or {}) do push(id32(S_, name), name, name) end
	return ids
end

	-- 自动清单要用的固定关节名。这些就是 compat 的 anchor 表里那些 HD2 原生骨名。
local SNAPSHOT_JOINTS =
{
	"hips", "spine", "spine1", "spine2", "chest", "neck", "head",
	"l_thigh", "r_thigh", "l_knee", "r_knee", "l_foot", "r_foot", "l_toe", "r_toe",
	"l_shoulder", "r_shoulder", "l_elbow", "r_elbow", "l_hand", "r_hand",
}

--- 自动清单：**运行时自己知道它在驱动哪些骨**，不需要外部把名字喂进来。
--- 2026-11-09 实测：请求里的名字是"上一局那件护甲"的，换一件就全落空；
--- 而链骨名字每帧都在 `session.sessions[i].chain` 里，直接抄下来最可靠。
local function snapshot_auto_ids(runtime)
	local ids, seen = {}, {}
	local function pushName(name)
		if type(name) ~= "string" or #name == 0 or seen[name] then return end
		if #ids >= SNAPSHOT_MAX_NODES then return end
		seen[name] = true
		ids[#ids + 1] = { name = name, label = name }
	end
	for _, name in ipairs(SNAPSHOT_JOINTS) do pushName(name) end
	for _, session in ipairs(runtime.sessions or {}) do
		for _, item in ipairs(session.sessions or {}) do
			for _, bone in ipairs(item.chain or {}) do pushName(bone.name) end
		end
	end
	return ids
end

--- 拍一帧：逐节点读世界位置 + 世界旋转，顺带把每条链的弹簧状态抄下来。
--- 所有引擎调用都包 pcall：一根骨读不出来不能让整次快照作废。
---
--- `units` 是**一串**候选 unit（不是单个）：一具护甲被拆成头盔/胸/髋/腿…好几个 unit，
--- 同一个骨名只在其中某一个上存在。只试第一个 unit 就会全军覆没——
--- 2026-11-09 实测：`hips` 和求解器正在驱动的链骨都报"缺"，就因为只试了头盔那一个。
local function snapshot_frame(S_, runtime, units, ids, frame)
	local nodeRows, missing = {}, 0
	local probe = runtime.snapshotState ~= nil and runtime.snapshotState.request ~= nil
		and runtime.snapshotState.request.probe == true
	for index = 1, #ids do
		local entry = ids[index]
		local detail, row, hitUnit = nil, nil, nil
		if probe then detail = "id=" .. tostring(entry.id) .. "(" .. type(entry.id) .. ") " end
		for unitIndex = 1, #units do
			local unit = units[unitIndex].unit
			-- ⚠️ 一律**先用名字字符串**：实测 `Application.string_to_id32` 返回的是
			-- `IdString32` userdata，而且它是**会失效的**（探针日志里直接印出 `[Stale IdString32]`）。
			-- 把它存起来跨帧用，`has_node` 一律返回 false —— 这正是"按哈希采回来 0 个"的原因。
			-- 名字字符串没有这个问题，`has_node(unit, "hips")` 稳定为 true。
			local key = entry.name
			local okHas, has = false, false
			if key ~= nil then
				okHas, has = pcall(S_.Unit.has_node, unit, key)
			end
			if not (okHas and has == true) and entry.id ~= nil then
				-- 只有哈希、没有名字的条目：现算一次 id32 再用（不跨帧缓存）。
				okHas, has = pcall(S_.Unit.has_node, unit, entry.id)
				if okHas and has == true then key = entry.id end
			end
			if probe then detail = detail .. string.format("u%d=%s ", unitIndex, tostring(has)) end
			if okHas and has == true then
				local okNode, node = pcall(function() return S_.Unit.node(unit, key) end)
				if probe then detail = detail .. "node=" .. tostring(node) .. "(" .. type(node) .. ") " end
				if okNode and node ~= nil then
					local okPos, px, py, pz = pcall(function()
						return S_.Vector3.to_elements(S_.Unit.world_position(unit, node))
					end)
					-- 四元数必须与 world_rotation 在同一次调用内取完（引擎句柄寿命问题，见 compat 的 read_quat4）
					local okRot, qx, qy, qz, qw = pcall(function()
						return S_.Quaternion.to_elements(S_.Unit.world_rotation(unit, node))
					end)
					if probe then
						detail = detail .. string.format("pos=%s/%s rot=%s/%s ", tostring(okPos),
							type(px) == "number" and string.format("%.3f", px) or tostring(px),
							tostring(okRot), type(qx) == "number" and string.format("%.3f", qx) or tostring(qx))
					end
					if okPos and type(px) == "number" and okRot and type(qx) == "number" then
						row = {
							id = entry.label,
							unit = unitIndex,
							x = px, y = py, z = pz,
							qx = qx, qy = qy, qz = qz, qw = qw,
						}
						hitUnit = unitIndex
						break
					end
				end
			end
		end
		if row ~= nil then
			nodeRows[#nodeRows + 1] = row
		else
			missing = missing + 1
		end
		if probe and index <= 6 then
			runtime.logger("[physbone][快照][探针] #%d %s⇒ %s", index, tostring(entry.name or entry.label),
				tostring(detail) .. (hitUnit ~= nil and ("命中 u" .. hitUnit) or "全未命中"))
		end
	end

	local chains = {}
	for _, session in ipairs(runtime.sessions or {}) do
		for _, item in ipairs(session.sessions or {}) do
			local bones = {}
			local springs = item.springs or {}
			local chain = item.chain or {}
			for i = 1, #chain do
				local spring = springs[i]
				-- 弹簧角度在求解器里是**弧度**；写出去换成度，省得读的人再做一次换算
				-- （已经因为"限位打印 0°"这种事白查过一轮）。
				bones[#bones + 1] = {
					name = tostring(chain[i] and chain[i].name or "?"),
					yawDeg = spring ~= nil and (spring.yaw or 0) * 180 / math.pi or 0,
					pitchDeg = spring ~= nil and (spring.pitch or 0) * 180 / math.pi or 0,
				}
			end
			chains[#chains + 1] = {
				root = tostring(chain[1] and chain[1].name or "?"),
				anchor = tostring(item.anchorNode ~= nil and "resolved" or "?"),
				bones = bones,
			}
		end
	end

	return { frame = frame, nodes = nodeRows, missing = missing, chains = chains }
end

local function json_number(value)
	if type(value) ~= "number" or value ~= value or value == math.huge or value == -math.huge then
		return "null"
	end
	return string.format("%.6f", value)
end

local function json_string(value)
	local text = tostring(value):gsub('[%z\1-\31\\"]', function(character)
		return string.format("\\u%04x", character:byte())
	end)
	return '"' .. text .. '"'
end

--- 把几帧拼成一份自描述的 JSON。字段名写清楚，是为了**半年后**还能读懂。
local function snapshot_json(runtime, request, unitTag, frames, capturedUtc)
	local parts = {}
	parts[#parts + 1] = string.format(
		'{"schema":"hd2physbone.snap/1","runtime":"%s","seq":%s,"captured":"%s","unit":%s,'
			.. '"framesRequested":%d,"stride":%d,"frames":[',
		tostring(M.VERSION), request.seq ~= nil and json_string(request.seq) or "null",
		capturedUtc, json_string(unitTag), request.frames, request.stride)
	for frameIndex, frame in ipairs(frames) do
		if frameIndex > 1 then parts[#parts + 1] = "," end
		parts[#parts + 1] = string.format('{"frame":%d,"missingNodes":%d,"nodes":[', frame.frame, frame.missing)
		for nodeIndex, node in ipairs(frame.nodes) do
			if nodeIndex > 1 then parts[#parts + 1] = "," end
			parts[#parts + 1] = string.format('{"id":"%s","x":%s,"y":%s,"z":%s,"qx":%s,"qy":%s,"qz":%s,"qw":%s}',
				node.id:gsub('"', ""), json_number(node.x), json_number(node.y), json_number(node.z),
				json_number(node.qx), json_number(node.qy), json_number(node.qz), json_number(node.qw))
		end
		parts[#parts + 1] = '],"chains":['
		for chainIndex, chain in ipairs(frame.chains) do
			if chainIndex > 1 then parts[#parts + 1] = "," end
			parts[#parts + 1] = string.format('{"root":%s,"bones":[', json_string(chain.root))
			for boneIndex, bone in ipairs(chain.bones) do
				if boneIndex > 1 then parts[#parts + 1] = "," end
				parts[#parts + 1] = string.format('{"name":%s,"yawDeg":%s,"pitchDeg":%s}',
					json_string(bone.name), json_number(bone.yawDeg), json_number(bone.pitchDeg))
			end
			parts[#parts + 1] = "]}"
		end
		parts[#parts + 1] = "]}"
	end
	parts[#parts + 1] = string.format('],"driveFrames":%d,"dt":%s}', runtime.driveFrames or 0, json_number(runtime.dt))
	return table.concat(parts)
end

--- 轮询一次请求。只有目录存在、请求内容变了才动手；平时每 30 帧一次 io.open，成本可忽略。
local function snapshot_poll(S_, runtime, options)
	local dir = snapshot_dir(options)
	if dir == nil then return end
	local state = runtime.snapshotState
	if state == nil then
		state = { seq = 0, signature = nil, framesLeft = 0, skip = 0, frames = {}, ids = nil, reported = false }
		runtime.snapshotState = state
	end

	-- ① 采集中：按 stride 每帧记一张，够数就落盘
	if state.framesLeft > 0 then
		if state.skip > 0 then
			state.skip = state.skip - 1
			return
		end
		-- 一具护甲是**一堆 unit**（头盔/胸/髋/腿…），同一个骨名只在其中一个上存在。
		-- 所以这里把所有会话的 unit 都收集起来，逐个试，第一个命中就用它。
		local units, tags = {}, {}
		for _, session in ipairs(runtime.sessions or {}) do
			if session.unit ~= nil then
				units[#units + 1] = { unit = session.unit, tag = tostring(session.unitTag or "?") }
				tags[#tags + 1] = tostring(session.unitTag or "?")
			end
		end
		if #units == 0 then
			-- 还没有挂上任何会话（读图/换甲的空窗期）：**不许消耗帧数**，
			-- 否则会写出一份 0 帧的残文件，看起来像"采到了"，实际什么都没有。
			state.waitFrames = (state.waitFrames or 0) + 1
			if state.waitFrames > (options.snapshotWaitFrames or 600) then
				runtime.logger("[physbone][快照] 等了 %d 帧仍然没有可采的会话，放弃这次请求", state.waitFrames)
				state.framesLeft = 0
				state.frames = {}
				state.waitFrames = 0
			end
			return
		end
		state.waitFrames = 0
		state.skip = (state.request and state.request.stride or 2) - 1
		local sampleIndex = (state.request.frames - state.framesLeft) + 1
		local unitTag = #tags > 0 and string.format("%s 等 %d 个", tags[1], #tags) or "?"
		-- 自动模式下**每帧现算**清单：护甲可能刚换过，链骨名跟着变。
		local ids = state.ids
		if state.request ~= nil and state.request.auto == true then ids = snapshot_auto_ids(runtime) end
		-- 每帧**先**打一行再采：万一这一帧把游戏读崩了，日志里最后一行就是崩在哪一帧、
		-- 采了多少个节点。没有这一行，"闪退"在现场只能靠猜（2026-11-09 就猜了一轮）。
		runtime.logger("[physbone][快照] 开始第 %d/%d 帧：%d 个节点、%d 个候选 unit",
			sampleIndex, state.request.frames, #(ids or {}), #units)
		local sample = snapshot_frame(S_, runtime, units, ids or {}, runtime.frame or 0)
		state.lastTag = unitTag
		state.framesLeft = state.framesLeft - 1
		-- 每帧单独落盘：游戏要是在后面的帧上崩了，前面采到的还在；
		-- "最后写出的那个文件"也直接告诉我们崩在第几帧。
		local name = string.format("snapshot-%04d-s%02d.json", state.seq, sampleIndex)
		local path = dir .. "\\" .. name
		local text = snapshot_json(runtime, state.request, unitTag, { sample }, os.date("%Y-%m-%dT%H:%M:%S"))
		local ok, handle = pcall(io.open, path, "wb")
		if ok and handle ~= nil then
			handle:write(text)
			handle:close()
			runtime.logger("[physbone][快照] 已写出 %s（%d 个节点，缺 %d）", name, #sample.nodes, sample.missing)
		else
			runtime.logger("[physbone][快照] 写不出 %s（目录是否存在？）", path)
		end
		if state.framesLeft > 0 then return end
		state.frames = {}
		local okDone, done = pcall(io.open, dir .. "\\done.txt", "wb")
		if okDone and done ~= nil then
			done:write(string.format("seq=%d frames=%d\n", state.seq, state.request.frames))
			done:close()
		end
		return
	end

	-- ② 空闲：每 N 帧看一次请求文件
	if ((runtime.driveFrames or 0) % (options.snapshotPollFrames or SNAPSHOT_POLL_FRAMES)) ~= 0 then return end
	local okOpen, handle = pcall(io.open, dir .. "\\request.txt", "rb")
	if not okOpen or handle == nil then return end
	local text = handle:read("*a") or ""
	handle:close()
	if #text == 0 or text == state.signature then return end

	local request = parse_snapshot_request(text)
	if request == nil then
		state.signature = text
		if not state.reported then
			state.reported = true
			runtime.logger("[physbone][快照] 请求里没有 node/name 行，忽略")
		end
		return
	end
	state.signature = text
	state.request = request
	state.ids = snapshot_ids(S_, request)
	state.seq = state.seq + 1
	state.frames = {}
	state.framesLeft = request.frames
	state.skip = 0
	runtime.logger("[physbone][快照] 收到请求 seq=%s：%d 帧 × 每 %d 帧一张、%d 个节点",
		tostring(request.seq or "?"), request.frames, request.stride, #state.ids)
end

-- ---------------------------------------------------------------------------
-- 裙摆外推（keep-out）
--
-- 依据（2026-11-09 实机 96 帧采样）：静止时后裙骨点离髋部身体表面 +0.8～+5 cm，
-- 跑动时最深 **−8.2 cm**（几乎总是右后那条 `source_extra_120`）。摆幅约 13 cm。
--
-- 做法：标定时记下"静止时的半径"，之后低于它就把链根往外转一点转回来。
-- 只动**挂在 hips 上的链的链根**——整片布靠链根朝向摆，逐节动会把布扯变形。
-- 参数全走调参文件（`%LOCALAPPDATA%\hd2pbx_tune.txt`，2 秒重读一次），所以
-- 在游戏里改数字就生效，不用重启、不用重新部署。
--
--   skirt        = 1     0=关，1=开
--   skirtcal     = 1     置 1 且**站着**，运行时就地记下当前半径当基准；之后置回 0
--   skirtGain    = 0.35  每帧纠正亏损量的比例（1 = 一步到位，容易抖）
--   skirtMaxDeg  = 12    单帧最多转多少度（保底，防止瞬间甩飞）
--   skirtMargin  = 0.005 额外留白（米）
--   skirtArm     = 0.30  力臂（米）：布大概从骨点往外延伸多远，用来把"距离"换成"角度"
-- ---------------------------------------------------------------------------
local SKIRT_DEFAULTS = {
	gain = 0.35, maxDeg = 12, margin = 0.005, arm = 0.30,
	-- 两处平滑的时间常数（秒）。它们存在的唯一目的是**去掉帧间抖动**：
	-- 权重和"正后方"轴如果每帧按当前姿态重算，就会跟着步频一起摆。
	-- weight 的语义其实是**静态**的（这根骨在裙子的哪一侧），所以宁慢不抖：1.2 秒。
	weightSeconds = 1.2, backSeconds = 0.5,
	-- 自适应上限（临时值，按布料相对自己基线的垂度反馈）：
	--   · tilt（骨方向与世界水平的夹角，取绝对值）比该链观察到的**峰值**低 capDropDelta 以上
	--     ⇒ 判定被推高/被甩高，每帧把上限降 capDropStep 度；
	--   · tilt 回到峰值 capRiseDelta 以内 ⇒ 每 capRiseFrames 帧恢复 capRiseStep 度；
	--   · 两者之间是**死区**，不动（没有死区就会在阈值上来回跳 = 慢速脉动）。
	-- 为什么用"相对峰值"而不是绝对角度：实测自然下垂的链只有 41~61°，
	-- 写死 45/65 会把大量自然下垂的链也判成被推高（2.1.11 就这么整片失效的）。
	-- tiltDecay：峰值每秒回落多少度，让裙子换了/姿态变了之后能重新学习。
	-- tiltBase 只在**没在推**的时候学（见 skirt_chain）。基线用来防误伤：
	-- 自然就只有 41~61° 的链不该被当成"被推高"。
	capDropDeg = 45, capRiseDeg = 65, capDropDelta = 5, capRiseDelta = 3,
	capDropStep = 1, capRiseStep = 1, capRiseFrames = 3,
	capFloor = 0, capSmoothSeconds = 0.15,
	-- 上限的硬性下限（相对手调值的比例）：保证"永远不会被压到 0"。
	capFloorRatio = 0.35,
}

--- 预设枚举：把"旋转加在哪个坐标系、正负号"这 8 种可能一次列全，让实机去分辨。
--- <para>
--- 为什么用枚举而不是推理：`write_pose` 把 (轴, 角) 解释成什么（父空间增量？局部增量？
--- 绝对旋转？）读代码看不出来，猜错的代价是"画面毫无变化"，而那时根本分不清是
--- 轴错了、帧错了、还是没写进去。8 个预设、每个都恒定 60°，一眼就能分出对错。
--- </para>
--- <para>
--- 预设**不看** keep-out、不做亏损判断：站着不动时也要一眼看得出效果，
--- 否则"没反应"会被误读成预设失效。这是诊断模式，不是最终行为。
--- </para>
local PRESET_DEGREES = 60
local PRESETS =
{
	-- { 模式, 符号 }  模式 1/2 = 前乘（轴：1=父空间、2=世界）；3/4 = 后乘（同理）
	{ 1, 1 }, { 1, -1 }, { 2, 1 }, { 2, -1 },
	{ 3, 1 }, { 3, -1 }, { 4, 1 }, { 4, -1 },
}

local function preset_label(index)
	local preset = PRESETS[index]
	if preset == nil then return "无" end
	local frame = (preset[1] == 1 or preset[1] == 3) and "父空间轴" or "世界轴"
	local order = (preset[1] <= 2) and "前乘" or "后乘"
	return string.format("%d/%d：%s %s %s%.0f°", index, #PRESETS, frame, order,
		preset[2] > 0 and "+" or "−", PRESET_DEGREES)
end

local function skirt_settings(runtime)
	local values = tune_values(runtime) or {}
	return {
		enabled = (values.skirt or 0) ~= 0,
		calibrate = (values.skirtcal or 0) ~= 0,
		gain = values.skirtGain or SKIRT_DEFAULTS.gain,
		maxDeg = values.skirtMaxDeg or SKIRT_DEFAULTS.maxDeg,
		margin = values.skirtMargin or SKIRT_DEFAULTS.margin,
		arm = values.skirtArm or SKIRT_DEFAULTS.arm,
		-- 手调角度（度）：>0 时**只要这条链有亏损**就用这个角度，不再按"亏损/力臂"推。
		-- 为什么需要它：力臂是估的，实测偏小会让算出来的角度只有 3~4°（肉眼等于没动）。
		-- 手调扫一遍，用眼睛定一个"多数裙子都好看又不穿"的值，比按力矩推可靠得多。
		manualDeg = values.skirtDeg or 0,
		-- 没手调时按"每 1 cm 亏损转多少度"换算（缺省 6°/cm）。
		degPerCm = values.skirtDegPerCm or 6,
		-- 运动斜坡：静止 0°、跑起来满额。实测"静止 0 度就够用，只有奔跑才需要角度"。
		idleSpeed = values.skirtIdleSpeed or 0.5,     -- m/s，以下视为静止
		runSpeed = values.skirtRunSpeed or 2.0,       -- m/s，以上视为满额
		rampSeconds = values.skirtRampSeconds or 0.25,
		-- "方向权重"收敛的多快。调小 = 更跟手但更抖；调大 = 更稳。
		weightSeconds = values.skirtWeightSeconds or SKIRT_DEFAULTS.weightSeconds,
		-- "正后方"轴收敛的多快。跑步时两条大腿前后交替，(l_thigh − r_thigh)
		-- 每一步都在转，不快不行；但又不能太快，否则权重跟着摆。
		backSeconds = values.skirtBackSeconds or SKIRT_DEFAULTS.backSeconds,
		-- 自适应上限：绝对阈值（capDropDeg/capRiseDeg）为主，
		-- 再加"相对自己历史峰值"的保险（capDropDelta/capRiseDelta），避免自然就趴着的链被误伤。
		capDropDeg = values.skirtCapDropDeg or SKIRT_DEFAULTS.capDropDeg,
		capRiseDeg = values.skirtCapRiseDeg or SKIRT_DEFAULTS.capRiseDeg,
		capDropDelta = values.skirtCapDropDelta or SKIRT_DEFAULTS.capDropDelta,
		capRiseDelta = values.skirtCapRiseDelta or SKIRT_DEFAULTS.capRiseDelta,
		capDropStep = values.skirtCapDropStep or SKIRT_DEFAULTS.capDropStep,
		capRiseStep = values.skirtCapRiseStep or SKIRT_DEFAULTS.capRiseStep,
		capRiseFrames = math.max(1, math.floor(values.skirtCapRiseFrames or SKIRT_DEFAULTS.capRiseFrames)),
		capFloor = values.skirtCapFloor or SKIRT_DEFAULTS.capFloor,
		capFloorRatio = values.skirtCapFloorRatio or SKIRT_DEFAULTS.capFloorRatio,
		capSmoothSeconds = values.skirtCapSmoothSeconds or SKIRT_DEFAULTS.capSmoothSeconds,
		-- 方向加权的手性开关：叉乘出来的"后"可能是前，翻号即可。
		backSign = (values.skirtBackSign or 1) < 0 and -1 or 1,
		-- sign 只为"方向验证"存在：置 -1 就把纠正方向反过来，不必改代码重新部署。
		sign = (values.skirtSign or 1) < 0 and -1 or 1,
		-- 预设枚举（诊断）：>0 时完全接管，恒定角度、不看 keep-out。
		preset = math.floor(values.preset or 0),
		all = (values.skirtAll or 0) ~= 0,
	}
end

--- 会话级的两个参照点：hips（圆心）与 chest（上方向）。解析不到就放弃这一帧。
local function skirt_refs(S_, session)
	local refs = session.skirtRefs
	if refs ~= nil then return refs end
	if S_.Unit == nil or session.unit == nil then return nil end
	local okCount, count = pcall(function() return S_.Unit.num_scene_graph_items(session.unit) end)
	if not okCount or type(count) ~= "number" then return nil end
	local function nodeOf(name)
		local okHas, has = pcall(S_.Unit.has_node, session.unit, name)
		if not okHas or has ~= true then return nil end
		local okNode, node = pcall(function() return S_.Unit.node(session.unit, name) end)
		if okNode and type(node) == "number" and node >= 1 and node <= count then return node end
		return nil
	end
	local hipsNode, chestNode = nodeOf("hips"), nodeOf("chest")
	if hipsNode == nil then return nil end
	refs = { hips = hipsNode, chest = chestNode, lThigh = nodeOf("l_thigh"), rThigh = nodeOf("r_thigh") }
	session.skirtRefs = refs
	return refs
end

local function worldPos(S_, unit, node)
	local ok, x, y, z = pcall(function()
		return S_.Vector3.to_elements(S_.Unit.world_position(unit, node))
	end)
	if ok and type(x) == "number" then return x, y, z end
	return nil
end

--- 哪些链算"裙摆/连衣裙布料"。
---
--- 原来只认父骨名含 `hips`，于是**挂在胸口背后的连衣裙骨被整条漏掉**
--- （实机反馈：有些裙子是连衣裙做法，骨在胸口那一层）。
--- 现在两条判据，命中任一即可；结果**缓存**在 `item.skirtEligible`（躯干带相对身体是不变的，
--- 只有名字认不出时才需要一次世界坐标查询）。
---
--- 安全边界不在"选中"，而在权重：正前/侧面的 cos(夹角) ≤ 0 ⇒ 一根都不会动。
--- 所以放宽选中范围不会让乳房/小腹的骨被推动。
local SKIRT_PARENT_HINTS = { "hips", "pelvis", "spine", "chest", "torso", "waist" }

---@return boolean|nil true/false = 已确定；nil = 这一帧还没法判断（等下次）
local function skirt_eligible(S_, session, item, body)
	if item.skirtEligible ~= nil then return item.skirtEligible end
	local parent = item.chain and item.chain[1] and item.chain[1].parentName
	local lower = type(parent) == "string" and parent:lower() or ""
	for _, hint in ipairs(SKIRT_PARENT_HINTS) do
		if lower:find(hint, 1, true) ~= nil then
			item.skirtEligible = true
			item.skirtEligibleWhy = "name:" .. hint
			return true
		end
	end
	-- 名字认不出（`source_extra_*` 这类）：用高度带兜底。
	-- 带 = [hips 下方 10 cm, chest 上方 15 cm]：兜住挂在胸口/腰上的布料，
	-- 同时把头/头发（在 chest 上方更远）和腿（在 hips 下方）排除。
	if body ~= nil and body.hz ~= nil and body.cz ~= nil and item.nodes ~= nil and item.nodes[1] ~= nil then
		local _, _, rz = worldPos(S_, session.unit, item.nodes[1])
		if rz ~= nil then
			local inside = rz >= (body.hz - 0.10) and rz <= (body.cz + 0.15)
			item.skirtEligible = inside
			item.skirtEligibleWhy = inside and "height" or "outside"
			return inside
		end
	end
	return nil
end

--- 对一条链做"标定"或"纠正"。返回是否改动了 pushOut。
local function skirt_chain(S_, runtime, session, item, settings, calibrating, logger)
	local refs = skirt_refs(S_, session)
	if refs == nil then return false end
	local nodes = item.nodes
	if nodes == nil or #nodes == 0 then return false end
	local deep = nodes[#nodes]
	local dx, dy, dz = worldPos(S_, session.unit, deep)
	if dx == nil then return false end
	local hx, hy, hz = worldPos(S_, session.unit, refs.hips)
	if hx == nil then return false end
	-- 上方向：優先 chest−hips，拿不到就用世界 +Z（HD2 是 Z 向上）
	local ux, uy, uz = 0, 0, 1
	if refs.chest ~= nil then
		local cx, cy, cz = worldPos(S_, session.unit, refs.chest)
		if cx ~= nil then
			local vx, vy, vz = cx - hx, cy - hy, cz - hz
			local len = math.sqrt(vx * vx + vy * vy + vz * vz)
			if len > 0.05 then ux, uy, uz = vx / len, vy / len, vz / len end
		end
	end
	-- 相对 hips 的位移，去掉沿上方向的分量 ⇒ 水平径向
	local px, py, pz = dx - hx, dy - hy, dz - hz
	local along = px * ux + py * uy + pz * uz
	local rx, ry, rz = px - along * ux, py - along * uy, pz - along * uz
	local radius = math.sqrt(rx * rx + ry * ry + rz * rz)
	if radius < 1e-4 then return false end
	local rnx, rny, rnz = rx / radius, ry / radius, rz / radius

	-- "这块布有多垂" = **这根骨自己的方向**（链根 → 链尾）与世界水平面的夹角，取绝对值。
	--
	-- 两个坑（都是实测踩的，别再踩）：
	--   ① 不能用 hips → 链尾：链尾在世界 z 上可能比 hips 还高，算出来恒为负，
	--      于是每一帧都被判成"太横"，上限几帧内归零 ⇒ 整个功能等于关掉。
	--   ② 不能依赖符号：z 轴朝上还是朝下、链的节点顺序从腰到摆还是从摆到腰，
	--      从运行时代码里看不出来。取绝对值后这些都不影响。
	--
	-- 角度本身也不适合写死阈值：实测"垂着"的链只有 41~61°，不是直觉上的 60~90°，
	-- 所以绝对阈值 45/65 会把大量自然下垂的链也判成"被推高"。改成相对各自基线（见下）。
	local tilt = 0
	do
		local r0x, r0y, r0z = worldPos(S_, session.unit, nodes[1])
		if r0x ~= nil then
			local vx, vy, vz = dx - r0x, dy - r0y, dz - r0z
			local horiz = math.sqrt(vx * vx + vy * vy)
			tilt = math.abs(math.atan2(vz, horiz)) * 180 / math.pi
		end
	end
	do
		local sk = (runtime.dt or (1 / 60)) / math.max(settings.capSmoothSeconds, 0.02)
		if sk > 1 then sk = 1 end
		if item.skirtSag == nil then item.skirtSag = tilt
		else item.skirtSag = item.skirtSag + (tilt - item.skirtSag) * sk end
	end
	item.skirtSagRaw = tilt

	if calibrating then
		if item.keepOut == nil then
			item.keepOut = radius
			logger("[physbone][裙摆][标定] %s r=%.3f m", tostring(item.chain[1] and item.chain[1].name), radius)
		end
		return false
	end
	-- 没标定过就用当前半径当基准：亏损算出来是 0，只影响下面的"按亏换算角度"那一档，
	-- 不影响手调角度。
	local keepOut = item.keepOut or radius
	local deficit = keepOut + settings.margin - radius
	-- **这里原本是致命的一处开关**（2.1.8 及更早）：deficit <= 0 就 pushOut=nil 并 return，
	-- 于是后面那句"无条件加"永远走不到。实机证据（同局日志）：180 条 hips 链只有 26~51
	-- 条被推，且每几帧在 26/34/35/43/51 之间跳；跑动时角度确实到 29~30° ⇒ 不是算小了，
	-- 是多数帧**根本没加**。用户看到的就是"大多被拉回，少数保持角度"。
	-- 现在亏损只用于报数和"按亏损换算角度"那一档，不再当开关。
	if settings.preset > 0 then
		-- 诊断模式：不看 keep-out、不算亏损，恒定 60° 往外摆（或往里，看符号）。
		local preset = PRESETS[settings.preset]
		if preset == nil then return false end
		local ax, ay, az = rny * uz - rnz * uy, rnz * ux - rnx * uz, rnx * uy - rny * ux
		local alen = math.sqrt(ax * ax + ay * ay + az * az)
		if alen < 1e-6 then return false end
		item.pushOut = { ax / alen, ay / alen, az / alen,
			PRESET_DEGREES * math.pi / 180 * preset[2], preset[1] }
		item.pushOutAll = settings.all
		return true
	end
		-- 把"要补的距离"换成"要转的角度"：θ ≈ 亏损 / 力臂
	-- 角度：手调优先（度），否则按"每厘米亏损多少度"换算。
	-- 上一次的教训：`gain × 亏损 / 力臂` 在 deficit≈2 cm、arm=0.3 m 时只有 3.8°，
	-- 而日志打的是**上限** 60°，于是我自己得出"推满了也没用"的错误结论。
	-- ① 方向权重：只给"朝后"的那部分。夹角按**角度线性**拆（45° 给一半 15°），
	--    这是用户明确要的语义：侧面/正前一点都不分。
	local back = session.skirtBack
	local liveWeight = 1.0
	if back ~= nil then
		local dot = rnx * back[1] + rny * back[2] + rnz * back[3]
		if dot < -1 then dot = -1 elseif dot > 1 then dot = 1 end
		local deviation = math.acos(dot) * 180 / math.pi      -- 与"正后方"的夹角
		-- **权重 = cos(夹角)**（= dot 本身），偏前一律 0。
		--
		-- 为什么不用线性 `1 − θ/90`：
		--   d/dθ[1 − θ/90] = 常数 ⇒ 侧面附近仍然按同样速度给角度，
		--   于是"差一点没到侧面"的骨头也拿得到可观的角度，前方布料一被物理甩到后面
		--   就会被我们顺势推进小腹（实机反馈）。
		--   d/dθ[cos θ] = −sin θ ⇒ 正后方导数为 0（平坦、稳），越靠侧面掉得越快，
		--   90° 处正好掉到 0。用户给的形状描述就是"半个椭圆"。
		-- 附带好处：cos 不需要再算 acos，dot 直接就是权重。
		liveWeight = dot > 0 and dot or 0
		item.skirtDeviation = deviation
	end
	-- 权重按时间常数平滑（不是每帧直接用瞬时值）。
	-- 为什么：瞬时权重 = f(当前姿态)，而姿态又被我们自己的偏移改变 ⇒ 反馈回路，
	-- 表现为同一块布的角度每帧变（实测整片在 13°↔30° 之间集体摆）。
	-- 平滑之后每条骨拿到的角度基本恒定 = 用户要的"恒定偏移"。
	local weight = item.skirtWeight
	if weight == nil then
		weight = liveWeight
	else
		local wdt = runtime.dt or (1 / 60)
		local k = wdt / math.max(settings.weightSeconds, 0.02)
		if k > 1 then k = 1 end
		weight = weight + (liveWeight - weight) * k
	end
	item.skirtWeight = weight

	-- ② 迟滞的"需不需要保护"：避免在边界上每帧翻转（翻转就是抖动）。
	local margin = settings.margin
	if deficit > margin then
		item.skirtNeed = 1
	elseif deficit < -margin then
		item.skirtNeed = nil
	end

	-- ③ 平滑追值：目标 = 方向权重 × 运动斜坡。
	--    **不再乘"需要标记"**：那个标记跟着链骨半径过门槛翻转，翻转就是闪烁。
	--    叠加关系，不是竞争关系：物理怎么摆，我们都在它的结果上多给一个恒定偏移。
	local target = weight * (session.skirtMotion or 0)
	local level = item.skirtLevel or 0
	local dt = runtime.dt or (1 / 60)
	local step = dt / math.max(settings.rampSeconds, 0.02)
	if level < target then
		level = math.min(target, level + step)
	elseif level > target then
		level = math.max(target, level - step)
	end
	item.skirtLevel = level
	item.skirtDebug = { weight = weight, level = level, deviation = back ~= nil and weight or nil }

	-- 按亏损换算的那一档必须夹住：deficit 现在可能为负（半径比标定值大），
	-- 直接乘会得到一个负角度 ⇒ 往里推 ⇒ 正好推进屁股里。
	local cm = deficit > 0 and deficit * 100 or 0
	local degrees = settings.manualDeg > 0 and settings.manualDeg or settings.degPerCm * cm
	local magnitude = degrees * level

	-- ③ 自适应上限（临时值）：按这块布**有多垂**反馈。
	--    基线 tiltBase 只在**没在推的时候**学（level < 0.05 ⇒ 这块布此刻的角度就是它自然的角度）。
	--    为什么不能在推的时候学：推本身就在压低 tilt，学出来的"基线"会跟着上限一起往下跑，
	--    系统会收敛到接近 0（离线模拟验证过，这是第一版相对阈值的致命缺陷）。
	--    判定：绝对阈值为主（用户给的规则），基线只用来**防误伤**——
	--    自然就只有 41~61° 的链不该被当成"被推高"，它要求比自己自然状态再低 capDropDelta 才算。
	local ceiling = settings.manualDeg > 0 and settings.manualDeg or settings.maxDeg
	local cap = item.skirtCap
	if cap == nil then cap = ceiling end
	local tiltSmooth = item.skirtSag or tilt
	if level < 0.05 then
		-- 自然状态：可能是站着、也可能是刚松开跑动。按 0.3 秒平滑学一次。
		local bk = dt / 0.3
		if bk > 1 then bk = 1 end
		if item.skirtTiltBase == nil then item.skirtTiltBase = tiltSmooth
		else item.skirtTiltBase = item.skirtTiltBase + (tiltSmooth - item.skirtTiltBase) * bk end
	end
	local base = item.skirtTiltBase
	local tooHigh = tiltSmooth < settings.capDropDeg
		and (base == nil or tiltSmooth < base - settings.capDropDelta)
	local recovered = tiltSmooth > settings.capRiseDeg
		or (base ~= nil and tiltSmooth > base - settings.capRiseDelta)
	if tooHigh then
		item.skirtCapRise = 0
		if magnitude > 0.5 then cap = cap - settings.capDropStep end
	elseif recovered then
		item.skirtCapRise = (item.skirtCapRise or 0) + 1
		if item.skirtCapRise >= settings.capRiseFrames then
			item.skirtCapRise = 0
			cap = cap + settings.capRiseStep
		end
	else
		item.skirtCapRise = 0
	end
	if cap > ceiling then cap = ceiling
	elseif cap < settings.capFloor then cap = settings.capFloor end
	-- 兜底：上限永远不低于 ceiling 的 capFloorRatio 倍。
	-- 为什么需要它：2.1.11 上限被压到 0，整个功能等于关掉（实机反馈"变成主线的物理一样"）。
	-- 有了这条，无论如何都还留着一部分推力，最坏情况是"推得不够"，不会是"完全没推"。
	local ratioFloor = ceiling * settings.capFloorRatio
	if cap < ratioFloor then cap = ratioFloor end
	item.skirtCap = cap
	item.skirtCapActive = magnitude > 0.5
	if magnitude > cap then magnitude = cap end

	local angle = settings.sign * magnitude * math.pi / 180
	local maxAngle = settings.maxDeg * math.pi / 180
	if angle > maxAngle then angle = maxAngle elseif angle < -maxAngle then angle = -maxAngle end
	if math.abs(angle) < 1e-5 then
		item.pushOut = nil
		return false
	end
	-- 轴 = r̂ × up：这样绕它转时，向下垂的布会朝 +r̂ 方向摆出去
	local ax, ay, az = rny * uz - rnz * uy, rnz * ux - rnx * uz, rnx * uy - rny * ux
	local alen = math.sqrt(ax * ax + ay * ay + az * az)
	if alen < 1e-6 then return false end
	item.pushOut = { ax / alen, ay / alen, az / alen, angle }
	item.pushOutAll = settings.all
	return true
end

--- 每帧调一次：遍历所有会话里"挂在 hips 上"的链。
local function skirt_apply(S_, runtime)
	local settings = skirt_settings(runtime)
	if not settings.enabled and not settings.calibrate and settings.preset == 0 then return end
	local allNodes = (tune_values(runtime).skirtAll or 0) ~= 0
	local pushed, total, weighted = 0, 0, 0
	local motionNow = 0
	for _, session in ipairs(runtime.sessions or {}) do
		if session.unit ~= nil then
			-- 会话级准备：① 骨盆速度 → 运动斜坡；② "正后方"轴（世界坐标）。
			local refs = skirt_refs(S_, session)
			if refs ~= nil then
				local hx, hy, hz = worldPos(S_, session.unit, refs.hips)
				if hx ~= nil then
					local dt = runtime.dt or (1 / 60)
					local last = session.skirtPelvis
					local speed = 0
					if last ~= nil and dt > 1e-4 then
						local dx, dy, dz = hx - last[1], hy - last[2], hz - last[3]
						speed = math.sqrt(dx * dx + dy * dy + dz * dz) / dt
					end
					session.skirtPelvis = { hx, hy, hz }
					-- 目标 0~1：静止 0、跑起来 1；再按时间常数追值，避免跳变。
					local raw = (speed - settings.idleSpeed) / math.max(settings.runSpeed - settings.idleSpeed, 0.1)
					if raw < 0 then raw = 0 elseif raw > 1 then raw = 1 end
					local motion = session.skirtMotion or 0
					local step = dt / math.max(settings.rampSeconds, 0.02)
					if motion < raw then motion = math.min(raw, motion + step)
					elseif motion > raw then motion = math.max(raw, motion - step) end
					session.skirtMotion = motion

					-- 正后方 = up × lateral（手性可能反，用 skirtBackSign 翻）
					if refs.lThigh ~= nil and refs.rThigh ~= nil and refs.chest ~= nil then
						local lx, ly, lz = worldPos(S_, session.unit, refs.lThigh)
						local rx2, ry2, rz2 = worldPos(S_, session.unit, refs.rThigh)
						local cx, cy, cz = worldPos(S_, session.unit, refs.chest)
						if lx ~= nil and rx2 ~= nil and cx ~= nil then
							-- 躯干高度带：给"名字认不出的连衣裙骨"兜底用（见 skirt_eligible）
							session.skirtBody = { hz = hz, cz = cz }
							-- lateral(points left) = l − r；up = chest − hips
							local ax, ay, az = lx - rx2, ly - ry2, lz - rz2
							local ux, uy, uz = cx - hx, cy - hy, cz - hz
							local bx, by, bz = uy * az - uz * ay, uz * ax - ux * az, ux * ay - uy * ax
							local blen = math.sqrt(bx * bx + by * by + bz * bz)
							if blen > 1e-6 then
								local nx, ny, nz = bx / blen * settings.backSign, by / blen * settings.backSign,
									bz / blen * settings.backSign
								-- 平滑追值：跑步时 l_thigh/r_thigh 前后交替，"l−r" 每一步都在转，
								-- 直接用瞬时值会让所有链的权重跟着步频摆（实测 13°↔30°）。
								local prev = session.skirtBack
								if prev == nil then
									session.skirtBack = { nx, ny, nz }
								else
									local k = dt / math.max(settings.backSeconds, 0.02)
									if k > 1 then k = 1 end
									local mx = prev[1] + (nx - prev[1]) * k
									local my = prev[2] + (ny - prev[2]) * k
									local mz = prev[3] + (nz - prev[3]) * k
									local mlen = math.sqrt(mx * mx + my * my + mz * mz)
									if mlen > 1e-6 then
										session.skirtBack = { mx / mlen, my / mlen, mz / mlen }
									end
								end
							end
						end
					end
				end
			end
			-- 会话级一次性诊断：把所有链的**父骨名字**统计出来。
			-- 为什么要它：连衣裙的骨挂在胸口那一层，名字是 `source_extra_*`，
			-- 只有看到真实的父骨名，才知道该把哪些名字加进 SKIRT_PARENT_HINTS。
			if session.skirtCensusDone ~= true then
				session.skirtCensusDone = true
				local counts, order = {}, {}
				for _, item in ipairs(session.sessions or {}) do
					local parent = item.chain and item.chain[1] and item.chain[1].parentName
					local key = type(parent) == "string" and parent or "?"
					if counts[key] == nil then
						counts[key] = 0
						order[#order + 1] = key
					end
					counts[key] = counts[key] + 1
				end
				local parts = {}
				for _, key in ipairs(order) do
					parts[#parts + 1] = string.format("%s×%d", key, counts[key])
				end
				runtime.logger("[physbone][裙摆][父骨] unit=%s 共 %d 条链：%s",
					tostring(session.unitTag or "?"), #order, table.concat(parts, " "))
			end
			for _, item in ipairs(session.sessions or {}) do
				-- 选中判据见 skirt_eligible：名字 hinted 命中，或链根落在躯干高度带里。
				local eligible = skirt_eligible(S_, session, item, session.skirtBody)
				if eligible == true then
					total = total + 1
					-- 标定只在**第一次**做：同一个会话里不重复覆盖（否则跑着跑着又标一次）
					local calibrating = settings.calibrate and not session.skirtCalibrated
					if skirt_chain(S_, runtime, session, item, settings, calibrating, runtime.logger) then
						pushed = pushed + 1
					end
					-- 诊断：有多少条链"有方向权重"（= 该加的那部分）。
					-- 推的条数应当≈这个数（跑动时）；差得多就说明还有别的门在挡。
					if (item.skirtWeight or 0) > 0.01 then weighted = weighted + 1 end
				end
			end
			motionNow = session.skirtMotion or motionNow
			if settings.calibrate then session.skirtCalibrated = true end
		end
	end
	local state = runtime.skirtState
	if state == nil then state = { reported = nil, preset = nil }; runtime.skirtState = state end
	if settings.preset > 0 and state.preset ~= settings.preset then
		state.preset = settings.preset
		runtime.logger("[physbone][裙摆][预设] %s（推了 %d 条 hips 链）",
			preset_label(settings.preset), pushed)
	end
	local message = string.format("%d/%d", pushed, total)
	-- 每秒报一次**实际用到的角度**（不是上限）。上限骗过一次人，不能再犯。
	local frameNow = runtime.driveFrames or 0
	if state.reported ~= message or frameNow % 120 == 0 then
		if state.reported ~= message or (state.lastLogFrame == nil or frameNow - state.lastLogFrame >= 120) then
			state.reported = message
			state.lastLogFrame = frameNow
			local maxDeg = 0
			local degSum, degN = 0, 0
			local bestWeight = 0
			local capMin, capLimited, capN = nil, 0, 0
			local sagSum, sagN = 0, 0
			local peakSum = 0
			local buckets = { 0, 0, 0, 0, 0, 0 }
			local ceilingNow = settings.manualDeg > 0 and settings.manualDeg or settings.maxDeg
			for _, session in ipairs(runtime.sessions or {}) do
				for _, item in ipairs(session.sessions or {}) do
					local push = item.pushOut
					if push ~= nil then
						local deg = math.abs(push[4]) * 180 / math.pi
						if deg > maxDeg then maxDeg = deg end
						degSum = degSum + deg
						degN = degN + 1
						-- 自适应上限：看它压到多少、有多少条被压过。
						local cap = item.skirtCap
						if cap ~= nil then
							capN = capN + 1
							if capMin == nil or cap < capMin then capMin = cap end
							if cap < ceilingNow - 0.5 then capLimited = capLimited + 1 end
						end
					end
					-- tilt 与基线：**不看是否在推**，否则站着（没有 pushOut）就永远是 0、看不到数据。
					local tiltNow = item.skirtSag
					if tiltNow ~= nil then
						sagSum = sagSum + tiltNow
						peakSum = peakSum + (item.skirtTiltBase or tiltNow)
						sagN = sagN + 1
					end
					-- 方向分布：只统计"会被我们考虑的"链（权重已算过），按偏离正后方每 30° 一格。
					-- 用途：判断"正后方"轴是否偏。环形裙子应当左右对称且最靠后那根接近 0°；
					-- 若最近的一根也在 30~60°，说明轴偏了或裙子本身没有正中骨。
					local dev = item.skirtDeviation
					if dev ~= nil and (item.skirtWeight or 0) > 0 then
						local index = math.floor(dev / 30) + 1
						if index < 1 then index = 1 elseif index > 6 then index = 6 end
						buckets[index] = buckets[index] + 1
						if (item.skirtWeight or 0) > bestWeight then bestWeight = item.skirtWeight end
					end
				end
			end
			-- 均值比最大值有用：最大值只能证明"有一条推满了"，均值才说明"整片有没有动"。
			runtime.logger(
				"[physbone][裙摆] 推 %d/%d 条（权重>0 %d）· 角度 均值 %.1f° / 最大 %.1f° · 斜坡 %.2f（手调 %.0f°）· 权重最大 %.2f · 上限 最小 %.0f°（被压 %d/%d）· tilt 均值 %.0f° / 基线 %.0f° · 分布 %d/%d/%d/%d/%d/%d",
				pushed, total, weighted, degN > 0 and degSum / degN or 0, maxDeg, motionNow,
				settings.manualDeg, bestWeight,
				capMin or ceilingNow, capLimited, capN,
				sagN > 0 and sagSum / sagN or 0, sagN > 0 and peakSum / sagN or 0,
				buckets[1], buckets[2], buckets[3], buckets[4], buckets[5], buckets[6])
		end
	end
end

local function drive(runtime)
	local frame = runtime.frame
	local options = runtime.options
	local logger = runtime.logger
	runtime.logContext.frame = frame
	Compat.begin_frame(frame)

	-- dt 用引擎自己的时间差，不写死 1/60。
	-- 实测回调约 120 次/秒（不是 70，也不是 375），写死 1/60 会让物理比真实时间快/慢一截，
	-- 观感是"回弹拖沓"或"甩得太开"，很容易被误判成阻尼/刚度调错了。
	--
	-- ⚠️ `Application.time_since_launch` **每帧只更新一次**，所以：
	--   ① 只能在**帧之间**做差（在一次回调内部取首尾差值恒为 0，踩过）；
	--   ② 一帧里被调用多次时，后面那几次拿到的差值是 0 —— 这时**绝不能再用上一次的 dt 积分**，
	--      否则一帧就积了 N 倍的时间，物理快 N 倍、永远停不下来（表现就是"飘逸过头"）。
	--      正确做法是：时钟没前进 ⇒ 这一拍不是新的时间步 ⇒ 什么都不做。
	local now = try(S.Application.time_since_launch)
	local advanced = true
	if type(now) == "number" and now == now then
		local previous = runtime.lastTime
		runtime.lastTime = now
		if previous == nil then
			advanced = true                      -- 首帧：没有差值可比，照常驱动
		elseif now > previous then
			local delta = now - previous
			-- 上限 0.25 与 compat 的稳定性闸一致：卡顿帧不该被当成"过了 0.5 秒"去积分。
			if delta > 0.25 then delta = 0.25 end
			if delta >= 0.001 then runtime.dt = delta end
			advanced = true
		else
			advanced = false                     -- 同一时间步内的重复调用
			runtime.repeatCalls = (runtime.repeatCalls or 0) + 1
			-- 兜底：引擎时钟**一直不动**（老版本没有这个量 / 被谁包了一层）时不能永远不动。
			-- 连续太多次没前进就退回"按上一次的 dt 照常积分"，并大声说一次——
			-- 宁可物理快一点，也不要静默冻住（静默失败是这一整层最贵的一类问题）。
			runtime.stuckClock = (runtime.stuckClock or 0) + 1
			if runtime.stuckClock >= (options.stuckClockLimit or 600) then
				if not runtime.stuckClockLogged then
					runtime.stuckClockLogged = true
					logger("[physbone][警告] 引擎时钟连续 %d 次没有前进（time_since_launch 不可用？）"
						.. "⇒ 退回固定 dt=%.4f 继续驱动", runtime.stuckClock, runtime.dt or (1 / 60))
				end
				runtime.stuckClock = 0
				advanced = true
			end
		end
	end
	if not advanced then return end

	if runtime.sessions ~= nil and #runtime.sessions > 0 then
		local dropped = pruneSessions(runtime)
		if #runtime.sessions == 0 then
			-- 全没了（换场景 / 全员阵亡）：退回等待，带退避（避免换甲期间每秒重挂一次）。
			-- 存活时长决定退避：刚挂上就死 = 抖动（换甲中），要越等越久；
			-- 稳定跑过 5 秒再死 = 正常换场景，下一次应当**立刻**重挂。
			local survived = frame - (runtime.attachedFrame or frame)
			runtime.sessions = nil
			runtime.reconcile = nil
			if survived >= (options.stableFrames or 600) then
				runtime.reArm = 1
			else
				runtime.reArm = (runtime.reArm or 0) + 1
			end
			local delay = math.min(options.maxReArmFrames or 600, (options.reArmFrames or 60) * runtime.reArm)
			runtime.rearmAt = frame + delay
			logger("[physbone][LIFECYCLE] 场景已变（存活 %d 帧）⇒ 退避 %d 帧后重扫", survived, delay)
		else
			-- 只掉了一部分（某个队友换甲 / 退出 / 阵亡）：**留着活着的那些**，只补缺的。
			-- 老版本在这一步是"丢掉全部会话再重扫"，多人局里那等于一个人换甲，全场人闪一下没物理。
			if dropped > 0 then
				logger("[physbone][LIFECYCLE] 掉了 %d 个会话，剩 %d 个继续跑，随后补挂缺的",
					dropped, #runtime.sessions)
				runtime.reconcile = frame
			end
			-- 会话全都还"健康"，但场上**玩家名单**可能变了（有人加入 / 有人退出 / 复活换了实体）。
			-- 这里同样**不释放任何东西**：变的是名单，谁还在谁就继续跑。
			local validateFrames = options.validateFrames == nil and 60 or options.validateFrames
			if validateFrames > 0 and (frame % validateFrames) == 0 and avatarSetChanged(runtime) then
				runtime.reconcile = frame
			end
		end
	end

	-- 名单变化后的补挂/收尾。走的是同一个 `tryAttach`（**增量**：只补没挂的、只收主人不在的），
	-- 所以不需要先把自己人的会话丢掉 —— 那正是老版本多人局里"所有人闪一下没物理"的来源。
	if runtime.reconcile ~= nil and runtime.sessions ~= nil and #runtime.sessions > 0 then
		if frame >= runtime.reconcile then
			local poll = options.reconcilePollFrames or (options.pollFrames or 20)
			if tryAttach(runtime) then
				runtime.reconcile = nil
			else
				runtime.reconcile = frame + poll     -- 场景还没稳，下一拍再来
			end
		end
	end

	if runtime.sessions == nil then
		if runtime.rearmAt ~= nil and frame < runtime.rearmAt then
			note_wait(runtime, "退避等待重扫")
			return
		end
		-- 等待中的轮询会跑一趟完整的 `discover`（扫所有 unit + 给每个候选打分，几千次引擎调用），
		-- 所以**必须**随等待时长放慢：刚掉线那 2 秒要快（复活/换甲很快结束），
		-- 之后就按 3 倍间隔扫一次 —— 否则"一直等不到"会变成持续的顿卡。
		runtime.waitSince = runtime.waitSince or frame
		local waited = frame - runtime.waitSince
		local poll = options.pollFrames or 20
		if waited > (options.waitSlowAfter or 200) then poll = poll * 3 end
		if (frame % poll) ~= 0 then return end
		if not tryAttach(runtime) then return end
		runtime.waitSince = nil
	else
		runtime.waitSince = nil
	end

	-- ---------- 驱动 ----------
	-- 计时不可靠（`Application.time_since_launch` 每帧只更新一次、`os.clock()` 被引擎其它
	-- 线程污染），所以性能只报**引擎调用次数**——唯一不会被环境噪声污染的口径。
	--
	-- 多人局的**预算**：一帧的驱动成本几乎正比于链数（实测单人 52 链 ≈ 180 次引擎调用/帧，
	-- 约 3.5 次/链）。四个玩家同屏就是 4 倍。真到那个量级还没问题，但"每人身上好几套 mod"
	-- 会让链数继续翻，所以这里给一条闸：**第一位玩家（离相机最近的那个，也就是自己）每帧都算；
	-- 其余玩家按 stride 分帧**，被动到的那一帧用**累积的 dt** 积分（compat 内部还会再拆子步），
	-- 所以物理速度不变，只是采样率降到 1/2。观感差别极小，成本直接对半。
	local chainTotal = 0
	for index = 1, #runtime.sessions do
		chainTotal = chainTotal + #(runtime.sessions[index].sessions or {})
	end
	local budget = options.maxChainsPerFrame or 160
	-- 首位玩家（自己）身上的链数：这部分**不参与分帧**，始终每帧算。
	local primaryChains = 0
	if runtime.sessions[1] ~= nil then
		primaryChains = #(runtime.sessions[1].sessions or {})
	end
	-- ── 自适应分帧（2026-11-03 实机：四人局红色折线约是绿色两倍、掉到 40 fps）──
	-- 旧实现是**固定 stride=2**，于是四人局的实际工作量是
	--     自己 418 链/帧 + 队友 3×418/2 ≈ 1045 链/帧
	-- 完全没有被 budget 约束住 —— 每帧预算形同虚设，玩家越多越线性变慢。
	--
	-- 现在按"队友链数 / 队友预算"反推步长：队友那边每帧最多驱动 budget/2 条链。
	-- 于是总工作量被压到 (自己 + budget/2) 附近，不再随玩家人数线性增长。
	-- dt 仍然累积补偿（compat 内部会再拆子步），所以**物理速度不变，只是队友的采样率下降**。
	-- 观感上：队友通常在画面里很小/很远，降采样几乎看不出来；自己永远是全速率。
	local otherChains = chainTotal - primaryChains
	local stride = 1
	local otherBudget = math.max(math.floor(budget / 2), 24)
	-- 只有**真的超预算**才分帧：队友链数在预算内时仍按每帧驱动（别为了省而省）。
	if otherChains > otherBudget then
		stride = math.max(2, math.ceil(otherChains / otherBudget))
		local cap = options.maxOtherPlayerStride or 16
		if stride > cap then stride = cap end
	end
	if stride ~= runtime.driveStride then
		runtime.driveStride = stride
		if stride > 1 then
			logger("[physbone][预算] 自己 %d 链/帧 + 队友 %d 链 ⇒ 队友每 %d 帧驱动一次（每帧预算 %d；dt 累积补偿，速度不变）",
				primaryChains, otherChains, stride, budget)
		else
			logger("[physbone][预算] 链数 %d 在每帧预算 %d 以内 ⇒ 所有玩家恢复每帧驱动",
				chainTotal, budget)
		end
	end

	local written, skippedSame, engineCalls, staticChains = 0, 0, 0, 0
	local attempted = 0
	local dt = runtime.dt or (1 / 60)
	for index = 1, #runtime.sessions do
		local session = runtime.sessions[index]
		-- compat 只在正常返回时刷新这两个计数；先清零，免得沿用上一帧的值把看门狗骗过去。
		session.skippedSame, session.engineCalls = 0, 0

		-- 时间**永远**先累积：没被驱动的这一帧不是"时间不存在"，攒到下一次一起积。
		-- 丢了它，物理会比真实时间慢 —— 表现是"队友的头发像在水里"。
		local pending = (session.pendingDt or 0) + dt
		local order = session.ownerOrder or 1
		local due = stride <= 1 or order <= 1 or (frame % stride) == ((order - 1) % stride)
		if not due then
			session.pendingDt = pending
		else
			session.pendingDt = 0
			attempted = attempted + 1
			local ok, out = pcall(Compat.update, session, pending)
			if ok then
				if type(out) == "number" then written = written + out end
			elseif not session.updateErrorLogged then
				-- 以前这里是裸 pcall：一条 unit 每帧都抛错，日志里却什么也没有。
				session.updateErrorLogged = true
				logger("[physbone][错误] unit=%s 驱动抛错：%s", tostring(session.unitTag or "?"), tostring(out))
			end
			skippedSame = skippedSame + (session.skippedSame or 0)
			engineCalls = engineCalls + (session.engineCalls or 0)
		end

		for _, item in ipairs(session.sessions) do
			if (item.staticFrames or 0) > 0 then staticChains = staticChains + 1 end
		end
	end
	runtime.driveFrames = (runtime.driveFrames or 0) + 1

	-- 抬升诊断（默认关；没有调参文件时这里只多一次缓存判断）。
	-- 放在物理写入之后：这一帧渲染用的就是"抬过"的 avatar 根。
	apply_lift(runtime)

	runtime.callsAccum = (runtime.callsAccum or 0) + engineCalls
	runtime.staticChains = staticChains
	runtime.lastChainTotal = chainTotal
	runtime.lastAttempted = attempted
	-- 这一帧一条都没驱动（stride 分帧的正常现象）时不能让"零帧看门狗"累加，
	-- 否则分帧本身会被误判成"驱动失效"→ 重挂 → 分帧 → 再判失效，无限循环。
	local attemptedAny = attempted > 0

	-- 峰值：**偏离绑定方向**的最大角，也就是"离模型原本的形状有多远"。
	--
	-- ⚠️ 它**不是**单节转角，所以拿它跟 `angleLimit` 比是错的（上一版就这么错了）：
	-- 求解器夹的是"相对父节"的偏转，而多节链上第 k 节的"偏离绑定"会把前面每一节的偏转累加进去
	-- ——5 节 × 25° 累到 100° 完全合法。上一版把它算成"超限骨"，报了 28~77，纯属误报。
	-- 判"限位有没有生效"要看相对父节的偏转，那是求解器内部量，不在这一层。
	-- 骨名取 item.chain[index].name，限位取 item.bones[index].gains —— **两个不同的对象**。
	-- 混用过一次，于是限位永远打印 0°，看着像"限位没生效"，白查一轮。
	local framePeak, worstName, worstLimit = 0, nil, 0
	for _, session in ipairs(runtime.sessions) do
		for _, item in ipairs(session.sessions) do
			local rotations = item.lastRotations
			if rotations ~= nil then
				for index = 1, #rotations do
					local angle = rotations[index][4]
					if angle > framePeak then
						framePeak = angle
						local chainBone = item.chain and item.chain[index]
						worstName = chainBone and chainBone.name or nil
						local simBone = item.bones and item.bones[index]
						local gains = simBone and simBone.gains
						worstLimit = (gains and gains.limit or 0) * 180 / math.pi
					end
				end
			end
		end
	end
	local framePeakDeg = framePeak * 180 / math.pi
	if framePeakDeg > (runtime.peakAngle or 0) then runtime.peakAngle = framePeakDeg end

	-- 阶段心跳：每 N 帧一行，证明驱动块确实被进入过，并给出当帧的关键事实。
	-- 上一轮出现过"phase 明明是 live 但 update 从不执行"，没有这条就无法区分
	-- "没进块"和"进了块但走了别的分支"。
	-- 600 帧（60 fps 下 10 秒）足够回答这个问题；120 帧是每秒 2 行，一场长局光心跳就上百 KB。
	local heartbeat = options.heartbeatFrames or 600
	if (runtime.driveFrames % heartbeat) == 0 then
		local chains = 0
		for _, session in ipairs(runtime.sessions) do chains = chains + #session.sessions end
		logger("[physbone] 驱动心跳 unit=%d 链=%d 玩家=%d 分帧=%d 本帧驱动=%d dt=%.4f 本帧 写入=%d 跳过=%d 引擎调用=%d 累计/帧=%.0f 静止链=%d 偏离绑定峰值=%.1f°(史上%.1f°) 最离谱=%s(限%.0f°) 同拍重复=%d",
			#runtime.sessions, chains, #(runtime.avatars or {}), runtime.driveStride or 1, attempted,
			runtime.dt or 0, written, skippedSame, engineCalls,
			runtime.callsAccum / runtime.driveFrames, staticChains, framePeakDeg,
			runtime.peakAngle or 0,
			worstName ~= nil and tostring(worstName) or "-", worstLimit,
			runtime.repeatCalls or 0)
	end

	-- 逐链明细（默认关）：区分"求解器在动"和"参数算出来就是 0"——后者在画面上就是钢板。
	if options.debug and (runtime.driveFrames % (options.debugDetailFrames or 240)) == 0 then
		local peak, parts, shown = 0, {}, 0
		for _, session in ipairs(runtime.sessions) do
			for _, item in ipairs(session.sessions) do
				local rotations = item.lastRotations
				if rotations ~= nil and shown < 6 then
					local itemPeak = 0
					for index = 1, #rotations do
						local angle = rotations[index][4] * 180 / math.pi
						if angle > itemPeak then itemPeak = angle end
					end
					if itemPeak > peak then peak = itemPeak end
					shown = shown + 1
					parts[#parts + 1] = string.format("%s=%.1f°",
						tostring(item.chain and item.chain[1] and item.chain[1].name or "?"), itemPeak)
				end
			end
		end
		logger("[physbone][debug] 逐链峰值 %s（全体峰值 %.1f°）", table.concat(parts, " "), peak)
	end

	-- ⚠️ 判"驱动失效"不能用"这一帧写了几个骨"：加了"姿态没变就跳过"之后，
	-- 站着不动时 written 天然就是 0，用它判失效会让静止的角色每 45 帧重挂一次（踩过）。
	-- 真正的判据是"求解器有没有在处理链"：写了 + 因没变而跳过 = 处理过的骨数。
	--
	-- 多人分帧之后还要再排除一种假信号：**这一帧按 stride 谁都不该被驱动**。
	-- 那种帧一条骨都不会被处理，但这不是故障——它是节流本身。判据要挂"有没有尝试过"。
	local handled = written + skippedSame
	if not attemptedAny then
		-- 分帧跳过的帧：不算数，也不清零（清零会让"卡住"永远攒不到上限）
	elseif handled == 0 then
		runtime.zeroFrames = (runtime.zeroFrames or 0) + 1
	else
		runtime.zeroFrames = 0
	end
	local tripped = false
	for _, session in ipairs(runtime.sessions) do
		for _, item in ipairs(session.sessions) do
			if item.disabled == true then tripped = true end
		end
	end
	if runtime.zeroFrames >= (options.zeroLimit or 45) or tripped then
		logger("[physbone][驱动失效] 连续 %d 帧一条骨都没处理（护栏=%s）⇒ 退回等待重挂",
			runtime.zeroFrames, tostring(tripped))
		runtime.sessions = nil
		runtime.zeroFrames = 0
		runtime.reArm = (runtime.reArm or 0) + 1
		local delay = math.min(options.maxReArmFrames or 600, (options.reArmFrames or 60) * runtime.reArm)
		runtime.rearmAt = frame + delay
	end

	-- 姿态快照：外部按下热键 ⇒ 写请求文件 ⇒ 这里把骨架采下来。
	-- 放在**物理写完、渲染之前**：采到的就是这一帧画到屏幕上的姿态。
	snapshot_poll(S, runtime, options)

	-- 裙摆外推：用这一帧的姿态算**下一帧**要叠的旋转（1 帧延迟，肉眼不可见）。
	-- 放在 update 之后是因为要用到这一帧写完之后的世界位置。
	skirt_apply(S, runtime)
end

--- 安装运行时。可重复调用：第二次直接返回同一个实例。
---@param stingray table 引擎命名空间
---@param options table { compat, logPath, debug, archiveId, ... }
---@return table runtime
function M.install(stingray, options)
	local existing = rawget(_G, INSTALL_KEY)
	if existing ~= nil then return existing end

	S = stingray
	options = options or {}
	local compat = options.compat
	if compat == nil then
		-- 外置模式下加载器可能把 compat 作为同名模块提供；找不到就明确报错，
		-- 而不是静默什么都不做（静默是上一轮排查最久的一类问题）。
		error("physbone runtime: 缺少 physbone_compat 模块（install 的 options.compat）", 2)
	end
	Compat = compat

	local logger, logContext = makeLogger(options)
	state = {
		options = options,
		logger = logger,
		logContext = logContext,
		cache = {},
		carriers = nil,
		sessions = nil,
		-- 场上的玩家 avatar 名单（每个玩家一项）。会话按 `session.owner` 归属到其中一项上，
		-- 名单变化只影响"补挂/收尾"，不会推翻别人的会话。
		avatars = nil,
		reconcile = nil,     -- 名单变化后待执行的补挂帧号；nil = 没有待办
		driveStride = 1,     -- 当前的分帧步长（链数超预算时 >1）
		frame = 0,
		dt = options.dt or (1 / 60),
		reArm = 0,
		rearmAt = nil,
		driveFrames = 0,
		callsAccum = 0,
		zeroFrames = 0,
		waitReason = nil,
		waitRepeat = 0,
		installed = true,
	}
	rawset(_G, INSTALL_KEY, state)

	local driver = state
	originalRender = rawget(_G, "render")
	rawset(_G, "render", function(...)
		-- 先让原来的 render 跑完（别的 mod 可能也在包装），再写我们的姿态：
		-- 这样引擎已经算完动画、还没画，我们写进去的 local pose 会直接用于本帧蒙皮。
		local result
		if originalRender ~= nil then result = originalRender(...) end
		if driver.installed then
			driver.frame = driver.frame + 1
			local ok, err = pcall(drive, driver)
			if not ok and not driver.driveErrorLogged then
				driver.driveErrorLogged = true
				driver.logger("[physbone][错误] 驱动失败：%s", tostring(err))
			end
		end
		return result
	end)

	logger("[physbone] 运行时 %s 已安装（%s）启动于 %s",
		M.VERSION,
		options.mode == "boot" and "独占 boot" or "外置模块",
		os.date("%Y-%m-%d %H:%M:%S"))
	return state
end

--- 卸载：把 render 还回去，并尽量把姿态交还给引擎。
function M.uninstall()
	local runtime = state
	if runtime == nil then return false end
	runtime.installed = false
	for _, session in ipairs(runtime.sessions or {}) do
		pcall(Compat.release, session)
	end
	runtime.sessions = nil
	rawset(_G, "render", originalRender)
	rawset(_G, INSTALL_KEY, nil)
	state = nil
	return true
end

--- 运行中改参数（热调）。
function M.setOptions(patch)
	if state == nil or type(patch) ~= "table" then return false end
	for key, value in pairs(patch) do state.options[key] = value end
	return true
end

M.install_ = M.install

-- 测试钩子：抬升探针的两块纯逻辑（读调参文件 / 给一个 unit 加偏移）。
-- 单独暴露出来，是因为"不累加、引擎重设后能重写、关掉时能还原"这三条
-- 只要错一条就会让角色每帧往上飞 —— 这种判据必须能被单测直接钉住，
-- 而不是靠进游戏看。
M.tune_values_ = tune_values
M.lift_unit_ = lift_unit
M.lift_bone_ = lift_bone
M.apply_lift_ = apply_lift

-- 测试钩子：日志闸门（轮转 + 单次启动预算 + 用尽后只留错误类）。
-- 这三条判据都只能看**落盘结果**，所以必须能直接造一个 logger 往临时文件里写，
-- 而不是靠进游戏观察。
M.make_logger_ = makeLogger

-- 测试钩子：姿态快照。解析、拼 JSON、轮询三段都能离线钉住——
-- 否则"按了键但什么都没写出来"这种问题只能靠用户再进一次游戏才发现。
M.snapshot_parse_ = parse_snapshot_request
M.snapshot_ids_ = snapshot_ids
M.snapshot_json_ = snapshot_json
M.snapshot_poll_ = snapshot_poll
M.snapshot_dir_ = snapshot_dir

return M
