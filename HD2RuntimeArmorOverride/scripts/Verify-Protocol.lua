-- 离线验证：把一份协议文件喂给正式解析器，确认它合法、并把关键量打出来。
--
-- 为什么值得单独跑：协议是**管理器编译、Lua 消费**的边界，格式错了 Lua 只会拒绝整份文件
-- （revision 不推进、游戏里什么都不变），而那种失败在游戏里看起来像"没生效"，
-- 会被误读成"这个配置也不管用"。所以每次手写协议之前都先用真解析器过一遍。
--
-- 用法：luajit.exe Verify-Protocol.lua <armor_registry_protocol.lua> <协议文件>
local protocol = dofile(assert(arg[1]))
local file = assert(io.open(assert(arg[2]), 'rb'))
local text = file:read('*a')
file:close()

local document = protocol.parse(text)
local pieces, byBody, banks = 0, {}, {}
for _, model in ipairs(document.models) do
    for _, piece in ipairs(model.pieces) do
        pieces = pieces + 1
        byBody[piece.body] = (byBody[piece.body] or 0) + 1
        if 1 <= piece.slot and piece.slot <= 9 then
            local bank = piece.piece_type == 0 and 0 or (piece.piece_type == 1 and 1 or 2)
            local key = piece.body .. ':' .. piece.slot .. ':' .. bank
            banks[key] = (banks[key] or 0) + 1
        end
    end
end

local aliases, held = 0, 0
for _, count in pairs(banks) do
    if count > 1 then aliases = aliases + 1; held = held + count end
end

print('解析成功')
print('  schema=' .. tostring(document.schema) .. ' revision=' .. tostring(document.revision)
    .. ' enabled=' .. tostring(document.enabled))
print('  models=' .. #document.models .. ' model_pieces=' .. pieces
    .. ' mappings=' .. #document.mappings .. ' piece_overrides=' .. #(document.piece_overrides or {}))
local bodies = {}
for body, count in pairs(byBody) do bodies[#bodies + 1] = body .. 'x' .. count end
table.sort(bodies)
print('  每个 body 的 Piece 数：' .. table.concat(bodies, ' '))
print('  (body,slot,bank) 别名组 ' .. aliases .. ' 个，被压住 ' .. held .. ' 个')
print('  composition_mode=' .. tostring(document.composition_mode or 'full'))
