-- 离线测试：kit_instance_probe.lua（假引擎，不需要游戏）。
-- 运行：luajit HD2RuntimeArmorOverride/tests/test_kit_instance_probe.lua
rawset(_G, 'HD2_KIT_PROBE_OFFLINE', true)
local path = (arg and arg[1]) or 'HD2RuntimeArmorOverride/src/kit_instance_probe.lua'
local M = dofile(path)
assert(M.status == 'offline', 'offline guard')

local marker = '82595b5d94c57343'
local id, form

-- decode_resource -----------------------------------------------------------
local obj = setmetatable({}, { __tostring = function() return '[IdString64 "#ID[4D1C334D294DFA97]" 0x4d1c334d294dfa97]' end })
id, form = M.decode_resource(obj)
assert(id == '4d1c334d294dfa97' and form == 'idstring_text', tostring(form))
id, form = M.decode_resource(setmetatable({}, { __tostring = function() return 'whatever' end }))
assert(id == nil and form == 'unparsed')
assert(M.decode_resource(marker) == marker)
assert(select(2, M.decode_resource(nil)) == 'nil')

-- classify ------------------------------------------------------------------
local function set(list) local s = {} for _, v in ipairs(list) do s[v] = true end return s end
local kit, basis = M.classify(set({ marker, '210fb2f60f8308ea' }))
assert(kit == 'FS05a' and basis == 'exact', kit)
kit, basis = M.classify(set({ '210fb2f60f8308ea', '79957cb82c5e6e9e' }))
assert(kit == 'CE64' and basis == 'exact', kit)
kit, basis = M.classify(set({ '210fb2f60f8308ea' }))
assert(kit == 'CE64+FS05a+FS05b+FS05c' and basis == 'ambiguous', kit)
kit, basis = M.classify(set({ '90b57e547e7ff7bb', '8d02a82d0c214171' }))
assert(kit == 'FS05b+FS05c' and basis == 'ambiguous', kit)
kit, basis = M.classify(set({ '8d02a82d0c214171', '79957cb82c5e6e9e' }))
assert(kit == 'conflict' and basis == 'no_kit_has_all', kit)
assert(M.classify({}) == 'none')
assert(M.classify(set({ 'ffffffffffffffff' })) == 'none')

-- 假引擎 --------------------------------------------------------------------
-- 两个 world：world1 有两个玩家，一个穿 FS-05、一个穿 CE-64；world2 是没有玩家的预览。
local function unit(hash, x, y, z, opts)
	return { hash = hash, x = x, y = y, z = z, alive = not (opts and opts.dead) }
end
local avatarA = unit('avatar', 0, 0, 0)
local avatarB = unit('avatar', 5, 0, 0)
local function fsUnits(ox)
	return {
		unit(marker, ox, 0, 0),
		unit('210fb2f60f8308ea', ox + 0.01, 0, 0),
		unit('74b2e79c1c5af767', ox, 0.02, 0),
	}
end
local function ceUnits(ox)
	return {
		unit('79957cb82c5e6e9e', ox, 0, 0),
		unit('210fb2f60f8308ea', ox, 0, 0.01),
	}
end
local world1 = { units = { avatarA, avatarB, unit('noise', 9, 9, 9), unit('dead', 0, 0, 0, { dead = true }) },
	avatars = { avatarA, avatarB } }
for _, u in ipairs(fsUnits(0)) do table.insert(world1.units, u) end
for _, u in ipairs(ceUnits(5)) do table.insert(world1.units, u) end
-- 飞船上 14 米外的静态 FS-05 模型：不能算到玩家头上
for _, u in ipairs(fsUnits(0)) do u.y = 14; table.insert(world1.units, u) end
local world2 = { units = fsUnits(100), avatars = {} }
local worlds = { world1, world2 }

