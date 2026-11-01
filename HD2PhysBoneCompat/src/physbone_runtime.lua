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

local M = {}

M.VERSION = "1.0.0"

--- 安装标记用的全局键。只写这一个全局，且值就是模块本身（便于外部检查是否已装）。
local INSTALL_KEY = "__hd2physbone_runtime"

local Compat          -- physbone_compat 模块（require 或注入）
local S               -- stingray 命名空间
local state = nil     -- 运行状态；nil = 未安装
local originalRender  -- 挂载前的 render（链式调用，不吞掉别人）

-- ---------------------------------------------------------------------------
-- 小工具
-- ---------------------------------------------------------------------------

local function noop() end

local function makeLogger(options)
	local handle = nil
	-- 默认就写日志：生产环境下"什么都没发生"和"根本没加载"从外部无法区分，
	-- 而这正是 2026-11-01 那次"物理完全没生效、日志一个字都没有"的排查代价。
	-- 只想彻底静默时传 `options.log = false`。
	local path = options and options.logPath
	if path == nil and not (options and options.log == false) then
		local base = os.getenv("LOCALAPPDATA")
		if type(base) == "string" and #base > 0 then path = base .. "\\hd2physbone.log" end
	end
	if path then
		local ok, h = pcall(io.open, path, "w")
		if ok then handle = h end
	end
	local debugEnabled = options ~= nil and options.debug == true
	return function(fmt, ...)
		if handle == nil and not debugEnabled then return end
		local text = select("#", ...) > 0 and string.format(fmt, ...) or fmt
		if handle ~= nil then
			pcall(function() handle:write(text, "\n") handle:flush() end)
		end
		if debugEnabled then
			local real = rawget(_G, "print")
			if type(real) == "function" then pcall(real, text) end
		end
	end
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
--- 返回 nil 表示"还没准备好"（场景在变 / 找不到目标）。
local function discover(S_, cache, options, logger)
	local world, units = pickWorld(S_, cache)
	if world == nil or units == nil then return nil end
	local count = #units
	local previous = cache.lastUnitCount
	cache.lastUnitCount = count
	-- 场景还在加载/切换（unit 总数在变）⇒ 不做全量扫描，下一拍再来。
	if previous ~= count then return nil end

	local supportMg = id32(S_, "support_mg")
	if supportMg == nil then return nil end
	local minNodes = options.minNodes or 90
	local maxUnits = options.maxUnits or 64

	local avatar, withMg, withoutMg = nil, {}, {}
	for index = 1, count do
		local unit = units[index]
		if Compat.unit_alive(S_, unit) then
			local nodes = try(S_.Unit.num_scene_graph_items, unit)
			if type(nodes) == "number" and nodes >= minNodes then
				if try(S_.Unit.has_animation_state_machine, unit) == true then
					-- 玩家 avatar：有动画状态机 **且** 带 support_mg 挂点。
					if avatar == nil and try(S_.Unit.has_node, unit, supportMg) == true then
						avatar = unit
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
		return nil
	end
	local armorList = withMg
	for _, candidate in ipairs(withoutMg) do
		if #armorList >= maxUnits then break end
		armorList[#armorList + 1] = candidate
	end
	if #armorList == 0 then return nil end

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

--- 就绪采样：等场景稳定（unit 总数连续两拍相同 + 主件位置不动）后再 attach。
local function tryAttach(runtime)
	local options, cache = runtime.options, runtime.cache
	local frame = runtime.frame
	local world, units, avatar, armorList = discover(S, cache, options, runtime.logger)
	if world == nil or armorList == nil then return false end
	local armor = armorList[1]

	local nodeCount = try(S.Unit.num_scene_graph_items, armor.unit)
	if type(nodeCount) ~= "number" or nodeCount < 10 then return false end
	local sample = readVector(S, try(S.Unit.world_position, armor.unit, 1))
	if sample == nil then return false end

	-- 两次采样一致 ⇒ 加载/传送/换装已经结束。间隔太短容易在动画还没落位时就 attach。
	-- ⚠️ 采样一旦记下就**不要每帧覆盖**，否则 (frame - previous.frame) 永远是 1，永远等不到。
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
	local plan = {}
	for _, candidate in ipairs(armorList) do
		local best = Compat.select_rig(S, { candidate.unit }, carriers)
		if best ~= nil and (best.full or allowPartial) then
			plan[#plan + 1] = { unit = candidate.unit, rig = best.rig, path = best.path, mg = candidate.mg }
		end
	end
	if #plan == 0 then return false end
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
	local sessions = {}
	for index = 1, #plan do
		local entry = plan[index]
		if Compat.unit_alive(S, entry.unit) then
			local session = Compat.attach(S, entry.unit, entry.rig, attachOptions)
			session.rigSource = entry.path
			if #session.sessions > 0 then sessions[#sessions + 1] = session end
		end
	end
	if #sessions == 0 then return false end

	local chains = 0
	for _, session in ipairs(sessions) do chains = chains + #session.sessions end
	runtime.sessions = sessions
	runtime.attachedFrame = frame
	runtime.reArm = 0
	runtime.rearmAt = nil
	runtime.logger("[physbone] 已挂载 %d 个 unit / %d 条链（%d 份容器）", #sessions, chains, #carriers)
	return true
end

--- 丢掉失效会话。任何一个失效都意味着场景变了 ⇒ 必须重扫（不能"还剩几个就继续开"）。
local function pruneSessions(runtime)
	local kept, dropped = {}, 0
	for _, session in ipairs(runtime.sessions or {}) do
		local health = Compat.session_health(session, runtime.frame)
		if health == "ok" then
			kept[#kept + 1] = session
		else
			dropped = dropped + 1
		end
	end
	runtime.sessions = kept
	if dropped > 0 then
		runtime.logger("[physbone] %d 个会话失效，准备重扫（剩 %d）", dropped, #kept)
	end
	return dropped
end

local function drive(runtime)
	local frame = runtime.frame
	Compat.begin_frame(frame)
	local options = runtime.options

	if runtime.sessions ~= nil and #runtime.sessions > 0 then
		local dropped = pruneSessions(runtime)
		if dropped > 0 or #runtime.sessions == 0 then
			-- 场景变了：退回等待，带退避（避免换甲期间每秒重挂一次）。
			runtime.sessions = nil
			runtime.reArm = (runtime.reArm or 0) + 1
			runtime.rearmAt = frame + math.min(options.maxReArmFrames or 600, (options.reArmFrames or 60) * runtime.reArm)
		end
	end

	if runtime.sessions == nil then
		if runtime.rearmAt ~= nil and frame < runtime.rearmAt then return end
		if (frame % (options.pollFrames or 20)) ~= 0 then return end
		if not tryAttach(runtime) then return end
	end

	for index = 1, #runtime.sessions do
		pcall(Compat.update, runtime.sessions[index], runtime.dt or (1 / 60))
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

	local logger = makeLogger(options)
	state = {
		options = options,
		logger = logger,
		cache = {},
		carriers = nil,
		sessions = nil,
		frame = 0,
		dt = options.dt or (1 / 60),
		reArm = 0,
		rearmAt = nil,
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

	logger("[physbone] 运行时 %s 已安装（%s）", M.VERSION,
		options.mode == "boot" and "独占 boot" or "外置模块")
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
