local source = arg[1] or 'HD2RuntimeArmorOverride/src/lua_surface_probe.lua'
local function fixture(options)
    options = options or {}
    local lines, calls, closed = {}, 0, false
    local file = {}
    function file:write(text)
        if options.fail_write then error('simulated write failure') end
        lines[#lines + 1] = text; return self
    end
    function file:flush() return true end
    function file:close() closed = true; return true end
    local forbidden = function() error('unexpected engine invocation') end
    local trap = {__index = forbidden, __pairs = forbidden, __tostring = forbidden}
    local unit = setmetatable({spawn = forbidden}, trap)
    local engine = {
        Unit = unit,
        World = {spawn_unit = forbidden},
        IdString64 = {
            from_hex = function(hex) assert(hex == '17da843a4908511e'); return {hex = hex} end,
            to_hex = function(id) return id.hex end
        },
        Application = {
            can_get = function(kind, id)
                assert(kind == 'unit' and id.hex == '17da843a4908511e')
                return true
            end
        }
    }
    local env = {
        rawget = rawget, rawset = rawset, pcall = pcall, next = next, pairs = pairs,
        ipairs = ipairs, type = type, tostring = tostring, error = error,
        setmetatable = setmetatable, string = string, table = table, math = math,
        os = {clock = os.clock}, debug = debug, stingray = engine,
        package = {loaded = {['customization/armor'] = {equip = forbidden}}},
        CowboyBingusModLoader = {api = 1, version = 16, open_log = function() return file end}
    }
    env._G = env
    local original = function(dt, marker)
        calls = calls + 1
        if options.update_error then error('original update error') end
        return marker, nil, 'third'
    end
    env.update = options.no_update and nil or original
    if options.no_update then env.update = nil end
    local function load()
        return setfenv(assert(loadfile(source)), env)()
    end
    return {env = env, lines = lines, load = load, original = original,
        calls = function() return calls end, closed = function() return closed end}
end
local f = fixture()
local m = f.load()
assert(m.status == 'active')
local installed = f.env.update
assert(f.load() == m and f.env.update == installed, 'duplicate initialization')
local a, b, c = f.env.update(0.25, 'first')
assert(a == 'first' and b == nil and c == 'third', 'return values lost')
assert(select('#', f.env.update(0.25, 'second')) == 3)
for i = 1, 600 do
    if i == 80 then
        f.env.package.loaded['customization/armor'].equip = function() error('do not call') end
        f.env.package.loaded['armory\npreview'] = {}
    end
    f.env.update(0.25, 'marker')
end
assert(m.status == 'complete' and f.env.update == f.original and f.closed())
local text = table.concat(f.lines)
assert(text:find('"change":"changed"', 1, true), 'function identity diff missing')
assert(text:find('u000a', 1, true), 'control character was not escaped')
assert(text:find('"record_type":"sample_end"', 1, true))
assert(text:find('"record_type":"unit_id_api"', 1, true))
assert(text:find('"can_get_truthy":true', 1, true))
assert(text:find('"can_get_result_type":"boolean"', 1, true))
assert(not text:find('"truncated":true', 1, true))
    local out = assert(io.open('HD2RuntimeArmorOverride/evidence/lua-surface-fixture.jsonl', 'w'))
out:write(text); out:close()
local g = fixture({fail_write = true})
assert(g.load().status == 'initialization_failed' and g.env.update == g.original)
local h = fixture({no_update = true})
assert(h.load().status == 'update_unavailable')
local e = fixture({update_error = true})
e.load()
local ok, err = pcall(e.env.update, 0.1)
assert(not ok and err:find('original update error', 1, true))
local j = fixture()
j.load()
local inner = j.env.update
local outer = function(...) return inner(...) end
j.env.update = outer
for _ = 1, 600 do j.env.update(0.25) end
assert(j.env.update == outer, 'another mod callback was overwritten')
print('PASS: bounded metadata, no getters/native calls, identity diff, JSON escapes,')
print('      duplicate guard, nil returns, original errors, log failure, callback restoration')
