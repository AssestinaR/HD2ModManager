-- 离线测试：armor_override_runtime.lua 的 update 钩子与按实例隐藏 Hider 的共存。
-- 运行：luajit <本文件> <armor_override_runtime.lua> <kit_instance_hider.lua>
local armorPath = (arg and arg[1]) or 'HD2RuntimeArmorOverride/src/armor_override_runtime.lua'
local hiderPath = (arg and arg[2]) or 'HD2RuntimeArmorOverride/src/kit_instance_hider.lua'
rawset(_G, 'HD2_KIT_HIDER_OFFLINE', true)
local Hider = dofile(hiderPath)

local function new_env()
	local env = {}
	env._G = env
	env.stingray = { Application = {}, IdString64 = {} }
	env.io = { open = function() return nil, 'No such file or directory', 2 end }
	env.os = { time = function() return 0 end, getenv = function() return 'C:/L' end, clock = os.clock }
	return setmetatable(env, { __index = _G })
end

local function load_armor(env)
	local chunk = assert(loadfile(armorPath))
	setfenv(chunk, env)
	chunk()
end

-- A. Hider 先装、护甲后装：两者都在跑 ----------------------------------------------
do
	local env = new_env()
	local ticks = 0
	env.stingray.Application.time_since_launch = function() ticks = ticks + 1; return 0 end
	env.stingray.Application.worlds = function() return {} end
	assert(Hider.install(env).status == 'active')
	assert(type(Hider.update_hook) == 'function' and rawget(env, 'update') == Hider.update_hook)
	env.HD2KitInstanceHider = Hider
	load_armor(env)
	assert(rawget(env, 'update') ~= Hider.update_hook, 'armor installs its own update')
	for _, name in ipairs({ 'init', 'shutdown', 'render' }) do assert(type(rawget(env, name)) == 'function') end
	local before = ticks
	env.update(0.016)
	assert(ticks > before, 'Hider hook must still run after armor is installed')
end

-- B. previous 不是 Hider 的钩子：不调用（独占 boot 下游戏原 update 的现有行为） ----------
do
	local env = new_env()
	local called = 0
	env.update = function() called = called + 1 end
	load_armor(env)
	env.update(0.016); env.update(0.016)
	assert(called == 0, 'foreign previous update must not be called')

	-- 有 Hider 模块，但 previous 不是它的钩子：同样不调用
	local env2 = new_env()
	local called2 = 0
	env2.HD2KitInstanceHider = { update_hook = function() end }
	env2.update = function() called2 = called2 + 1 end
	load_armor(env2)
	env2.update(0.016)
	assert(called2 == 0, 'previous that is not the Hider hook must not be called')

	-- update_hook 为空（Hider 未装上）而 previous 也是 nil
	local env3 = new_env()
	env3.HD2KitInstanceHider = {}
	load_armor(env3)
	env3.update(0.016)
end

-- C. Hider 钩子抛错不影响护甲的 update ---------------------------------------------
do
	local env = new_env()
	local hook = function() error('boom') end
	env.HD2KitInstanceHider = { update_hook = hook }
	env.update = hook
	load_armor(env)
	assert(pcall(env.update, 0.016))
end

-- D. 旧 SEAF 链接忽略，普通映射继续应用 -------------------------------------------
do
	local env = new_env()
	local applied = {}
	local document = 'schema=3\nrevision=71\nenabled=true\n'
		.. 'link=75F7E96AF7DCD303,079b8593cf6b1dad\n'
		.. 'link=1111111111111111,2222222222222222\n'
	env.io.open = function(_, mode)
		if mode ~= 'rb' then return nil end
		return { read = function() return document end, close = function() end }
	end
	env.stingray.Application.time_since_launch = function() return 1 end
	env.stingray.IdString64.from_hex = function(id) return id end
	env.stingray.Application.set_resource_override = function(kind, source, target)
		assert(kind == 'unit')
		applied[source] = target
	end
	load_armor(env)
	env.update(0.016)
	env.update(0.016)
	assert(applied['75f7e96af7dcd303'] == nil, 'SEAF must use its original resource key')
	assert(applied['1111111111111111'] == '2222222222222222', 'regular links still apply')
end

-- E. MDL3 ProgramData 安装：log_directory 指向 C:/ProgramData 时协议仍从真实 LOCALAPPDATA 读取
do
	local env = new_env()
	local applied = {}
	local requestedPaths = {}
	local openedLogs, logLines = {}, {}
	local expectedProtocol = 'C:/Users/Alice/AppData/Local/CowboyBingus/Helldivers2/ArmorRegistry/wardrobe.links'
	local document = 'schema=3\nrevision=8\nenabled=true\nlink=aaaa1111bbbb2222,cccc3333dddd4444\n'
	env.os = {
		time = function() return 10 end,
		getenv = function(k) assert(k == 'LOCALAPPDATA'); return 'C:/Users/Alice/AppData/Local' end,
		clock = os.clock,
	}
	env.CowboyBingusModLoader = {
		api = 1,
		mdl3 = true,
		log_directory = 'C:/ProgramData/CowboyBingus/Helldivers2/Logs',
		open_log = function(name)
			openedLogs[#openedLogs + 1] = name
			return {
				write = function(_, text) logLines[#logLines + 1] = text end,
				flush = function() end,
			}
		end,
	}
	env.io.open = function(path, mode)
		requestedPaths[#requestedPaths + 1] = path .. '|' .. tostring(mode)
		if path == expectedProtocol and mode == 'rb' then
			return { read = function() return document end, close = function() end }
		end
		return nil, 'No such file or directory', 2
	end
	env.stingray.IdString64.from_hex = function(id) return id end
	env.stingray.Application.set_resource_override = function(kind, source, target)
		assert(kind == 'unit')
		applied[source] = target
		return true
	end
	load_armor(env)
	env.update(0.016)
	env.update(0.016)
	assert(applied['aaaa1111bbbb2222'] == 'cccc3333dddd4444', 'MDL3 ProgramData must still apply overrides from real LOCALAPPDATA')
	assert(requestedPaths[1] == expectedProtocol .. '|rb', 'must open real LOCALAPPDATA wardrobe.links, got ' .. tostring(requestedPaths[1]))
	assert(openedLogs[1] == 'ArmorOverrideRuntime.log' and #logLines >= 2, 'must log via loader.open_log when available')
end

print('test_armor_override_runtime_update: ok')