local calls = {}
local function count(name) calls[name] = (calls[name] or 0) + 1 end
local clock = 0
local S = {
	Application = { worlds = function() count('worlds') return worlds end },
	World = {
		units = function(w) count('units') return w.units end,
		units_by_resource = function(w, res)
			count('units_by_resource')
			assert(res == 'content/fac_helldivers/cha_avatar/avatar_helldiver')
			return w.avatars
		end,
	},
	Unit = {
		alive = function(u) return u.alive end,
		name_hash = function(u) assert(u.alive, 'name_hash on dead unit') return '#IDPRE_!' .. u.hash:sub(1, 8) end,
		world_position = function(u, node)
			assert(node == 1 and u.alive, 'world_position contract')
			return { u.x, u.y, u.z }
		end,
		num_meshes = function() return 3 end,
		resource_name = function(u)
			assert(u.alive, 'resource_name on dead unit')
			return setmetatable({}, { __tostring = function()
				return '[IdString64 "#ID[' .. u.hash .. ']" 0x' .. u.hash .. ']'
			end })
		end,
	},
	Vector3 = {
		x = function(v) return v[1] end, y = function(v) return v[2] end, z = function(v) return v[3] end,
	},
}
-- 允许清单之外的东西一律不准碰。
setmetatable(S.Unit, { __index = function(_, k) error('unexpected Unit.' .. k) end })
setmetatable(S.World, { __index = function(_, k) error('unexpected World.' .. k) end })
setmetatable(S.Application, { __index = function(_, k) error('unexpected Application.' .. k) end })

local lines = {}
local probe = M.new({
	stingray = S,
	now = function() return clock end,
	cpu = function() return clock end,
	write = function(line) lines[#lines + 1] = line end,
})

local function run_until_snapshot(maxTicks)
	local before = #lines
	for _ = 1, maxTicks do
		probe.tick()
		for i = before + 1, #lines do
			if lines[i]:find('"kind":"snapshot"', 1, true) then return lines[i] end
		end
	end
end

local snap = run_until_snapshot(20)
assert(snap, 'no snapshot produced')
assert(snap:find('"group":"w1/avatar1"', 1, true), snap)
assert(snap:find('"basis":"exact","group":"w1/avatar1","ids":"210fb2f6 74b2e79c 82595b5d","kit":"FS05a"', 1, true), snap)
assert(snap:find('"group":"w1/avatar2","ids":"210fb2f6 79957cb8","kit":"CE64"', 1, true), snap)
assert(snap:find('"group":"w1/pos0,280,0"', 1, true), snap) -- 静态模型自成一组
assert(snap:find('"group":"w2/pos2000,0,0"', 1, true), snap)
for _, l in ipairs(lines) do assert(not l:find('"kind":"hit"', 1, true), 'per-hit lines removed in 0.4') end

-- 结果不变 ⇒ 不重复写快照；1 秒内不重新扫描
local n = #lines
for _ = 1, 10 do probe.tick() end
assert(#lines == n, 'no new lines inside interval')
clock = clock + 1.5
for _ = 1, 10 do probe.tick() end
for i = n + 1, #lines do
	assert(not lines[i]:find('"kind":"snapshot"', 1, true), 'unchanged sweep must not re-log snapshot')
end

-- 玩家 A 换成 CE-64 ⇒ 新快照
for i = #world1.units, 1, -1 do
	local u = world1.units[i]
	if u.x == 0 and u.y < 1 and u.hash ~= 'avatar' and u.hash ~= 'dead' then
		table.remove(world1.units, i)
	end
end
for _, u in ipairs(ceUnits(0)) do table.insert(world1.units, u) end
clock = clock + 1.5
snap = run_until_snapshot(20)
assert(snap, 'no snapshot after switching kits')
assert(snap:find('"group":"w1/avatar1","ids":"210fb2f6 79957cb8","kit":"CE64"', 1, true), snap)

-- 预算：一帧最多 300 个 unit
local big = { units = {}, avatars = {} }
for i = 1, 700 do big.units[i] = unit('noise', i, 0, 0) end
worlds = { big }
calls = {}
clock = clock + 2
local before = #lines
probe.tick()
assert(calls.units == 1)
local ticks = 1
while #lines == before and ticks < 10 do
	probe.tick(); ticks = ticks + 1
end
assert(lines[#lines]:find('"kind":"snapshot"', 1, true), lines[#lines])
assert(ticks == 3, 'expected 3 frames for 700 units, got ' .. ticks)
local total = 0
for _, l in ipairs(lines) do total = total + #l end
assert(total < 8 * 1024, 'log too chatty: ' .. total)

-- 只写不读的保证：没有任何 set_/spawn/destroy/link（元表会直接报错）
print('test_kit_instance_probe: ok (' .. #lines .. ' log lines)')
