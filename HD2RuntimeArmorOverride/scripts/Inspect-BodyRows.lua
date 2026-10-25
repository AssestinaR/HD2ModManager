-- 原版 Kit 的 Body 行结构：几行、body 值是什么、什么顺序、每行几个 Piece。
--
-- 为什么要这个：运行时 clone 的行顺序是 `rank={[3]=1,[0]=2,[1]=3}`，
-- 也就是 **body=3 永远排第一**。如果原生消费者按行位置索引、而原版数据的行顺序不是这样，
-- 那么"Any 行在前"就会让后面的行错位 —— 这正好能解释"身体（body=0/1 那些行）整片消失，
-- 而 body=3 的头盔活下来"。
--
-- 用法：luajit.exe Inspect-BodyRows.lua src/composition_schema.lua <armor_sets>
local schema = dofile(assert(arg[1]))
local file = assert(io.open(assert(arg[2]), 'rb'))
local kits = schema.library(file:read('*a'))
file:close()

local rowCounts, orders, perRowBody = {}, {}, {}
local maxRows = 0
for _, kit in ipairs(kits) do
    local order = {}
    for _, body in ipairs(kit.bodies) do order[#order + 1] = body.rawBodyType end
    local key = table.concat(order, ',')
    orders[key] = (orders[key] or 0) + 1
    rowCounts[#order] = (rowCounts[#order] or 0) + 1
    if #order > maxRows then maxRows = #order end
    for index, body in ipairs(kit.bodies) do
        perRowBody[index] = perRowBody[index] or {}
        perRowBody[index][body.rawBodyType] = (perRowBody[index][body.rawBodyType] or 0) + 1
    end
end

local function sortedPairs(t, numeric)
    local a = {}
    for k, v in pairs(t) do a[#a + 1] = { k, v } end
    table.sort(a, function(x, y)
        if numeric then return tonumber(x[1]) < tonumber(y[1]) end
        return x[1] < y[1]
    end)
    return a
end

print('kit_count=' .. #kits .. '  max_rows=' .. maxRows)
print()
print('每个 kit 有几行：')
for _, row in sortedPairs(rowCounts, true) do print('  ' .. row[1] .. ' 行 -> ' .. row[2] .. ' 个 kit') end
print()
print('行顺序（body 值序列）分布：')
for _, row in sortedPairs(orders, false) do
    if row[2] >= 5 then print(string.format('  [%-12s] %d 个 kit', row[1], row[2])) end
end
print()
print('第 N 行的 body 值分布：')
for _, row in sortedPairs(perRowBody, true) do
    local parts = {}
    for _, inner in sortedPairs(row[2], true) do parts[#parts + 1] = 'body' .. inner[1] .. 'x' .. inner[2] end
    print(string.format('  第 %s 行：%s', row[1], table.concat(parts, '  ')))
end
print()
print('含 body=3 行的 kit 里，body=3 排在第几行：')
local pos = {}
for _, kit in ipairs(kits) do
    for index, body in ipairs(kit.bodies) do
        if body.rawBodyType == 3 then pos[index] = (pos[index] or 0) + 1 end
    end
end
for _, row in sortedPairs(pos, true) do print('  第 ' .. row[1] .. ' 行 -> ' .. row[2] .. ' 个 kit') end
