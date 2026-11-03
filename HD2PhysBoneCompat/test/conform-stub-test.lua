-- 离线干跑一致性电池：用假引擎把 probe_conform.lua 的整条流程跑一遍。
-- 目的：在你花一次"进游戏"之前，先证明
--   ① 就绪判定能收敛；② E0/E1/E2/E3/E4/E5/E5b/E6 的判据逻辑不会崩、且结论方向正确；
--   ③ 汇总表能打印出来。
-- 用法：luajit conform-stub-test.lua   （需要先把 LOCALAPPDATA 指到临时目录，见 run-conform-test.ps1）

local function rodrigues(ax, ay, az, angle)
	local len = math.sqrt(ax * ax + ay * ay + az * az)
	ax, ay, az = ax / len, ay / len, az / len
	local c, s = math.cos(angle), math.sin(angle)
	local t = 1 - c
	return {
		t * ax * ax + c, t * ax * ay - s * az, t * ax * az + s * ay,
		t * ax * ay + s * az, t * ay * ay + c, t * ay * az - s * ax,
		t * ax * az - s * ay, t * ay * az + s * ax, t * az * az + c,
	}
end

-- 假 q：{ax,ay,az,angle}；假 matrix：{12 个数}；假 vector：{x,y,z}
local fake = { layout = arg and arg[1] or "packed9_colmajor", frame = 0 }

local S = {}
S.Vector3 = setmetatable({
	to_elements = function(v) return v[1], v[2], v[3] end,
}, { __call = function(_, x, y, z) return { x, y, z } end })

S.Quaternion = {
	axis_angle = function(axis, angle)
		return { axis[1], axis[2], axis[3], angle }
	end,
	to_elements = function(q)
		local half = q[4] / 2
		local s = math.sin(half)
		local len = math.sqrt(q[1] ^ 2 + q[2] ^ 2 + q[3] ^ 2)
		if len < 1e-9 then return 0, 0, 0, 1 end
		return q[1] / len * s, q[2] / len * s, q[3] / len * s, math.cos(half)
	end,
	from_elements = function(x, y, z, w) return { x, y, z, w } end,
}

S.Matrix4x4 = {
	from_quaternion_position = function(q, position)
		local r = rodrigues(q[1], q[2], q[3], q[4])
		local tx, ty, tz = position[1], position[2], position[3]
		local e
		if fake.layout == "packed9_colmajor" then
			-- 列主序 3x4：先三个列向量，再平移
			e = { r[1], r[4], r[7], r[2], r[5], r[8], r[3], r[6], r[9], tx, ty, tz }
		elseif fake.layout == "per_row" then
			-- 逐行 3x4：每行 3 个旋转 + 1 个平移
			e = { r[1], r[2], r[3], tx, r[4], r[5], r[6], ty, r[7], r[8], r[9], tz }
		else
			-- packed9 行主序
			e = { r[1], r[2], r[3], r[4], r[5], r[6], r[7], r[8], r[9], tx, ty, tz }
		end
		return e
	end,
	to_elements = function(m)
		return m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12]
	end,
	rotation = function(m) return m end,
	translation = function(m) return { m[10], m[11], m[12] } end,
	is_valid = function() return true end,
}
S.IdString64 = { to_hex = function() return "0x0" end }

-- 假场景：一个 avatar，head 在 +Y 上方 0.62 m
local avatar = { name = "avatar" }
S.Application = {
	worlds = function() return { {} } end,
	string_to_id32 = function(text) return text end,
	string_to_id64 = function() return "avatarid" end,
	time_since_launch = function() return fake.frame / 60 end,
}
S.World = {
	units = function() return { avatar } end,
	unit_by_id = function() return avatar end,
	update_unit = function() end,
}
local headPose = { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0.62, 0 }
S.Unit = {
	num_scene_graph_items = function() return 100 end,
	has_node = function(_, name) return name == "head" or name == "hips" end,
	node = function(_, name)
		if name == "head" then return 7 end
		if name == "hips" then return 8 end
		return nil
	end,
	has_animation_state_machine = function() return true end,
	debug_name = function() return "#ID[deadbeef]" end,
	bones = function() return { "head", "hips" } end,
	world_position = function(_, node)
		if node == 7 then return { 0, 1.62, 0 } end
		if node == 8 then return { 0, 1.00, 0 } end
		return { 0, 0, 0 }
	end,
	local_pose = function() return headPose end,
	world_rotation = function()
		-- 让旋转随"时间"变化，验证 E5 的观察逻辑
		local angle = 0.001 * fake.frame
		return { 0, 0, 1, angle }
	end,
	set_local_pose = function() end,
}

rawset(_G, "stingray", S)

-- 加载探针（默认原始源码；用 CONFORM_TARGET 指向合成后的 boot 可验证打包产物）
local target = os.getenv("CONFORM_TARGET") or "tmp/lua-boot/probe_conform.lua"
print("target = " .. target)
dofile(target)

local update = rawget(_G, "update")
assert(type(update) == "function", "探针没有装上 update 钩子")

-- 跑 3200 帧（约 53 秒游戏时间），足以覆盖 就绪 → 全部实验 → E5/E6 观察窗口
for i = 1, 3200 do
	fake.frame = i
	update(1 / 60)
end

print("=== 干跑结束：3200 帧无 Lua 错误 ===")
