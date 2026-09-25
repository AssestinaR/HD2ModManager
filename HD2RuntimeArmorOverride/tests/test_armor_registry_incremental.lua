local root='HD2RuntimeArmorOverride/src/'
local T=assert(loadfile(root..'armor_registry_transaction.lua'))()
local C=assert(loadfile(root..'armor_registry_composition.lua'))()
local metadata=assert(loadfile(root..'armor_metadata_probe_core.lua'))()
local ffi=require('ffi')
local memory={[8]=100,[16]=200}
local allocations,writes=0,0
local fail_slot,fail_rollback
local tx=T.new({
    read=function(slot)return memory[slot]end,
    writable=function()return true end,
    write=function(slot,value,expected)
        assert(memory[slot]==expected)
        writes=writes+1
        -- Simulate a write which succeeds but reports failure.
        memory[slot]=value
        if slot==fail_slot then
            if not fail_rollback then fail_slot=nil end
            return false
        end
        return true
    end})
local function request(key,signature)
    return {key=tostring(key),signature=signature,slot=key*8,original=key*100,bytes=96,
        build=function()
            allocations=allocations+1
            local buffer=ffi.new('uint8_t[96]')
            return {buffer=buffer,bytes=96,base=1000+allocations*100}
        end}
end
local function apply(requests)
    local p=assert(tx.prepare(requests))
    assert(tx.commit(p))
    return p.stats
end
assert(apply({request(1,'a'),request(2,'a')}).built==2)
local a=tx.active['1']
local s=apply({request(2,'a'),request(1,'a')})
assert(s.built==0 and s.writes==0 and s.reused==2 and allocations==2)
s=apply({request(1,'b'),request(2,'a')})
assert(s.built==1 and s.writes==1 and s.reused==1)
local weak=setmetatable({a.buffer},{__mode='v'});a=nil
collectgarbage('collect');assert(weak[1],'retired buffer collected')
s=apply({request(1,'a')})
assert(s.built==0 and s.writes==2 and s.removed==1,'A/B/A must reuse retained clone')
local active=tx.active
local before=memory[8]
fail_slot=16
local plan=assert(tx.prepare({request(1,'c'),request(2,'c')}))
assert(not tx.commit(plan))
assert(memory[8]==before and memory[16]==200 and tx.active==active)
assert(tx.retained_count==5,'even transiently published clones must stay pinned')
memory[8]=9999
assert(not tx.commit(assert(tx.prepare({request(1,'a')}))))
memory[8]=before
assert(apply({}).removed==1 and memory[8]==100)
assert(tx.retained_count==5)
local limited=T.new({read=function()return 100 end,write=function()error('must not write')end},{bytes=1})
assert(not limited.prepare({request(1,'x')}))
local failed=request(1,'bad');failed.build=function()return nil,'expected validation failure'end
local p,e=tx.prepare({failed})
assert(not p and e:find('expected validation failure',1,true))
-- External changes between preparation and commit leave every slot untouched.
local stale=assert(tx.prepare({request(1,'a')}))
apply({request(1,'b')})
assert(not tx.commit(stale))
-- A partial native publication followed by failed rollback permanently blocks
-- further writes, while retaining every possibly exposed object.
fail_slot=16;fail_rollback=true
local fault=assert(tx.prepare({request(1,'c'),request(2,'c')}))
assert(not tx.commit(fault))
assert(tx.faulted and not tx.prepare({request(1,'a')}))
collectgarbage('collect')
assert(tx.pool['2']['c'].buffer~=nil)

local function piece(id,slot,weight)
    local data=ffi.new('uint64_t[1]',id)
    return ffi.string(data,8)..C.pack(slot,4)..C.pack(0,4)..C.pack(weight,4)
        ..string.rep('\0',68)..string.char(2)..string.rep('\0',7)
end
local unit=0x9841b16544c8fbaeULL
assert(C.unit(piece(unit,2,0))=='9841b16544c8fbae')
local function snapshot(item,weight)
    return {header=C.pack(item,4)..string.rep('\0',60),rows={
        {body=0,header=C.pack(0,8),pieces={piece(unit,2,weight),piece(3ULL,6,weight)}},
        {body=1,header=C.pack(1,8),pieces={piece(4ULL,2,weight)}}}}
end
local target,source=snapshot(1,0),snapshot(2,2)
local yes=function()return true end
local first=assert(C.plan(target,source,nil,yes,metadata))
assert(first.pieces==3 and first.payload:sub(1,48)==target.header:sub(1,48))
assert(C.u32(first.payload,64+48+16)==0,'target gameplay weight was not preserved')
local filtered=assert(C.plan(target,source,nil,function(_,id)return id~='9841b16544c8fbae'end,metadata))
assert(filtered.pieces==2 and first.payload~=filtered.payload)
assert(first.payload==assert(C.plan(target,source,nil,yes,metadata)).payload)
assert(not C.plan(target,source,nil,function()return false end,metadata))
local model={pieces={{body=3,unit='9841b16544c8fbae',slot=2,piece_type=0,tone=3},
    {body=0,unit='0000000000000003',slot=6,piece_type=0}}}
local custom=assert(C.plan(target,nil,model,yes,metadata))
assert(custom.bodies==2 and custom.pieces==2)
assert(C.u32(custom.payload,64)==3,'Any must precede Stocky')
assert(C.unit(custom.payload:sub(113))=='9841b16544c8fbae')
assert(custom.payload:byte(113+88)==3)
model.pieces[2].enabled=false
assert(assert(C.plan(target,nil,model,yes,metadata)).pieces==1)
print('PASS: incremental no-op/delta/remove/reuse, retained GC roots, rollback, limits, exact Unit IDs and composition')
-- Diagnostic B preserves every donor byte except Unit and target weight.
local donor=piece(unit,2,2)
donor=donor:sub(1,12)..C.pack(1,4)..donor:sub(17,24)
    ..string.rep('\123',64)..string.char(7)..string.rep('\45',7)
local diagnostic={pieces={{body=0,unit='6fe9c822322c0001',slot=2,piece_type=0,tone=0}},
    test_templates={['6fe9c822322c0001']={}}}
local donorSource={test_templates={['6fe9c822322c0001']={body=0,data=donor}}}
local b=assert(C.plan(target,donorSource,diagnostic,yes,metadata))
local actual=b.payload:sub(89,184)
assert(C.unit(actual)=='6fe9c822322c0001')
assert(C.u32(actual,12)==1 and C.u32(actual,16)==0)
assert(actual:sub(9,16)==donor:sub(9,16) and actual:sub(21)==donor:sub(21))
assert(not pcall(C.plan,target,nil,diagnostic,yes,metadata))
print('PASS: diagnostic donor template preserves native type, tone and auxiliary bytes')
