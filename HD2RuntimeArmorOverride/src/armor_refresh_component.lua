-- Fixed evidence-derived component list, not a general heap scanner.
-- Raw fields are observations only: 0x124 is NOT asserted to be armor.
local C={}
function C.new(host)
    local read,u32=host.read,host.u32
    local base,verified,last,full_at=host.base('game.dll'),false,nil,0
    local self={}
    local function ptr(s,o)
        local hi=s and u32(s,o+4)
        if hi and hi<0x8000 then return u32(s,o)+hi*4294967296 end
    end
    local function hex(v)return string.format('0x%016x',v)end
    local function fail(reason)
        return {kind='component_frame',state=reason,qpc_seconds=host.clock(),called=false,atomic=false}
    end
    function self.tick(avatar,children)
        local began=host.clock()
        if not verified then
            local dos=base and base>=65536 and read(base,64)
            local off=dos and u32(dos,0x3c)
            local pe=off and off<0x100000 and read(base+off,0x58)
            if not pe or pe:sub(1,4)~='PE\0\0' or u32(pe,8)~=1790161983
                or u32(pe,0x50)~=74727424 then return fail('build_mismatch') end
            verified=true
        end
        local root_bytes=read(base+0x3326e68,8)
        local root=ptr(root_bytes,0)
        if not root or root<65536 then return fail('root_unavailable') end
        local count_bytes=read(root+0x7380,4)
        local count=count_bytes and u32(count_bytes,0)
        if not count or count>256 then return fail('list_bound') end
        local list=count==0 and '' or read(root+0x7388,count*16)
        if not list then return fail('list_unavailable') end
        local found={}
        for i=0,count-1 do
            if u32(list,i*16+8)==0x124 then found[#found+1]=ptr(list,i*16) end
        end
        -- Do not arbitrarily select one of several component objects.
        if #found~=1 or not found[1] or found[1]<65536 then return fail('component_ambiguous') end
        local object=found[1]
        local bytes=read(object,0xc30)
        if not bytes or #bytes~=0xc30 then return fail('component_unreadable') end
        local stable=read(base+0x3326e68,8)==root_bytes and read(root+0x7380,4)==count_bytes
            and read(root+0x7388,count*16)==list and read(object,0xc30)==bytes
        if not stable then return fail('unstable') end
        local changed=last~=nil and last.object==object and last.bytes~=bytes
        local reset=not last or last.object~=object
        local row={kind='component_frame',state='captured',qpc_seconds=began,
            object=hex(object),component_kind=0x124,list_count=count,
            raw_cursor=u32(bytes,0),raw_queue_cursor=u32(bytes,0xc28),
            changed=changed,first_or_new_object=reset,called=false,atomic=false,
            semantics_proven=false}
        local handles,keys,matches,offsets={},{},{},{}
        for i=0,31 do
            handles[#handles+1]=string.format('%d:%08x',i,u32(bytes,i*0x58+0x18))
            local lo,hi=u32(bytes,i*0x58+0x10),u32(bytes,i*0x58+0x14)
            keys[#keys+1]=string.format('%d:%08x%08x',i,hi,lo)
        end
        -- Exact numeric equality, not pointer ownership proof. Include offsets.
        for at=0,0xc28,8 do
            local v=ptr(bytes,at)
            if v and v>=65536 then
                local label=hex(v)
                if label==avatar or children[label] then
                    matches[#matches+1]=string.format('%x:%s:%s',at,label,label==avatar and 'avatar' or 'child')
                end
            end
        end
        row.object_equalities=table.concat(matches,';')
        if reset or changed or began>=full_at then
            full_at=began+0.25
            row.record_u32_at18=table.concat(handles,';')
            row.record_u64_at10=table.concat(keys,';')
            if not reset then
                for at=0,0xc2c,4 do
                    if bytes:sub(at+1,at+4)~=last.bytes:sub(at+1,at+4) then
                        offsets[#offsets+1]=string.format('%x:%08x',at,u32(bytes,at))
                    end
                end
                row.changed_dwords=table.concat(offsets,';')
            end
        end
        last={bytes=bytes,object=object}
        row.inspection_ms=(host.clock()-began)*1000
        return row
    end
    return self
end
return C
