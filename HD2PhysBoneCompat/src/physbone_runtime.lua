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

M.VERSION = "1.9.0"

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

--- 一趟扫完：玩家 avatar + 候选护甲件（存活、节点数够、无动画状态机），按"离 avatar 距离"排序。
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

	cache.avatarCandidates = 0
	local avatar, withMg, withoutMg = nil, {}, {}
	-- 优先用"上一次认定的玩家"：阵亡后尸体可能也满足 avatar 判据，
	-- 如果这里只取"第一个"，很容易一直选中尸体（于是新玩家永远接不上物理）。
	local prefer = cache.preferAvatar
	local preferFound = false
	for index = 1, count do
		local unit = units[index]
		if Compat.unit_alive(S_, unit) then
			local nodes = try(S_.Unit.num_scene_graph_items, unit)
			if type(nodes) == "number" and nodes >= minNodes then
				if try(S_.Unit.has_animation_state_machine, unit) == true then
					-- 玩家 avatar：有动画状态机 **且** 带 support_mg 挂点。
					if try(S_.Unit.has_node, unit, supportMg) == true then
						-- 记一下**有几个**候选：阵亡后如果尸体仍保有动画状态机，这里会变成 2 个。
						-- 这条数字是"换人检测"最需要的事实，没有它只能靠猜。
						cache.avatarCandidates = (cache.avatarCandidates or 0) + 1
						if prefer ~= nil and rawequal(unit, prefer) then
							avatar, preferFound = unit, true
						elseif avatar == nil and not preferFound then
							avatar = unit
						end
					end
				elseif try(S_.Unit.has_node, unit, supportMg) == true then
					if #withMg < maxUnits then
						withMg[#withMg + 1] = { unit = unit, nodes = nodes, mg = true }
					end
				elseif #withoutMg < maxUnits then
					withoutMg[#withoutMg + 1] = { unit = unit, nodes = nodes, mg = false }
				end
			end
		end
	end
	if avatar == nil then
		cache.worldIndex = nil   -- 可能选错了世界，下一拍重新挑
		return nil, string.format("没找到玩家 avatar（%d 个 unit 里没有同时满足动画状态机 + support_mg 的）", count)
	end
	local armorList = withMg
	for _, candidate in ipairs(withoutMg) do
		if #armorList >= maxUnits then break end
		armorList[#armorList + 1] = candidate
	end
	if #armorList == 0 then
		return nil, string.format("没有候选护甲件（%d 个 unit，节点数门槛 %d）", count, minNodes)
	end

	-- 玩家自己那套护甲挂在 avatar 骨架上，位置几乎重合；人台/队友都在别处。
	-- 这一步决定"谁是主件"、以及被上限截断时谁留下。
	local ax, ay, az = readVector(S_, try(S_.Unit.world_position, avatar, 1))
	if ax ~= nil then
		for _, candidate in ipairs(armorList) do
			local ux, uy, uz = readVector(S_, try(S_.Unit.world_position, candidate.unit, 1))
			if ux ~= nil then
				local dx, dy, dz = ux - ax, uy - ay, uz - az
				candidate.distance = dx * dx + dy * dy + dz * dz
			else
				candidate.distance = math.huge
			end
		end
		table.sort(armorList, function(a, b)
			if a.mg ~= b.mg then return a.mg end
			return (a.distance or math.huge) < (b.distance or math.huge)
		end)
	end
	return world, units, avatar, armorList
end

--- 从部署后的 patch 尾部读物理容器。返回 { { rig = ..., path = ... }, ... }。
--- 路径是"猜"的（运行时不知道 data 的绝对路径），所以多前缀都试一遍，成功后按相对名去重。
local function loadCarriers(S_, cache, options, logger)
	local archive = options.archiveId or "9ba626afa44a3aa3"
	local maxIndex = options.maxPatchIndex or 63
	local carriers = {}
	local seen = {}
	for _, prefix in ipairs({ "data/", "data\\", "./data/", "../data/", "..\\data\\" }) do
		for index = 0, maxIndex do
			local relative = archive .. ".patch_" .. index .. ".gpu_resources"
			if seen[relative] == nil then
				local handle = io.open(prefix .. relative, "rb")
				if handle ~= nil then
					handle:close()
					seen[relative] = true
					local rig = Compat.load_rig({ prefix .. relative })
					if rig ~= nil and type(rig.bones) == "table" and #rig.bones > 0 then
						carriers[#carriers + 1] = { rig = rig, path = prefix .. relative }
					end
				end
			end
		end
	end
	if #carriers > 0 then
		logger("[physbone] 读到 %d 份物理容器", #carriers)
	else
		logger("[physbone] 没找到任何物理容器（来源模型可能没做 embed）")
	end
	return carriers
end

-- ---------------------------------------------------------------------------
-- 挂载 / 卸载
-- ---------------------------------------------------------------------------

--- 就绪采样：等场景稳定（unit 总数连续两拍相同 + 主件节点数不变）后再 attach。
local function tryAttach(runtime)
	local options, cache = runtime.options, runtime.cache
	local frame = runtime.frame
	local world, units, avatar, armorList, reason = discover(S, cache, options, runtime.logger)
	if world == nil or armorList == nil then
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
	-- 只挂**离玩家近**的护甲件。为什么必须过滤：阵亡后尸体留在原地、unit 一直活着，
	-- 而复活点常在别处 ⇒ 不过滤的话会把尸体也一起挂上（既浪费，也让"给尸体关物理"落空）。
	-- 距离读不到（avatar 位置拿不到）时不过滤，行为与以前一致。
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
		runtime.logger("[physbone] 跳过 %d 个离玩家超过 %.1f m 的护甲件（尸体/人台）", farCount, maxDist)
	end
	-- 给候选打分是这里最贵的一步（每份容器 × 每根骨一次 has_node，一件 ~460 次调用）。
	-- 候选多时（军械库里几十个人台）不设上限就是几万次调用 ⇒ 明显卡顿。
	-- 只测最近的一批：真正属于玩家的护甲一定是最近的那几件。
	local maxScored = options.maxScoredCandidates or 12
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
		if (a.unit == armor.unit) ~= (b.unit == armor.unit) then return a.unit == armor.unit end
		if a.mg ~= b.mg then return a.mg end
		return false
	end)

	local attachOptions = {
		logger = runtime.logger,
		world = world,
		strictIdentity = options.strictIdentity == true,
		fastWrite = options.fastWrite ~= false,
	}
	local sessions, tags, emptyUnits = {}, {}, 0
	for index = 1, #plan do
		local entry = plan[index]
		if Compat.unit_alive(S, entry.unit) then
			local session = Compat.attach(S, entry.unit, entry.rig, attachOptions)
			session.rigSource = entry.path
			session.unitTag = entry.tag
			session.attachFrame = frame
			if #session.sessions > 0 then
				sessions[#sessions + 1] = session
				tags[#tags + 1] = entry.tag
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
	if #sessions == 0 then
		note_wait(runtime, "attach 没有产出任何链", string.format("%d 个 unit 通过容器匹配、%d 个产出 0 条链", #plan, emptyUnits))
		return false
	end

	local chains = 0
	for _, session in ipairs(sessions) do chains = chains + #session.sessions end
	runtime.sessions = sessions
	-- 记住"当时谁是玩家"。⚠️ 判"换没换人"要比 **userdata 同一性**，不是 `debug_name`：
	-- 实测丢掉 7 个 unit 后重新挂上的还是同一批 `#ID[...]`，说明那个 id 是按资源算的。
	runtime.avatar = avatar
	runtime.avatarTag = unit_tag(S, avatar)
	cache.preferAvatar = avatar
	runtime.attachedFrame = frame
	runtime.reArm = 0
	runtime.rearmAt = nil
	runtime.waitReason, runtime.waitRepeat = nil, nil
	runtime.zeroFrames = 0
	runtime.driveFrames = 0
	runtime.callsAccum = 0
	runtime.peakAngle = 0
	runtime.repeatCalls = 0
	runtime.logger("[physbone] 已挂载 %d 个 unit / %d 条链（%d 份容器）unit=%s",
		#sessions, chains, #carriers, table.concat(tags, ","))
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

--- 便宜的 avatar 扫描：只做两项引擎调用，让"换人检测"能在驱动中定期跑。
--- 返回候选列表；`nil` 表示"这次扫不了"（世界/单位列表取不到）。
---
--- 为什么不用 `discover`：那一趟还要算节点数、算到玩家的距离、给每个候选打分排序，
--- 一次约 7 千次引擎调用 —— 每 1~2 秒来一趟就是肉眼可见的顿卡（2026-11-01 实测掉到 60 fps）。
--- 判"换人"只需要"场上有哪些 unit 同时有动画状态机 + support_mg"，两项调用就够。
local function scanAvatars(runtime)
	local world, units = pickWorld(S, runtime.cache)
	if world == nil or units == nil then return nil end
	local supportMg = id32(S, "support_mg")
	if supportMg == nil then return nil end
	local found = {}
	for index = 1, #units do
		local unit = units[index]
		if Compat.unit_alive(S, unit)
			and try(S.Unit.has_animation_state_machine, unit) == true
			and try(S.Unit.has_node, unit, supportMg) == true then
			found[#found + 1] = unit
		end
	end
	return found
end

local function isIgnored(runtime, unit)
	local list = runtime.ignoredAvatars
	if list == nil then return false end
	for index = 1, #list do
		if rawequal(list[index], unit) then return true end
	end
	return false
end

local function ignoreAvatar(runtime, unit)
	if unit == nil then return end
	local list = runtime.ignoredAvatars
	if list == nil then list = {}; runtime.ignoredAvatars = list end
	if isIgnored(runtime, unit) then return end
	list[#list + 1] = unit
end

--- 玩家换人了吗（阵亡 → 尸体留在原地 → 新实体出现）。
---
--- 为什么必须单独做这件事：阵亡时**旧实体不会被销毁**（尸体还活着），
--- `pruneSessions` 只会在 `Unit.alive == false` 或节点数变化时才丢会话，
--- 于是我们一直驱动着尸体，而新玩家的护甲 unit 从来没被挂过 —— "复活之后没有物理"。
---
--- ⚠️ 判据用 **userdata 同一性**（`rawequal`），**不是** `debug_name`。
--- 2026-11-01 实测：丢掉 7 个 unit 之后重新挂上的还是**同一批 `#ID[...]`**，
--- 说明那个 id 是按资源/描述符算的，不是按实例 —— 拿它比"是不是同一个人"永远为真，
--- 于是换人检测一次都没触发（日志里 `[换人]` 计数为 0）。userdata 才是实例。
---
--- 尸体若仍保有动画状态机，场上会有 2 个候选；所以用"忽略表"记住已经判定为尸体的 avatar，
--- 只认**没被忽略的新候选**，避免每 1.2 秒都判一次换人造成反复重挂。
---@return boolean changed 玩家换了（调用方应当释放并重挂）
local function playerChanged(runtime)
	local avatars = scanAvatars(runtime)
	if avatars == nil then return false end
	-- 清掉已经销毁的忽略项，免得表无限增长
	local kept = {}
	for _, unit in ipairs(runtime.ignoredAvatars or {}) do
		if Compat.unit_alive(S, unit) then kept[#kept + 1] = unit end
	end
	runtime.ignoredAvatars = kept

	-- 优先挑"没被忽略、且不是当前这个"的候选：那就是新出现的玩家实体。
	for _, unit in ipairs(avatars) do
		if not rawequal(unit, runtime.avatar) and not isIgnored(runtime, unit) then
			runtime.logger("[physbone][换人] 出现新的玩家实体（候选 %d 个，旧=%s，新=%s）⇒ 释放旧会话后重挂",
				#avatars, tostring(runtime.avatarTag), tostring(unit_tag(S, unit)))
			ignoreAvatar(runtime, runtime.avatar)
			runtime.avatar = unit
			runtime.avatarTag = unit_tag(S, unit)
			-- 让下一次 discover 优先认它：阵亡后尸体可能也满足 avatar 判据，
			-- 只取"第一个"很容易一直选中尸体。
			runtime.cache.preferAvatar = unit
			return true
		end
	end

	-- 没有新候选：确认我们那个还活着就行。
	if runtime.avatar ~= nil and Compat.unit_alive(S, runtime.avatar) then return false end

	-- 我们的 avatar 没了，且场上没有别的候选：等下一拍（可能正在复活加载）。
	runtime.avatar, runtime.avatarTag = nil, nil
	return false
end

--- 释放并丢弃全部会话。
--- 对**还活着**的 unit（尸体）要把姿态交还成基准姿态：不交还的话头发会停在最后写入的
--- 那一帧（半空中的姿势），看着像被冻住；交还之后回到静止形状，才像一具正常尸体。
local function dropAllSessions(runtime, why)
	for _, session in ipairs(runtime.sessions or {}) do
		if Compat.unit_alive(S, session.unit) then
			local ok, restored = pcall(Compat.release, session)
			runtime.logger("[physbone][换人] 释放 unit=%s（%s）%s",
				tostring(session.unitTag or "?"), tostring(why),
				ok and ("已交还 " .. tostring(restored) .. " 节基准姿态") or "交还失败")
		end
	end
	runtime.sessions = nil
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
		if dropped > 0 or #runtime.sessions == 0 then
			-- 场景变了：退回等待，带退避（避免换甲期间每秒重挂一次）。
			-- 存活时长决定退避：刚挂上就死 = 抖动（换甲中），要越等越久；
			-- 稳定跑过 5 秒再死 = 正常换场景，下一次应当**立刻**重挂。
			local survived = frame - (runtime.attachedFrame or frame)
			runtime.sessions = nil
			if survived >= (options.stableFrames or 600) then
				runtime.reArm = 1
			else
				runtime.reArm = (runtime.reArm or 0) + 1
			end
			local delay = math.min(options.maxReArmFrames or 600, (options.reArmFrames or 60) * runtime.reArm)
			runtime.rearmAt = frame + delay
			logger("[physbone][LIFECYCLE] 场景已变（存活 %d 帧）⇒ 退避 %d 帧后重扫", survived, delay)
		else
			-- 会话全都还"健康"，但玩家可能已经换人了：阵亡后尸体会一直活着，
			-- `pruneSessions` 判不出来（它只看 Unit.alive / 节点数）。所以定期问一次"谁是玩家"。
			local validateFrames = options.validateFrames == nil and 120 or options.validateFrames
			if validateFrames > 0 and (frame % validateFrames) == 0 and playerChanged(runtime) then
				-- 尸体交还基准姿态（否则头发停在最后写入的姿势，像被冻住），然后重新挂新玩家。
				dropAllSessions(runtime, "玩家已换人")
				runtime.reArm = 1                     -- 换人不是抖动，下一次要尽快重挂
				runtime.rearmAt = frame + (options.respawnReArmFrames or 30)
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
	local written, skippedSame, engineCalls, staticChains = 0, 0, 0, 0
	local dt = runtime.dt or (1 / 60)
	for index = 1, #runtime.sessions do
		local session = runtime.sessions[index]
		-- compat 只在正常返回时刷新这两个计数；先清零，免得沿用上一帧的值把看门狗骗过去。
		session.skippedSame, session.engineCalls = 0, 0
		local ok, out = pcall(Compat.update, session, dt)
		if ok then
			if type(out) == "number" then written = written + out end
		elseif not session.updateErrorLogged then
			-- 以前这里是裸 pcall：一条 unit 每帧都抛错，日志里却什么也没有。
			session.updateErrorLogged = true
			logger("[physbone][错误] unit=%s 驱动抛错：%s", tostring(session.unitTag or "?"), tostring(out))
		end
		skippedSame = skippedSame + (session.skippedSame or 0)
		engineCalls = engineCalls + (session.engineCalls or 0)
		for _, item in ipairs(session.sessions) do
			if (item.staticFrames or 0) > 0 then staticChains = staticChains + 1 end
		end
	end
	runtime.driveFrames = (runtime.driveFrames or 0) + 1
	runtime.callsAccum = (runtime.callsAccum or 0) + engineCalls
	runtime.staticChains = staticChains

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
		logger("[physbone] 驱动心跳 unit=%d 链=%d dt=%.4f 本帧 写入=%d 跳过=%d 引擎调用=%d 累计/帧=%.0f 静止链=%d 偏离绑定峰值=%.1f°(史上%.1f°) 最离谱=%s(限%.0f°) 同拍重复=%d",
			#runtime.sessions, chains, runtime.dt or 0, written, skippedSame, engineCalls,
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
	local handled = written + skippedSame
	if handled == 0 then
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
