-- Engine-independent incremental publication. Published buffers are never freed
-- during this Lua state's lifetime: native consumers may retain old pointers.
local T={}
function T.new(io,limits)
    limits=limits or {}
    local self={active={},pool={},retained_bytes=0,retained_count=0,
        byte_limit=limits.bytes or 32*1024*1024,
        count_limit=limits.count or 8192,faulted=false}

    function self.prepare(requests,checkpoint)
        if self.faulted then return nil,'publication fault: restart the game' end
        local next_active,new={},{}
        local stats={reused=0,built=0,removed=0,writes=0,
            mode='full',replaced=0,skipped_source=0,structure_preserved=false,
            skipped=0,skipped_targets={}}
        local bytes,count=self.retained_bytes,self.retained_count
        for _,request in ipairs(requests) do
            local composition=request.composition
            if composition and composition.mode=='existing_only' then
                stats.mode='existing_only'
                stats.structure_preserved=true
                stats.replaced=stats.replaced+(composition.replaced or 0)
                stats.skipped_source=stats.skipped_source+(composition.skipped_source or 0)
            end
            local key=request.key
            if next_active[key] then return nil,'duplicate target '..key end
            local active=self.active[key]
            if request.retain_active then
                if not active then return nil,'cannot retain inactive target '..key end
                if active.slot~=request.slot or active.original~=request.original then
                    return nil,'registry identity changed for target '..key
                end
                next_active[key]=active
                stats.reused=stats.reused+1
                if checkpoint then checkpoint() end
            else
                local pooled=self.pool[key] and self.pool[key][request.signature]
                local clone
                if active and active.signature==request.signature then
                    clone=active
                elseif pooled then clone=pooled end
                if clone then
                    if clone.slot~=request.slot or clone.original~=request.original then
                        return nil,'registry identity changed for target '..key
                    end
                    stats.reused=stats.reused+1
                else
                    bytes=bytes+request.bytes;count=count+1
                    if bytes>self.byte_limit or count>self.count_limit then
                        return nil,'published buffer limit reached: restart the game'
                    end
                    local reason
                    clone,reason=request.build()
                    if not clone then return nil,'target '..key..': '..tostring(reason) end
                    if clone.bytes~=request.bytes then return nil,'clone size mismatch' end
                    clone.signature=request.signature;clone.key=key
                    clone.slot=request.slot;clone.original=request.original
                    new[#new+1]=clone
                    stats.built=stats.built+1
                end
                next_active[key]=clone
                if checkpoint then checkpoint() end
            end
        end
        local operations={}
        for key,active in pairs(self.active) do
            if not next_active[key] then
                operations[#operations+1]={slot=active.slot,before=active.base,
                    after=active.original,key=key}
                stats.removed=stats.removed+1
            end
        end
        for key,clone in pairs(next_active) do
            local old=self.active[key]
            if old~=clone then
                operations[#operations+1]={slot=clone.slot,
                    before=old and old.base or clone.original,after=clone.base,key=key}
            end
        end
        table.sort(operations,function(a,b)return a.slot<b.slot end)
        return {active=next_active,new=new,operations=operations,stats=stats,
            previous=self.active}
    end

    function self.commit(plan)
        if self.faulted or plan.previous~=self.active then return false,'stale transaction' end
        -- Validate unchanged slots as well; do not silently accept another writer.
        for key,clone in pairs(self.active) do
            if io.read(clone.slot)~=clone.base then
                return false,'index slot changed externally for target '..key
            end
        end
        for _,op in ipairs(plan.operations) do
            if io.read(op.slot)~=op.before then
                return false,'index slot changed externally for target '..op.key
            end
            if io.writable and not io.writable(op.slot) then
                return false,'index slot is not writable for target '..op.key
            end
        end
        -- Pin BEFORE any native pointer is exposed, including failed attempts.
        for _,clone in ipairs(plan.new) do
            self.pool[clone.key]=self.pool[clone.key] or {}
            self.pool[clone.key][clone.signature]=clone
            self.retained_bytes=self.retained_bytes+clone.bytes
            self.retained_count=self.retained_count+1
        end
        local attempted={}
        for _,op in ipairs(plan.operations) do
            attempted[#attempted+1]=op
            local ok,written=pcall(io.write,op.slot,op.after,op.before)
            if not ok or not written then
                local rollback_ok=true
                for i=#attempted,1,-1 do
                    local undo=attempted[i]
                    local current=io.read(undo.slot)
                    if current==undo.after then
                        local restored,value=pcall(io.write,undo.slot,undo.before,undo.after)
                        if not restored or not value then rollback_ok=false end
                    elseif current~=undo.before then rollback_ok=false end
                end
                self.faulted=not rollback_ok
                return false,rollback_ok and 'publication failed; rolled back'
                    or 'rollback failed; buffers pinned, restart the game'
            end
            plan.stats.writes=plan.stats.writes+1
        end
        self.active=plan.active
        return true,plan.stats
    end
    return self
end
return T
