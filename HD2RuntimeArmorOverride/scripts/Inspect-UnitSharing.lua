-- 某个 Kit 的 Unit 有没有被别的 Kit 共用？以及同 setId 的变体有几个？
--
-- 为什么必须先查：同键替换（把我们的 Unit 写到目标护甲的 Unit id 上）是"简单安装"唯一可行的做法，
-- 但它**不区分引用者**。只要某个 Unit 还被别的护甲引用，替换它就会把那件护甲也一起改掉。
-- 卖出去的包不能有这种副作用，所以要先知道每个 Unit 的引用范围。
--
-- 用法：luajit.exe Inspect-UnitSharing.lua src/composition_schema.lua <armor_sets> <itemId>
local schema = dofile(assert(arg[1]))
local file = assert(io.open(assert(arg[2]), 'rb'))
local kits = schema.library(file:read('*a'))
file:close()
if not arg[3] then print('用法：Inspect-UnitSharing.lua <schema> <armor_sets> <itemId>'); os.exit(2) end
local wanted = tonumber(arg[3])

local target
for _, kit in ipairs(kits) do if kit.itemId == wanted then target = kit break end end
if not target then print('找不到 itemId=' .. tostring(wanted)); os.exit(1) end

-- unit -> 引用它的 kit 集合
local owners = {}
for _, kit in ipairs(kits) do
    for _, piece in ipairs(kit.pieces) do
        owners[piece.unit] = owners[piece.unit] or {}
        owners[piece.unit][kit.itemId] = true
    end
end

print(string.format('目标 itemId=%d  setId=%d  pieces=%d', target.itemId, target.setId, #target.pieces))

-- 同 setId 的其它变体
local siblings = {}
for _, kit in ipairs(kits) do
    if kit.setId == target.setId and kit.itemId ~= target.itemId then
        siblings[#siblings + 1] = kit.itemId
    end
end
table.sort(siblings)
print(string.format('同 setId(%d) 的其它 Kit：%d 个  %s',
    target.setId, #siblings, table.concat(siblings, ',', 1, math.min(10, #siblings))))

print()
print('unit               独占/共用  引用它的 kit 数  其它 kit')
local shared, exclusive = 0, 0
for _, piece in ipairs(target.pieces) do
    local set = owners[piece.unit]
    local list, others = {}, {}
    for id in pairs(set) do list[#list + 1] = id; if id ~= target.itemId then others[#others + 1] = id end end
    table.sort(list); table.sort(others)
    if #others == 0 then exclusive = exclusive + 1 else shared = shared + 1 end
    print(string.format('  %s  %-9s %-14d %s', piece.unit,
        #others == 0 and '独占' or '共用', #list,
        table.concat(others, ',', 1, math.min(6, #others))))
end
print()
print(string.format('独占 %d 个，共用 %d 个', exclusive, shared))
