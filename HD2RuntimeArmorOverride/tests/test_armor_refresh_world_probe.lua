local W=assert(loadfile('HD2RuntimeArmorOverride/src/armor_refresh_world_probe.lua'))()
local function fixture(options)
    options=options or {}
    local time,rows,calls=0,{},{}
    local world,unit={},{}
    local function called(name) calls[name]=(calls[name] or 0)+1 end
    local globals={stingray={
        Application={worlds=function()
            called('worlds')
            if options.world_count then
                local list={}
                for i=1,options.world_count do list[i]=world end
                return list
            end
            return options.worlds or {world}
        end,
            main_world=function()error('must not select main_world')end},
        EntityManager={unit_component=function(w)assert(w==world);return world end},
        UnitComponent={unit=function(c,e)assert(c==world and e==unit);called('entity_unit');return unit end},
        World={entities=function(w)assert(w==world);called('entities');return options.empty_entities and {} or {unit} end,
            num_units=function(w)assert(w==world);called('count');return options.count or 1 end,
            units=function(w)
                assert(w==world);called('units')
                local list={}
                for i=1,(options.count or 1) do list[i]=unit end
                return list
            end},
        IdString64={to_hex=function(id)
            assert(id=='#IDPRE_!\1\2\3\4\5\6\7\8')
            called('to_hex');return '0807060504030201'
        end},
        Vector3={x=function(v)assert(v==world);return 12 end,
            y=function(v)assert(v==world);return 34 end,
            z=function(v)assert(v==world);return 56 end},
        Unit={id=function(u)assert(u==unit);called('id');return options.level_id end,
            has_data=function(u,key)
                assert(u==unit and type(key)=='string');called('has_data')
                if options.bad_has_data then return 1 end
                return key==options.present_key
            end,
            get_data=function(u,key)
                assert(u==unit and key==options.present_key);called('get_data')
                return options.data_value
            end,
            world_position=function(u,node)
                assert(u==unit and node==1);called('position')
                if options.bad_position then return nil end
                return world
            end,
            alive=function(u)assert(u==unit);called('alive');return options.alive~=false end,
            name_hash=function(u)assert(u==unit);called('hash')
                if options.throw then error('test') end
                return options.hash or '9841b16544c8fbae'
            end}
    }}
    local probe=W.new({globals=globals,clock=options.clock or function()time=time+0.000001;return time end,
        inspect_binding=options.inspect_binding,
        inspect_unit=options.inspect_unit,
        emit=function(r)rows[#rows+1]=r end,identity=function(x)return x==world and 'world:1' or 'unit:2'end,
        flush=function()end})
    return probe,rows,calls
end
local p,rows,calls=fixture()
p.tick('worlds')
assert(p.worlds_ok and not calls.units and not calls.hash)
p.tick('units')
assert(p.units_seen==1 and p.hashes_seen==1 and calls.hash==1)
assert(rows[#rows].kind=='unit_batch')
local p2,r2,c2=fixture({alive=false});p2.tick('units')
assert(p2.units_seen==0 and not c2.hash)
local p3,r3,c3=fixture({count=5000});p3.tick('units');p3.tick('units')
assert(p3.errors==1 and not c3.units and c3.worlds==1)
local p4,r4,c4=fixture({throw=true});p4.tick('units');p4.tick('units')
assert(p4.errors==1 and c4.hash==1)
local p5=fixture({worlds={}});p5.tick('worlds');assert(not p5.worlds_ok)
local p6,r6=fixture({hash='\1\2\3\4\5\6\7\8'});p6.tick('units')
local found=false
for _,r in ipairs(r6) do if r.kind=='unit_observation' then
    assert(r.resource_hash=='0102030405060708' and r.hash_encoding=='raw_bytes_in_order');found=true end end
assert(found)
local p7=fixture({hash=123});p7.tick('units')
assert(p7.errors==0 and p7.units_seen==1 and p7.hashes_seen==0)
local ps,rs=fixture({hash='#ID[9841b16544c8fbae]'});ps.tick('units')
assert(ps.errors==0 and ps.units_seen==1 and ps.hashes_seen==0)
assert(rs[#rs-1].hash_type=='string' and rs[#rs-1].hash_length==21)
local pt,rt,ct=fixture({hash='#IDPRE_!\1\2\3\4\5\6\7\8'});pt.tick('units')
assert(pt.hashes_seen==1 and ct.to_hex==1)
assert(rt[#rt-1].resource_hash=='0807060504030201' and rt[#rt-1].hash_encoding=='engine_to_hex')
local p8,r8,c8=fixture({world_count=12})
for i=1,12 do p8.tick('worlds') end
assert(p8.errors==0 and p8.world_count==12 and p8.world_queries==12 and c8.count==12)
assert(r8[#r8].world_index==12 and not p8.world_list_capped)
local p9,r9=fixture({world_count=140})
for i=1,129 do p9.tick('worlds') end
assert(p9.errors==0 and p9.world_count==128 and p9.world_list_capped)
assert(r9[#r9].world_index==1 and r9[#r9].cycle==2)
local p10=fixture({worlds={false}});p10.tick('worlds')
assert(p10.errors==1 and p10.last_error=='Application.worlds:unexpected_world_handle')
local pf,rf,cf=fixture({count=2200,world_count=2,level_id='1234567890abcdef'})
pf.tick('focus');pf.tick('focus');pf.tick('focus')
local batches={}
for _,r in ipairs(rf) do if r.kind=='unit_batch' then batches[#batches+1]=r end end
assert(#batches==3 and batches[1].world_index==1 and batches[2].world_index==2)
assert(batches[1].first_index==1945 and batches[3].first_index==batches[1].next_index)
assert(cf.id and not calls.id and batches[3].coverage_floor==1945)
local pn,rn=fixture();pn.tick('focus')
assert(pn.errors==0 and rn[#rn-1].level_id_type=='nil')
local pe,re,ce=fixture();pe.tick('entities')
assert(pe.errors==0 and pe.units_seen==1 and ce.entity_unit==1 and not ce.units)
assert(re[#re].kind=='entity_unit' and re[#re].resource_hash=='9841b16544c8fbae')
local pem,rem,cem=fixture({empty_entities=true});pem.tick('entities')
assert(pem.errors==0 and pem.units_seen==0 and not cem.entity_unit)
local psp,rsp,csp=fixture({count=2200})
psp.tick('spatial')
assert(psp.errors==0 and csp.position==32 and not csp.id)
local positions=0
for _,r in ipairs(rsp) do
    if r.kind=='unit_observation' then
        assert(r.position_x==12 and r.position_y==34 and r.position_z==56
            and r.position_node==1 and r.focus_tail)
        positions=positions+1
    end
end
assert(positions==32)
local pbad=fixture({bad_position=true,count=256})
pbad.tick('spatial')
assert(pbad.errors==1 and pbad.last_error=='Unit.world_position:unexpected_vector')
local pempty,rempty,cempty=fixture({count=1})
pempty.tick('spatial')
assert(pempty.largest_world_units==1 and pempty.units_seen==0 and not cempty.position)
local ptr,rtr,ctr=fixture({count=2200,world_count=2})
for _=1,12 do ptr.tick('spatial') end
local first_spatial,groups={},{}
for _,row in ipairs(rtr) do
    if row.kind=='unit_batch' then
        groups[#groups+1]=row
        first_spatial[#first_spatial+1]=row.first_index
        assert(row.world_index==1 and row.focus_tail)
    end
end
assert(#groups==12 and first_spatial[1]==1945 and first_spatial[2]==groups[1].next_index)
assert(first_spatial[9]==1945 and ctr.position==12*32)
local po,ro,co=fixture({count=256,hash='4d1c334d294dfa97'})
for _=1,15 do po.tick('owner_data') end
assert(po.errors==0 and po.owner_candidates==1 and po.owner_queries==10
    and po.owner_present==0 and co.has_data==10 and not co.get_data and not co.position)
local missing,rm,cm=fixture({count=256})
missing.tick('owner_data')
assert(missing.owner_candidates==0 and not cm.has_data)
local got_table=setmetatable({owner={},PRIVATE_NAME='secret'},{
    __index=function()error('metatable access forbidden')end,
    __pairs=function()error('metatable iteration forbidden')end})
local pp,rp,cp=fixture({count=256,hash='4d1c334d294dfa97',
    present_key='extensions',data_value=got_table})
pp.tick('owner_data')
assert(pp.owner_present==1 and cp.get_data==1 and pp.owner_queries==1)
local got=false
for _,row in ipairs(rp) do
    if row.kind=='owner_data' then
        got=true;assert(row.key=='extensions' and row.value_type=='table'
            and row.member_keys=='owner:table' and row.ownership_proven==false)
    end
end
assert(got)
local pb,rb,cb=fixture({count=256,hash='4d1c334d294dfa97',bad_has_data=true})
pb.tick('owner_data');pb.tick('owner_data')
assert(pb.errors==1 and cb.has_data==1 and not cb.get_data)
local inspected=0
local pi,ri,ci=fixture({inspect_binding=function(f)
    assert(type(f)=='function');inspected=inspected+1
    return {state='captured',rva=123,code_hex='c3',byte_count=1}
end})
for _=1,12 do pi.tick('bindings') end
local binding_rows=0
for _,row in ipairs(ri) do
    if row.kind=='binding_inspection' then
        binding_rows=binding_rows+1;assert(row.called==false)
    end
end
assert(binding_rows==7 and inspected==2 and next(ci)==nil,
    'binding inspection invoked a game API or repeated')
local native_calls=0
local no,nr,nc=fixture({count=256,hash='4d1c334d294dfa97',
    inspect_unit=function(_,u)
        assert(u~=nil);native_calls=native_calls+1
        return {state='resolved_candidate',relationship_proven=false}
    end})
for _=1,12 do no.tick('owner_native') end
assert(native_calls==1 and nc.alive and not nc.position and not nc.has_data)
local emitted=0
for _,row in ipairs(nr) do
    assert(row.kind~='unit_observation','native mode emitted redundant per-frame observations')
    if row.kind=='native_owner_candidate' then
        emitted=emitted+1;assert(row.called==false and row.relationship_proven==false)
    end
end
assert(emitted==1)
local unmatched=fixture({count=256,inspect_unit=function()error('unmatched resource inspected')end})
unmatched.tick('owner_native');assert(unmatched.errors==0)
-- Temporal capture revisits fresh handles, preserves original one-shot mode,
-- includes failed reads, and cannot grow without bound.
local trace_time,trace_calls=0,0
local tr,tr_rows,tr_calls=fixture({count=256,hash='4d1c334d294dfa97',
    clock=function()return trace_time end,
    inspect_unit=function()
        trace_calls=trace_calls+1
        if trace_calls==3 then return {state='generation_mismatch'} end
        return {state='resolved_candidate',object='0x10000',object_world='0x20000',
            handle_index=3,handle_generation=2,association_state='complete',
            association_count=1,association_objects=trace_calls==1 and '0x30000' or '0x40000',
            method_code_hex='large_code'}
    end})
tr.tick('owner_trace');tr.tick('owner_trace')
assert(trace_calls==1,'trace rate gate ignored')
trace_time=0.26;tr.tick('owner_trace')
trace_time=0.52;tr.tick('owner_trace')
local trace_rows={}
for _,r in ipairs(tr_rows) do if r.kind=='native_owner_trace' then trace_rows[#trace_rows+1]=r end end
assert(#trace_rows==3 and trace_rows[1].first_observation and trace_rows[2].changed
    and trace_rows[2].prior_sample==1 and trace_rows[3].state=='generation_mismatch'
    and not trace_rows[1].method_code_hex and trace_rows[1].lifetime_proven==false)
for _=1,200 do trace_time=trace_time+0.26;tr.tick('owner_trace') end
assert(trace_calls==160 and not tr_calls.position and not tr_calls.has_data)
print('PASS: native read allowlist, staged queries, caps, dead Units, blocked retries and exact hash bytes')
