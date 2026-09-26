-- Diagnostic rolling scalar snapshots. No arbitrary calls, writes, or retained
-- engine userdata. Numeric handle is re-resolved and generation-checked per frame.
local H={}
local function json(row)
    local t={}
    for k,v in pairs(row) do
        local function quote(s)
            return '"'..s:gsub('[%z\1-\31\\"]',function(c)
                return string.format('\\u%04x',c:byte())
            end)..'"'
        end
        if type(v)=='string' then v=quote(v)
        elseif type(v)=='number' and v==v and math.abs(v)<math.huge then v=string.format('%.9f',v)
        elseif type(v)=='boolean' then v=tostring(v) else v='null' end
        t[#t+1]=quote(k)..':'..v
    end
    return '{'..table.concat(t,',')..'}\n'
end
function H.new(host)
    local session,seq,last,poll,started,phase=nil,0,nil,0,0,'idle'
    local native,candidate,world_at,cache,ring,pending,ready
    local revision,key,completed=0,nil,0
    local resources,resource_events,targets,resource_at,resource_max_ms,resource_state
    local component,component_events,component_state,component_at,last_assembly,resource_reset,component_max_ms
    local self={}
    local function status(state,extra)
        phase=state
        assert(host.write('refresh-hotkey.status','schema=1\nhotkey_version=4\npid='..host.pid
            ..'\nsession='..(session or '')..'\nrequest='..seq..'\nstate='..state
            ..'\nfrequency='..host.frequency..'\nqpc_seconds='..string.format('%.9f',host.clock())
            ..'\ncompleted='..completed..'\n'..(extra or '')..'\n'))
    end
    local function clear()
        native=nil;candidate=nil;cache={};ring={};pending=nil;ready=false;key=nil
        resources=nil;resource_events={};targets={};resource_at=0;resource_max_ms=0;resource_state='not_started'
        component=nil;component_events={};component_state='not_started';component_at=0;component_max_ms=0
        last_assembly=nil;resource_reset=false
    end
    clear()
    local function finish_window()
        local mark=pending
        local left,right=mark.time-0.5,mark.time+0.5
        local samples={}
        local before,after,maxgap,prev,invalid=0,0,0,nil,false
        local first,lasttime
        local changed,signature=0,nil
        for _,r in ipairs(ring) do
            if r.qpc_seconds>=left-0.1 and r.qpc_seconds<=right+0.1 then
                first=first or r.qpc_seconds;lasttime=r.qpc_seconds
                if prev then maxgap=math.max(maxgap,r.qpc_seconds-prev) end
                prev=r.qpc_seconds
                invalid=invalid or not r.valid
                if r.qpc_seconds>=left and r.qpc_seconds<=right then
                    samples[#samples+1]=r
                    if r.qpc_seconds<mark.time then before=before+1 else after=after+1 end
                    if signature and signature~=r.association_objects then changed=changed+1 end
                    signature=r.association_objects
                end
            end
        end
        local valid=mark.valid and not invalid and first and first<=left and lasttime>=right
            and before>=3 and after>=3 and maxgap<=0.1
        local reason=not mark.valid and mark.reason or invalid and 'invalid_object_or_revision'
            or not valid and 'insufficient_history_or_frame_gap' or 'ok'
        local name='hotkey-'..session..'-'..mark.seq..'.jsonl'
        local file=assert(host.open(name))
        file:write(json({kind='window',session=session,pid=host.pid,request=mark.seq,
            qpc_marker=mark.original_time,lua_received_qpc=mark.received,transport_ms=(mark.received-mark.original_time)*1000,
            os_message_delay_ms=mark.message_delay,marker_is_equip_event=false,
            before_seconds=0.5,after_seconds=0.5,valid=not not valid,reason=reason,
            samples=#samples,before_samples=before,after_samples=after,max_gap_ms=maxgap*1000,
            changes=changed,revision=revision,resources_resolved=false,local_player_proven=false,
            resource_capture=resource_state,resource_batch_max_ms=resource_max_ms,
            component_capture=component_state,component_batch_max_ms=component_max_ms,resource_supplement_seconds=0.75,
            resource_associations_time_scoped=true}))
        for _,r in ipairs(samples) do file:write(json(r)) end
        -- Include recently observed IDs as separate timestamped evidence, never
        -- label every frame with a cached pointer->resource claim.
        for _,r in ipairs(resource_events) do
            if r.qpc_seconds>=left-2 and r.qpc_seconds<=host.clock() then file:write(json(r)) end
        end
        -- Include a preceding baseline for delta interpretation, separate from
        -- the strict one-second primary frame window.
        local baselines={}
        for _,r in ipairs(component_events) do
            if r.qpc_seconds<left and (r.full_hex or r.record_u32_at18) then
                baselines[r.site or 'component']=r
            end
        end
        for _,r in pairs(baselines) do file:write(json(r)) end
        for _,r in ipairs(component_events) do
            local baseline=baselines[r.site or 'component']
            -- Preserve intervening deltas between each baseline and the primary
            -- window; a full snapshot alone can otherwise be stale at its edge.
            if r.qpc_seconds<=right and (r.qpc_seconds>=left
                or (baseline and r.qpc_seconds>baseline.qpc_seconds)) then
                file:write(json(r))
            end
        end
        file:flush();file:close()
        pending=nil;completed=completed+1
        status('ready','log='..name..'\nwindow_valid='..tostring(not not valid)..'\nreason='..reason)
    end
    local function request(text)
        local r={}
        for line in (text..'\n'):gmatch('(.-)\r?\n') do
            local k,v=line:match('^([%w_]+)=(.*)$')
            if k then assert(not r[k],'duplicate key');r[k]=v end
        end
        if r.schema~='1' or r.pid~=tostring(host.pid) then return end
        assert(r.session and #r.session==32 and r.session:match('^[a-f0-9]+$'))
        local n=tonumber(r.request)
        assert(n and n>=1 and n<=1000 and n==math.floor(n))
        if r.command=='arm' and (phase=='idle' or phase=='stopped' or phase=='error') then
            if r.session==session and n<=seq then return end
            session=r.session;seq=n;started=host.clock();revision=host.revision();completed=0;clear()
            local ids=setmetatable({},{__mode='k'});local nextid=0
            native=host.world_probe.new({globals=host.globals,clock=host.clock,flush=function()end,
                identity=function(v) if not ids[v] then nextid=nextid+1;ids[v]='h'..nextid end return ids[v] end,
                inspect_unit=function(f,u)return host.inspect_unit(f,u,cache)end,
                emit=function(row)
                    if row.kind=='native_owner_trace' and row.resource_hash=='4d1c334d294dfa97'
                        and row.state=='resolved_candidate' and row.association_state=='complete'
                        and row.encoded_handle then candidate=row end
                end})
            world_at=started+0.6;status('discovering');return
        end
        if r.session~=session or n<=seq then return end
        seq=n
        if r.command=='stop' then
            clear();status('stopped');return
        end
        assert(r.command=='mark' and ready and not pending,'not ready or busy')
        local time=tonumber(r.qpc_seconds);local frequency=tonumber(r.frequency)
        local delay=tonumber(r.message_delay_ms)
        assert(time and time==time and math.abs(time)<math.huge)
        local now=host.clock()
        local valid=frequency==host.frequency and time<=now+0.002 and now-time<=0.15
            and delay and delay>=0 and delay<=100
        pending={time=time,original_time=time,received=now,seq=n,valid=not not valid,message_delay=delay,
            reason='clock_or_transport_delay'}
        if not valid then
            -- Invalid clocks must never hold an unbounded pending window.
            pending.time=now;pending.reason='clock_or_transport_delay'
        end
        status('capturing')
    end
    function self.tick()
        local now=host.clock()
        if now>=poll then
            poll=now+((phase=='idle' or phase=='stopped' or phase=='error') and 0.5 or 0.01)
            local text=host.read('refresh-hotkey.request',2048)
            if text and text~=last then last=text;request(text) end
        end
        if phase=='idle' or phase=='stopped' or phase=='error' then return end
        if now-started>300 or completed>=30 then clear();status('stopped','reason=session_limit');return end
        if native then
            if now-started>25 then clear();status('error','reason=avatar_discovery_timeout');return end
            native.tick(now<world_at and 'worlds' or 'owner_trace')
            if native.errors>0 then clear();status('error','reason=native_preflight');return end
            if not candidate then return end
            key=table.concat({candidate.object or '',candidate.object_world or '',
                candidate.handle_index or '',candidate.handle_generation or ''},':')
            native=nil
            if host.resource_probe then
                resources=host.resource_probe.new({globals=host.globals,clock=host.clock,
                    world_index=candidate.world_index,cache=cache,inspect_unit=host.inspect_unit,
                    emit=function(r) resource_events[#resource_events+1]=r end})
                resource_state='collecting'
            else resource_state='unavailable' end
            if host.component_probe then
                component=host.component_probe.new(host.component_host)
                component_state='collecting'
            else component_state='unavailable' end
            status('warming')
        end
        local began=host.clock()
        local engine=rawget(host.globals,'stingray')
        local unit=type(engine)=='table' and rawget(engine,'Unit')
        local f=type(unit)=='table' and rawget(unit,'world')
        local row=host.inspect_unit(f,candidate.encoded_handle,cache)
        local identity=table.concat({row.object or '',row.object_world or '',
            row.handle_index or '',row.handle_generation or ''},':')
        key=key or identity
        local valid=row.state=='resolved_candidate' and row.association_state=='complete'
            and identity==key and host.revision()==revision
        local sample={kind='frame',qpc_seconds=began,inspection_ms=(host.clock()-began)*1000,
            valid=not not valid,state=row.state,association_state=row.association_state,
            object=row.object,world=row.object_world,handle_index=row.handle_index,
            handle_generation=row.handle_generation,association_count=row.association_count,
            association_objects=row.association_objects,atomic=false}
        ring[#ring+1]=sample
        while #ring>0 and (now-ring[1].qpc_seconds>2 or #ring>1024) do table.remove(ring,1) end
        if not valid then
            if pending then pending.valid=false;pending.reason='object_or_revision_changed';finish_window() end
            clear();status('error','reason=object_or_revision_changed');return
        end
        for object in (row.association_objects or ''):gmatch('[^;]+') do targets[object]=now end
        for object,t in pairs(targets) do if now-t>2 then targets[object]=nil end end
        if last_assembly and last_assembly~=row.association_objects then resource_reset=true end
        last_assembly=row.association_objects
        if component and now>=component_at then
            component_at=now+0.01
            local observed=component.tick(row.object,targets,{encoded_handle=candidate.encoded_handle})
            component_max_ms=math.max(component_max_ms,observed.inspection_ms or 0)
            if observed.rows then
                for _,r in ipairs(observed.rows) do component_events[#component_events+1]=r end
            else component_events[#component_events+1]=observed end
            if observed.state~='captured' and observed.state~='unstable' then
                component_state=observed.state;component=nil
            elseif (observed.inspection_ms or 0)>2 then
                component_state='budget_disabled';component=nil
            end
        end
        if resources and now>=resource_at then
            resource_at=now+0.01
            local okay,ms=resources.tick(targets,row.object_world,resource_reset)
            resource_reset=false
            resource_max_ms=math.max(resource_max_ms,ms)
            if not okay or ms>5 then
                resource_state=not okay and 'read_error_disabled' or 'budget_disabled';resources=nil
            end
        end
        while #resource_events>0 and
            (now-resource_events[1].qpc_seconds>3 or #resource_events>1024) do table.remove(resource_events,1) end
        while #component_events>0 and
            (now-component_events[1].qpc_seconds>3 or #component_events>2048) do table.remove(component_events,1) end
        if not ready and #ring>=4 and now-ring[1].qpc_seconds>=0.65 then
            ready=true;status('ready')
        end
        -- Keep primary samples +/-0.5s, but allow 0.75s extra for bounded IDs.
        if pending and now>=pending.time+1.25 then finish_window() end
        -- Single frame cannot be preempted; stop rather than repeat costly reads.
        if sample.inspection_ms>10 then clear();status('error','reason=inspection_budget');return end
    end
    function self.fail() clear();pcall(status,'error','reason=hotkey_probe_failed') end
    return self
end
return H
