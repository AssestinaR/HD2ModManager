-- 导出某一个护甲 Kit 的完整 Piece 表（body / slot / type / unit id）。
--
-- 为什么要这个：做"同键替换"的交付包时，必须先知道目标护甲到底引用哪些 Unit：
--   哪些槽位要放我们的 Unit、剩下哪些 Unit 必须隐藏掉。
-- 判据全部来自游戏本体数据，不猜、也不抄参考 mod 的 id 列表。
--
-- 用法：luajit.exe Inspect-KitPieces.lua src/composition_schema.lua <armor_sets> <itemId>
local schema = dofile(assert(arg[1]))
local file = assert(io.open(assert(arg[2]), 'rb'))
local kits = schema.library(file:read('*a'))
file:close()
-- ⚠️ `assert(x, msg)` 在 Lua 里会**返回全部参数**，所以不能写 tonumber(assert(a,b))：
-- 那样等于 tonumber(x, msg)，第二个参数被当成进制。
if not arg[3] then print('用法：Inspect-KitPieces.lua <schema> <armor_sets> <itemId>'); os.exit(2) end
local wanted = tonumber(arg[3])
if not wanted then print('itemId 不是数字：' .. tostring(arg[3])); os.exit(2) end

local kit
for _, candidate in ipairs(kits) do
    if candidate.itemId == wanted then kit = candidate break end
end
if not kit then
    print('找不到 itemId=' .. wanted)
    local ids = {}
    for _, candidate in ipairs(kits) do ids[#ids + 1] = candidate.itemId end
    table.sort(ids)
    print('可用的 itemId 前 20 个：' .. table.concat(ids, ',', 1, math.min(20, #ids)))
    os.exit(1)
end

print(string.format('itemId=%d  dlcId=%d  setId=%d  kitType=%d  archive=%s',
    kit.itemId, kit.dlcId, kit.setId, kit.kitType, kit.archive))
print(string.format('bodies=%d  pieces=%d', #kit.bodies, #kit.pieces))
print()
print('行  body  slot  type  unit                tone  资源')
-- ⚠️ schema 的 M.kit 已经把 Piece **摊平**到 kit.pieces（每片自带 rawBodyType），
-- kit.bodies 只是 {rawBodyType, offset, count} 的描述。所以直接遍历 kit.pieces。
local seen = {}
for _, piece in ipairs(kit.pieces) do
    local res = {}
    for role, id in pairs(piece.resources) do res[#res + 1] = role end
    table.sort(res)
    print(string.format('    %-4d  %-4d  %-4d  %-4d  %s  %-4d  %s',
        piece.rawBodyType, piece.rawBodyType, piece.slot, piece.pieceType,
        piece.unit, piece.toneVariations, table.concat(res, ',')))
    seen[piece.unit] = (seen[piece.unit] or 0) + 1
end
print()
local units = {}
for unit, count in pairs(seen) do units[#units + 1] = { unit, count } end
table.sort(units, function(a, b) return a[1] < b[1] end)
print('不同 Unit 数 = ' .. #units)
for _, row in ipairs(units) do
    print(string.format('  %s  被 %d 个 Piece 引用', row[1], row[2]))
end
