-- 离线验证：M.attach 是不是只处理了第一条链就 return 了
-- 用法：luajit attach-loop-test.lua <physbone_compat.lua> [asis|fix]
--   asis = 原样加载
--   fix  = 内存里把「for 的收尾 end」从 return 之后搬到 return 之前（不改磁盘）
--
-- 这个测试不碰引擎：S 是一套最小 stub，只回答 attach 会问的那几个问题。

local path = arg[1] or "HD2PhysBoneCompat/src/physbone_compat.lua"
local mode = arg[2] or "asis"

local fh = assert(io.open(path, "rb"))
local source = fh:read("*a")
fh:close()

if mode == "fix" then
	local lines = {}
	for line in (source .. "\n"):gmatch("([^\n]*)\n") do lines[#lines + 1] = line end

	local anchor, tail = nil, nil
	for i, line in ipairs(lines) do
		if line:find("attach：可用链", 1, true) then anchor = i end
		if line:find("补上 M.attach 的收尾", 1, true) then tail = i end
	end
	assert(anchor and tail, "找不到目标行")

	-- tail-1 是多出来的那个 end，删掉；在锚点之前补一个 end
	table.remove(lines, tail - 1)
	table.insert(lines, anchor, "end")
	source = table.concat(lines, "\n")
end

local M = assert(loadfile(path) and loadstring(source))()

-- ---------------------------------------------------------------------------
-- 最小引擎 stub
-- ---------------------------------------------------------------------------
local idByText, textById = {}, {}
local nextId = 0
local function id32(text)
	if idByText[text] == nil then
		nextId = nextId + 1
		idByText[text] = 1000 + nextId
		textById[1000 + nextId] = text
	end
	return idByText[text]
end

local nodeByName = {
	head = 1,
	jiggle_L1 = 11, jiggle_L2 = 12, jiggle_L3 = 13,
	jiggle_R1 = 21, jiggle_R2 = 22, jiggle_R3 = 23,
}

local S = {}
S.Application = { string_to_id32 = id32 }
S.Quaternion = { from_elements = function() return {} end }
S.Vector3 = {
	to_elements = function(v) return v[1], v[2], v[3] end,
}
S.Matrix4x4 = {
	translation = function() return { 0, 0, 0 } end,
	rotation = function() return {} end,
	to_elements = function()
		return 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0
	end,
	is_valid = function() return true end,
	from_quaternion_position = function() return {} end,
}
S.Unit = {
	num_scene_graph_items = function() return 999 end,
	has_node = function(_, id) return textById[id] ~= nil end,
	node = function(_, id) return nodeByName[textById[id]] or 999999 end,
	local_pose = function(_, node) return { poseOf = node } end,
	world_position = function(_, node) return { node * 1.0, 0, 0 } end,
	set_local_pose = function() end,
}
S.World = { update_unit = function() end }

-- ---------------------------------------------------------------------------
-- 造一份「两条链、每链三节」的 rig
-- ---------------------------------------------------------------------------
local function bone(index, parent, name, parentName, x)
	return {
		index = index, parent = parent, name = name, parentName = parentName,
		flags = 0x31, pos = { x, 0, 0 },
		params = {
			length = 30, tipMass = 200,
			yawStiffness = 50, yawDamping = 7,
			pitchStiffness = 50, pitchDamping = 7,
			angleLimit = 0.611,
		},
	}
end

local rig = { bones = {
	bone(101, 1, "jiggle_L1", "head", -0.19),
	bone(102, 101, "jiggle_L2", "jiggle_L1", -0.22),
	bone(103, 102, "jiggle_L3", "jiggle_L2", -0.31),
	bone(201, 1, "jiggle_R1", "head", 0.19),
	bone(202, 201, "jiggle_R2", "jiggle_R1", 0.22),
	bone(203, 202, "jiggle_R3", "jiggle_R2", 0.31),
} }

local logged = {}
local function logger(fmt, ...)
	local text = string.format(fmt, ...)
	logged[#logged + 1] = text
	print("  LOG " .. text)
end

print(("=== mode=%s ==="):format(mode))
local session = M.attach(S, { unit = "dummy" }, rig, { logger = logger, world = {} })

print(("sessions=%d  #chains 期望 2"):format(#session.sessions))
print(("链日志条数=%d"):format(select(2, string.gsub(table.concat(logged, "\n"), "链 #", ""))))

local ok = (#session.sessions == 2)
print(mode == "asis"
	and (ok and "结果：两条链都挂上了（与预期不符）" or "结果：确实只挂上 1 条 ⇒ 复现")
	or (ok and "结果：修好后两条链都挂上" or "结果：修了仍然只有 1 条"))
