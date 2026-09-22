-- Offline observations only; maxima are not engine capacity limits.
local schema=dofile(assert(arg[1]))
local file=assert(io.open(assert(arg[2]),'rb'))
local kits=schema.library(file:read('*a'));file:close()
local total,maxKit,maxBody,maxSlot,duplicateKeys=0,0,0,0,0
local biggest,slots,types={}, {}, {}
for _,kit in ipairs(kits) do
    total=total+#kit.pieces
    if #kit.pieces>maxKit then maxKit=#kit.pieces;biggest={} end
    if #kit.pieces==maxKit then biggest[#biggest+1]=tostring(kit.itemId) end
    for _,body in ipairs(kit.bodies) do maxBody=math.max(maxBody,body.count) end
    local perSlot,perKey={}, {}
    for _,p in ipairs(kit.pieces) do
        local slotKey=p.rawBodyType..':'..p.slot
        local key=slotKey..':'..p.pieceType
        perSlot[slotKey]=(perSlot[slotKey] or 0)+1
        maxSlot=math.max(maxSlot,perSlot[slotKey])
        perKey[key]=(perKey[key] or 0)+1
        if perKey[key]==2 then duplicateKeys=duplicateKeys+1 end
        slots[p.slot]=true;types[p.pieceType]=true
    end
end
local function list(t)
    local a={};for n in pairs(t) do a[#a+1]=n end
    table.sort(a);return table.concat(a,',')
end
print('kit_count='..#kits)
print('piece_count='..total)
print('max_pieces_per_kit='..maxKit)
print('max_pieces_per_body='..maxBody)
print('max_pieces_per_body_slot='..maxSlot)
print('duplicate_body_slot_piece_type_keys='..duplicateKeys)
print('observed_slots='..list(slots))
print('observed_piece_types='..list(types))
print('max_kit_item_ids='..table.concat(biggest,','))
print('engine_capacity_proven=false')
