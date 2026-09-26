-- Reacquire legitimate World/Unit handles every batch. No native-pointer-to-
-- userdata conversion, resource loading, unknown calls, or retained handles.
local R={}
function R.new(host)
    local cursor,world_index,last_count=1,host.world_index or 1,-1
    local self={errors=0}
    local function field(t,k)return type(t)=='table' and rawget(t,k)end
    local engine=field(host.globals,'stingray')
    local app,world,unit,ids=field(engine,'Application'),field(engine,'World'),
        field(engine,'Unit'),field(engine,'IdString64')
    local function call(t,k,...)
        local f=field(t,k);assert(type(f)=='function','missing_'..k)
        return f(...)
    end
    local function hash(v)
        if type(v)~='string' then return end
        local s=v:gsub('^0[xX]','')
        if #s==16 and s:match('^%x+$') then return s:lower() end
        if #v==16 and v:sub(1,8)=='#IDPRE_!' then
            local h=call(ids,'to_hex',v)
            if type(h)=='string' then
                h=h:gsub('^0[xX]','')
                if #h==16 and h:match('^%x+$') then return h:lower() end
            end
        end
        -- Do not guess raw-byte ID endianness.
    end
    function self.tick(targets,expected_world,assembly_changed)
        local began=host.clock()
        local ok,err=pcall(function()
            local worlds=call(app,'worlds');assert(type(worlds)=='table','world_list')
            if world_index>128 or not rawget(worlds,world_index) then world_index=1 end
            local w=rawget(worlds,world_index);if not w then return end
            local count=call(world,'num_units',w)
            assert(type(count)=='number' and count>=0 and count<=4096 and count==math.floor(count),'unit_count')
            local units=call(world,'units',w);assert(type(units)=='table','unit_list')
            -- Prioritize recent allocations after an observed assembly change.
            -- Then wrap through the whole list; tail ordering is NOT assumed.
            if last_count<0 or assembly_changed then cursor=math.max(1,count-511) end
            if cursor>count then cursor=1 end
            last_count=count
            for _=1,24 do
                if cursor>count then cursor=1;break end
                local u=rawget(units,cursor);cursor=cursor+1
                if u and call(unit,'alive',u)==true then
                    local row=host.inspect_unit(field(unit,'world'),u,host.cache,true)
                    if row.state=='resolved_candidate' and row.object_world==expected_world
                        and targets[row.object] then
                        local id=hash(call(unit,'name_hash',u))
                        -- Verify the exact handle/object/generation after the API read.
                        local check=host.inspect_unit(field(unit,'world'),u,host.cache,true)
                        if id and check.state=='resolved_candidate' and check.object==row.object
                            and check.object_world==row.object_world
                            and check.handle_index==row.handle_index and check.handle_generation==row.handle_generation then
                            host.emit({kind='resource_identity',qpc_seconds=host.clock(),
                                object=row.object,world=row.object_world,resource_hash=id,
                                handle_index=row.handle_index,handle_generation=row.handle_generation,
                                atomic=false,ownership_proven=false})
                        end
                    end
                end
                if host.clock()-began>=0.0005 then break end
            end
        end)
        local elapsed=(host.clock()-began)*1000
        if not ok then self.errors=self.errors+1 end
        return ok,elapsed,not ok and tostring(err) or nil
    end
    return self
end
return R
