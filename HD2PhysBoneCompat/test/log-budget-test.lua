-- 日志闸门自检（physbone_runtime 的 makeLogger）
--
-- 为什么这值得单测：日志是这个项目唯一的事后证据来源，而它同时是最容易反噬的东西——
--   * 只追加不轮转 ⇒ 文件随每次启动长大，几百 MB 之后连打开都费劲；
--   * 心跳这类周期行不设上限 ⇒ 玩多久就写多久，一场长局就是上百 KB 无用行；
--   * 一刀切"写满就全停" ⇒ 真出事的那一行恰好被吞掉，日志留着反而误人。
-- 三条判据都只能看**落盘结果**，所以这里直接造 logger 往临时文件里写。
--
-- 用法：luajit HD2PhysBoneCompat/test/log-budget-test.lua [runtime]

local runtimePath = arg[1] or "HD2PhysBoneCompat/src/physbone_runtime.lua"
local Runtime = assert(assert(loadfile(runtimePath))())
local makeLogger = assert(Runtime.make_logger_, "运行时缺少 make_logger_ 测试钩子")

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-52s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

local TEMP = os.getenv("TEMP") or os.getenv("TMP") or "."
local stamp = tostring(os.time())
local serial = 0
local made = {}

local function tempPath(tag)
	serial = serial + 1
	local path = string.format("%s\\physbone-log-test-%s-%d-%s.log", TEMP, stamp, serial, tag)
	made[#made + 1] = path
	os.remove(path)
	os.remove(path .. ".1")
	return path
end

local function readAll(path)
	local handle = io.open(path, "rb")
	if handle == nil then return nil end
	local text = handle:read("*a") or ""
	handle:close()
	return text
end

local function sizeOf(path)
	local handle = io.open(path, "rb")
	if handle == nil then return nil end
	local size = handle:seek("end") or 0
	handle:close()
	return size
end

local function countOf(text, needle)
	local count, from = 0, 1
	while true do
		local found = string.find(text, needle, from, true)
		if found == nil then return count end
		count = count + 1
		from = found + #needle
	end
end

-- ---------------------------------------------------------------------------
-- ① 预算之内的行一条不少，超出的只丢常规行
-- ---------------------------------------------------------------------------
do
	local path = tempPath("budget")
	local log, context = makeLogger({ logPath = path, logMaxBytes = 600 })
	for _ = 1, 200 do
		context.frame = context.frame + 1
		log("[physbone] 驱动心跳 unit=1 链=4 玩家=1 本帧驱动=4 引擎调用=8")
	end
	local text = readAll(path) or ""
	check("写满预算就收手（不再无限增长）", #text <= 600 + 400, "落盘 " .. #text .. " 字节")
	check("落了一行「已达上限」", string.find(text, "日志已达上限", 1, true) ~= nil)
	-- 心跳行是 ~60 字节一行，600 字节预算之内本该有十来行：既不是一条没写，也不是全写。
	local beats = countOf(text, "驱动心跳")
	check("预算之内的心跳照写", beats >= 2 and beats <= 20, "心跳行 " .. beats .. " 条")

	-- 出事的行永远不能因为省字节被吞掉。
	local before = #text
	log("[physbone][错误] unit=#ID[x] 驱动抛错：%s", "boom")
	text = readAll(path) or ""
	check("预算用尽后错误行仍落盘", #text > before and string.find(text, "boom", 1, true) ~= nil)
	before = #text
	log("[physbone][警告] 引擎时钟连续 %d 次没有前进", 8)
	text = readAll(path) or ""
	check("预算用尽后警告行仍落盘", #text > before)
	before = #text
	log("[physbone] 驱动心跳 unit=1 链=4 玩家=1 本帧驱动=4 引擎调用=8")
	text = readAll(path) or ""
	check("预算用尽后常规行确实被丢掉", #text == before)
end

-- ---------------------------------------------------------------------------
-- ② 没给上限时不该提前刹车（默认预算远大于这次写的量）
-- ---------------------------------------------------------------------------
do
	local path = tempPath("default")
	local log = makeLogger({ logPath = path })
	for index = 1, 5 do log("[physbone] 第 %d 行", index) end
	local text = readAll(path) or ""
	check("默认预算下 5 行全落盘", countOf(text, "[physbone] 第") == 5, "落盘 " .. #text .. " 字节")
	check("默认预算下不写「已达上限」", string.find(text, "日志已达上限", 1, true) == nil)
	check("每行带时间戳与帧号", string.find(text, " f0 ", 1, true) ~= nil)
end

-- ---------------------------------------------------------------------------
-- ③ 跨会话轮转：旧文件超阈值就挪成 .1，本次从空文件起
-- ---------------------------------------------------------------------------
do
	local path = tempPath("rotate")
	local seed = io.open(path, "wb")
	seed:write(string.rep("旧", 400))   -- 800 字节
	seed:close()
	local before = sizeOf(path)
	local log = makeLogger({ logPath = path, logRotateBytes = 300 })
	log("[physbone] 轮转之后的第一行")
	local rotated = sizeOf(path .. ".1")
	local current = readAll(path) or ""
	check("旧文件被轮转成 .1", rotated == before, "之前 " .. tostring(before) .. " 字节，.1 是 " .. tostring(rotated))
	check("本次日志从空文件起", countOf(current, "轮转之后的第一行") == 1 and #current < 200,
		"现任 " .. #current .. " 字节")
	check("没有触发预算提示", string.find(current, "日志已达上限", 1, true) == nil)
end

-- ---------------------------------------------------------------------------
-- ④ 未超阈值时不轮转（别把上一次的证据无故挤掉）
-- ---------------------------------------------------------------------------
do
	local path = tempPath("keep")
	local seed = io.open(path, "wb")
	seed:write("上一次启动留下的证据")
	seed:close()
	local log = makeLogger({ logPath = path, logRotateBytes = 4096 })
	log("[physbone] 这次启动")
	local text = readAll(path) or ""
	check("未超阈值时不轮转", sizeOf(path .. ".1") == nil)
	check("旧证据与本次共存", string.find(text, "上一次启动留下的证据", 1, true) ~= nil
		and string.find(text, "这次启动", 1, true) ~= nil)
end

-- ---------------------------------------------------------------------------
-- ⑤ log == false 时彻底静默（不是"写到别处"，是一条都不写）
-- ---------------------------------------------------------------------------
do
	local ok, err = pcall(function()
		local log = makeLogger({ log = false })
		for index = 1, 100 do log("[physbone] 静默测试 %d", index) end
	end)
	check("关掉日志时调用不抛错", ok, tostring(err))
end

for _, path in ipairs(made) do
	os.remove(path)
	os.remove(path .. ".1")
end

print(string.format("\n失败 %d 项", failures))
os.exit(failures == 0 and 0 or 1)
