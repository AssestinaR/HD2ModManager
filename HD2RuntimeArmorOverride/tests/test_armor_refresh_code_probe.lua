local C=assert(loadfile('HD2RuntimeArmorOverride/src/armor_refresh_code_probe.lua'))()
local function p32(n)
    local t={}
    for _=1,4 do t[#t+1]=string.char(n%256);n=math.floor(n/256) end
    return table.concat(t)
end
local function u32(s,o)
    local a,b,c,d=s:byte(o+1,o+4)
    return a and b and c and d and a+b*256+c*65536+d*16777216
end
local function put(s,o,b)return s:sub(1,o)..b..s:sub(o+#b+1)end
local function p64(n)return p32(n%4294967296)..p32(math.floor(n/4294967296))end
local function fixture(bad,fail_read,with_seed,focused)
    local mem,rows={},{}
    local bases={['helldivers2.exe']=0x140000000,['game.dll']=0x180000000}
    for name,base in pairs(bases) do
        mem[base]=put(string.rep('\0',64),0x3c,p32(0x80))
        local h='PE\0\0'..string.rep('\0',260)
        h=put(h,6,'\2\0')
        h=put(h,8,p32(name=='game.dll' and 1790161983 or (bad and 1 or 1790149348)))
        h=put(h,20,'\240\0');h=put(h,24,'\11\2')
        h=put(h,0x50,p32(name=='game.dll' and 74727424 or 60719104))
        h=put(h,0xa0,p32(0x5000));h=put(h,0xa4,p32(12))
        mem[base+0x80]=h
        local section=string.rep('\0',40)
        section=put(section,8,p32(0x20000));section=put(section,12,p32(0x10000))
        section=put(section,36,p32(0x60000020))
        local datasection=string.rep('\0',40)
        datasection=put(datasection,8,p32(0x100));datasection=put(datasection,12,p32(0x7000))
        datasection=put(datasection,36,p32(0x40000040))
        mem[base+0x188]=section..datasection
        mem[base+0x5000]=p32(0x1fff0)..p32(0x20020)..p32(0x6000)
        local code=string.rep('\x90',0x20000)
        code=put(code,0xfff0,'\x48\x8b\x05'..p32(0x3326308-(0x1fff0+7)))
        -- Unrelated early candidate must not exhaust the evidence budget.
        code=put(code,0x20,'\xff\x90'..p32(0x108))
        local at=0xffff -- split across the 64K boundary
        code=put(code,at,'\xe8'..p32((bases['helldivers2.exe']+0x1af240-(base+0x10000+at+5))%4294967296))
        code=put(code,0x10005,'\xff\x15'..p32((0x7000-0x20005-6)%4294967296))
        code=put(code,0x10010,'\x48\x8b\x05'..p32((0x7000-0x20010-7)%4294967296))
        code=put(code,0x10018,'\xff\x90'..p32(0x108))
        local ptr=bases['helldivers2.exe']+0x3362c0
        mem[base+0x7000]=p32(ptr%4294967296)..p32(math.floor(ptr/4294967296))..string.rep('\0',248)
        mem[base+0x10000]=code
    end
    local b=bases['helldivers2.exe']
    mem[b+0x3361c0]='\x48\x89\x5c\x24\x08'
    mem[b+0x336240]='\x48\x89\x5c\x24\x08'
    mem[b+0x3362c0]='\xb8\x01\x00\x00\x00'
    mem[b+0x336310]='\x48\x89\x5c\x24\x08'
    mem[b+0x1af240]='\x48\x8b\xc4';mem[b+0x1af4a0]='\x48\x8b\xc4'
    local fields={}
    for _,rva in ipairs({0x3361c0,0x336240,0x3362c0,0x336310}) do
        fields[#fields+1]=p32((b+rva)%4294967296)..p32(math.floor((b+rva)/4294967296))
    end
    mem[b+0x27cabf8]=table.concat(fields)
    mem[bases['game.dll']+0x3326308]=p64(b+0x27c8d80)
    mem[b+0x27c8d80]=put(string.rep('\0',0x180),0xe0,p64(b+0x27cab00))
    mem[b+0x27cab00]=put(string.rep('\0',0x180),0xf8,table.concat(fields))
    local object=0x200000000
    mem[bases['game.dll']+0x3326e68]=p64(object)
    mem[object+0x7380]=p32(1)
    mem[object+0x7388]=p64(object+0x10000)..p32(0x124)..p32(0)
    mem[object+0x10000]=p64(0)
    mem[object+0x10000+0xc28]=p32(3)
    if with_seed then
        local g=bases['game.dll']
        -- Separate chained range and primary range, deliberately not a
        -- contiguous function body or a callable-entry assertion.
        mem[g+0x80]=put(mem[g+0x80],0xa4,p32(24))
        mem[g+0x5000]=mem[g+0x5000]..p32(0x12ad05a)..p32(0x12ad2e9)..p32(0x6100)
        mem[g+0x6100]='\x21\0\0\0'..p32(0x12ad000)..p32(0x12ad050)..p32(0x6200)
        mem[g+0x6200]='\1\0\0\0'
        mem[g+0x12ad000]=string.rep('\x90',0x50)
        mem[g+0x12ad05a]=put(string.rep('\x90',0x28f),0x174,'\x41\xff\x92\xc8\0\0\0')
        -- Add a narrowly bounded executable section for the seed fixture.
        local section=string.rep('\0',40)
        section=put(section,8,p32(0x1000));section=put(section,12,p32(0x12ad000))
        section=put(section,36,p32(0x60000020))
        mem[g+0x80]=put(mem[g+0x80],6,'\3\0')
        mem[g+0x188]=mem[g+0x188]..section
    end
    local c=C.new({u32=u32,generic_table_survey=not focused,base=function(n)return bases[n]end,
        emit=function(r)rows[#rows+1]=r end,
        read=function(at,n)
            if fail_read and at==b+0x10000 then return nil end
            for start,data in pairs(mem) do
                if at>=start and at+n<=start+#data then return data:sub(at-start+1,at-start+n) end
            end
        end})
    return c,rows
end
local c,rows=fixture()
for _=1,20 do c.tick() end
assert(c.done and c.errors==0)
local hits,pointers,accesses,table_calls,root_verified=0,0,0,0,false
for _,r in ipairs(rows) do
    if r.kind=='code_reference_candidate' then
        hits=hits+1
        assert(r.envelope_state=='captured' and #r.code_hex==96 and not r.decoded and not r.called)
        if r.reference_kind=='rel32' then
            assert(r.site_rva==0x1ffff and r.target_rva==0x1af240)
        else assert(r.site_rva==0x20005 and r.target_rva==0x3362c0 and r.pointer_slot_rva==0x7000) end
    elseif r.kind=='module_pointer_candidate' then
        pointers=pointers+1
        assert(r.slot_rva==0x7000 and r.target=='enqueue_node_link' and r.aligned
            and r.neighbours=='0:helldivers2.exe+3362c0' and not r.table_identity_proven)
    elseif r.kind=='pointer_access_candidate' then
        accesses=accesses+1
        assert(r.site_rva==0x20010 and r.destination_rva==0x7000
            and r.slot_rva==0x7000 and r.relative_to_slot==0 and r.envelope_state=='captured')
    elseif r.kind=='table_call_candidate' then
        table_calls=table_calls+1
        assert(r.module=='game.dll' and r.site_rva==0x20018 and r.displacement==0x108
            and not r.api_identity_proven and not r.called and r.envelope_state=='captured')
    elseif r.kind=='api_root_snapshot' and r.path=='root+e0' then
        root_verified=r.identity=='helldivers2.exe+27cab00' and r.root_slot_stable
            and r.module_fields:find('f8:helldivers2.exe+3361c0',1,true)~=nil and not r.called
    end
end
assert(hits==4 and pointers==2 and accesses==2 and table_calls==1 and rows[#rows].kind=='code_survey_end')
assert(root_verified and rows[#rows].table_call_candidates==2
    and rows[#rows].table_call_prefilter_matches==1 and not rows[#rows].table_call_records_capped)
local bad=fixture(true);bad.tick();assert(bad.done and bad.errors==1)
local unreadable=fixture(false,true);for _=1,10 do unreadable.tick() end
assert(unreadable.done and unreadable.errors==1)
local seeded,seed_rows=fixture(false,false,true)
seeded.tick();seeded.tick()
local seed_count=0
for _,r in ipairs(seed_rows) do
    if r.kind=='code_seed_range' then
        seed_count=seed_count+1
        assert(r.envelope_state=='captured' and not r.called and not r.callable_entry_proven)
        assert(r.chain_depth==seed_count-1)
        assert(r.function_begin==(seed_count==1 and 0x12ad05a or 0x12ad000))
    end
end
assert(seed_count==2)
local focused,focused_rows=fixture(false,false,false,true)
for _=1,20 do focused.tick() end
assert(focused.done and focused.errors==0)
assert(focused_rows[#focused_rows].table_call_candidates==0
    and focused_rows[#focused_rows].table_call_prefilter=='disabled_focused_seed_callers')
local component_checked=false
for _,r in ipairs(focused_rows) do
    if r.kind=='component_list_snapshot' then
        assert(r.state=='captured' and r.count==1 and r.kinds=='0:124'
            and r.root_stable and r.count_stable and not r.called and not r.atomic)
        assert(r.matches=='0:readable=true:first_code=none:queue=3')
        component_checked=true
    end
end
assert(component_checked)
print('PASS: bounded code reference survey, cross-chunk/cross-module rel32, envelopes, fingerprint/read rejection')
