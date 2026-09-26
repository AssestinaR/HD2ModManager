local P=assert(loadfile('HD2RuntimeArmorOverride/src/armor_refresh_route_probe.lua'))()
local function p32(n)
    local t={} for _=1,4 do t[#t+1]=string.char(n%256);n=math.floor(n/256) end
    return table.concat(t)
end
local function put(s,o,b)return s:sub(1,o)..b..s:sub(o+#b+1)end
local function u32(s,o)local a,b,c,d=s:byte(o+1,o+4);return a and d and a+b*256+c*65536+d*16777216 end
local base=0x180000000
local function fixture(bad,missing)
    local image=string.rep('\0',0x40000)
    image=put(image,0x3c,p32(0x80))
    local h='PE\0\0'..string.rep('\0',260)
    h=put(h,6,'\1\0');h=put(h,20,'\240\0');h=put(h,24,'\11\2')
    h=put(h,8,p32(bad and 1 or 1790161983));h=put(h,0x50,p32(74727424))
    h=put(h,0xa0,p32(0x5000));h=put(h,0xa4,p32(24));image=put(image,0x80,h)
    local section=string.rep('\0',40)
    section=put(section,8,p32(0x20000));section=put(section,12,p32(0x10000))
    section=put(section,36,p32(0x60000020));image=put(image,0x188,section)
    image=put(image,0x5000,p32(0x1fff0)..p32(0x20100)..p32(0x6000)
        ..p32(0x22000)..p32(0x22100)..p32(0x6010))
    image=put(image,0x6000,'\1\0\0\0');image=put(image,0x6010,'\1\0\0\0')
    local site=0x1fffb
    image=put(image,site,'\x45\x38\x77\x29\x74\x14\x8b\x53\x08\x44\x8b\xc6'
        ..'\x48\x8b\x4c\x24\x78\xe8'..p32(0x22000-site-22)..'\x45\x88\x77\x29')
    local rows={}
    local probe=P.new({base=function()return base end,u32=u32,
        emit=function(r)rows[#rows+1]=r end,
        read=function(at,n)
            assert(n<=65600)
            if missing and at==base+0x10000 then return nil end
            local o=at-base
            if o<0 or o+n>#image then return nil end
            return image:sub(o+1,o+n)
        end})
    for _=1,200 do probe.tick();if probe.done then break end end
    assert(probe.done)
    return probe,rows
end
local probe,rows=fixture()
assert(probe.errors==0)
local labels={}
for _,r in ipairs(rows) do
    if r.kind=='route_candidate' then labels[r.label]=r.rva end
end
assert(labels.dirty_consume==0x1fffb and labels.dirty_direct_callee==0x22000)
assert(rows[#rows].complete and rows[#rows].candidates==2)
assert(fixture(true).errors==1)
assert(fixture(false,true).errors==1)
print('PASS: route fingerprint, cross-chunk anchor, callee displacement, bounded captures, read rejection')

local function identity_fixture(problem)
    local memory,rows={},{}
    local function add(at,b)memory[at]=b end
    local function p64(n)return p32(n%4294967296)..p32(math.floor(n/4294967296))end
    add(base+0x3c,p32(0x80))
    local h=string.rep('\0',0x58)
    h=put(h,0,'PE\0\0');h=put(h,8,p32(problem=='build' and 1 or 1790161983))
    h=put(h,0x50,p32(74727424));add(base+0x80,h)
    add(base+0x874f8f,'\x4c\x8b\x15\x62\x15\xab\x02')
    add(base+0x877b79,'\x4c\x8b\x2d\xa0\xf1\xaa\x02')
    add(base+0x874b4b,'\x48\x8b\x05\x16\x19\xab\x02')
    add(base+0x874c02,'\x8b\x53\x08\x44\x8b\xc6\x48\x8b\x4c\x24\x78\xe8\x9e\x2e\x00\x00')
    add(base+0x606654,'\x48\x8b\x15\x0d\xfe\xd1\x02')
    add(base+0xfd9ba4,'\x4c\x8b\x1d\xed\x23\x49\x02')
    add(base+0xfd9d50,'\x4c\x8b\x15\x41\x22\x49\x02')
    add(base+0xfd9c49,'\x8b\x40\x04\x48\x8d\x04\x40\x48\x8d\x80\xe4\x65\x1e\x00')
    add(base+0xfd9dd4,'\x8b\x40\x04\x48\x8d\x04\x40\x48\x8d\x80\xe3\x65\x1e\x00')
    add(base+0x874bfc,'\x45\x38\x77\x29\x74\x14\x8b\x53\x08\x44\x8b\xc6\x48\x8b\x4c\x24\x78\xe8\x9e\x2e\x00\x00\x45\x88\x77\x29')
    add(base+0x874dc0,'\x0f\x28\xd6\x8b\xd3\x48\x8b\xcf\xe8\x53\xf7\xff\xff\x8b\x87\xe8\x08\x00\x00\xff\xc3\x3b\xd8\x72\xe7')
    add(base+0x87734c,'\x89\x87\xac\x09\x00\x00')
    add(base+0x877362,'\x8b\x8f\xac\x09\x00\x00')
    add(base+0x8773a0,'\xff\x90\x90\x00\x00\x00')
    for _,rva in ipairs({0x606630,0xfd9ba0,0xfd9d40})do add(base+rva,string.rep('\xcc',1024))end
    local manager,player,avatar=0x29000000000,0x29100000000,0x29200000000
    add(base+0x33264f8,p64(manager));add(base+0x3326468,p64(player));add(base+0x3326d20,p64(avatar))
    local owner=0x29300000000
    add(base+0x346bf98,p64(owner))
    add(player+0xd0,string.rep('\0',20))
    add(manager+0x8e4,p32(problem=='count' and 5 or 1)..p32(1)..p32(0))
    add(player+0x84,p32(0)..p32(0));add(player+0x3a8,p32(0x7fff))
    local entry=manager+0x2000
    add(manager+0x948,p64(entry));add(entry,p32(0)..p32(0)..p32(0xfedcba98)..string.rep('\0',12))
    add(manager+0x96c,string.rep('\0',0x44));add(manager+0xa7c,string.rep('\0',0x40))
    add(avatar+0x6c,p32(0));add(avatar+0xf8,string.rep('\0',20))
    if problem=='joined' or problem=='nomatch' then
        local function map(at,key,index)
            local data=at+0x10000000
            add(at,p64(data)..p32(16)..p32(0xffffffff)..p32(0xfffffff1))
            local product=(key%65536)*0xfff1+
                (((key%65536)*0xffff+math.floor(key/65536)*0xfff1)%65536)*65536
            add(data+(product%4294967296%16)*8,
                p32(problem=='nomatch' and 0xffffffff or key)..p32(index))
        end
        map(player+0xd0,0xfedcba98,0)
        map(owner+0xf22ec8,164,7)
        map(owner+0xf1aeb0,251,7)
        map(avatar+0xf8,251,0)
        add(player+0x3a8,p32(164))
        local entity=p32(0x294dfa97)..p32(0x4d1c334d)..p32(251)..p32(2215)..p32(164)..p32(1)
        add(owner+0xf32f18+7*24,entity)
        add(avatar+0x6c,p32(1));add(avatar+0x110,p64(owner+0xf32f18+7*24))
        add(avatar+0x546ae3,'\1');add(avatar+0x546b4c,string.rep('\0',0x80))
    end
    local reads={}
    local probe=P.identity({base=function()return base end,u32=u32,emit=function(r)rows[#rows+1]=r end,
        read=function(at,n)
            assert(n<=1024)
            reads[at]=(reads[at] or 0)+1
            if problem=='unstable' and at==base+0x33264f8 and reads[at]==2 then return p64(manager+8)end
            if problem=='missing' and at==entry then return nil end
            local b=memory[at];return b and b:sub(1,n)
        end})
    for _=1,10 do probe.tick()end
    assert(probe.done)
    return probe,rows
end
local ip,ir=identity_fixture()
assert(ip.errors==0 and ir[#ir].complete)
assert(ir[6].key08==0xfedcba98 and ir[5].stable)
assert(ir[5].manager=='0x29000000000' and ir[7].sample==1)
assert(identity_fixture('count').errors==1)
assert(identity_fixture('build').errors==1)
assert(identity_fixture('missing').errors==1)
local _,unstable=identity_fixture('unstable')
assert(unstable[5].stable==false)
local joined,jr=identity_fixture('joined')
assert(joined.errors==0)
assert(jr[5].local_entity_id==251 and jr[6].matches_local and jr[6].reverse_entity_matches)
assert(jr[6].component_index==jr[7].index and jr[7].matches_local)
local missed,mr=identity_fixture('nomatch')
assert(missed.errors==0 and mr[5].local_entity_id==nil and mr[6].entity_id==nil)
print('PASS: identity fixed reads, high pointer precision, counts, build, missing data, drift rejection')

local function pulse_fixture(options)
    local module=assert(loadfile('HD2RuntimeArmorOverride/src/armor_refresh_route_probe.lua'))()
    local now,writes,output,records=0,0,string.rep(options.empty_output and '00' or '01',128),{}
    local entry=string.rep('00',24)
    local entity='97fa4d294d331c4d'..'fb000000a7080000a400000001000000'
    module.identity=function()
        local summary={stable=not options.unstable,instance_count=1,component_count=1,
            player_counts_hex='0100000001000000',player_record_hex=entry,
            local_entity_hex=entity,local_unit=164}
        local instance={matches_local=true,reverse_entity_matches=true,component_index=0,
            player_index=0,entry_hex=entry,entity_hex=entity,index=0,key08=5,entity_id=251,
            dirty=options.pending and 1 or 0,requested_hex=string.rep('00',64),
            applied_hex=string.rep('00',68)}
        if options.equipped then
            local item=p32(options.equipped):gsub('.',function(c)return string.format('%02x',c:byte())end)
            instance.requested_hex=instance.requested_hex:sub(1,24)..item..instance.requested_hex:sub(33)
            instance.applied_hex=instance.applied_hex:sub(1,24)..item..instance.applied_hex:sub(33)
        end
        if writes>0 and options.tail_change then
            instance.applied_hex=instance.applied_hex:sub(1,128)..'02000000'
        end
        if writes>0 and options.protected_change then
            instance.applied_hex='01'..instance.applied_hex:sub(3)
        end
        if writes>0 and options.equipment_change then
            instance.requested_hex='01'..instance.requested_hex:sub(3)
        end
        local c={matches_local=true,index=0,entity_hex=entity,output_hex=output}
        return {tick=function()end,done=true,errors=0,observation={summary=summary,
            instances={instance},components={c},manager=0x200000,guards={}}}
    end
    local host={clock=function()return now end,revision=function()return 46 end,
        target=options.target,targets=options.targets,adaptive=options.adaptive,
        pulse_allowed=function()return not options.disallowed end,
        emit=function(r)records[#records+1]=r end,flush=function()end,
        pulse_write=function(at,manager,index)
            assert(at==manager+0x995 and index==0)
            writes=writes+1
            output=string.rep('02',128)
            return not options.write_failed
        end}
    local probe=module.pulse(host)
    for _=1,100 do now=now+0.05;probe.tick()end
    assert(probe.done)
    if options.repeat_request then
        probe=module.pulse(host)
        for _=1,100 do now=now+0.05;probe.tick()end
        assert(probe.errors==1 and writes==1,'replayed process action budget')
    end
    return probe,writes,records
end
local pp,pw,pr=pulse_fixture({})
assert(pp.errors==0 and pw==1)
assert(pr[#pr].complete and pr[#pr].flag_consumed and pr[#pr].outputs_changed)
for _,o in ipairs({{unstable=true},{pending=true},{disallowed=true}})do
    local p,w=pulse_fixture(o);assert(p.errors==1 and w==0)
end
local failed,w=pulse_fixture({write_failed=true})
assert(failed.errors==1 and w==1)
pulse_fixture({repeat_request=true})
local skipped,sw,sr=pulse_fixture({target=190632838,equipped=1})
assert(skipped.errors==0 and sw==0 and sr[#sr].skipped=='target_not_equipped')
local matched,mw=pulse_fixture({target=190632838,equipped=190632838})
assert(matched.errors==0 and mw==1)
local relaxed,rw,rr=pulse_fixture({targets={['42']=true},equipped=42,adaptive=true,tail_change=true})
assert(relaxed.errors==0 and rw==1 and rr[#rr].protected_state_unchanged
    and not rr[#rr].applied_except_dirty_unchanged)
local bad,bw=pulse_fixture({targets={['42']=true},equipped=42,adaptive=true,protected_change=true})
assert(bad.errors==1 and bw==1)
local empty,ew=pulse_fixture({targets={['42']=true},equipped=42,adaptive=true,empty_output=true})
assert(empty.errors==0 and ew==1,'empty visual could not refresh after re-enable')
local transition,tw=pulse_fixture({targets={['42']=true},equipped=42,adaptive=true,equipment_change=true})
assert(transition.errors==0 and transition.result=='deferred' and tw==1)
print('PASS: pulse guards, global equipped matching, derived-tail diagnostic, protected changes fail closed')

local function auto_fixture()
    local module=assert(loadfile('HD2RuntimeArmorOverride/src/armor_refresh_route_probe.lua'))()
    local now,rev,enabled,calls=0,47,true,0
    local ready,result=true,'complete'
    local jobs={}
    module.pulse=function(h)
        calls=calls+1;jobs[#jobs+1]=h
        assert(h.adaptive and h.reserve_attempt())
        local p={done=false,errors=0}
        function p.tick() p.done=true;p.result=result end
        return p
    end
    local auto=module.auto({clock=function()return now end,
        config=function()return enabled and {}end,
        revision=function()return rev end,ready=function()return ready end,
        emit=function()end})
    local function advance()now=now+2;auto.tick();auto.tick()end
    auto.published(rev,{['190632838']=true});advance()
    assert(calls==0,'startup refreshed')
    rev=48;auto.published(rev,{});advance();assert(calls==0,'no-op refreshed')
    rev=49;auto.published(rev,{['999']=true})
    ready=false;advance();assert(calls==0)
    ready=true;advance();assert(calls==1 and jobs[1].targets['999'],'global/busy queue failed')
    rev=50;auto.published(rev,{['190632838']=true})
    rev=51;auto.published(rev,{['111']=true})
    advance();assert(calls==2 and jobs[2].targets['111'] and jobs[2].targets['190632838'])
    -- Supersede an already started observation, retaining both affected keys.
    rev=52;auto.published(rev,{['222']=true});now=now+2;auto.tick()
    result='superseded';rev=53;auto.published(rev,{['333']=true});auto.tick()
    result='complete';advance()
    assert(jobs[#jobs].targets['222'] and jobs[#jobs].targets['333'])
    for r=54,64 do rev=r;auto.published(rev,{['999']=true});advance() end
    assert(calls>6 and not auto.faulted,'unexpected lifetime test budget')
    local old=calls;enabled=false;rev=65;auto.published(rev,{['999']=true});advance()
    assert(calls==old)
    enabled=true;result='deferred';rev=66;auto.published(rev,{['999']=true});advance()
    for _=1,20 do advance()end
    old=calls;advance();assert(calls==old,'unbounded retry')
end
auto_fixture()
print('PASS: global targets, baseline/no-op, busy retention, union, supersession, no six-use cap, bounded retry')
