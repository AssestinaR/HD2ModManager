local schema = dofile(arg[1])
local input = assert(io.open(arg[2],'rb'))
local data = input:read('*a'); input:close()
local kits = schema.library(data)
local selected = {
    [488691438]=true, [820333399]=true,
    [3340614564]=true, [2546928266]=true
}
-- RE-824 is selected by archive to avoid guessing its item ID.
for _,kit in ipairs(kits) do
    if selected[kit.itemId] or kit.archive == 'f50c7fd427839c47' then
        for _,p in ipairs(kit.pieces) do
            local resources = {}
            for role,id in pairs(p.resources) do resources[#resources+1]=role..'='..id end
            table.sort(resources)
            print(table.concat({kit.itemId,kit.archive,p.rawBodyType,p.slot,
                p.pieceType,p.weight,p.toneVariations,p.unit,
                table.concat(resources,',')},'\t'))
        end
    end
end
