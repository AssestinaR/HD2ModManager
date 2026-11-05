-- 抬升探针的纯逻辑回归
--
-- 为什么这个测试值钱：抬升是**每帧写 unit 根节点**的活，判据错一条就会
--   · 引擎重设后没重写 ⇒ 抬升时有时无（看起来像"随机抖动"）；
--   · 引擎没重设却又加了一次 ⇒ 每帧 +9 cm，角色飞走；
--   · 关掉时不还原 ⇒ 角色永远停在抬高状态。
-- 这三条都只能靠单测钉住，进游戏看是看不出来的。
--
-- 用法：luajit HD2PhysBoneCompat/test/lift-probe-test.lua [模块路径]

local path = arg[1] or "HD2PhysBoneCompat/src/physbone_runtime.lua"

-- 模块加载时不会碰 stingray（命名空间是 install 时才注入的），所以这里可以安全地
-- 直接 loadfile，只调它暴露出来的两个纯函数。
local M = assert(assert(loadfile(path)))()

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-56s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

local function near(a, b) return type(a) == "number" and math.abs(a - b) < 1e-6 end

-- ---------------------------------------------------------------------------
-- 假引擎：unit.pos 是 {x,y,z}，`reset` 用来模拟"引擎每帧把它设回基线"
-- ---------------------------------------------------------------------------
local function make_engine()
	local counters = { writes = 0, updated = 0 }
	local S = { Application = {}, Unit = {}, World = {} }
	S.Vector3 = setmetatable({
		to_elements = function(v) return v.x, v.y, v.z end,
	}, { __call = function(_, x, y, z) return { x = x, y = y, z = z } end })
	S.Application.string_to_id32 = function() return 7 end
	S.Unit.alive = function(unit) return unit.alive ~= false end
	S.Unit.local_position = function(unit)
		local p = unit.pos
		return { x = p[1], y = p[2], z = p[3] }
	end
	S.Unit.set_local_position = function(unit, node, v)
		counters.writes = counters.writes + 1
		counters.node = node
		unit.pos = { v.x, v.y, v.z }
	end
	S.Unit.world_position = function() return { x = 0, y = 0, z = 0 } end
	S.Unit.has_node = function() return false end
	S.Unit.node = function() return nil end
	S.World.update_unit = function() counters.updated = counters.updated + 1 end
	S.World.debug_camera_pose = function() return nil end
	return S, counters
end

-- ---------------------------------------------------------------------------
-- 调参文件
-- ---------------------------------------------------------------------------
local TUNE = (os.getenv("TEMP") or ".") .. "\\hd2pbx_tune_test.txt"

local function write_tune(text)
	local handle = assert(io.open(TUNE, "w"))
	handle:write(text)
	handle:close()
end

