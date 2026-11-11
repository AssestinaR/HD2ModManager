-- 姿态快照自检：请求文件 → 轮询 → 快照文件。
--
-- 为什么必须离线验：这条路只有"用户进游戏、按下热键"才会跑。它要是坏了，现象是
-- **按了键什么都没发生**——那意味着再浪费用户一整轮游戏时间，而且他没法自己判断
-- 是我没写好还是他没按对。所以三段都钉住：请求解析、轮询时序、落盘内容。
--
-- 用法：luajit HD2PhysBoneCompat/test/snapshot-test.lua [runtime]

local runtimePath = arg[1] or "HD2PhysBoneCompat/src/physbone_runtime.lua"
local Runtime = assert(assert(loadfile(runtimePath))())

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-52s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

local parse = assert(Runtime.snapshot_parse_, "缺少 snapshot_parse_ 钩子")
local idsOf = assert(Runtime.snapshot_ids_, "缺少 snapshot_ids_ 钩子")
local poll = assert(Runtime.snapshot_poll_, "缺少 snapshot_poll_ 钩子")

local TEMP = os.getenv("TEMP") or os.getenv("TMP") or "."
local DIR = string.format("%s\\physbone-snapshot-test-%s", TEMP, tostring(os.time()))

local function sh(cmd) os.execute(cmd) end
local function ensureDir()
	sh('mkdir "' .. DIR .. '" 2>nul')
end

local function writeFile(path, text)
	local handle = assert(io.open(path, "wb"))
	handle:write(text)
	handle:close()
end

local function readFile(path)
	local handle = io.open(path, "rb")
	if handle == nil then return nil end
	local text = handle:read("*a") or ""
	handle:close()
	return text
end

