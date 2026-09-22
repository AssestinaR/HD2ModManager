-- HD2-Addon: mods/codex/armor_re824_to_re2310_override
-- Redirects all 22 RE-824 body Unit resources to compatible RE-2310 Units.
local existing = rawget(_G, 'HD2ArmorRE824ToRE2310')
if existing then return existing end
local M = {version = '0.2.0', status = 'starting', applied = 0}
rawset(_G, 'HD2ArmorRE824ToRE2310', M)
local loader = rawget(_G, 'CowboyBingusModLoader')
if type(loader) ~= 'table' or rawget(loader, 'api') ~= 1 then
    M.status = 'loader_unavailable'; return M
end
local open_log = rawget(loader, 'open_log')
if type(open_log) ~= 'function' then M.status = 'logging_unavailable'; return M end
local ok, file = pcall(open_log, 'ArmorOverride_RE824_to_RE2310.log')
if not ok or not file then M.status = 'log_open_failed'; return M end

local function quote(value)
    return '"' .. tostring(value):gsub('[%z\1-\31\\"]', function(c)
        if c == '\\' then return '\\\\' end
        if c == '"' then return '\\"' end
        return string.format('\\u%04x', string.byte(c))
    end) .. '"'
end
local function emit(kind, fields)
    local row = {'"record_type":' .. quote(kind)}
    for k, v in pairs(fields or {}) do
        if type(v) == 'string' then v = quote(v)
        elseif type(v) == 'boolean' then v = v and 'true' or 'false'
        elseif type(v) == 'number' and v == v and math.abs(v) < math.huge then
            v = tostring(v)
        else v = 'null' end
        row[#row + 1] = quote(k) .. ':' .. v
    end
    file:write('{' .. table.concat(row, ',') .. '}\n'); file:flush()
end

-- Generated from the current manager authority database by matching
-- body variant and part location. RE-824's stocky left shoulder is tagged
-- Attachment while RE-2310's corresponding Unit is tagged Armor.
local specs = {
    {'0165616c2d36b9c9', 'c9904983657e9fc8', 'left_leg_inner_any'},
    {'02a687a27d2edf23', '4f7bce4ca49ccda8', 'right_arm_inner_stocky'},
    {'107564155e2f08e2', '7b355519dcfb3085', 'left_shoulder_stocky_attachment_to_armor'},
    {'341d9af932c123be', '226dfb2e738ae56e', 'right_leg_armor_any'},
    {'37dea2fa2be07685', '17da843a4908511e', 'crotch_inner_slim'},
    {'38099f47ce1cc5d2', '9a7b05af54f6722f', 'right_arm_armor_any'},
    {'488aeb0d7bec796f', '7336c6b662522f0e', 'right_arm_inner_slim'},
    {'54ad12df7bf8fb48', '45d79d84b8933a7f', 'crotch_inner_stocky'},
    {'62159622b3ed9438', '5de0cf947d0e6520', 'chest_armor_stocky'},
    {'644aabedaa772162', '6533a416930570a5', 'right_shoulder_armor_slim'},
    {'76839fa34029dbd7', '3c09e08493ee44b8', 'chest_armor_slim'},
    {'7ee47e1da2c4b8c1', '9841b16544c8fbae', 'chest_inner_stocky'},
    {'8d5749d3e407ce6f', '9a5e6dc74cdb08a3', 'left_arm_inner_stocky'},
    {'8fea70710444b2a0', '91eb7be84016ceab', 'left_arm_inner_slim'},
    {'a7730cbbad3a1190', 'b22e65e604a6ef18', 'left_leg_armor_any'},
    {'d4fb21daef73a74d', 'cae35ee15168145f', 'chest_inner_slim'},
    {'d79fa7882bb836e6', '3692b4d3a0b8d5d3', 'crotch_armor_slim'},
    {'dcf96504e28aca18', '914e7539788418fc', 'crotch_armor_stocky'},
    {'e686766b29c8a4ad', '3a91f5a56da3201e', 'right_shoulder_armor_stocky'},
    {'eb769b5f32f01f79', '7cfd3805e70ea7bd', 'left_arm_armor_any'},
    {'f14330fd78a9d706', '9eb91a5581707ef1', 'right_leg_inner_any'},
    {'fba2839df9dee3fe', '144d95c1500b73bc', 'left_shoulder_armor_slim'},
}

local engine = rawget(_G, 'stingray')
local app = type(engine) == 'table' and rawget(engine, 'Application')
local ids = type(engine) == 'table' and rawget(engine, 'IdString64')
local can_get = type(app) == 'table' and rawget(app, 'can_get')
local set_override = type(app) == 'table' and rawget(app, 'set_resource_override')
local clear_override = type(app) == 'table' and rawget(app, 'clear_resource_override')
local from_hex = type(ids) == 'table' and rawget(ids, 'from_hex')
if type(can_get) ~= 'function' or type(set_override) ~= 'function'
    or type(clear_override) ~= 'function' or type(from_hex) ~= 'function' then
    M.status = 'api_unavailable'
    emit('override_error', {reason = M.status})
    file:close(); return M
end

local pairs = {}
for index, spec in ipairs(specs) do
    local source_ok, source = pcall(from_hex, spec[1])
    local target_ok, target = pcall(from_hex, spec[2])
    if not source_ok or not target_ok then
        M.status = 'id_conversion_failed'
        emit('override_error', {reason = M.status, index = index, label = spec[3]})
        file:close(); return M
    end
    pairs[index] = {source = source, target = target, source_hex = spec[1],
        target_hex = spec[2], label = spec[3]}
end

local function clear_all(reason)
    for index = M.applied, 1, -1 do
        pcall(clear_override, 'unit', pairs[index].source)
    end
    emit('override_cleared', {reason = reason or 'manual', cleared = M.applied})
    M.applied = 0; M.status = 'cleared'
end
M.clear = clear_all

local source_ready, target_ready = 0, 0
for _, pair in ipairs(pairs) do
    local source_ok, source_found = pcall(can_get, 'unit', pair.source)
    local target_ok, target_found = pcall(can_get, 'unit', pair.target)
    if source_ok and source_found then source_ready = source_ready + 1 end
    if target_ok and target_found then target_ready = target_ready + 1 end
end
emit('override_start', {version = M.version, pair_count = #pairs,
    source_armor = 'RE-824', target_armor = 'RE-2310',
    writes_native_memory_directly = false, apply_phase = 'addon_startup',
    source_ready_before_apply = source_ready,
    target_ready_before_apply = target_ready})
for index, pair in ipairs(pairs) do
    local applied, reason = pcall(
        set_override, 'unit', pair.source, pair.target)
    if not applied then
        M.status = 'apply_failed'
        emit('override_error', {reason = tostring(reason), index = index,
            label = pair.label, applied = M.applied})
        clear_all('rollback'); file:close(); return M
    end
    M.applied = index
    emit('unit_override', {index = index, label = pair.label,
        source = pair.source_hex, target = pair.target_hex})
end
M.status = 'applied'
emit('override_applied', {count = M.applied, apply_phase = 'addon_startup',
    source_armor = 'RE-824', target_armor = 'RE-2310'})
file:flush()
return M