local function make_runtime(path)
	local lines = {}
	local runtime = {
		frame = 1,
		driveFrames = 60,
		options = { tunePath = path },
		logger = function(fmt, ...) lines[#lines + 1] = string.format(fmt, ...) end,
		avatars = {},
		sessions = {},
		cache = {},
		world = "W",
	}
	return runtime, lines
end

-- ---------------------------------------------------------------------------
-- ① 解析
-- ---------------------------------------------------------------------------
write_tune("lift=0.09\nlift_armor=1\nlift_bone=chest\n# 注释行不算\nlift_log_frames = 30\n")
do
	local runtime = make_runtime(TUNE)
	local values = M.tune_values_(runtime)
	check("lift=0.09", near(values.lift, 0.09), tostring(values.lift))
	check("lift_armor=1", near(values.lift_armor, 1), tostring(values.lift_armor))
	check("带空格和等号也能解析", near(values.lift_log_frames, 30), tostring(values.lift_log_frames))
	check("骨名按字符串解析", values.text and values.text.lift_bone == "chest",
		tostring(values.text and values.text.lift_bone))
	check("注释行不产生键", values["#"] == nil, tostring(values["#"]))
	-- 缓存：改了文件也不会立刻生效（避免每帧 io.open）
	write_tune("lift=0.5\n")
	local again = M.tune_values_(runtime)
	check("两秒内命中缓存（不重读文件）", near(again.lift, 0.09), tostring(again.lift))
	runtime.frame = runtime.frame + 1000
	local refreshed = M.tune_values_(runtime)
	check("超时之后重读", near(refreshed.lift, 0.5), tostring(refreshed.lift))
end

-- ---------------------------------------------------------------------------
-- ② 每帧重写 / 不叠加 / 不重复
-- ---------------------------------------------------------------------------
do
	local S, counters = make_engine()
	local unit = { pos = { 0, 0, 1.0 } }
	local state = {}
	local wrote = M.lift_unit_(S, "W", unit, state, 0.09)
	check("首次写入", wrote == true and near(unit.pos[3], 1.09), "z=" .. tostring(unit.pos[3]))

	-- 引擎把它设回基线（根 unit 的正常情况：玩家一动就被重设）
	unit.pos = { 0, 0, 1.0 }
	local wrote2 = M.lift_unit_(S, "W", unit, state, 0.09)
	check("引擎重设后能重写（不是 1.18）", wrote2 == true and near(unit.pos[3], 1.09), "z=" .. tostring(unit.pos[3]))

	-- 引擎没有重设：值还停在我们写的地方 ⇒ 这一帧不能再加
	local wrote3, kept3 = M.lift_unit_(S, "W", unit, state, 0.09)
	check("写入被留住时不再累加", wrote3 == false and kept3 == true and near(unit.pos[3], 1.09),
		string.format("wrote=%s kept=%s z=%s", tostring(wrote3), tostring(kept3), tostring(unit.pos[3])))

	check("每次写入都调 World.update_unit", counters.updated == 2, "update=" .. tostring(counters.updated))
	check("写的永远是根节点", counters.node == 1, "node=" .. tostring(counters.node))
end

-- ---------------------------------------------------------------------------
-- ③ 关掉时还原
-- ---------------------------------------------------------------------------
do
	write_tune("lift=0\n")
	local S = make_engine()
	local unit = { pos = { 0, 0, 1.0 } }
	local runtime, lines = make_runtime(TUNE)
	runtime.avatars = { { unit = unit } }
	runtime.tune = nil

	M.apply_lift_(runtime, S)
	check("lift=0 时不动任何东西", near(unit.pos[3], 1.0), "z=" .. tostring(unit.pos[3]))

	write_tune("lift=0.09\n")
	runtime.tune = nil
	M.apply_lift_(runtime, S)
	check("打开后抬上去", near(unit.pos[3], 1.09), "z=" .. tostring(unit.pos[3]))
	check("打开时打了一行说明", #lines >= 1 and tostring(lines[1]):find("抬升", 1, true) ~= nil,
		lines[1] or "(没有日志)")

	write_tune("lift=0\n")
	runtime.tune = nil
	unit.pos = { 0, 0, 1.09 }        -- 写入被留住的情形
	M.apply_lift_(runtime, S)
	check("关掉时把位置还回基线", near(unit.pos[3], 1.0), "z=" .. tostring(unit.pos[3]))
	check("关掉后清空状态", runtime.liftState == nil, tostring(runtime.liftState))
end

-- ---------------------------------------------------------------------------
-- ④ 没有调参文件 ⇒ 完全不介入
-- ---------------------------------------------------------------------------
do
	local S = make_engine()
	local unit = { pos = { 0, 0, 0.5 } }
	local runtime = make_runtime(TUNE .. ".missing")
	runtime.avatars = { { unit = unit } }
	M.apply_lift_(runtime, S)
	check("文件不存在时一个字节都不写", near(unit.pos[3], 0.5), "z=" .. tostring(unit.pos[3]))
end

-- ---------------------------------------------------------------------------
-- ④ 骨模式：写 avatar 上的一根骨，并量它的世界 z（root 那次就是漏了这个读数）
-- ---------------------------------------------------------------------------
do
	local S = make_engine()
	-- 让 world_position 直接返回 local（最简单的"骨链跟着父节点走"模型）
	local nodeOf = { chest = 5, head = 9 }
	S.Unit.has_node = function(unit, id) return nodeOf[id] ~= nil end
	S.Unit.node = function(unit, id) return nodeOf[id] end
	local pos = { [5] = { 0, 0, 1.30 }, [9] = { 0, 0, 1.69 } }
	S.Unit.local_position = function(unit, node)
		local p = pos[node]
		return { x = p[1], y = p[2], z = p[3] }
	end
	S.Unit.set_local_position = function(unit, node, v) pos[node] = { v.x, v.y, v.z } end
	S.Unit.world_position = S.Unit.local_position
	S.Application.string_to_id32 = function(text) return text end

	local avatar = { unit = true, ids = { chest = true, head = true } }
	local runtime = make_runtime(TUNE)
	runtime.avatar = avatar
	runtime.avatars = { { unit = avatar } }
	runtime.sessions = {}

	local result = assert(M.lift_bone_(S, runtime, "chest", 0.09))
	check("骨模式：写进去了", result.wrote == true, tostring(result.wrote))
	check("骨模式：该骨世界 z 抬了 9 cm",
		near(result.after - result.before, 0.09),
		string.format("%.3f→%.3f", result.before, result.after))
	check("骨模式：头骨读数也拿到了", near(result.head, 1.69), tostring(result.head))
	check("骨模式：没有护甲件时不报错", result.armor == nil, tostring(result.armor))

	-- 引擎重设后要能重写（不能因为"上次写过"就永久跳过）
	pos[5] = { 0, 0, 1.30 }
	local second = assert(M.lift_bone_(S, runtime, "chest", 0.09))
	check("骨模式：引擎重设后能重写", second.wrote == true and near(pos[5][3], 1.39),
		"z=" .. tostring(pos[5][3]))
end

pcall(os.remove, TUNE)

print()
if failures == 0 then
	print("lift-probe: 全部通过")
	os.exit(0)
else
	print(string.format("lift-probe: %d 条失败", failures))
	os.exit(1)
end
