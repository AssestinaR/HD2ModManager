-- Bounded multi-candidate observer. No heap walk, recursive pointer following,
-- unknown calls or writes. Lists are supported by previously captured code.
local P={}
function P.new(host)
    local read,u32=host.read,host.u32
    local base=host.base('game.dll')
    local verified=false
    local previous={}
    local cursor=1
    local last_topology=''
    local function ptr(s,o)
        local hi=s and u32(s,o+4)
        if hi and hi<0x8000 then return u32(s,o)+hi*4294967296 end
    end
    local function hx(v)return string.format('0x%016x',v)end
    local function hexbytes(s)return (s:gsub('.',function(c)return string.format('%02x',c:byte())end))end
    local self={}
    function self.tick(avatar,children,identity)
        local began=host.clock()
        local rows={}
        local function event(r)
            r.kind='candidate_frame';r.qpc_seconds=host.clock()
            r.called=false;r.atomic=false;r.ownership_proven=false
            rows[#rows+1]=r
        end
        if not verified then
            local dos=base and base>=65536 and read(base,64)
            local off=dos and u32(dos,0x3c)
            local pe=off and off<0x100000 and read(base+off,0x58)
            if not pe or pe:sub(1,4)~='PE\0\0' or u32(pe,8)~=1790161983
                or u32(pe,0x50)~=74727424 then
                return {state='build_mismatch',rows=rows,inspection_ms=0}
            end
            verified=true
        end
        local sites={}
        -- The game's base-16 tonumber path can saturate at 32 bits, unlike
        -- standalone LuaJIT. Parse bounded limbs; addresses remain below 2^47.
        local ah=type(avatar)=='string' and avatar:match('^0x(%x%x%x%x%x%x%x%x%x%x%x%x%x%x%x%x)$')
        local a=ah and tonumber(ah:sub(1,8),16)*4294967296+tonumber(ah:sub(9,16),16)
        if a and a>=65536 then sites[#sites+1]={label='avatar',address=a,size=0x1e8} end
        local root_bytes=read(base+0x3326e68,8)
        local root=ptr(root_bytes,0)
        if root and root>=65536 then
            -- Raw game-side hub header, not asserted to be a player manager.
            sites[#sites+1]={label='component_hub',address=root,size=0x100}
            for _,spec in ipairs({{0x6240,0xdf},{0x65b8,0xeb},{0x7380,0x124}}) do
                local at,kind=spec[1],spec[2]
                local cb=read(root+at,4)
                local count=cb and u32(cb,0)
                local data=count and count<=64 and (count==0 and '' or read(root+at+8,count*16))
                if data and read(root+at,4)==cb
                    and (count==0 or read(root+at+8,count*16)==data) then
                    local matches=0
                    for i=0,count-1 do
                        local p=ptr(data,i*16)
                        if u32(data,i*16+8)==kind and p and p>=65536 then
                            matches=matches+1
                            if kind~=0x124 and matches<=6 then
                                sites[#sites+1]={label=string.format('list_%x_kind_%x_%d',at,kind,i),
                                    address=p,size=0x100}
                            end
                        end
                    end
                    local signature=string.format('%d/%d',count,matches)
                    local label=string.format('directory_%x',at)
                    if previous[label]~=signature then
                        event({site=label,state='directory',count=count,matching_kind_count=matches,
                            capped=matches>6,component_kind=kind,
                            skipped_negative_candidate=kind==0x124})
                        previous[label]=signature
                    end
                else event({site=string.format('directory_%x',at),state='unavailable_or_unstable'}) end
            end
        else event({site='component_hub',state='root_unavailable'}) end
        if read(base+0x3326e68,8)~=root_bytes then
            return {state='unstable',rows=rows,inspection_ms=(host.clock()-began)*1000}
        end
        local topology={}
        for _,s in ipairs(sites) do topology[#topology+1]=s.label..':'..hx(s.address) end
        local top=table.concat(topology,';')
        if top~=last_topology then
            event({site='topology',state='topology',sites=top})
            last_topology=top;cursor=1
            -- No historic object data survives a changed candidate list.
            for k in pairs(previous) do if k:sub(1,10)~='directory_' then previous[k]=nil end end
        end
        -- Up to three small regions per frame; each has an independent timestamp.
        for _=1,math.min(3,#sites) do
            if cursor>#sites then cursor=1 end
            local s=sites[cursor];cursor=cursor+1
            local bytes=read(s.address,s.size)
            if not bytes or #bytes~=s.size then
                event({site=s.label,object=hx(s.address),state='unreadable'})
            elseif read(s.address,s.size)~=bytes then
                event({site=s.label,object=hx(s.address),state='unstable'})
            else
                local prev=previous[s.label]
                local reset=type(prev)~='table' or prev.address~=s.address
                local changed=not reset and prev.bytes~=bytes
                local r={site=s.label,object=hx(s.address),state='captured',
                    changed=changed,first_or_new_object=reset,byte_count=s.size}
                local offsets,equalities={},{ }
                for off=0,s.size-4,4 do
                    local value=u32(bytes,off)
                    if not reset and value~=u32(prev.bytes,off) then
                        offsets[#offsets+1]=string.format('%x:%08x',off,value)
                    end
                    if identity and value~=0 then
                        if value==identity.encoded_handle then
                            equalities[#equalities+1]=string.format('%x:encoded_avatar',off)
                        elseif identity.encoded_handle and value==math.floor(identity.encoded_handle/4) then
                            equalities[#equalities+1]=string.format('%x:avatar_id_candidate',off)
                        end
                    end
                end
                for off=0,s.size-8,8 do
                    local p=ptr(bytes,off)
                    if p and p>=65536 then
                        local label=hx(p)
                        if label==avatar or children[label] then
                            equalities[#equalities+1]=string.format('%x:%s',off,label)
                        end
                    end
                end
                r.value_equalities=table.concat(equalities,';')
                if changed then r.changed_dwords=table.concat(offsets,';') end
                if reset or host.clock()-prev.full_at>=0.25 then
                    r.full_hex=hexbytes(bytes)
                end
                previous[s.label]={address=s.address,bytes=bytes,
                    full_at=r.full_hex and host.clock() or prev.full_at}
                event(r)
            end
            if host.clock()-began>=0.00075 then break end
        end
        return {state='captured',rows=rows,inspection_ms=(host.clock()-began)*1000}
    end
    return self
end
return P
