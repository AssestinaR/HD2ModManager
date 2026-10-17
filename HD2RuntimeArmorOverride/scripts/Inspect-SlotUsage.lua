-- 原版护甲数据里，每个 (body, slot) 到底装了什么、装了几个。
--
-- 为什么要这个：`Inspect-CompositionLimits.lua` 只给出全局最大值
-- （max_pieces_per_body_slot=3、piece_types=0,1,2）。要判断"配件能不能摊到别的 slot 上"，
-- 必须知道**哪些 slot 真的在用、每个 slot 在原版里承担什么**，而不是只看最大值。
--
-- 用法：luajit.exe Inspect-SlotUsage.lua src/composition_schema.lua <armor_sets>
local schema = dofile(assert(arg[1]))
local file = assert(io.open(assert(arg[2]), 'rb'))
local kits = schema.library(file:read('*a'))
file:close()

local perSlot = {}      -- [slot] = {kits=, pieces=, bodies={}, types={}}
local perBodySlot = {}  -- [body..':'..slot] = count
local typeHist = {}     -- [type] = count
for _, kit in ipairs(kits) do
    local seenSlot = {}
    for _, piece in ipairs(kit.pieces) do
        local slot, kind, body = piece.slot, piece.pieceType, piece.rawBodyType
        local e = perSlot[slot]
        if not e then e = {kits = 0, pieces = 0, types = {}, bodies = {}}; perSlot[slot] = e end
        e.pieces = e.pieces + 1
        e.types[kind] = (e.types[kind] or 0) + 1
        e.bodies[body] = (e.bodies[body] or 0) + 1
        if not seenSlot[slot] then seenSlot[slot] = true; e.kits = e.kits + 1 end
        local key = body .. ':' .. slot
        perBodySlot[key] = (perBodySlot[key] or 0) + 1
        typeHist[kind] = (typeHist[kind] or 0) + 1
    end
end

local function keys(t)
    local a = {}
    for k in pairs(t) do a[#a + 1] = tostring(k) end
    table.sort(a, function(x, y) return tonumber(x) < tonumber(y) end)
    return table.concat(a, ',')
end
local function counts(t)
    local a = {}
    for k, v in pairs(t) do a[#a + 1] = tostring(k) .. 'x' .. v end
    table.sort(a)
    return table.concat(a, ' ')
end

print('kit_count=' .. #kits)
print('piece_type_histogram: ' .. counts(typeHist))
print()
print('slot  kits  pieces  bodies        pieceTypes')
for slot = 0, 15 do
    local e = perSlot[slot]
    if e then
        print(string.format('%-5d %-5d %-7d %-13s %s', slot, e.kits, e.pieces, counts(e.bodies), counts(e.types)))
    end
end
print()
print('每个 (body, slot) 的 piece 数（只列出现过的）：')
local a = {}
for k, v in pairs(perBodySlot) do a[#a + 1] = { k, v } end
table.sort(a, function(x, y) return x[1] < y[1] end)
for _, row in ipairs(a) do
    io.write(string.format('%s=%d  ', row[1], row[2]))
end
print()
print()
print('--- slot 8 / 9 具体长什么样（前 12 条）---')
local shown = 0
for _, kit in ipairs(kits) do
    for _, piece in ipairs(kit.pieces) do
        if piece.slot == 8 or piece.slot == 9 then
            local res = {}
            for role, id in pairs(piece.resources) do res[#res + 1] = role end
            table.sort(res)
            print(string.format('  itemId=%d body=%d slot=%d type=%d unit=%s tone=%d resources=%s',
                kit.itemId, piece.rawBodyType, piece.slot, piece.pieceType,
                piece.unit:sub(1, 16), piece.toneVariations, table.concat(res, ',')))
            shown = shown + 1
            if shown >= 12 then return end
        end
    end
end
if shown == 0 then print('  （没有任何 Kit 使用 slot 8 或 9）') end
