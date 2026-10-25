-- 护甲外观映射运行时（资源覆盖版）。
--
-- 它替换掉旧的 ffi 内存修补运行时：不再扫描内存、不再改 unit TOC，只调用引擎公开的
-- resource override API。旧运行时在 26088801 上加载时就要 ffi 模块，而该模块在这版引擎里
-- 已经不存在，于是它一进来就退出；这条链路完全不依赖它。
--
-- 契约（2026-10-23 实测确定，见 notes/migration-preflight-two-answers-20261023.md）：
--
--   新增映射   热生效（set_resource_override）
--   修改映射   热生效（同一个源改指向新目标；后写覆盖前写）
--   删除映射   需要重启游戏
--   整体关闭   需要重启游戏
--
-- 为什么删除不能热做：通过覆盖创建出来的实例无法在进程内被取消覆盖。释放表项会让这些
-- 实例悬空，改回自己则让它们在军械库重建时半新半旧——两种都在几秒后崩溃，且不产生
-- 转储。所以这里从不释放任何表项，只记录"需要重启"。
--
-- 协议文件：<ArmorRegistry>/wardrobe.links，由管理器原子替换。
local noop = function() end
init = noop
shutdown = noop
render = noop

local sr = stingray
local App, Id64 = sr.Application, sr.IdString64

local POLL_SECONDS = 1
local MAX_BYTES = 4 * 1024 * 1024
-- 一次写 2000 条会拖住一帧，所以按帧摊开。
local SET_BUDGET_PER_TICK = 32
local PROTOCOL_NAME = 'wardrobe.links'

local function base_directory()
	local loader = rawget(_G, 'CowboyBingusModLoader')
	if type(loader) == 'table' and type(loader.log_directory) == 'string' then
		return loader.log_directory:gsub('[/\\]Logs$', '')
	end
	local local_app_data = os.getenv('LOCALAPPDATA')
	if type(local_app_data) == 'string' and local_app_data ~= '' then
		return local_app_data .. '/CowboyBingus/Helldivers2'
	end
	return nil
end

local base = base_directory()
local protocol_path = base and (base .. '/ArmorRegistry/' .. PROTOCOL_NAME) or nil
local log_path = base and (base .. '/Logs/ArmorOverrideRuntime.log') or nil

local function log(text)
	if log_path == nil or type(io) ~= 'table' or not io.open then return end
	local ok, file = pcall(io.open, log_path, 'ab')
	if ok and file then
		pcall(file.write, file, tostring(text) .. '\n')
		pcall(file.close, file)
	end
end

local function read()
	if protocol_path == nil then return nil, 'no ArmorRegistry directory' end
	local ok, file = pcall(io.open, protocol_path, 'rb')
	if not ok or not file then return nil, 'open failed' end
	local text = file:read(MAX_BYTES + 1)
	file:close()
	if type(text) ~= 'string' then return nil, 'read failed' end
	if #text > MAX_BYTES then return nil, 'too large' end
	return text
end

-- 只解析运行时需要的键。任何格式错误都视为"这一版无效"，保留上一版，绝不半应用。
local function parse(text)
	local result = { revision = nil, enabled = true, links = {} }
	local seen = {}
	for line in text:gmatch('[^\r\n]+') do
		local trimmed = line:gsub('^%s+', ''):gsub('%s+$', '')
		if trimmed ~= '' and trimmed:sub(1, 1) ~= '#' then
			local key, value = trimmed:match('^([%w_]+)%s*=%s*(.-)$')
			if key == 'schema' then
				if tonumber(value) ~= 3 then return nil, 'unsupported schema ' .. tostring(value) end
			elseif key == 'revision' then
				result.revision = tonumber(value)
				if result.revision == nil then return nil, 'bad revision' end
			elseif key == 'enabled' then
				if value ~= 'true' and value ~= 'false' then return nil, 'bad enabled' end
				result.enabled = value == 'true'
			elseif key == 'link' then
				local source, target = value:match('^([0-9a-fA-F]+),([0-9a-fA-F]+)$')
				if source == nil or #source ~= 16 or #target ~= 16 then
					return nil, 'bad link: ' .. trimmed
				end
				local key_text = source:lower()
				if seen[key_text] == nil then
					seen[key_text] = true
					result.links[#result.links + 1] = { source = key_text, target = target:lower() }
				end
			end
			-- map= 是管理器的意图记录，运行时不需要。
		end
	end
	if result.revision == nil then return nil, 'revision missing' end
	return result
end

local applied = {}          -- source -> target 当前已注册
local appliedCount = 0
local queue = {}
local queueCursor = 1
local setTotal = 0
local failedTotal = 0
local lastText = nil
local nextPoll = 0
local lastRemovalCount = 0

local function enqueue(document)
	queue = {}
	queueCursor = 1
	setTotal = 0
	failedTotal = 0
	local wanted = {}
	for _, link in ipairs(document.links) do
		wanted[link.source] = link.target
		if applied[link.source] ~= link.target then
			queue[#queue + 1] = { source = link.source, target = link.target }
		end
	end
	local removals = 0
	for source in pairs(applied) do
		if wanted[source] == nil then removals = removals + 1 end
	end
	lastRemovalCount = removals
	if #queue > 0 then log(string.format('queued %d sets', #queue)) end
end

local function drain()
	if queueCursor > #queue then return end
	local budget = SET_BUDGET_PER_TICK
	while budget > 0 and queueCursor <= #queue do
		local item = queue[queueCursor]
		queueCursor = queueCursor + 1
		budget = budget - 1
		local okSource, source = pcall(Id64.from_hex, item.source)
		local okTarget, target = pcall(Id64.from_hex, item.target)
		if okSource and okTarget and pcall(App.set_resource_override, 'unit', source, target) then
			if applied[item.source] == nil then appliedCount = appliedCount + 1 end
			applied[item.source] = item.target
			setTotal = setTotal + 1
		else
			-- 引擎拒绝写入通常是覆盖表满了：这个计数能把它和"映射本身有问题"分开。
			failedTotal = failedTotal + 1
		end
	end
	if queueCursor > #queue then
		log(string.format('queue drained (%d sets, %d rejected), total overrides=%d (open the armoury to see it)',
			setTotal, failedTotal, appliedCount))
		queue = {}
		queueCursor = 1
	end
end

local function poll()
	local text, error_text = read()
	if text == nil then
		log('read failed: ' .. tostring(error_text))
		return
	end
	if text == lastText then return end
	local document, parse_error = parse(text)
	if document == nil then
		log('parse failed, keeping previous revision: ' .. tostring(parse_error))
		lastText = text
		return
	end
	lastText = text
	if not document.enabled then
		log(string.format('revision=%s disabled: %d overrides stay active until the game restarts',
			tostring(document.revision), appliedCount))
		return
	end
	enqueue(document)
	log(string.format('revision=%s links=%d (diff queued)', tostring(document.revision), #document.links))
	if lastRemovalCount > 0 then
		log(string.format('  %d mappings were removed from the file; they stay active until the game restarts',
			lastRemovalCount))
	end
end

log('runtime loaded; protocol=' .. tostring(protocol_path))
update = function()
	local okDrain, drainError = pcall(drain)
	if not okDrain then log('drain error: ' .. tostring(drainError)) end
	local now = os.time()
	if now < nextPoll then return end
	nextPoll = now + POLL_SECONDS
	local ok, err = pcall(poll)
	if not ok then log('poll error: ' .. tostring(err)) end
end
