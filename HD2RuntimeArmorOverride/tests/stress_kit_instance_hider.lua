-- 运行：python -I HD2RuntimeArmorOverride/scripts/run_lua_test.py <lupa所在目录> HD2RuntimeArmorOverride/tests/stress_kit_instance_hider.lua HD2RuntimeArmorOverride/src/kit_instance_hider.lua
-- 数据与结论见 docs/agent/kit-instance-mapping-deploy-plan-20261114.md §11.6。这是压测，不是断言测试。
-- 压测：kit_instance_hider.lua 在 R 条规则、每条 H 个 hide 时的 Lua 侧开销（假引擎，调用本身近乎零成本，
-- 所以这里量的是 Hider 自己的 Lua 开销；引擎调用成本另算：探针实测约 0.25 ms / 300 unit）。
rawset(_G, 'HD2_KIT_HIDER_OFFLINE', true)
local M = dofile(arg[1])
local clock = os.clock

local function hex(n) return string.format('%016x', n) end

local function build(R, H, POOL)
	local lines = { 'schema=1', 'revision=1' }
	for i = 1, R do
		local hide = {}
		for k = 1, H do hide[k] = hex(0x1000000 + ((i * 7 + k * 13) % POOL)) end
		lines[#lines + 1] = 'rule=name:r' .. i .. ';radius:0.3;all:' .. hex(0x9000000 + i)
			.. ';none:;hide:' .. table.concat(hide, ',')
	end
	return table.concat(lines, '\n') .. '\n'
end

local function run(R, H, CHARS, BASE_UNITS)
	local POOL = 1500
	local text = build(R, H, POOL)
	local t0 = clock()
	local parsed = assert(M.parse_rules(text))
	local parse_ms = (clock() - t0) * 1000

	-- 世界：BASE_UNITS 个无关 unit + CHARS 个角色，每个角色 = 1 个签名 + 24 个隐藏候选件（来自隐藏池）。
	local units = {}
	for i = 1, BASE_UNITS do units[#units + 1] = { id = hex(0x5000000 + i), x = i * 0.5, y = 0, z = 0, alive = true } end
	for c = 1, CHARS do
		local rule = (c % R) + 1
		local cx = 1000 + c * 20
		units[#units + 1] = { id = hex(0x9000000 + rule), x = cx, y = 0, z = 0, alive = true }
		for k = 1, 24 do
			local pick = parsed.rules[rule].hide[((k * 5) % H) + 1]
			units[#units + 1] = { id = pick, x = cx, y = 0, z = 0, alive = true }
		end
	end
	local hidden = 0
	local S = {
		Application = { worlds = function() return { { units = units } } end, time_since_launch = function() return 0 end },
		World = { units = function(w) return w.units end },
		Unit = {
			alive = function(u) return u.alive end,
			resource_name = function(u) return u.id end,
			world_position = function(u) return { u.x, u.y, u.z } end,
			set_unit_visibility = function() hidden = hidden + 1 end,
		},
		Vector3 = { x = function(v) return v[1] end, y = function(v) return v[2] end, z = function(v) return v[3] end },
	}
	local now = 0
	local H_ = M.new({
		stingray = S, now = function() return now end, cpu = clock, write = function() end,
		read_rules = function() return text end,
	})
	local t1 = clock()
	now = 10
	H_.tick()   -- 读规则 + 建表 + 第一帧
	local install_ms = (clock() - t1) * 1000
	-- 跑 60 轮完整扫描（os.clock 只有 ~1 ms 分辨率，所以取总耗时除以帧数得平均帧耗时）。
	local frames, base_sweeps = 0, H_.sweeps
	local worst_frame = 0
	local s0 = clock()
	while H_.sweeps - base_sweeps < 60 do
		now = now + 1
		local f0 = clock()
		H_.tick()
		local ms = (clock() - f0) * 1000
		if ms > worst_frame then worst_frame = ms end
		frames = frames + 1
		now = now + 0.0001
	end
	local total_ms = (clock() - s0) * 1000
	local worst_sweep = total_ms / 60 * 1
	local avg_frame = total_ms / frames
	print(string.format('R=%3d H=%3d chars=%2d units=%5d | file %7.1f KiB | parse %6.2f ms | install+frame1 %6.2f ms | avg frame %6.3f ms (worst %4.1f) | avg sweep %6.2f ms | hides/sweep ~%d',
		R, H, CHARS, #units, #text / 1024, parse_ms, install_ms, avg_frame, worst_frame, worst_sweep, math.floor(hidden / math.max(1, H_.sweeps))))
end

print(jit and jit.version or _VERSION)
for _, case in ipairs({
	{ 64, 128, 30, 4000 },
	{ 200, 24, 30, 4000 },
	{ 400, 30, 30, 4000 },    -- 200 个目标，每个目标 2 条规则（原 ID 签名 + 来源 ID 签名）
	{ 400, 128, 30, 4000 },
	{ 400, 128, 60, 8000 },
	{ 1024, 56, 30, 4000 },   -- 新上限
	{ 1024, 100, 60, 8000 },  -- 新上限，接近 2 MiB
	{ 1024, 256, 30, 4000 },  -- 超过 2 MiB：文件会被拒，这里只为看解析开销（预期 file too large）
}) do
	local ok, err = pcall(run, unpack(case))
	if not ok then print(string.format('R=%d H=%d: %s', case[1], case[2], tostring(err))) end
end
