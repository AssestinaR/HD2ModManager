-- Explicit read-only native allowlist. No stale World/Unit handles cross ticks.
-- API contracts and limitations: notes/refresh-instance-preflight.md.
local W={}
local function field(t,k) return type(t)=='table' and rawget(t,k) or nil end
local function handle(v) return type(v)=='userdata' or type(v)=='table' end
local function integer(v,max) return type(v)=='number' and v>=0 and v<=max and v==math.floor(v) end
local function hash_string(v)
    if type(v)~='string' then return nil end
    local h=v:gsub('^0[xX]','')
    if #h==16 and h:match('^%x+$') then return h:lower(),'hex_text' end
    if #v==8 then
        local out={}
        for i=1,8 do out[i]=string.format('%02x',v:byte(i)) end
        return table.concat(out),'raw_bytes_in_order'
    end
    return nil
end
function W.new(host)
    local api,blocked,attempted={},false,{}
    local world_index,unit_index,cycle=1,1,1
    local focus_cursors={}
    local owner_keys={'extensions','extension','owner','player','player_unit',
        'inventory','equipment','customization','armor','character'}
    local owner_progress={}
    local native_seen,native_count={},0
    -- Temporal diagnostic retains scalar observations, never native handles.
    -- Keep the one-shot mode unchanged for existing association summaries.
    local trace_at,trace_samples,trace_last,trace_keys=0,0,{},0
    local binding_index=1
    local binding_names={'Unit.is_child','Unit.scene_graph_parent','Unit.world',
        'Unit.id','World.link_unit','Unit.debug_name','Unit.get_data'}
    local self={worlds_ok=false,units_seen=0,hashes_seen=0,errors=0,max_call_ms=0,
        world_count=0,world_list_capped=false,world_queries=0,last_error='',
        largest_world_units=0,owner_candidates=0,owner_queries=0,owner_present=0}
    local function emit(row) host.emit(row) end
    local function fail(api_name,reason)
        self.errors=self.errors+1;blocked=true;self.last_error=api_name..':'..reason
        emit({kind='native_unavailable',api=api_name,reason=reason})
        return false
    end
    local function call(name,...)
        if blocked then return false end
        local f=api[name]
        if type(f)~='function' then return fail(name,'missing_function') end
        if not attempted[name] then
            attempted[name]=true
            emit({kind='native_first_call',api=name})
            host.flush()
        end
        local started=host.clock()
        local ok,v=pcall(f,...)
        self.max_call_ms=math.max(self.max_call_ms,(host.clock()-started)*1000)
        if not ok then return fail(name,'lua_call_failed') end
        return true,v
    end
    local engine=field(host.globals,'stingray')
    for _,spec in ipairs({
        {'Application','worlds'},{'Application','main_world'},
        {'World','num_units'},{'World','units'},{'World','entities'},
        {'EntityManager','unit_component'},{'UnitComponent','unit'},
        {'Unit','alive'},{'Unit','name_hash'},{'IdString64','to_hex'},{'Unit','id'},
        {'Unit','world_position'},{'Vector3','x'},{'Vector3','y'},{'Vector3','z'},
        {'Unit','has_data'},{'Unit','get_data'},{'Unit','is_child'},
        {'Unit','scene_graph_parent'},{'Unit','world'},{'World','link_unit'},{'Unit','debug_name'}
    }) do
        local name=spec[1]..'.'..spec[2]
        api[name]=field(field(engine,spec[1]),spec[2])
        emit({kind='native_capability',api=name,available=type(api[name])=='function'})
    end
    -- main_world is enumerated but intentionally not called: it is a debug
    -- helper, not an authoritative local-player-world selector.
    function self.tick(mode)
        if blocked then return end
        local trace=mode=='owner_trace'
        if trace then
            if host.clock()<trace_at or trace_samples>=160 then return end
            mode='owner_native'
        end
        if mode=='bindings' then
            local name=binding_names[binding_index]
            if not name then return end
            binding_index=binding_index+1
            local row
            if type(api[name])~='function' then row={state='function_unavailable'}
            elseif type(host.inspect_binding)~='function' then row={state='inspector_unavailable'}
            else
                local ok,result=pcall(host.inspect_binding,api[name],name)
                row=ok and type(result)=='table' and result or {state='inspection_failed'}
            end
            row.kind='binding_inspection';row.api=name;row.called=false
            emit(row);host.flush();return
        end
        local started=host.clock()
        local ok,worlds=call('Application.worlds')
        if not ok then return end
        if type(worlds)~='table' then fail('Application.worlds','unexpected_return_type');return end
        local n=0
        for i=1,128 do
            if rawget(worlds,i)==nil then break end
            n=i
        end
        local capped=rawget(worlds,129)~=nil
        if self.world_count~=n or self.world_list_capped~=capped then
            emit({kind='world_list',count=n,count_is_lower_bound=capped,
                capped=capped,max_worlds=128,all_worlds_validated=false})
        end
        self.world_count=n;self.world_list_capped=capped
        if n==0 then emit({kind='world_list',count=0});return end
        if mode=='spatial' or mode=='owner_data' or mode=='owner_native' then
            local selected,largest=nil,-1
            for i=1,n do
                local candidate=rawget(worlds,i)
                if not handle(candidate) then fail('Application.worlds','unexpected_world_handle');return end
                local read,units=call('World.num_units',candidate)
                if not read then return end
                if not integer(units,1000000) then fail('World.num_units','unexpected_count');return end
                if units>largest then selected=i;largest=units end
            end
            world_index=selected
        end
        if world_index>n then world_index=1;unit_index=1;cycle=cycle+1 end
        local world=rawget(worlds,world_index)
        if not handle(world) then fail('Application.worlds','unexpected_world_handle');return end
        local wid=host.identity(world)
        local counted,count=call('World.num_units',world)
        if not counted then return end
        if not integer(count,1000000) then fail('World.num_units','unexpected_count');return end
        self.worlds_ok=true
        self.world_queries=self.world_queries+1
        self.largest_world_units=math.max(self.largest_world_units,count)
        emit({kind='world_observation',world=wid,world_index=world_index,world_count=n,
            unit_count=count,cycle=cycle,local_player_world_proven=false})
        if mode=='worlds' then world_index=world_index+1;return end
        if (mode=='spatial' or mode=='owner_data' or mode=='owner_native') and count<256 then return end
        if mode=='entities' then
            local listed,entities=call('World.entities',world)
            if not listed then return end
            if type(entities)~='table' then fail('World.entities','unexpected_return_type');return end
            local entity_count=0
            for i=1,4096 do
                if rawget(entities,i)==nil then break end
                entity_count=i
            end
            emit({kind='entity_list',world=wid,world_index=world_index,count=entity_count,
                capped=rawget(entities,4097)~=nil,cycle=cycle})
            if entity_count>0 then
                local got,component=call('EntityManager.unit_component',world)
                if not got then return end
                if not handle(component) then fail('EntityManager.unit_component','unexpected_handle');return end
                local cursor=focus_cursors[world_index]
                local index=cursor and cursor.count==entity_count and cursor.next_index or 1
                if index>entity_count then index=1 end
                for _=1,16 do
                    if index>entity_count then break end
                    local entity=rawget(entities,index)
                    -- Pass the freshly returned opaque value, never manufacture
                    -- an Entity from a Unit or an enumeration index.
                    local queried,unit=call('UnitComponent.unit',component,entity)
                    if not queried then return end
                    local row={kind='entity_unit',world=wid,world_index=world_index,
                        entity_index=index,entity_type=type(entity),unit_type=type(unit),
                        cycle=cycle,ownership_proven=false}
                    if handle(entity) then row.entity=host.identity(entity) end
                    if unit~=nil then
                        if not handle(unit) then fail('UnitComponent.unit','unexpected_handle');return end
                        local checked,alive=call('Unit.alive',unit)
                        if not checked then return end
                        if type(alive)~='boolean' then fail('Unit.alive','unexpected_return_type');return end
                        row.alive=alive
                        if alive then
                            row.unit=host.identity(unit);self.units_seen=self.units_seen+1
                            local read,name=call('Unit.name_hash',unit)
                            if not read then return end
                            row.resource_hash,row.hash_encoding=hash_string(name)
                            if not row.resource_hash and type(name)=='string' and #name==16
                                and name:sub(1,8)=='#IDPRE_!' then
                                local converted,hex=call('IdString64.to_hex',name)
                                if not converted then return end
                                if type(hex)=='string' and #hex==16 and hex:match('^%x+$') then
                                    row.resource_hash=hex:lower();row.hash_encoding='engine_to_hex'
                                end
                            end
                            if row.resource_hash then self.hashes_seen=self.hashes_seen+1 end
                        end
                    end
                    emit(row);index=index+1
                    if host.clock()-started>=0.0005 then break end
                end
                focus_cursors[world_index]={count=entity_count,next_index=index}
            end
            world_index=world_index+1;return
        end
        if count>4096 then fail('World.units','unit_limit_exceeded');return end
        local listed,units=call('World.units',world)
        if not listed then return end
        if type(units)~='table' then fail('World.units','unexpected_return_type');return end
        -- Count changes invalidate the cursor, never turn a partial scan into
        -- a deletion claim. Native arrays are reacquired, not retained.
        -- Native calls may manufacture a new userdata wrapper every time.
        -- A wrapper token must not reset the cursor forever on the first batch.
        -- Ordinal traversal is approximate if worlds/units reorder mid-scan.
        if mode=='focus' or mode=='spatial' or mode=='owner_data' or mode=='owner_native' then
            local cursor=focus_cursors[world_index]
            local floor=math.max(1,count-255)
            unit_index=cursor and cursor.count==count and cursor.next_index or floor
            if unit_index>count then unit_index=floor end
        elseif self.last_world_index~=world_index or self.last_count~=count then unit_index=1 end
        self.last_world_index=world_index;self.last_count=count
        local first,inspected=unit_index,0
        local queried_owner={}
        for _=1,32 do
            if unit_index>count then break end
            local unit=rawget(units,unit_index)
            if not handle(unit) then fail('World.units','unexpected_unit_handle');return end
            local live,alive=call('Unit.alive',unit)
            if not live then return end
            if type(alive)~='boolean' then fail('Unit.alive','unexpected_return_type');return end
            if alive then
                local read,name=call('Unit.name_hash',unit)
                if not read then return end
                local hash,encoding=hash_string(name)
                -- Observed HD2 tagged ID string. Ask the engine to serialize it;
                -- do not guess byte order or pass arbitrary userdata to this API.
                if not hash and type(name)=='string' and #name==16
                    and name:sub(1,8)=='#IDPRE_!' then
                    local converted,hex=call('IdString64.to_hex',name)
                    if not converted then return end
                    if type(hex)=='string' then
                        local text=hex:gsub('^0[xX]','')
                        if #text==16 and text:match('^%x+$') then
                            hash=text:lower();encoding='engine_to_hex'
                        end
                    end
                end
                self.units_seen=self.units_seen+1
                if hash then self.hashes_seen=self.hashes_seen+1 end
                if mode=='owner_native' and hash and (hash=='4d1c334d294dfa97'
                    or hash=='40ae90fff236865b' or hash=='d40f23a128bd988d'
                    or hash:sub(1,12)=='5236bd49853b') and (not trace or hash=='4d1c334d294dfa97') then
                    local token=host.identity(unit)
                    if trace or (not native_seen[token] and native_count<48) then
                        if not trace then native_seen[token]=true;native_count=native_count+1 end
                        local row
                        if type(host.inspect_unit)~='function' then row={state='inspector_unavailable'}
                        else
                            local okay,result=pcall(host.inspect_unit,api['Unit.world'],unit)
                            row=okay and type(result)=='table' and result or {state='inspection_failed'}
                        end
                        row.kind=trace and 'native_owner_trace' or 'native_owner_candidate'
                        row.unit=token;row.resource_hash=hash
                        row.world_index=world_index;row.unit_index=unit_index;row.called=false
                        if trace then
                            trace_samples=trace_samples+1
                            trace_at=host.clock()+0.25
                            local key
                            if row.state=='resolved_candidate' and row.object
                                and row.object_world and row.handle_index and row.handle_generation then
                                key=table.concat({row.object_world,row.object,
                                    row.handle_index,row.handle_generation},':')
                            end
                            local signature=table.concat({row.state or '',row.association_state or '',
                                row.association_count or '',row.association_objects or ''},'|')
                            local prior=key and trace_last[key]
                            row.sample_index=trace_samples
                            row.prior_sample=prior and prior.sample
                            row.changed=prior~=nil and prior.signature~=signature
                            row.first_observation=prior==nil
                            row.lifetime_proven=false
                            row.observation_key=key
                            if key and (prior or trace_keys<48) then
                                if not prior then trace_keys=trace_keys+1 end
                                trace_last[key]={sample=trace_samples,signature=signature}
                            end
                            -- No repeated machine-code blobs. Heartbeats retain
                            -- complete lists so partial logs remain interpretable.
                            row.method_code_hex=nil
                            row.method_byte_count=nil
                        end
                        emit(row);host.flush()
                        if trace then
                            -- At most one object inspection per tick. Reacquire
                            -- World/Units and alive state on every later visit.
                            inspected=inspected+1;unit_index=unit_index+1
                            break
                        end
                    end
                end
                if mode=='owner_data' and hash=='4d1c334d294dfa97' then
                    local token=host.identity(unit)
                    local progress=owner_progress[token]
                    if not progress and self.owner_candidates<8 then
                        progress={next_key=1};owner_progress[token]=progress
                        self.owner_candidates=self.owner_candidates+1
                    end
                    local key=progress and owner_keys[progress.next_key]
                    if key and not queried_owner[token] then
                        queried_owner[token]=true
                        local checked,present=call('Unit.has_data',unit,key)
                        if not checked then return end
                        if type(present)~='boolean' then fail('Unit.has_data','unexpected_return_type');return end
                        local row={kind='owner_data',world=wid,world_index=world_index,
                            unit=token,unit_index=unit_index,resource_hash=hash,key=key,
                            present=present,key_is_hypothesis=true,ownership_proven=false}
                        self.owner_queries=self.owner_queries+1
                        if present then
                            self.owner_present=self.owner_present+1
                            local read,value=call('Unit.get_data',unit,key)
                            if not read then return end
                            row.value_type=type(value)
                            -- Do not follow opaque handles or call returned functions.
                            -- Only retain session-local identity, not arbitrary values.
                            if handle(value) or type(value)=='function' then
                                row.value_identity=host.identity(value)
                            end
                            if type(value)=='table' then
                                local names,previous={},nil
                                for _=1,33 do
                                    local k=next(value,previous)
                                    if k==nil then break end
                                    previous=k
                                    if _==33 then row.members_capped=true;break end
                                    -- Restrict names as well as values: dynamic table
                                    -- keys may contain account/player-identifying data.
                                    for _,allowed in ipairs(owner_keys) do
                                        if k==allowed then names[#names+1]=allowed..':'..type(rawget(value,k)) end
                                    end
                                end
                                row.member_keys=table.concat(names,',')
                            end
                        end
                        emit(row);progress.next_key=progress.next_key+1
                    end
                end
                local raw_hex
                if not hash and type(name)=='string' then
                    local bytes={}
                    for i=1,math.min(#name,128) do bytes[i]=string.format('%02x',name:byte(i)) end
                    raw_hex=table.concat(bytes)
                end
                local level_id,level_id_type
                if mode=='focus' then
                    local read_id,id=call('Unit.id',unit)
                    if not read_id then return end
                    level_id_type=type(id)
                    if type(id)=='string' then
                        level_id=hash_string(id)
                        if not level_id and #id==16 and id:sub(1,8)=='#IDPRE_!' then
                            local converted,hex=call('IdString64.to_hex',id)
                            if not converted then return end
                            if type(hex)=='string' and #hex==16 and hex:match('^%x+$') then
                                level_id=hex:lower()
                            end
                        end
                    end
                end
                local position
                if mode=='spatial' and unit_index>=count-255 then
                    local got,vector=call('Unit.world_position',unit,1)
                    if not got then return end
                    if not handle(vector) then fail('Unit.world_position','unexpected_vector');return end
                    local coordinates={}
                    for _,axis in ipairs({'x','y','z'}) do
                        local read,value=call('Vector3.'..axis,vector)
                        if not read then return end
                        if type(value)~='number' or value~=value or math.abs(value)>100000000 then
                            fail('Vector3.'..axis,'unexpected_coordinate');return
                        end
                        coordinates[axis]=value
                    end
                    position=coordinates
                end
                -- This mode already emits bounded, deduplicated native candidates.
                -- Repeated ordinary observations drown out the association evidence.
                if mode~='owner_native' then emit({kind='unit_observation',world=wid,unit=host.identity(unit),
                    world_index=world_index,unit_index=unit_index,resource_hash=hash,
                    hash_encoding=encoding,hash_type=type(name),
                    hash_length=type(name)=='string' and #name or nil,
                    hash_raw_prefix=raw_hex,hash_unresolved=hash==nil,cycle=cycle,
                    level_id=level_id,level_id_type=level_id_type,
                    position_x=position and position.x,position_y=position and position.y,
                    position_z=position and position.z,position_node=position and 1,
                    focus_tail=mode=='focus' or mode=='spatial' or mode=='owner_data' or mode=='owner_native',stable_instance_identity_proven=false,
                    identity_is_session_hint=true}) end
            end
            inspected=inspected+1;unit_index=unit_index+1
            if host.clock()-started>=0.0005 then break end
        end
        emit({kind='unit_batch',world=wid,first_index=first,inspected=inspected,
            world_index=world_index,next_index=unit_index,total=count,cycle=cycle,
            focus_tail=mode=='focus' or mode=='spatial' or mode=='owner_data' or mode=='owner_native',
            coverage_floor=(mode=='focus' or mode=='spatial' or mode=='owner_data' or mode=='owner_native') and math.max(1,count-255) or 1,
            atomic=false,deletion_inference=false})
        if mode=='focus' or mode=='spatial' or mode=='owner_data' or mode=='owner_native' then
            focus_cursors[world_index]={count=count,next_index=unit_index}
            if mode=='focus' then world_index=world_index+1 end
        elseif unit_index>count then world_index=world_index+1;unit_index=1 end
    end
    return self
end
return W
