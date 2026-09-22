local source = arg[1] or 'HD2RuntimeArmorOverride/src/re824_to_re2310_override.lua'
local log, overrides, clears, updates = {}, {}, {}, 0
local file = {}
function file:write(text) log[#log + 1] = text; return self end
function file:flush() return true end
function file:close() return true end
local available = false
local function from_hex(hex) return {hex = hex} end
local app = {
    can_get = function(kind, id)
        assert(kind == 'unit' and id.hex); return available
    end,
    set_resource_override = function(kind, source_id, target_id)
        assert(kind == 'unit' and source_id.hex and target_id.hex)
        overrides[#overrides + 1] = {source_id.hex, target_id.hex}
    end,
    clear_resource_override = function(kind, source_id)
        assert(kind == 'unit' and source_id.hex)
        clears[#clears + 1] = source_id.hex
    end
}
local env = {
    _G = false, rawget = rawget, rawset = rawset, pcall = pcall, pairs = pairs,
    ipairs = ipairs, type = type, tostring = tostring, error = error,
    string = string, table = table, math = math,
    stingray = {Application = app, IdString64 = {from_hex = from_hex}},
    CowboyBingusModLoader = {
        api = 1, open_log = function(name)
            assert(name == 'ArmorOverride_RE824_to_RE2310.log'); return file
        end
    }
}
env._G = env
local original = function(dt, marker)
    updates = updates + 1; return marker, nil, 'third'
end
env.update = original
local module = setfenv(assert(loadfile(source)), env)()
assert(module.status == 'applied' and module.applied == 22)
local a, b, c = env.update(1, 'marker')
assert(a == 'marker' and b == nil and c == 'third')
assert(#overrides == 22 and env.update == original)
assert(overrides[1][1] == '0165616c2d36b9c9')
assert(overrides[1][2] == 'c9904983657e9fc8')
assert(overrides[22][1] == 'fba2839df9dee3fe')
assert(overrides[22][2] == '144d95c1500b73bc')
module.clear('fixture')
assert(module.status == 'cleared' and #clears == 22)
assert(clears[1] == overrides[22][1] and clears[22] == overrides[1][1])
assert(table.concat(log):find('"record_type":"override_applied"', 1, true))
assert(updates == 1)
print('PASS: 22 startup Unit mappings, callback untouched, apply, reverse rollback')