local function listSnapshots()
	local names = {}
	local handle = io.popen('dir /b "' .. DIR .. '\\snapshot-*.json" 2>nul')
	if handle ~= nil then
		for line in handle:lines() do names[#names + 1] = line end
		handle:close()
	end
	return names
end

local function removeAll()
	sh('del /q "' .. DIR .. '\\*.json" 2>nul')
	sh('del /q "' .. DIR .. '\\request.txt" 2>nul')
	sh('del /q "' .. DIR .. '\\done.txt" 2>nul')
end

-- ---------------------------------------------------------------- ① 解析请求
do
	local request = parse("# 注释行\nseq = 7\nframes = 4\nstride = 3\nnode = 0x8e8d63a0\nnode=0x1234\nname = hips\n空行下面那行是垃圾\n")
	check("解析出 seq", request ~= nil and request.seq == "7", request and request.seq)
	check("解析出 frames/stride", request ~= nil and request.frames == 4 and request.stride == 3,
		request and (request.frames .. "/" .. request.stride))
	check("解析出两个哈希节点", request ~= nil and #request.nodes == 2,
		request and #request.nodes)
	check("哈希按十六进制读", request ~= nil and request.nodes[1] == 0x8e8d63a0,
		request and string.format("0x%08x", request.nodes[1]))
	check("解析出名字", request ~= nil and #request.names == 1 and request.names[1] == "hips")

	check("空文本返回 nil", parse("") == nil)
	check("没有节点行返回 nil", parse("frames = 8\n") == nil)
	local capped = parse("frames = 9999\nstride = 999\nnode = 1\n")
	check("frames/stride 有上限", capped.frames == 32 and capped.stride == 30,
		capped.frames .. "/" .. capped.stride)
	-- limit：只采前 N 个节点。一次读几百根会读崩游戏，所以必须能缩。
	local limited = parse("limit = 2\nnode = 1\nnode = 2\nnode = 3\n")
	check("limit 解析出来了", limited ~= nil and limited.limit == 2, limited and limited.limit)
	local engine = {
		Application = { string_to_id32 = function(text) return #tostring(text) end },
	}
	local ids = idsOf(engine, limited)
	check("limit 真的截断节点表", #ids == 2, #ids)
end

-- ---------------------------------------------------------------- ② 假引擎
-- 只假到快照用到的那几个 API：Unit.has_node / node / world_position / world_rotation
local POSITIONS = {}
local function fakeEngine()
	local nodes = {}
	return {
		Application = { string_to_id32 = function(text) return #tostring(text) end },
		Vector3 = { to_elements = function(v) return v[1], v[2], v[3] end },
		Quaternion = { to_elements = function(q) return q[1], q[2], q[3], q[4] end },
		Unit = {
			has_node = function(_, id) return POSITIONS[id] ~= nil end,
			node = function(_, id) return POSITIONS[id] ~= nil and id or nil end,
			world_position = function(_, node) return POSITIONS[node] end,
			world_rotation = function() return { 0, 0, 0, 1 } end,
		},
	}
end

local function fakeRuntime(frames, withSession)
	local runtime = {
		frame = frames or 0,
		driveFrames = frames or 0,
		dt = 0.0166,
		sessions = {},
		logger = function() end,
		options = {},
	}
	if withSession then
		runtime.sessions = {
			{
				unit = { tag = "fake" },
				unitTag = "#ID[fake]",
				ownerOrder = 1,
				sessions = {
					{
						chain = { { name = "j1" }, { name = "j2" } },
						springs = { { yaw = 0.1, pitch = -0.2 }, { yaw = 0.3, pitch = 0 } },
						nodes = { {}, {} },
						anchorNode = 1,
					},
				},
			},
		}
	end
	return runtime
end

-- ---------------------------------------------------------------- ③ 端到端
do
	ensureDir()
	removeAll()
	POSITIONS = {
		[0x8e8d63a0] = { 0.1, -0.2, 1.03 },
		[0x4c1bb3d1] = { -0.22, 0.03, 1.13 },
	}
	local S = fakeEngine()
	local options = { snapshotDir = DIR, snapshotPollFrames = 1 }
	local runtime = fakeRuntime(0, true)

	-- 没有请求文件：什么都不该发生
	for _ = 1, 5 do
		runtime.driveFrames = runtime.driveFrames + 1
		runtime.frame = runtime.driveFrames
		poll(S, runtime, options)
	end
	check("没有请求时不写文件", #listSnapshots() == 0, "文件数 " .. #listSnapshots())

	-- 写请求：3 帧、每帧一张
	writeFile(DIR .. "\\request.txt", "seq = 1\nframes = 3\nstride = 1\nnode = 0x8e8d63a0\nnode = 0x4c1bb3d1\n")
	for _ = 1, 8 do
		runtime.driveFrames = runtime.driveFrames + 1
		runtime.frame = runtime.driveFrames
		poll(S, runtime, options)
	end

	local files = listSnapshots()
	-- 每帧单独落盘（崩了前面的还在），所以 3 帧 = 3 个文件
	check("三帧各落一份", #files == 3, "文件数 " .. #files)
	table.sort(files)
	local text = files[1] ~= nil and readFile(DIR .. "\\" .. files[1]) or nil
	check("快照有内容", text ~= nil and #text > 100, text and (#text .. " 字节"))
	if text ~= nil then
		check("schema 打头", text:find('"schema":"hd2physbone.snap/1"', 1, true) ~= nil)
		check("带 runtime 版本", text:find('"runtime":"2.1.19"', 1, true) ~= nil)
		check("单份文件只装一帧", select(2, text:gsub('"frame"', "")) == 1,
			"帧数 " .. select(2, text:gsub('"frame"', "")))
		check("帧里两个节点", select(2, text:gsub('"id":"0x8e8d63a0"', "")) == 1
			and select(2, text:gsub('"id":"0x4c1bb3d1"', "")) == 1)
		check("坐标写成了数", text:find('"z":1.030000', 1, true) ~= nil)
		check("四元数写成了数", text:find('"qw":1.000000', 1, true) ~= nil)
		-- 弹簧角度在求解器里是弧度，落盘要换成度；写成 null 说明字段名对不上（踩过一次）
		check("弹簧角度写成度数", text:find('"yawDeg":null', 1, true) == nil
			and text:find('"yawDeg":5.7', 1, true) ~= nil)
	end
	local doneText = readFile(DIR .. "\\done.txt")
	check("留了 done 标记", doneText ~= nil and doneText:find("frames=3", 1, true) ~= nil, doneText)

	-- 同一份请求再来一遍：不该重复采（内容没变）
	for _ = 1, 5 do
		runtime.driveFrames = runtime.driveFrames + 1
		poll(S, runtime, options)
	end
	check("请求没变就不重复采", #listSnapshots() == 3, "文件数 " .. #listSnapshots())

	-- 请求变了：再采一份
	writeFile(DIR .. "\\request.txt", "seq = 2\nframes = 1\nstride = 1\nnode = 0x8e8d63a0\n")
	for _ = 1, 5 do
		runtime.driveFrames = runtime.driveFrames + 1
		poll(S, runtime, options)
	end
	check("新请求再采一份", #listSnapshots() == 4, "文件数 " .. #listSnapshots())
end

-- ---------------------------------------------------------------- ④ 健壮性
do
	-- 不存在的目录：不许抛错，也不许写
	local runtime = fakeRuntime(0, true)
	local ok = pcall(function()
		for _ = 1, 40 do
			runtime.driveFrames = runtime.driveFrames + 1
			poll(fakeEngine(), runtime, { snapshotDir = DIR .. "\\不存在的子目录", snapshotPollFrames = 1 })
		end
	end)
	check("目录不存在时不抛错", ok)

	-- 没有会话（还没挂上物理）：请求被接受，但不写半份文件
	removeAll()
	writeFile(DIR .. "\\request.txt", "seq = 3\nframes = 2\nstride = 1\nnode = 0x8e8d63a0\n")
	local lonely = fakeRuntime(0)
	for _ = 1, 10 do
		lonely.driveFrames = lonely.driveFrames + 1
		poll(fakeEngine(), lonely, { snapshotDir = DIR, snapshotPollFrames = 1 })
	end
	check("没有会话时不写残缺文件", #listSnapshots() == 0, "文件数 " .. #listSnapshots())

	-- 关掉快照：目录里就算有请求也不动
	writeFile(DIR .. "\\request.txt", "seq = 4\nframes = 1\nstride = 1\nnode = 0x8e8d63a0\n")
	local off = fakeRuntime(0)
	for _ = 1, 10 do
		off.driveFrames = off.driveFrames + 1
		poll(fakeEngine(), off, { snapshot = false, snapshotPollFrames = 1 })
	end
	check("snapshot=false 时彻底不动", #listSnapshots() == 0, "文件数 " .. #listSnapshots())
end

removeAll()
sh('rmdir "' .. DIR .. '" 2>nul')

print(string.format("\n失败 %d 项", failures))
os.exit(failures == 0 and 0 or 1)


