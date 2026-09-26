-- Guided observer. Metadata default; native reads require explicit staged mode.
local P={}
function P.new(host)
    local last_text,session,sequence=nil,nil,0
    local active,previous,ids=nil,{},setmetatable({},{__mode='k'})
    local next_id,poll_at=0,0
    local native_worlds_validated=false
    local explicit_roots={'Application','World','Unit','EntityManager','UnitComponent',
        'UnitSynchronizer','IdString64','ResourcePackage','Script','ScriptComponent'}
    local terms={'armor','armour','equip','loadout','customiz','appearance',
        'character','player','preview','inventory','unit','spawn','world','manager'}
    local scalar_keys={item_id=true,armor_id=true,body_type=true,slot=true,
        equipped=true,visible=true,dirty=true,needs_refresh=true}
    local function relevant(s)
        s=s:lower()
        for _,term in ipairs(terms) do if s:find(term,1,true) then return true end end
        return false
    end
    local function quote(s)
        return '"'..s:gsub('[%z\1-\31\\"]',function(c)
            if c=='\\' then return '\\\\' elseif c=='"' then return '\\"' end
            return string.format('\\u%04x',c:byte())
        end)..'"'
    end
    local function json(row)
        local fields={}
        for k,v in pairs(row) do
            if type(v)=='string' then v=quote(v)
            elseif type(v)=='boolean' then v=tostring(v)
            elseif type(v)=='number' and v==v and math.abs(v)<math.huge then v=tostring(v)
            else v='null' end
            fields[#fields+1]=quote(k)..':'..v
        end
        return '{'..table.concat(fields,',')..'}\n'
    end
    local function status(state,request,extra)
        assert(host.write('refresh-probe.status', 'schema=1\nprobe_version=10\npid='..host.pid
            ..'\nsession='..(session or '')..'\nrequest='..request..'\nstate='..state
            ..'\n'..(extra or '')..'\n'),'status write failed')
    end
    local function emit(row)
        row.session=session;row.request=active.request;row.phase=active.phase
        row.seconds=host.clock()-active.started
        local line=json(row)
        if active.bytes+#line>4*1024*1024 or active.records>=12000 then
            active.truncated=true;active.capped=true;return
        end
        assert(active.file:write(line),'probe log write failed')
        active.bytes=active.bytes+#line;active.records=active.records+1
    end
    local function finish(state)
        if not active then return end
        local native=active.native
        if active.mode=='worlds' and native and native.worlds_ok and native.errors==0 then
            native_worlds_validated=true
        end
        if native then
            emit({kind='native_summary',worlds_ok=native.worlds_ok,units_seen=native.units_seen,
                hashes_seen=native.hashes_seen,errors=native.errors,max_call_ms=native.max_call_ms,
                world_count=native.world_count,world_list_capped=native.world_list_capped,
                world_queries=native.world_queries,last_error=native.last_error,
                largest_world_units=native.largest_world_units,owner_candidates=native.owner_candidates,
                owner_queries=native.owner_queries,owner_present=native.owner_present})
        end
        emit({kind='phase_end',samples=active.samples,truncated=active.truncated,
            atomic=false,deletion_inference=false})
        active.file:flush();active.file:close()
        local a=active;active=nil
        status(state,a.request,'log='..a.name..'\nrecords='..a.records
            ..'\nsamples='..a.samples..'\ntruncated='..tostring(a.truncated)
            ..'\nworlds_validated='..tostring(native_worlds_validated)
            ..'\nnative_units_seen='..tostring(native and native.units_seen or 0)
            ..'\nnative_errors='..tostring(native and native.errors or a.code and a.code.errors or 0)
            ..'\nworld_count='..tostring(native and native.world_count or 0)
            ..'\nworld_queries='..tostring(native and native.world_queries or 0)
            ..'\nlargest_world_units='..tostring(native and native.largest_world_units or 0)
            ..'\nowner_candidates='..tostring(native and native.owner_candidates or 0)
            ..'\nowner_queries='..tostring(native and native.owner_queries or 0)
            ..'\nowner_present='..tostring(native and native.owner_present or 0)
            ..'\nworld_list_capped='..tostring(native and native.world_list_capped or false)
            ..'\nnative_last_error='..tostring(native and native.last_error or ''))
    end
    local function identity(v)
        if not ids[v] then next_id=next_id+1;ids[v]=next_id end
        return type(v)..':'..ids[v]
    end
    local function begin_sample()
        local s={queue={},visited={},head=1,entries=0,state={},truncated=false}
        local function enqueue(t,path,depth)
            if type(t)~='table' or s.visited[t] then return end
            if #s.queue>=64 then s.truncated=true;return end
            s.visited[t]=true;s.queue[#s.queue+1]={t=t,path=path,depth=depth,count=0}
        end
        s.enqueue=enqueue
        enqueue(host.globals,'_G',0)
        local pkg=rawget(host.globals,'package')
        enqueue(type(pkg)=='table' and rawget(pkg,'loaded'),'package.loaded',0)
        enqueue(rawget(host.globals,'stingray'),'stingray',0)
        local engine=rawget(host.globals,'stingray')
        if type(engine)=='table' then
            for _,name in ipairs(explicit_roots) do
                enqueue(rawget(engine,name),'stingray.'..name,1)
            end
        end
        active.sample=s;active.samples=active.samples+1
        emit({kind='sample_begin',sample=active.samples,registry_revision=host.revision()})
    end
    local function sample_step()
        if not active.sample then begin_sample() end
        local s=active.sample;local started=host.clock()
        for _=1,64 do
            local n=s.queue[s.head]
            if not n or s.entries>=6000 then break end
            local ok,k,v=pcall(next,n.t,n.key)
            if not ok or k==nil or n.count>=512 then
                if not ok or k~=nil then s.truncated=true end
                s.head=s.head+1
            else
                n.key=k;n.count=n.count+1;s.entries=s.entries+1
                local key=type(k)=='string' and k or type(k)=='number' and tostring(k)
                if key and #key<=160 and not key:find('^HD2')
                    and key~='_G' and key~='CowboyBingusModLoader' then
                    local path=n.path..'['..quote(key)..']'
                    local t=type(v);local sig=t
                    if t=='table' or t=='function' or t=='userdata' then sig=identity(v) end
                    if scalar_keys[key] and (t=='boolean' or t=='number') then sig=t..':'..tostring(v) end
                    s.state[path]=sig
                    if previous[path]~=sig then
                        local row={kind='member',sample=active.samples,path=path,
                            signature=sig,change=previous[path] and 'changed' or 'observed',
                            relevant=relevant(key)}
                        if t=='function' and host.getinfo then
                            local good,info=pcall(host.getinfo,v,'S')
                            if good and type(info)=='table' then
                                row.function_kind=rawget(info,'what')
                                row.source=(rawget(info,'short_src') or ''):sub(1,200)
                                row.line=rawget(info,'linedefined')
                            end
                        end
                        emit(row)
                    end
                    if t=='table' and n.depth<3 and (n.depth>0 or relevant(key)) then
                        s.enqueue(v,path,n.depth+1)
                    end
                end
            end
            if host.clock()-started>=0.0005 then break end
        end
        if s.head>#s.queue or s.entries>=6000 then
            s.truncated=s.truncated or s.head<=#s.queue
            emit({kind='sample_end',sample=active.samples,entries=s.entries,
                tables=#s.queue,truncated=s.truncated,atomic=false,deletion_inference=false})
            active.truncated=active.truncated or s.truncated
            previous=s.state;active.sample=nil;active.next_sample=host.clock()+0.5
            active.file:flush()
        end
    end
    local function request(text)
        local r={}
        for line in (text..'\n'):gmatch('(.-)\r?\n') do
            local k,v=line:match('^([%w_]+)=(.*)$')
            if k then assert(not r[k],'duplicate request key');r[k]=v end
        end
        if r.schema~='1' or r.pid~=tostring(host.pid) then return end
        assert(r.session and r.session:match('^[a-f0-9]+$') and #r.session==32,'invalid session')
        local seq=tonumber(r.request)
        assert(seq and seq>=1 and seq<=1000 and seq==math.floor(seq),'invalid sequence')
        if r.command=='ping' then
            if session==r.session and seq<=sequence then return end
            if active then return end
            if session~=r.session then
                previous={};ids=setmetatable({},{__mode='k'});next_id=0
                native_worlds_validated=false
            end
            session=r.session;sequence=seq;status('ready',seq);return
        end
        if session~=r.session or seq<=sequence then return end
        sequence=seq
        if r.command=='stop' then
            finish('cancelled');status('stopped',seq);return
        end
        assert(r.command=='capture' and not active,'invalid or busy request')
        local duration=tonumber(r.duration)
        assert(duration and duration>=5 and duration<=45,'invalid capture duration')
        assert(r.phase and r.phase:match('^[a-z0-9_]+$') and #r.phase<=48,'invalid phase')
        local mode=r.mode or 'metadata'
        assert(mode=='metadata' or mode=='worlds' or mode=='units' or mode=='focus'
            or mode=='entities' or mode=='spatial' or mode=='owner_data' or mode=='bindings'
            or mode=='owner_native' or mode=='owner_trace' or mode=='code_refs' or mode=='route_code'
            or mode=='route_identity' or mode=='route_pulse','invalid capture mode')
        if mode=='route_pulse' then
            assert(r.confirm=='one_pending_byte_once' and duration>=10,
                'explicit one-shot diagnostic confirmation required')
        end
        assert((mode=='metadata' or mode=='worlds') or native_worlds_validated,'World preflight required in this session')
        assert(mode=='metadata' or host.world_probe,'native probe unavailable')
        local name='refresh-'..session..'-'..seq..'.jsonl'
        local file=assert(host.open(name),'log open failed')
        active={file=file,name=name,request=seq,phase=r.phase,duration=duration,
            started=host.clock(),next_sample=0,samples=0,records=0,bytes=0,truncated=false,
            mode=mode,next_native=0}
        emit({kind='phase_begin',pid=host.pid,metadata_only=mode=='metadata',
            native_calls=mode~='metadata' and mode~='route_code' and mode~='route_identity'
                and mode~='code_refs' and mode~='route_pulse',
            diagnostic_write_requested=mode=='route_pulse',
            mode=mode,
            call_tracing=false,registry_revision=host.revision()})
        if mode~='metadata' then
            if mode=='code_refs' or mode=='route_code' or mode=='route_identity' or mode=='route_pulse' then
                local probe=mode~='code_refs' and host.route_probe or host.code_probe
                assert(probe and host.code_host,'code survey unavailable')
                local factory=mode=='route_identity' and probe.identity or mode=='route_pulse' and probe.pulse or probe.new
                active.code=factory({read=host.code_host.read,u32=host.code_host.u32,
                    base=host.code_host.base,emit=emit,clock=host.clock,revision=host.revision,
                    capture_tail=mode=='route_identity',
                    pulse_allowed=host.pulse_allowed,pulse_write=host.pulse_write,
                    flush=function()active.file:flush()end})
            else active.native=host.world_probe.new({globals=host.globals,clock=host.clock,
                emit=emit,identity=identity,inspect_binding=host.inspect_binding,inspect_unit=host.inspect_unit,
                flush=function()active.file:flush()end}) end
        end
        status('capturing',seq,'log='..name)
    end
    local self={}
    function self.tick()
        local now=host.clock()
        if now>=poll_at then
            poll_at=now+0.5
            local text=host.read('refresh-probe.request',2048)
            if text and text~=last_text then last_text=text;request(text) end
        end
        if active then
            if now-active.started>=active.duration or active.capped then
                if active.sample then active.truncated=true end
                finish('complete')
            elseif active.code then
                active.code.tick()
                if active.code.done then finish('complete') end
            elseif active.native and now>=active.next_native then
                active.native.tick(active.mode)
                active.next_native=host.clock()+(active.mode=='worlds' and 0.5
                    or (active.mode=='focus' or active.mode=='spatial' or active.mode=='owner_data'
                        or active.mode=='owner_native' or active.mode=='owner_trace') and 0.01 or 0.1)
            elseif active.mode~='owner_trace' and (active.sample or now>=active.next_sample) then
                sample_step()
            end
        end
    end
    function self.fail()
        if active then
            pcall(function() active.file:close() end);active=nil
        end
        pcall(status,'error',sequence,'message=probe_failed')
    end
    return self
end
return P
