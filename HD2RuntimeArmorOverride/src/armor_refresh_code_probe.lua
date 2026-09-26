-- Diagnostic-only direct/RIP-indirect reference survey of two fingerprinted modules.
-- No hooks, protection changes, function invocation or request-supplied address.
local C={}
function C.new(host)
    local read,u32=host.read,host.u32
    local sections,targets,modules,data_sections={},{},{},{}
    local pointer_slots,pointer_count,data_index,data_offset,data_total={},0,1,0,0
    local data_done,access_count=false,0
    local indirect_count,indirect_logged,captured_bytes=0,0,0
    local relevant_count=0
    local captured_ranges={}
    local section_index,offset,total,hits=1,0,0,0
    local self={done=false,errors=0}
    local function emit(r) host.emit(r) end
    local function stop(reason)
        self.errors=self.errors+1;self.done=true
        emit({kind='code_survey_error',reason=reason})
    end
    local function finish()
        self.done=true
        emit({kind='code_survey_end',bytes=total,hits=hits,complete=true,
            data_bytes=data_total,pointer_hits=pointer_count,access_hits=access_count,
            table_call_candidates=indirect_count,table_call_logged=indirect_logged,
            table_call_prefilter_matches=relevant_count,
            table_call_records_capped=relevant_count>indirect_logged,
            table_call_prefilter=host.generic_table_survey==true and 'nearby_master_root_load'
                or 'disabled_focused_seed_callers',prefilter_complete=false,
            rip_indirect_calls_covered=true,register_indirect_calls_covered=false,
            instruction_boundaries_validated=false})
    end
    local function u16(s,o)
        local a,b=s:byte(o+1,o+2)
        return a and b and a+b*256
    end
    local function module(name,timestamp,size)
        local base=host.base(name)
        local dos=base and base>=65536 and read(base,64)
        local off=dos and u32(dos,0x3c)
        local h=off and off<0x100000 and read(base+off,0x108)
        if not h or h:sub(1,4)~='PE\0\0' or u16(h,24)~=0x20b
            or u32(h,8)~=timestamp or u32(h,0x50)~=size then return nil end
        local count,opt=u16(h,6),u16(h,20)
        if count<1 or count>96 or opt<0xf0 or opt>4096
            or off+24+opt+count*40>size then return nil end
        local pdata,plen=u32(h,0xa0),u32(h,0xa4)
        if pdata==0 or plen==0 or plen%12~=0 or plen>0x400000
            or pdata+plen>size then return nil end
        local table_bytes=read(base+off+24+opt,count*40)
        if not table_bytes then return nil end
        local m={name=name,base=base,size=size,pdata=pdata,entries=plen/12}
        modules[#modules+1]=m
        for i=0,count-1 do
            local at=i*40
            local bytes,rva,flags=u32(table_bytes,at+8),u32(table_bytes,at+12),u32(table_bytes,at+36)
            if math.floor(flags/0x20000000)%2==1 and bytes>0 then
                if rva+bytes>size then return nil end
                sections[#sections+1]={module=m,rva=rva,bytes=bytes}
            elseif bytes>0 and math.floor(flags/0x40000000)%2==1 then
                if rva+bytes>size then return nil end
                data_sections[#data_sections+1]={module=m,rva=rva,bytes=bytes}
            end
        end
        return m
    end
    local initialized=false
    local function init()
        initialized=true
        local exe=module('helldivers2.exe',1790149348,60719104)
        local game=module('game.dll',1790161983,74727424)
        if not exe or not game or #sections==0 then stop('module_fingerprint_or_headers');return end
        for _,spec in ipairs({
            {0x3361c0,'unit_link_wrapper','\x48\x89\x5c\x24\x08'},
            {0x336240,'unit_matching_link_wrapper','\x48\x89\x5c\x24\x08'},
            {0x3362c0,'enqueue_node_link','\xb8\x01\x00\x00\x00'},
            {0x336310,'enqueue_matching_link','\x48\x89\x5c\x24\x08'},
            {0x1af240,'world_node_link','\x48\x8b\xc4'},
            {0x1af4a0,'world_matching_node_link','\x48\x8b\xc4'}
        }) do
            if read(exe.base+spec[1],#spec[3])~=spec[3] then stop('target_signature_mismatch');return end
            targets[exe.base+spec[1]]={rva=spec[1],label=spec[2]}
        end
        -- Reviewed initializer at 1ce5a2 takes 27cab00 and stores it at
        -- 27c8e60. Verify the actual function fields before following copies.
        local fields=read(exe.base+0x27cabf8,32)
        if fields then
            local valid=true
            for i,rva in ipairs({0x3361c0,0x336240,0x3362c0,0x336310}) do
                local at=(i-1)*8
                if u32(fields,at)+u32(fields,at+4)*4294967296~=exe.base+rva then valid=false end
            end
            if valid then
                targets[exe.base+0x27cab00]={rva=0x27cab00,label='link_api_table'}
            else stop('api_table_fields_mismatch');return end
        else stop('api_table_unreadable');return end
        -- Offline-reviewed RIP loads preceding decoded indirect calls. These
        -- snapshots identify table provenance, never authorize native calls.
        for _,global in ipairs({0x3326308,0x3326338}) do
            local slot=read(game.base+global,8)
            local function decode(b,at)
                local hi=b and u32(b,at+4)
                if hi and hi<0x8000 then return u32(b,at)+hi*4294967296 end
            end
            local root=decode(slot,0)
            local snapshots={}
            local function snapshot(address,label)
                if not address or address<65536 then return end
                local data=read(address,0x180)
                local fields={}
                if data then
                    for at=0,0x178,8 do
                        local value=decode(data,at)
                        if value then
                            for _,m in ipairs(modules) do
                                if value>=m.base and value<m.base+m.size then
                                    fields[#fields+1]=string.format('%x:%s+%x',at,m.name,value-m.base)
                                end
                            end
                        end
                    end
                end
                local identity='heap_or_other'
                for _,m in ipairs(modules) do
                    if address>=m.base and address<m.base+m.size then
                        identity=m.name..'+'..string.format('%x',address-m.base)
                    end
                end
                snapshots[#snapshots+1]={kind='api_root_snapshot',global_rva=global,
                    path=label,identity=identity,state=data and 'captured' or 'unreadable',
                    module_fields=table.concat(fields,';'),called=false,atomic=false}
                return data
            end
            local data=snapshot(root,'root')
            if data and global==0x3326308 then
                for _,at in ipairs({0x8,0x10,0x40,0xe0,0x128}) do
                    snapshot(decode(data,at),'root+'..string.format('%x',at))
                end
            end
            local stable=slot and read(game.base+global,8)==slot
            if #snapshots==0 then
                emit({kind='api_root_snapshot',global_rva=global,state='root_unavailable',called=false})
            else
                for _,r in ipairs(snapshots) do r.root_slot_stable=not not stable;emit(r) end
            end
        end
        emit({kind='code_survey_begin',survey_format=11,sections=#sections,chunk_bytes=65536,
            max_hits=64,max_pointer_hits=64,max_access_hits=64,
            max_table_call_records=256,max_table_code_bytes=524288,
            table_call_prefilter=host.generic_table_survey==true and 'nearby_master_root_load'
                or 'disabled_focused_seed_callers',prefilter_bytes=256,
            access_neighborhood=64,
            data_sections=#data_sections,read_only=true,called=false})
    end
    local function encode_pointer(n)
        local b={}
        for _=1,8 do b[#b+1]=string.char(n%256);n=math.floor(n/256) end
        return table.concat(b)
    end
    local function pointer(data,at)
        local hi=u32(data,at+4)
        if hi and hi<0x8000 then return u32(data,at)+hi*4294967296 end
    end
    local function data_tick()
        local s=data_sections[data_index]
        if not s then data_done=true;return end
        local m=s.module
        local n=math.min(65536,s.bytes-data_offset)
        local size=math.min(n+7,s.bytes-data_offset)
        local bytes=read(m.base+s.rva+data_offset,size)
        if not bytes or #bytes~=size then stop('data_section_unreadable');return end
        for address,target in pairs(targets) do
            local cursor=1
            while cursor<=n do
                local i=bytes:find(encode_pointer(address),cursor,true)
                if not i or i>n then break end
                cursor=i+1
                if pointer_count>=64 then stop('pointer_hit_limit');return end
                pointer_count=pointer_count+1
                local slot=s.rva+data_offset+i-1
                pointer_slots[#pointer_slots+1]={module=m.name,rva=slot,target=target.label}
                local neighbours={}
                -- Only output normalized module pointers, not raw data/strings.
                local first=math.max(s.rva,slot-64)
                local last=math.min(s.rva+s.bytes,slot+72)
                local context=read(m.base+first,last-first)
                if context then
                    for at=0,#context-8,8 do
                        local value=pointer(context,at)
                        if value then
                            for _,other in ipairs(modules) do
                                if value>=other.base and value<other.base+other.size then
                                    neighbours[#neighbours+1]=string.format('%d:%s+%x',
                                        first+at-slot,other.name,value-other.base)
                                end
                            end
                        end
                    end
                end
                emit({kind='module_pointer_candidate',module=m.name,slot_rva=slot,
                    target=target.label,target_rva=target.rva,
                    aligned=slot%8==0,neighbours=table.concat(neighbours,';'),
                    table_identity_proven=false,called=false})
            end
        end
        data_total=data_total+n;data_offset=data_offset+n
        if data_offset>=s.bytes then data_index=data_index+1;data_offset=0 end
    end
    -- PE exception ranges bound captured code, but can be chained ranges or
    -- funclets, NOT callable top-level entries. Leaf code without an entry is
    -- unresolved; a byte match itself is NOT a decoded instruction.
    local function envelope(m,rva)
        local lo,hi=0,m.entries-1
        for _=1,24 do
            if lo>hi then return nil end
            local mid=math.floor((lo+hi)/2)
            local b=read(m.base+m.pdata+mid*12,12)
            if not b then return nil end
            local first,last,unwind=u32(b,0),u32(b,4),u32(b,8)
            if first>=last or last>m.size or unwind>=m.size then return nil end
            if rva<first then hi=mid-1
            elseif rva>=last then lo=mid+1
            else return first,last,unwind end
        end
    end
    local seeded=false
    local function focused_windows()
        local game
        for _,m in ipairs(modules) do if m.name=='game.dll' then game=m end end
        if not game then return end
        -- Fixed evidence-derived sites. Raw windows are not instruction-boundary
        -- or callable-entry claims; capture neighbouring branches in one launch.
        for _,spec in ipairs({
            {0xbc2c80,0x200,'table_reference_neighbourhood'},
            {0xba23c0,0x100,'table_first_entry'},
            {0xba2400,0x100,'table_second_entry'},
            {0xba24b0,0x300,'table_third_entry'},
            {0xba2750,0x300,'table_fourth_entry'}
        }) do
            local rva,size,label=spec[1],spec[2],spec[3]
            local valid=false
            for _,s in ipairs(sections) do
                if s.module==game and rva>=s.rva and rva+size<=s.rva+s.bytes then valid=true end
            end
            local bytes=valid and read(game.base+rva,size)
            local first,last=envelope(game,rva)
            emit({kind='focused_code_window',module=game.name,rva=rva,label=label,
                state=bytes and 'captured' or 'unavailable',
                code_hex=bytes and bytes:gsub('.',function(c)return string.format('%02x',c:byte())end),
                exception_begin=first,exception_end=last,called=false,boundary_verified=false})
        end
        -- Bounded MSVC RTTI candidate preceding the observed code-pointer run.
        -- No native RTTI calls; offsets must remain inside the fingerprinted image.
        local b=read(game.base+0x214c2d8,8)
        local locator=b and pointer(b,0)
        if locator and locator>=game.base and locator+24<=game.base+game.size then
            local h=read(locator,24)
            if h and u32(h,0)==1 and locator-game.base==u32(h,20) then
                local type_rva=u32(h,12)
                local data=type_rva+272<=game.size and read(game.base+type_rva,272)
                local name=data and data:sub(17):match('^([^%z]+)%z')
                emit({kind='rtti_candidate',locator_rva=locator-game.base,type_rva=type_rva,
                    name=name and name:match('^[%w_%.%?%$%@]+$') and name or nil,
                    identity_proven=false,called=false})
            end
        end
    end
    local function dispatch_snapshot()
        local game
        for _,m in ipairs(modules) do if m.name=='game.dll' then game=m end end
        if not game then return end
        local function executable(v)
            for _,s in ipairs(sections) do
                if v and v>=s.module.base+s.rva and v<s.module.base+s.rva+s.bytes then
                    return s.module.name..'+'..string.format('%x',v-s.module.base)
                end
            end
        end
        -- Observed static slot, not a request-provided address.
        local slot=0x214c6e0
        local b=read(game.base+slot,8)
        if b and pointer(b,0)==game.base+0xba92c0 then
            local first=slot
            local fields={}
            for step=0,511 do
                local at=slot-step*8
                local q=read(game.base+at,8)
                local label=q and executable(pointer(q,0))
                if not label then break end
                first=at
                fields[#fields+1]=string.format('%x:%s',at,label)
            end
            emit({kind='dispatch_table_snapshot',module=game.name,slot_rva=slot,
                contiguous_begin_rva=first,backward_entries=#fields,
                backward_capped=#fields==512,fields=table.concat(fields,';'),
                table_boundary_proven=false,called=false})
            if #fields>0 and #fields<512 then
                targets[game.base+first]={rva=first,label='dispatch_table_run_candidate'}
                pointer_slots[#pointer_slots+1]={module=game.name,rva=first,target='dispatch_table_run_candidate'}
            end
        end
        local global=0x3326e68
        local root_bytes=read(game.base+global,8)
        local root=root_bytes and pointer(root_bytes,0)
        local count_bytes=root and root>=65536 and read(root+0x7380,4)
        local count=count_bytes and u32(count_bytes,0)
        local row={kind='component_list_snapshot',global_rva=global,count=count,
            state='unavailable',called=false,atomic=false}
        if count and count<=256 then
            local data=count==0 and '' or read(root+0x7388,count*16)
            if data then
                local kinds,matched={},{}
                for i=0,count-1 do
                    local kind=u32(data,i*16+8)
                    kinds[#kinds+1]=string.format('%d:%x',i,kind)
                    if kind==0x124 then
                        local object=pointer(data,i*16)
                        local header=object and object>=65536 and read(object,8)
                        local queue=object and object>=65536 and read(object+0xc28,4)
                        matched[#matched+1]=string.format('%d:readable=%s:first_code=%s:queue=%s',
                            i,tostring(header~=nil),header and executable(pointer(header,0)) or 'none',
                            queue and tostring(u32(queue,0)) or 'unreadable')
                    end
                end
                row.state='captured';row.kinds=table.concat(kinds,';')
                row.matches=table.concat(matched,';')
                row.root_stable=read(game.base+global,8)==root_bytes
                row.count_stable=read(root+0x7380,4)==count_bytes
            end
        elseif count then row.state='count_rejected' end
        emit(row)
    end
    local function seed_chain(site,signature,label)
        -- Follow only the previously observed call's exception metadata.
        -- UNW_FLAG_CHAININFO identifies parent ranges, not refresh semantics.
        local game
        for _,m in ipairs(modules) do if m.name=='game.dll' then game=m end end
        if not game or read(game.base+site,#signature)~=signature then
            emit({kind='code_seed_status',seed=label,state='signature_unavailable',called=false});return
        end
        local first,last,unwind=envelope(game,site)
        local visited={}
        for depth=0,7 do
            if not first or visited[unwind] then break end
            visited[unwind]=true
            local h=read(game.base+unwind,4)
            local version=h and h:byte(1)%8
            local flags=h and math.floor(h:byte(1)/8)
            local count=h and h:byte(3)
            local row={kind='code_seed_range',module=game.name,site_rva=first,
                function_begin=first,function_end=last,unwind_rva=unwind,
                seed=label,chain_depth=depth,unwind_version=version,unwind_flags=flags,
                called=false,callable_entry_proven=false,envelope_state='unavailable'}
            local executable=false
            for _,s in ipairs(sections) do
                if s.module==game and first>=s.rva and last<=s.rva+s.bytes then executable=true end
            end
            if not executable then row.envelope_state='non_executable';emit(row);break end
            if last-first<=16384 then
                local code=read(game.base+first,last-first)
                if code and #code==last-first then
                    row.code_hex=code:gsub('.',function(c)return string.format('%02x',c:byte())end)
                    row.envelope_state='captured'
                end
            end
            emit(row)
            if version~=1 or not flags or flags%4~=0 or math.floor(flags/4)%2~=1 then
                if version==1 and flags and flags<4 then
                    targets[game.base+first]={rva=first,label=label}
                end
                break
            end
            local at=unwind+4+math.floor((count+1)/2)*4
            local b=at+12<=game.size and read(game.base+at,12)
            if not b then break end
            first,last,unwind=u32(b,0),u32(b,4),u32(b,8)
            if first>=last or last>game.size or unwind+4>game.size then break end
        end
    end
    function self.tick()
        if self.done then return end
        if not initialized then init();return end
        if not seeded then
            seeded=true
            seed_chain(0x12ad1ce,'\x41\xff\x92\xc8\0\0\0','observed_link_chain_owner_candidate')
            seed_chain(0xb9fe12,'\xe8\x39\xd1\x70\0','component_dispatch_a')
            seed_chain(0xba9388,'\xe8\xc3\x3b\x70\0','component_dispatch_b')
            dispatch_snapshot()
            focused_windows()
            return
        end
        if not data_done then data_tick();return end
        local section=sections[section_index]
        if not section then finish();return end
        local m=section.module
        local n=math.min(65536,section.bytes-offset)
        local length=math.min(n+6,section.bytes-offset)
        local bytes=read(m.base+section.rva+offset,length)
        if not bytes or #bytes~=length then stop('executable_section_unreadable');return end
        local cursor=1
        while cursor<=n do
            local i=bytes:find('[\xe8\xe9\xff]',cursor)
            if not i or i>n or i+4>#bytes then break end
            cursor=i+1
            local rva=section.rva+offset+i-1
            local opcode=bytes:byte(i)
            -- Game-side call/jump [register+disp32], no SIB addressing.
            -- Offsets alone DO NOT establish which API/vtable is in the register.
            local modrm=opcode==0xff and bytes:byte(i+1)
            if host.generic_table_survey==true and m.name=='game.dll' and modrm and modrm>=128 and modrm<192
                and (math.floor(modrm/8)%8==2 or math.floor(modrm/8)%8==4)
                and modrm%8~=4 and i+5<=#bytes then
                local displacement=u32(bytes,i+1)
                if displacement==0xf8 or displacement==0x100
                    or displacement==0x108 or displacement==0x110
                    or displacement==0xb8 or displacement==0xc0
                    or displacement==0xc8 or displacement==0xd0 then
                    indirect_count=indirect_count+1
                    -- Include the +40 alias's b8/c0/c8/d0 slots. Require a
                    -- nearby RIP load of the observed master root, not merely
                    -- a common structure offset. Still NOT decoded provenance.
                    -- Reads overlap chunk boundaries and stay inside the section.
                    local before=math.min(256,rva-section.rva)
                    local context=before>0 and read(m.base+rva-before,before)
                    local nearby=false
                    if context then
                        local search=1
                        while search<=#context do
                            local j=context:find('[\x48\x4c]\x8b',search)
                            if not j then break end
                            search=j+1
                            local mr=context:byte(j+2)
                            if mr and mr<64 and mr%8==5 and j+6<=#context then
                                local d=u32(context,j+2)
                                if d>=0x80000000 then d=d-0x100000000 end
                                if rva-before+j-1+7+d==0x3326308 then nearby=true;break end
                            end
                        end
                    end
                    if nearby then relevant_count=relevant_count+1 end
                    if nearby and indirect_logged<256 then
                        indirect_logged=indirect_logged+1
                        local row={kind='table_call_candidate',module=m.name,site_rva=rva,
                            displacement=displacement,opcode=opcode,modrm=modrm,
                            api_identity_proven=false,called=false,decoded=false,
                            prefilter='nearby_master_root_load',prefilter_bytes=before,
                            envelope_state='unavailable'}
                        local first,last=envelope(m,rva)
                        if first and first>=section.rva and last<=section.rva+section.bytes then
                            row.function_begin=first;row.function_end=last
                            local key=m.name..':'..first
                            if captured_ranges[key] then row.envelope_state='shared_capture'
                            elseif last-first<=16384 and captured_bytes+last-first<=524288 then
                                local code=read(m.base+first,last-first)
                                if code and #code==last-first then
                                    row.code_hex=code:gsub('.',function(c)return string.format('%02x',c:byte())end)
                                    row.envelope_state='captured';captured_ranges[key]=true
                                    captured_bytes=captured_bytes+#code
                                else row.envelope_state='unreadable' end
                            else row.envelope_state='size_limit' end
                        end
                        emit(row)
                    end
                end
            end
            local indirect=opcode==0xff and (bytes:byte(i+1)==0x15 or bytes:byte(i+1)==0x25)
            local delta=(opcode~=0xff or indirect) and u32(bytes,i+(indirect and 1 or 0))
            if not delta then delta=0 end
            if delta>=0x80000000 then delta=delta-0x100000000 end
            local destination=m.base+rva+5+delta
            local slot
            if opcode==0xff then
                destination=nil
                if indirect and i+5<=#bytes then
                    slot=rva+6+delta
                    if slot>=0 and slot+8<=m.size then
                        local pointer=read(m.base+slot,8)
                        local hi=pointer and u32(pointer,4)
                        if hi and hi<0x8000 then destination=u32(pointer,0)+hi*4294967296 end
                    end
                end
            end
            local target=destination and targets[destination]
            if target then
                if hits>=64 then stop('hit_limit');return end
                hits=hits+1
                local row={kind='code_reference_candidate',module=m.name,site_rva=rva,
                    opcode=opcode,target_rva=target.rva,target=target.label,
                    reference_kind=indirect and 'rip_indirect' or 'rel32',pointer_slot_rva=slot,
                    called=false,decoded=false,envelope_state='unavailable'}
                local first,last=envelope(m,rva)
                if first and first>=section.rva and last<=section.rva+section.bytes then
                    row.function_begin=first;row.function_end=last
                    if last-first<=16384 then
                        local code=read(m.base+first,last-first)
                        if code and #code==last-first then
                            row.code_hex=code:gsub('.',function(c)return string.format('%02x',c:byte())end)
                            row.envelope_state='captured'
                        else row.envelope_state='unreadable' end
                    else row.envelope_state='oversized' end
                end
                emit(row)
            end
        end
        -- Candidates only: RIP-relative MOV/LEA into a discovered slot's bounded
        -- neighborhood. Offline decoding must verify instruction boundaries.
        local pos=1
        while pos<=n and #pointer_slots>0 do
            local i=bytes:find('[\x48\x4c][\x8b\x8d]',pos)
            if not i or i>n or i+6>#bytes then break end
            pos=i+1
            local modrm=bytes:byte(i+2)
            if modrm<64 and modrm%8==5 then
                local delta=u32(bytes,i+2)
                if delta>=0x80000000 then delta=delta-0x100000000 end
                local rva=section.rva+offset+i-1
                local destination=rva+7+delta
                local selected
                for _,slot in ipairs(pointer_slots) do
                    if slot.module==m.name and math.abs(destination-slot.rva)<=64 then
                        if not selected or math.abs(destination-slot.rva)<math.abs(destination-selected.rva) then
                            selected=slot
                        end
                    end
                end
                if selected then
                    if access_count>=64 then stop('access_hit_limit');return end
                    access_count=access_count+1
                    local row={kind='pointer_access_candidate',module=m.name,site_rva=rva,
                        destination_rva=destination,slot_rva=selected.rva,target=selected.target,
                        relative_to_slot=destination-selected.rva,decoded=false,called=false,
                        envelope_state='unavailable'}
                    local first,last=envelope(m,rva)
                    if first and first>=section.rva and last<=section.rva+section.bytes then
                        row.function_begin=first;row.function_end=last
                        if last-first<=16384 then
                            local code=read(m.base+first,last-first)
                            if code and #code==last-first then
                                row.code_hex=code:gsub('.',function(c)return string.format('%02x',c:byte())end)
                                row.envelope_state='captured'
                            end
                        else row.envelope_state='oversized' end
                    end
                    emit(row)
                end
            end
        end
        total=total+n;offset=offset+n
        if offset>=section.bytes then
            emit({kind='code_section_end',module=m.name,section_rva=section.rva,bytes=section.bytes})
            section_index=section_index+1;offset=0
        end
    end
    return self
end
return C
