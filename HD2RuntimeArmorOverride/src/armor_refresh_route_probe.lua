-- Opt-in current-build diagnostics. new/identity are read-only; pulse is an
-- explicitly confirmed single-byte experiment. No native candidate is called.
local P={}
local pulse_used=false -- Diagnostic action budget: one attempt per game process.
function P.new(host)
    local read,u32=host.read,host.u32
    local self={done=false,errors=0}
    local base,size,pdata,entries,sections
    local si,offset,total=1,0,0
    local queue,seen,roots={}, {}, {}
    local qi=1
    local initialized,scanned=false,false
    local patterns={
        {'dirty_merge','\x45\x38\x77\x29\x75\x09\x40\x84\xf6'},
        {'dirty_consume','\x45\x38\x77\x29\x74\x14\x8b\x53\x08\x44\x8b\xc6'},
        {'state_layout','\x4d\x8d\xa0\x7c\x0a\x00\x00'},
        {'state_setter','\x4d\x8d\x82\x7c\x0a\x00\x00\x48\xc1\xe0\x06'},
        {'kit_body','\x4c\x8b\x55\x30\x8b\xc3\x4c\x8d\x0c\x40'},
    }
    local function emit(r) host.emit(r) end
    local function fail(reason)
        self.done=true;self.errors=self.errors+1
        emit({kind='code_survey_error',reason=reason})
    end
    local function u16(b,o)local a,c=b:byte(o+1,o+2);return a+c*256 end
    local function executable(rva,n)
        for _,s in ipairs(sections) do
            if rva>=s.rva and rva+n<=s.rva+s.size then return s end
        end
    end
    local function nominate(rva,label,depth)
        if seen[rva] or not executable(rva,1) then return end
        if #queue>=40 then fail('candidate_limit');return end
        seen[rva]=true;queue[#queue+1]={rva=rva,label=label,depth=depth or 0}
        emit({kind='route_candidate',rva=rva,label=label,depth=depth or 0,called=false})
    end
    local function initialize()
        base=host.base('game.dll')
        local dos=base and read(base,64)
        local off=dos and u32(dos,60)
        local h=off and off<0x100000 and read(base+off,0x108)
        if not h or h:sub(1,4)~='PE\0\0' or u16(h,24)~=0x20b
            or u32(h,8)~=1790161983 or u32(h,0x50)~=74727424 then
            fail('route_build_mismatch');return
        end
        size=u32(h,0x50);pdata=u32(h,0xa0)
        local length=u32(h,0xa4)
        if length==0 or length%12~=0 or length>0x400000 or pdata+length>size then
            fail('route_pdata_bounds');return
        end
        entries=length/12
        local count,opt=u16(h,6),u16(h,20)
        if count<1 or count>96 or opt<0xf0 or opt>4096 then fail('route_sections');return end
        local st=read(base+off+24+opt,count*40)
        if not st then fail('route_section_read');return end
        sections={}
        for i=0,count-1 do
            local at=i*40;local n,rva,flags=u32(st,at+8),u32(st,at+12),u32(st,at+36)
            if math.floor(flags/0x20000000)%2==1 and n>0 then
                if rva+n>size then fail('route_section_bounds');return end
                sections[#sections+1]={rva=rva,size=n}
            end
        end
        initialized=true
        emit({kind='route_begin',timestamp=1790161983,image_size=size,sections=#sections,
            called=false,chunk_bytes=65536,max_candidates=40})
    end
    local function envelope(site)
        local low,high=0,entries-1
        local record
        while low<=high do
            local mid=math.floor((low+high)/2)
            local r=read(base+pdata+mid*12,12)
            if not r then return nil,'pdata_read' end
            local a,b=u32(r,0),u32(r,4)
            if site<a then high=mid-1 elseif site>=b then low=mid+1 else record=r;break end
        end
        if not record then return nil,'pdata_missing' end
        local original=record
        local visited={}
        for _=1,12 do
            local a,b,u=u32(record,0),u32(record,4),u32(record,8)
            if visited[u] or a>=b or not executable(a,b-a) or u+4>size then return nil,'unwind_bounds' end
            visited[u]=true
            local h=read(base+u,4)
            if not h or h:byte(1)%8~=1 then return nil,'unwind_header' end
            if math.floor(h:byte(1)/32)%2==0 then
                return a,nil,u32(original,0),u32(original,4),u32(original,8)
            end
            local at=u+4+math.floor((h:byte(3)+1)/2)*4
            if at+12>size then return nil,'unwind_chain_bounds' end
            record=read(base+at,12)
            if not record then return nil,'unwind_chain_read' end
        end
        return nil,'unwind_chain_limit'
    end
    local function hx(b)return (b:gsub('.',function(c)return string.format('%02x',c:byte())end))end
    local active
    function self.tick()
        if self.done then return end
        if not initialized then initialize();return end
        if not scanned then
            local s=sections[si]
            if not s then scanned=true;return end
            local n=math.min(65536,s.size-offset)
            local length=math.min(n+64,s.size-offset)
            local b=read(base+s.rva+offset,length)
            if not b or #b~=length then fail('route_code_read');return end
            for _,p in ipairs(patterns) do
                local from=1
                while true do
                    local at=b:find(p[2],from,true)
                    if not at or at>n then break end
                    local rva=s.rva+offset+at-1
                    nominate(rva,p[1])
                    if self.done then return end
                    -- Known historical branch shape only nominates the direct
                    -- callee. Its instructions/ABI are not thereby validated.
                    if p[1]=='dirty_consume' and b:byte(at+17)==0xe8 then
                        local rel=u32(b,at+17)
                        if rel>=2147483648 then rel=rel-4294967296 end
                        nominate(rva+22+rel,'dirty_direct_callee',1)
                    end
                    from=at+1
                end
            end
            offset=offset+n;total=total+n
            if offset>=s.size then si=si+1;offset=0 end
            return
        end
        if active then
            local n=math.min(1024,active.size-active.offset)
            local b=read(base+active.root+active.offset,n)
            if not b or #b~=n then fail('route_capture_read');return end
            emit({kind='route_code_chunk',root_rva=active.root,rva=active.root+active.offset,
                code_hex=hx(b),called=false})
            active.offset=active.offset+n
            if active.offset==active.size then active=nil end
            return
        end
        local q=queue[qi]
        if not q then
            self.done=true
            emit({kind='code_survey_end',complete=true,route_format=1,bytes=total,
                candidates=#queue,called=false,instruction_boundaries_validated=false})
            return
        end
        qi=qi+1
        local root,reason,a,b,u=envelope(q.rva)
        emit({kind='route_envelope',site_rva=q.rva,label=q.label,root_rva=root,
            segment_begin=a,segment_end=b,unwind_rva=u,state=root and 'captured' or reason,
            function_end_known=false,called=false})
        if root and not roots[root] then
            roots[root]=true
            local s=executable(root,1)
            -- Bounded windows, NOT alleged whole functions; may include neighbors.
            local length=math.min(8192,s.rva+s.size-root)
            active={root=root,size=length,offset=0}
            emit({kind='route_window',root_rva=root,byte_count=length,called=false})
        end
    end
    return self
end
-- A separate opt-in mode: fixed reviewed code and small state only.
-- It does not scan executable sections, execute callees, or mutate state.
function P.identity(host)
    local self={done=false,errors=0}
    local base,stage,sample=host.base('game.dll'),0,0
    local function hx(s)return (s:gsub('.',function(c)return string.format('%02x',c:byte())end))end
    local function read(at,n)
        local b=host.read(at,n)
        assert(b and #b==n,'identity_read_failed')
        return b
    end
    local function num(at)return host.u32(read(at,4),0)end
    local guards={}
    local function watched(at,n)
        local b=read(at,n);guards[#guards+1]={at,b};return b
    end
    local function ptr(at)
        local b=watched(at,8)
        local lo,hi=host.u32(b,0),host.u32(b,4)
        assert(hi<0x8000,'identity_pointer_range')
        local p=lo+hi*4294967296
        assert(p>=65536,'identity_null_pointer')
        return p
    end
    local function emit(row)row.called=false;host.emit(row)end
    local globals={0x33264f8,0x3326468,0x3326d20}
    local checks={
        {0x874f8f,'\x4c\x8b\x15\x62\x15\xab\x02'},
        {0x877b79,'\x4c\x8b\x2d\xa0\xf1\xaa\x02'},
        {0x874b4b,'\x48\x8b\x05\x16\x19\xab\x02'},
        {0x874c02,'\x8b\x53\x08\x44\x8b\xc6\x48\x8b\x4c\x24\x78\xe8\x9e\x2e\x00\x00'},
        {0x606654,'\x48\x8b\x15\x0d\xfe\xd1\x02'},
        {0xfd9ba4,'\x4c\x8b\x1d\xed\x23\x49\x02'},
        {0xfd9d50,'\x4c\x8b\x15\x41\x22\x49\x02'},
        {0xfd9c49,'\x8b\x40\x04\x48\x8d\x04\x40\x48\x8d\x80\xe4\x65\x1e\x00'},
        {0xfd9dd4,'\x8b\x40\x04\x48\x8d\x04\x40\x48\x8d\x80\xe3\x65\x1e\x00'},
        {0x874bfc,'\x45\x38\x77\x29\x74\x14\x8b\x53\x08\x44\x8b\xc6\x48\x8b\x4c\x24\x78\xe8\x9e\x2e\x00\x00\x45\x88\x77\x29'},
        {0x874dc0,'\x0f\x28\xd6\x8b\xd3\x48\x8b\xcf\xe8\x53\xf7\xff\xff\x8b\x87\xe8\x08\x00\x00\xff\xc3\x3b\xd8\x72\xe7'},
        {0x87734c,'\x89\x87\xac\x09\x00\x00'},
        {0x877362,'\x8b\x8f\xac\x09\x00\x00'},
        {0x8773a0,'\xff\x90\x90\x00\x00\x00'},
    }
    local code={{0x606630,1024},{0xfd9ba0,1024},{0xfd9d40,1024}}
    if host.capture_tail then code[#code+1]={0x877200,1024} end
    local function lookup(at,key,limit)
        local h=watched(at,20)
        local n,empty,mult=host.u32(h,8),host.u32(h,12),host.u32(h,16)
        assert(n<=limit and (n==0 or n==2^math.floor(math.log(n)/math.log(2)+0.5)),
            'identity_map_capacity')
        if n==0 or key==empty or key==0xffffffff then return nil end
        local data=ptr(at)
        -- Exact low 32 bits, avoiding Lua double's 64-bit product rounding.
        local a,b=key%65536,math.floor(key/65536)
        local c,d=mult%65536,math.floor(mult/65536)
        local hash=(a*c+((a*d+b*c)%65536)*65536)%4294967296
        for j=0,math.min(n,128)-1 do
            local r=watched(data+((hash+j)%n)*8,8)
            local k=host.u32(r,0)
            if k==key then
                local index=host.u32(r,4)
                if index~=0xffffffff then return index end
                return nil
            end
            if k==empty then return nil end
        end
        error('identity_map_probe_limit')
    end
    local function step()
        if stage==0 then
            assert(base and base>=65536,'identity_module_missing')
            local off=num(base+0x3c);assert(off<0x100000,'identity_pe_bounds')
            local h=read(base+off,0x58)
            assert(h:sub(1,4)=='PE\0\0' and host.u32(h,8)==1790161983
                and host.u32(h,0x50)==74727424,'identity_build_mismatch')
            for _,c in ipairs(checks) do assert(read(base+c[1],#c[2])==c[2],'identity_code_mismatch') end
            emit({kind='identity_begin',timestamp=1790161983,image_size=74727424,
                max_instances=4,max_components=8,atomic=false})
            stage=host.omit_code and (#code+1) or 1;return
        end
        if stage<=#code then
            local c=code[stage]
            emit({kind='identity_code',rva=c[1],code_hex=hx(read(base+c[1],c[2])),
                byte_count=c[2],window_not_function=true})
            stage=stage+1;return
        end
        guards={}
        local manager,player,avatar=ptr(base+globals[1]),ptr(base+globals[2]),ptr(base+globals[3])
        local owner=ptr(base+0x346bf98)
        local function entity_for_unit(unit)
            if unit==0x7fff then return nil end
            local index=lookup(owner+0xf22ec8,unit,1048576)
            if not index then return nil end
            assert(index<262144,'identity_entity_index')
            return watched(owner+0xf32f18+index*24,24)
        end
        local head=watched(manager+0x8e4,12)
        local count=host.u32(head,0)
        assert(count<=4,'identity_instance_count')
        local counts=watched(player+0x84,8)
        assert(host.u32(counts,0)<=4 and host.u32(counts,4)<=4,'identity_player_count')
        local row={kind='identity_snapshot',sample=sample,manager=string.format('0x%x',manager),
            player_manager=string.format('0x%x',player),avatar_manager=string.format('0x%x',avatar),
            instance_count=count,instance_counters_hex=hx(head),player_counts_hex=hx(counts),
            local_unit=host.u32(watched(player+0x3a8,4),0),instances={},components={}}
        if host.u32(counts,0)>0 then
            row.player_record_hex=hx(watched(ptr(player+0xe8),24))
            row.player_record_layout='historical_candidate_not_current_ownership_proof'
        end
        local local_entity=entity_for_unit(row.local_unit)
        row.local_entity_hex=local_entity and hx(local_entity)
        row.local_entity_id=local_entity and host.u32(local_entity,8)
        local instance_rows,component_rows={},{}
        for i=0,count-1 do
            local at=ptr(manager+0x948+i*8)
            local entry=watched(at,24)
            local applied=watched(manager+0x96c+i*0x44,0x44)
            local instance={kind='identity_instance',sample=sample,index=i,entry_address=string.format('0x%x',at),
                entry_hex=hx(entry),key08=host.u32(entry,8),key10=host.u32(entry,16),
                requested_hex=hx(watched(manager+0xa7c+i*0x40,0x40)),
                applied_hex=hx(applied),dirty=applied:byte(0x2a)}
            local pi=lookup(player+0xd0,instance.key08,64)
            instance.player_index=pi
            if pi then
                assert(pi<4,'identity_player_index')
                local unit=host.u32(watched(player+0x3a8+pi*32,4),0)
                local entity=entity_for_unit(unit)
                instance.player_unit=unit
                instance.entity_hex=entity and hx(entity)
                instance.entity_id=entity and host.u32(entity,8)
                instance.matches_local=entity~=nil and entity==local_entity
                if entity then
                    local ei=lookup(owner+0xf1aeb0,instance.entity_id,1048576)
                    assert(ei and ei<262144,'identity_reverse_map_missing')
                    instance.reverse_entity_matches=watched(owner+0xf32f18+ei*24,24)==entity
                    instance.component_index=lookup(avatar+0xf8,instance.entity_id,64)
                end
            end
            instance_rows[#instance_rows+1]=instance
        end
        -- Historical count/list offsets are explicitly a candidate cross-check,
        -- not assumed to prove this component belongs to the local player.
        local n=host.u32(watched(avatar+0x6c,4),0)
        assert(n<=8,'identity_component_count')
        row.component_count=n
        row.component_map_header_hex=hx(watched(avatar+0xf8,20))
        for i=0,n-1 do
            local entity=watched(ptr(avatar+0x110+i*8),24)
            local at=avatar+i*0x1b8
            component_rows[#component_rows+1]={kind='identity_component',sample=sample,index=i,entity_hex=hx(entity),
                entity_id=host.u32(entity,8),engine_unit=host.u32(entity,16),
                body_flag=watched(at+0x546ae3,1):byte(1),
                output_hex=hx(watched(at+0x546b4c,0x80)),matches_local=entity==local_entity}
        end
        row.stable=true
        for _,g in ipairs(guards) do
            if read(g[1],#g[2])~=g[2] then row.stable=false;break end
        end
        row.instances=nil;row.components=nil
        emit(row)
        for _,r in ipairs(instance_rows) do r.stable=row.stable;emit(r) end
        for _,r in ipairs(component_rows) do r.stable=row.stable;emit(r) end
        self.observation={summary=row,instances=instance_rows,components=component_rows,
            guards=guards,manager=manager}
        sample=sample+1
        if sample>=2 then
            self.done=true;emit({kind='code_survey_end',complete=true,identity_format=1,samples=sample})
        end
    end
    function self.tick()
        if self.done then return end
        local ok,err=pcall(step)
        if not ok then
            self.done=true;self.errors=self.errors+1
            self.reason=tostring(err)
            emit({kind='code_survey_error',reason=tostring(err)})
        end
    end
    return self
end

-- Experimental, explicit opt-in. Only sets the already reviewed one-byte
-- pending flag. The engine consumes it on its own update; no native callee is
-- invoked. This is NOT an automatically enabled production refresh feature.
function P.pulse(host)
    assert(host.clock and host.pulse_write and host.revision and host.pulse_allowed,
        'pulse_host_unavailable')
    local self={done=false,errors=0}
    local started=host.clock()
    local initial_revision=host.revision()
    local next_at,reader,before,write_at,post_count=started,nil,nil,nil,0
    local write_attempted=false
    local stable_outputs,last_output=0,nil
    local function emit(r)r.called=false;host.emit(r)end
    local function fail(reason)
        self.done=true;self.errors=self.errors+1
        emit({kind='code_survey_error',reason=reason,write_attempted=write_attempted,
            write_confirmed=write_at~=nil})
    end
    local function candidate(o)
        assert(o and o.summary.stable,'pulse_unstable')
        local s=o.summary
        assert(s.instance_count==1 and s.component_count==1
            and s.player_counts_hex=='0100000001000000','pulse_requires_single_player')
        local i,c=o.instances[1],o.components[1]
        assert(i and c and i.matches_local and i.reverse_entity_matches and c.matches_local
            and i.component_index==c.index and i.player_index==0
            and i.entry_hex==s.player_record_hex,'pulse_identity_mismatch')
        assert(i.entity_hex==s.local_entity_hex and c.entity_hex==s.local_entity_hex
            and s.local_unit~=0x7fff,'pulse_no_local_avatar')
        local e=s.local_entity_hex
        assert(e:sub(1,16)=='97fa4d294d331c4d'
            and tonumber(e:sub(41,42),16)%2==1,'pulse_not_owned_avatar')
        -- Empty output can be intentional when every visual component is
        -- disabled. It must not prevent a subsequent re-enable publication.
        if not host.adaptive then
            assert(c.output_hex:sub(1,240):find('[1-9a-f]'),'pulse_no_appearance_output')
        end
        return i,c
    end
    local function observe(o)
        local i,c=candidate(o)
        if not before then
            if not host.reserve_attempt then assert(not pulse_used,'pulse_process_budget_spent') end
            assert(i.dirty==0,'pulse_already_pending')
            if host.target or host.targets then
                local function body_item(h)
                    local at=25 -- applied/requested +0xc, four bytes little-endian.
                    return tonumber(h:sub(at,at+1),16)+tonumber(h:sub(at+2,at+3),16)*256
                        +tonumber(h:sub(at+4,at+5),16)*65536+tonumber(h:sub(at+6,at+7),16)*16777216
                end
                local equipped=body_item(i.applied_hex)
                local accepted=host.targets and host.targets[tostring(equipped)]
                    or host.target and equipped==host.target
                if not accepted or body_item(i.requested_hex)~=equipped then
                    self.done=true
                    self.result='not_equipped'
                    emit({kind='code_survey_end',complete=true,pulse_format=1,writes=0,
                        skipped='target_not_equipped',target=host.target})
                    return
                end
                self.target=equipped
            end
            assert(host.pulse_allowed() and host.revision()==initial_revision,'pulse_runtime_changed')
            local address=o.manager+0x96c+i.index*0x44+0x29
            before={summary=o.summary,instance=i,component=c,address=address,manager=o.manager}
            -- Flush intent first: a crash must not lose the attempted action.
            emit({kind='pulse_intent',revision=initial_revision,instance_key=i.key08,
                entity_id=i.entity_id,target=self.target,address=string.format('0x%x',address),
                expected=0,replacement=1,max_writes=1,requested_hex=i.requested_hex,
                applied_hex=i.applied_hex,output_hex=c.output_hex})
            host.flush()
            if host.reserve_attempt then assert(host.reserve_attempt(),'pulse_action_budget_spent')
            else pulse_used=true end
            write_attempted=true
            assert(host.pulse_write(address,o.manager,i.index,o.guards),'pulse_write_rejected')
            write_at=host.clock()
            emit({kind='pulse_written',byte_count=1,revision=initial_revision})
            host.flush()
            next_at=write_at+0.05
            return
        end
        assert(o.manager==before.manager and i.key08==before.instance.key08
            and i.entity_hex==before.instance.entity_hex and c.index==before.component.index,
            'pulse_identity_changed_after_write')
        post_count=post_count+1
        local requested_same=i.requested_hex==before.instance.requested_hex
        local function masked(s)return s:sub(1,82)..'00'..s:sub(85)end
        local applied_same=masked(i.applied_hex)==masked(before.instance.applied_hex)
        -- +0x40 is the separately diagnosed armor audio classification cache.
        -- Preserve comparison of every other byte; do not ignore all tail data.
        local protected_same=masked(i.applied_hex):sub(1,128)==masked(before.instance.applied_hex):sub(1,128)
        local changed=c.output_hex~=before.component.output_hex
        if i.dirty==0 and c.output_hex==last_output then stable_outputs=stable_outputs+1
        else stable_outputs=i.dirty==0 and 1 or 0 end
        last_output=c.output_hex
        emit({kind='pulse_observation',post_count=post_count,after_ms=(host.clock()-write_at)*1000,
            dirty=i.dirty,requested_unchanged=requested_same,applied_except_dirty_unchanged=applied_same,
            protected_state_unchanged=protected_same,audio_cache_hex=i.applied_hex:sub(129,136),
            outputs_changed=changed,output_hex=c.output_hex,applied_hex=i.applied_hex})
        assert(requested_same,'pulse_equipment_request_changed')
        if host.adaptive and not protected_same then error('pulse_protected_state_changed') end
        if (host.adaptive and stable_outputs>=3 and host.clock()-write_at>=0.5)
            or host.clock()-write_at>=3 then
            self.done=true
            self.result=i.dirty==0 and protected_same and 'complete' or 'postcondition_failed'
            emit({kind='code_survey_end',complete=true,pulse_format=1,writes=1,
                flag_consumed=i.dirty==0,outputs_changed=changed,
                requested_unchanged=requested_same,applied_except_dirty_unchanged=applied_same,
                protected_state_unchanged=protected_same,target=self.target,
                visual_confirmation=false,safety_proven=false})
        else next_at=host.clock()+0.2 end
    end
    function self.tick()
        if self.done then return end
        local ok,err=pcall(function()
            assert(host.clock()-started<8,'pulse_observation_timeout')
            if host.adaptive and (host.revision()~=initial_revision or not host.pulse_allowed()) then
                self.done=true;self.result='superseded'
                emit({kind='pulse_deferred',reason='superseded_or_busy',write_attempted=write_attempted})
                return
            end
            assert(host.revision()==initial_revision and host.pulse_allowed(),'pulse_runtime_changed')
            if host.clock()<next_at then return end
            if not reader then
                reader=P.identity({read=host.read,u32=host.u32,base=host.base,emit=function()end,
                    omit_code=true})
            end
            reader.tick()
            if reader.done then
                assert(reader.errors==0,'pulse_identity_read_failed:'..tostring(reader.reason))
                observe(reader.observation);reader=nil
            end
        end)
        if not ok then
            local reason=tostring(err)
            if host.adaptive and
                (reason:find('pulse_identity_changed_after_write',1,true)
                or reason:find('pulse_equipment_request_changed',1,true)) then
                -- A normal equip/scene change invalidates this observation,
                -- not the ABI. Re-resolve later; never use the old address.
                self.done=true;self.result='deferred'
                emit({kind='pulse_deferred',reason=reason,write_attempted=write_attempted})
            elseif host.adaptive and not write_attempted and
                (reason:find('pulse_unstable',1,true) or reason:find('pulse_already_pending',1,true)
                or reason:find('pulse_requires_single_player',1,true)
                or reason:find('pulse_no_local_avatar',1,true)
                or (reason:find('pulse_identity_read_failed',1,true) and not
                    (reason:find('build_mismatch',1,true) or reason:find('code_mismatch',1,true)))) then
                self.done=true;self.result='deferred'
                emit({kind='pulse_deferred',reason=reason,write_attempted=false})
            else self.result='failed';fail(reason) end
        end
    end
    return self
end

-- All armor targets, but only the uniquely verified local equipped body.
-- Single-player restriction stays until remote/preview ownership is validated.
function P.auto(host)
    local self={faulted=false,attempts=0}
    local pending,active,job,last_revision,next_action=nil,nil,nil,nil,0
    local function emit(r)host.emit(r)end
    local function merge(targets,revision,deadline)
        if not pending then pending={targets={},revision=revision,deadline=deadline or host.clock()+30} end
        for k in pairs(targets) do pending.targets[k]=true end
        pending.revision=revision;pending.due=host.clock()+0.75
    end
    function self.published(revision,changed)
        local previous=last_revision;last_revision=revision
        if self.faulted then return end
        local config=host.config()
        if not config then pending=nil;return end
        if previous==nil then emit({kind='auto_refresh_baseline',revision=revision});return end
        if revision<=previous then
            pending=nil;self.faulted=true
            emit({kind='auto_refresh_disabled',reason='non_monotonic_revision'});return
        end
        local targets={}
        for key in pairs(changed) do
            if not config.target or key==tostring(config.target) then targets[key]=true end
        end
        if active and job then
            -- A publication can supersede observation after its one byte was
            -- written. Carry its affected target forward to the newest revision.
            for k in pairs(job.targets) do targets[k]=true end
        end
        if next(targets) then
            merge(targets,revision)
            emit({kind='auto_refresh_queued',revision=revision})
        elseif pending then pending.revision=revision;pending.due=host.clock()+0.75
        else emit({kind='auto_refresh_skipped',revision=revision,reason='no_target_change'}) end
    end
    function self.tick()
        if self.faulted then return end
        if active then
            active.tick()
            if active.done then
                local result=active.result
                if active.errors>0 or result=='postcondition_failed' then
                    self.faulted=true;pending=nil
                    emit({kind='auto_refresh_disabled',reason='unsafe_postcondition_or_write_failure'})
                elseif (result=='deferred' or result=='superseded') and host.clock()<job.deadline then
                    merge(job.targets,host.revision(),job.deadline)
                end
                active=nil;job=nil;next_action=host.clock()+1
            end
            return
        end
        if not pending or host.clock()<math.max(pending.due,next_action) then return end
        local config=host.config()
        if not config then pending=nil;emit({kind='auto_refresh_skipped',reason='disabled'});return end
        if host.clock()>pending.deadline then
            pending=nil;emit({kind='auto_refresh_skipped',reason='context_wait_expired'});return
        end
        if config.max_attempts and self.attempts>=config.max_attempts then
            pending=nil;emit({kind='auto_refresh_skipped',reason='session_budget_spent'});return
        end
        if host.revision()~=pending.revision or not host.ready() then
            pending.due=host.clock()+0.5;return -- retain, never silently drop
        end
        job=pending;pending=nil
        local current=job
        active=P.pulse({clock=host.clock,read=host.read,u32=host.u32,base=host.base,
            targets=current.targets,adaptive=true,revision=host.revision,flush=host.flush,
            pulse_write=host.pulse_write,
            pulse_allowed=function()return host.config() and host.ready()
                and host.revision()==current.revision end,
            reserve_attempt=function()
                local c=host.config()
                if not c or (c.max_attempts and self.attempts>=c.max_attempts) then return false end
                self.attempts=self.attempts+1;return true
            end,
            emit=function(row)
                row.revision=current.revision;row.attempt=self.attempts;emit(row)
            end})
    end
    function self.fail(reason)
        self.faulted=true;pending=nil;active=nil;job=nil
        emit({kind='auto_refresh_disabled',reason=tostring(reason)})
    end
    return self
end
return P
