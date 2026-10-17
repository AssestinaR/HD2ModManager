-- 反查：哪些 Kit 引用了某个 Unit？用来确认"这个 Unit 键换成我们的模型后，谁会变"。
--
-- 交付同键替换包时这是最后一道安全检查：目标 Unit 必须只被预期的护甲引用。
--
-- 用法：luajit.exe Inspect-UnitOwner.lua <schema> <armor_sets> <unitId...>
local schema = dofile(assert(arg[1]))
local file = assert(io.open(assert(arg[2]), 'rb'))
local kits = schema.library(file:read('*a'))
file:close()
if not arg[3] then print('用法：Inspect-UnitOwner.lua <schema> <armor_sets> <unitId...>'); os.exit(2) end

local byUnit = {}
for _, kit in ipairs(kits) do
    for _, piece in ipairs(kit.pieces) do
        byUnit[piece.unit] = byUnit[piece.unit] or {}
        local entry = byUnit[piece.unit]
        entry[#entry + 1] = { kit = kit, piece = piece }
    end
end

for index = 3, #arg do
    local unit = arg[index]
    local hits = byUnit[unit]
    if not hits then
        print(string.format('%s  -> 没有任何 Kit 引用它（替换它对护甲外观没有影响）', unit))
    else
        print(string.format('%s  -> %d 处引用', unit, #hits))
        for _, hit in ipairs(hits) do
            local kit, piece = hit.kit, hit.piece
            print(string.format('    itemId=%-12d setId=%-12d kitType=%d  body=%d slot=%d type=%d',
                kit.itemId, kit.setId, kit.kitType, piece.rawBodyType, piece.slot, piece.pieceType))
        end
    end
end
