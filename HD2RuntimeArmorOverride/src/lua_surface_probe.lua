-- HD2-Addon: mods/codex/armor_lua_surface_probe
-- Metadata only: no FFI, native calls, module loads, getters or memory scans.
local existing = rawget(_G, 'HD2ArmorLuaSurfaceProbe')
if existing then return existing end
local M = {version = '1.2.0', status = 'starting'}
rawset(_G, 'HD2ArmorLuaSurfaceProbe', M)
local loader = rawget(_G, 'CowboyBingusModLoader')
if type(loader) ~= 'table' or rawget(loader, 'api') ~= 1 then
    M.status = 'loader_unavailable'; return M
end
local open_log = rawget(loader, 'open_log')
if type(open_log) ~= 'function' then M.status = 'logging_unavailable'; return M end
local ok, file = pcall(open_log, 'ArmorLuaSurface_v1.log')
if not ok or not file then M.status = 'log_open_failed'; return M end

local function quote(s)
    return '"' .. s:gsub('[%z\1-\31\\"]', function(c)
        if c == '\\' then return '\\\\' end
        if c == '"' then return '\\"' end
        return string.format('\\u%04x', string.byte(c))
    end) .. '"'
end
local function emit(kind, fields)
    local row = {'"record_type":' .. quote(kind)}
    for k, v in pairs(fields) do
        local t = type(v)
        if t == 'string' then v = quote(v)
        elseif t == 'boolean' then v = v and 'true' or 'false'
        elseif t == 'number' and v == v and math.abs(v) < math.huge then v = tostring(v)
        else v = 'null' end
        row[#row + 1] = quote(k) .. ':' .. v
    end
    local written, reason = file:write('{' .. table.concat(row, ',') .. '}\n')
    if not written then error(reason or 'log_write_failed') end
end

local terms = {'armor', 'armour', 'armory', 'customiz', 'equip', 'loadout',
    'appearance', 'cosmetic', 'resource', 'package', 'unit', 'spawn', 'actor',
    'character', 'player', 'preview', 'inventory', 'skin', 'model', 'manager',
    'override', 'idstring', 'hash'}
local api_roots = {Application = true, World = true, Unit = true,
    ResourcePackage = true, UnitResource = true, Mesh = true, Material = true,
    Level = true, IdString32 = true, IdString32Box = true, IdString64 = true,
    IdString64Box = true}
local function relevant(name)
    name = string.lower(name)
    for _, term in ipairs(terms) do
        if name:find(term, 1, true) then return true end
    end
    return false
end
local identities, next_id = setmetatable({}, {__mode = 'k'}), 0
local function identity(v)
    local t = type(v)
    if t ~= 'function' and t ~= 'table' and t ~= 'userdata' then return '' end
    if not identities[v] then next_id = next_id + 1; identities[v] = next_id end
    return t .. ':' .. identities[v]
end
local dbg = rawget(_G, 'debug')
local getinfo = type(dbg) == 'table' and rawget(dbg, 'getinfo')
local clock = os.clock
local schedule = {0, 5, 15, 30, 60, 120}
local elapsed, sample_index, previous, current = 0, 1, {}, nil
local previous_update, wrapper = rawget(_G, 'update'), nil
local limits = {tables = 96, entries = 12000, per_table = 1024, per_tick = 96}
local function enqueue(s, value, path, mode, depth)
    if type(value) ~= 'table' or s.visited[value] then return end
    if #s.queue >= limits.tables then s.truncated = true; return end
    s.visited[value] = true
    s.queue[#s.queue + 1] = {value = value, path = path, mode = mode,
        depth = depth, count = 0}
end
local validate_unit_id_api
local function start_sample()
    local s = {queue = {}, visited = {}, head = 1, entries = 0, changes = 0,
        state = {}, started = elapsed, cpu = 0, ticks = 0, max_tick = 0,
        truncated = false, index = sample_index}
    current = s
    validate_unit_id_api('sample_' .. s.index)
    enqueue(s, _G, '_G', 'globals', 0)
    local pkg = rawget(_G, 'package')
    enqueue(s, type(pkg) == 'table' and rawget(pkg, 'loaded'),
        'package.loaded', 'modules', 0)
    enqueue(s, rawget(_G, 'stingray'), 'stingray', 'engine', 0)
    emit('sample_begin', {sample = s.index, elapsed = elapsed,
        scheduled = schedule[sample_index], atomic = false})
end
local function observe(s, node, key, value)
    if type(key) ~= 'string' or #key > 256 then return end
    if key == 'HD2ArmorLuaSurfaceProbe' or key == 'HD2ArmorRegistryProbe'
        or key == 'CowboyBingusModLoader' or key == '_G' then return end
    local path = node.path .. '[' .. quote(key) .. ']'
    local t, id = type(value), identity(value)
    -- Do not record user/account values, opaque object addresses or cdata hashes.
    local signature = t .. ':' .. id
    s.state[path] = signature
    if previous[path] ~= signature then
        local row = {sample = s.index, path = path, value_type = t, identity = id,
            change = previous[path] and 'changed' or 'added',
            relevant = relevant(key), namespace = node.mode}
        if t == 'function' and type(getinfo) == 'function' then
            local success, info = pcall(getinfo, value, 'S')
            if success and type(info) == 'table' then
                row.function_kind = rawget(info, 'what')
                row.source = string.sub(rawget(info, 'short_src') or '', 1, 240)
                row.line = rawget(info, 'linedefined')
            end
        end
        emit('member', row); s.changes = s.changes + 1
    end
    if t == 'table' and node.depth < 2 then
        if (node.mode == 'engine' and (api_roots[key] or relevant(key)))
            or (node.mode ~= 'engine' and relevant(key)) then
            enqueue(s, value, path, 'members', node.depth + 1)
        end
    end
end
local function finish_sample()
    local s = current
    -- Missing members are not called deleted: enumeration is incremental and bounded.
    previous = s.state
    emit('sample_end', {sample = s.index, elapsed = elapsed, started = s.started,
        entries = s.entries, tables = #s.queue, changed = s.changes,
        truncated = s.truncated, ticks = s.ticks, cpu_seconds = s.cpu,
        max_tick_cpu_seconds = s.max_tick, deletion_inference = false})
    file:flush()
    sample_index = sample_index + 1; current = nil
end
local function stop(reason)
    M.status = reason
    if wrapper and rawget(_G, 'update') == wrapper then
        rawset(_G, 'update', previous_update)
    end
    pcall(emit, 'probe_stop', {reason = reason, elapsed = elapsed})
    pcall(function() file:flush(); file:close() end)
end
validate_unit_id_api = function(stage)
    local engine = rawget(_G, 'stingray')
    local ids = type(engine) == 'table' and rawget(engine, 'IdString64')
    local app = type(engine) == 'table' and rawget(engine, 'Application')
    local from_hex = type(ids) == 'table' and rawget(ids, 'from_hex')
    local to_hex = type(ids) == 'table' and rawget(ids, 'to_hex')
    local can_get = type(app) == 'table' and rawget(app, 'can_get')
    if type(from_hex) ~= 'function' or type(to_hex) ~= 'function'
        or type(can_get) ~= 'function' then
        emit('unit_id_api', {stage = stage, available = false}); return
    end
    local known_hex = '17da843a4908511e'
    local converted, id = pcall(from_hex, known_hex)
    local roundtrip, hex = false, nil
    if converted then roundtrip, hex = pcall(to_hex, id) end
    local queried, found = false, nil
    if converted then queried, found = pcall(can_get, 'unit', id) end
    local found_truthy = nil
    if queried then found_truthy = not not found end
    emit('unit_id_api', {stage = stage, available = true, known_unit = known_hex,
        conversion_ok = converted, roundtrip_ok = roundtrip,
        roundtrip_hex = roundtrip and type(hex) == 'string' and hex or nil,
        can_get_call_ok = queried,
        can_get_result_type = queried and type(found) or nil,
        can_get_truthy = found_truthy,
        exact_id_object_used = converted})
end
local function tick()
    if not current then
        if sample_index > #schedule then stop('complete'); return end
        if elapsed < schedule[sample_index] then return end
        start_sample()
    end
    local s, began = current, clock()
    for _ = 1, limits.per_tick do
        local node = s.queue[s.head]
        if not node or s.entries >= limits.entries then
            if node then s.truncated = true end
            break
        end
        -- next/rawget avoid __pairs and __index. Mutation can invalidate a saved
        -- next key; abandon that table without propagating an error to gameplay.
        local success, key, value = pcall(next, node.value, node.key)
        if not success or key == nil or node.count >= limits.per_table then
            if not success or key ~= nil then s.truncated = true end
            s.head = s.head + 1
        else
            node.key = key; node.count = node.count + 1
            s.entries = s.entries + 1; observe(s, node, key, value)
        end
        if clock() - began >= 0.001 then break end
    end
    local duration = clock() - began
    s.cpu = s.cpu + duration; s.ticks = s.ticks + 1
    s.max_tick = math.max(s.max_tick, duration)
    if s.head > #s.queue or s.entries >= limits.entries then finish_sample() end
end
local initialized, err = pcall(function()
    emit('probe_start', {version = M.version, loader_api = rawget(loader, 'api'),
        loader_version = rawget(loader, 'version'), native_memory_access = false,
        invokes_discovered_functions = 'allowlisted_id_conversion_and_can_get_only',
        reads_scalar_values = false,
        sample_count = #schedule, entry_budget = limits.entries,
        frame_entry_budget = limits.per_tick, frame_cpu_target_ms = 1})
    validate_unit_id_api('startup')
    file:flush()
    if type(previous_update) ~= 'function' then stop('update_unavailable'); return end
    wrapper = function(dt, ...)
        if M.status == 'active' then
            if type(dt) == 'number' and dt == dt and dt >= 0 and dt < 10 then
                elapsed = elapsed + dt
            end
            local success, reason = pcall(tick)
            if not success then
                pcall(emit, 'probe_error', {reason = tostring(reason)})
                stop('probe_error')
            end
        end
        return previous_update(dt, ...)
    end
    M.status = 'active'; rawset(_G, 'update', wrapper)
end)
if not initialized then stop('initialization_failed'); M.error = tostring(err) end
return M
