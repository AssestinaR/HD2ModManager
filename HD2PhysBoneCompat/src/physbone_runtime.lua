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

-- 2.0.0：**多人局**。以前整条调度只认"一个玩家 avatar"，多出来的候选一律当成尸体；
--        于是队友一出现就被判成"玩家换人了"，会话全丢、重新挂到队友身上，自己身上反而没物理。
--        现在按玩家分组：每个活动玩家各自持有自己的会话，谁死了只收谁。
--        附：`World.units_by_resource` 直接列出所有玩家 avatar（一次调用，替代扫 2290 个 unit）；
--        `World.debug_camera_pose` 给出相机位置，用来排序（最近的那个就是自己）；
--        链数超预算时非首位玩家按 2 帧一次驱动（dt 累积补偿，不丢时间）。
--
-- 1.9.1：容器扫描窗口改成"按实际部署的最大 patch 序号 + 5"（以前写死 0..63，
--        装一堆 mod 时排在 63 之后的物理 mod 会被漏掉，日志还误报成"没做 embed"）。
M.VERSION = "2.0.0"

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

--- 造一个 logger。返回 `log, context`。
--- <para>
--- 两个刻意的选择：
---   ① **追加**而不是覆盖。覆盖式会把上一次启动的证据抹掉，于是"这次和上次哪里不一样"
---      永远无从对比——2026-11-01 排查就卡在这里（日志只剩最后 30 秒）。
---   ② 每行带**时间戳 + 帧号**。没有帧号，跨会话的"30 帧后全灭"这种时序规律看不出来。
--- 帧号由 `drive` 每帧写进 `context.frame`：compat 会直接调 `session.logger`，
--- 只能靠这个共享表把上下文带进去。
--- </para>
local function makeLogger(options)
	local handle = nil
	local path = defaultLogPath(options)
	if path then
		local ok, h = pcall(io.open, path, "a")
		if ok then handle = h end
	end
	local debugEnabled = options ~= nil and options.debug == true
	local context = { frame = 0 }
	local function emit(text)
		if handle ~= nil then
			pcall(function() handle:write(text, "\n") handle:flush() end)
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
	local stride = chainTotal > budget and (options.otherPlayerStride or 2) or 1
	if stride ~= runtime.driveStride then
		runtime.driveStride = stride
		if stride > 1 then
			logger("[physbone][预算] 链数 %d 超过每帧预算 %d ⇒ 非首位玩家改为每 %d 帧驱动一次（dt 累积补偿，速度不变）",
				chainTotal, budget, stride)
		else
			logger("[physbone][预算] 链数 %d 回到每帧预算 %d 以内 ⇒ 所有玩家恢复每帧驱动", chainTotal, budget)
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
	local heartbeat = options.heartbeatFrames or 120
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

return M
