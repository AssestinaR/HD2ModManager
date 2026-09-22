-- Two explicit hypotheses only: payload-relative arrays, or relocated
-- absolute arrays with otherwise unchanged serialized field layout.
return function(schema, expected)
    local function u32(s, p)
        local a,b,c,d=s:byte(p+1,p+4)
        return a+b*256+c*65536+d*16777216
    end
    local function number64(s,p)
        local lo,hi=u32(s,p),u32(s,p+4)
        if hi >= 32768 then return nil end
        return lo+hi*4294967296
    end
    local function pack64(v)
        local out={}
        for _=1,8 do out[#out+1]=string.char(v%256);v=math.floor(v/256) end
        return table.concat(out)
    end
    local function signature(p)
        local resources={}
        for role,id in pairs(p.resources) do resources[#resources+1]=role..'='..id end
        table.sort(resources)
        return table.concat({p.rawBodyType,p.slot,p.pieceType,p.weight,
            p.toneVariations,p.unit,table.concat(resources,',')},'|')
    end
    local function storage_report(read, root, bodies, bodyCount, arrays, mode)
        local report={in_place_growth_proven=false,ownership_proven=false,
            consumer_limit_known=false}
        local wrapper=read(root-24,24)
        if not wrapper or wrapper:sub(1,4)~='LDLD'
            or u32(wrapper,4)~=1 or u32(wrapper,8)~=0xd9a55aa0 then
            report.wrapper_valid=false
            return report
        end
        local length=u32(wrapper,12)
        report.wrapper_valid=true
        report.payload_bytes=length
        local spans={{kind='kit',start=root,stop=root+64},
            {kind='bodies',start=bodies,stop=bodies+bodyCount*24}}
        for _,row in ipairs(arrays) do
            -- Avoid the game's base-16 tonumber conversion for 64-bit addresses.
            local start=0
            for digit in row.address:sub(3):gmatch('.') do
                start=start*16+assert(('0123456789abcdef'):find(digit,1,true))-1
            end
            spans[#spans+1]={kind='pieces_'..row.body_type,start=start,
                stop=start+row.count*96}
        end
        table.sort(spans,function(a,b) return a.start<b.start end)
        local valid=mode=='absolute'
        local endAt=root
        local occupied=0
        for _,s in ipairs(spans) do
            if s.start<endAt or s.start<root or s.stop>root+length then valid=false end
            occupied=occupied+s.stop-s.start
            endAt=math.max(endAt,s.stop)
        end
        report.nonoverlapping_inside_payload=valid
        report.occupied_bytes=occupied
        report.packed_payload=valid and occupied==length
        local following=read(root+length,24)
        report.followed_by_ldld=following~=nil and following:sub(1,4)=='LDLD'
            and u32(following,4)==1
        report.arrays={}
        for i,s in ipairs(spans) do
            if s.kind:sub(1,7)=='pieces_' then
                local boundary=spans[i+1] and spans[i+1].start or root+length
                report.arrays[#report.arrays+1]={
                    kind=s.kind,start=string.format('0x%016x',s.start),
                    bytes=s.stop-s.start,
                    gap_before_next_span=valid and boundary-s.stop or nil,
                    next_span=spans[i+1] and spans[i+1].kind or 'payload_end'}
            end
        end
        return report
    end
    return function(read, root)
        local header=read(root,64)
        if not header then return nil end
        local want=expected[u32(header,0)]
        if not want then return nil end
        local count=number64(header,56)
        if not count or count~=want.bodyCount or count>16 then return nil end
        local field=number64(header,48)
        if not field then return nil end
        for _,mode in ipairs({'relative','absolute'}) do
            local bodyAddress=mode=='relative' and root+field or field
            if bodyAddress>65536 then
                local bodies=read(bodyAddress,count*24)
                if bodies then
                    local rebuilt, chunks, addresses={}, {}, {}
                    local offset, total=64+count*24,0
                    local valid=true
                    for i=0,count-1 do
                        local at=i*24
                        local n=number64(bodies,at+16)
                        local f=number64(bodies,at+8)
                        if not n or not f or n<1 or n>want.pieceCount then valid=false;break end
                        total=total+n
                        if total>want.pieceCount then valid=false;break end
                        local address=mode=='relative' and root+f or f
                        local pieces=address>65536 and read(address,n*96)
                        if not pieces then valid=false;break end
                        rebuilt[#rebuilt+1]=bodies:sub(at+1,at+8)..pack64(offset)..pack64(n)
                        chunks[#chunks+1]=pieces
                        addresses[#addresses+1]={body_type=u32(bodies,at),
                            address=string.format('0x%016x',address),count=n}
                        offset=offset+#pieces
                    end
                    if valid and total==want.pieceCount then
                        local payload=header:sub(1,48)..pack64(64)..pack64(count)
                            ..table.concat(rebuilt)..table.concat(chunks)
                        local ok,kit=pcall(schema.kit,payload)
                        if ok and kit.archive==want.archive and kit.kitType==0 then
                            local actual={}
                            for _,p in ipairs(kit.pieces) do actual[#actual+1]=signature(p) end
                            table.sort(actual)
                            if table.concat(actual,'\n')==want.signature then
                                return {item=kit.itemId,archive=kit.archive,
                                    root=string.format('0x%016x',root),mode=mode,
                                    bodies=string.format('0x%016x',bodyAddress),
                                    arrays=addresses,matched_pieces=total,
                                    storage=storage_report(read,root,bodyAddress,
                                        count,addresses,mode),
                                    consumer_proven=false,ownership_proven=false,
                                    self_copy_excluded=false}
                            end
                        end
                    end
                end
            end
        end
        return nil
    end
end
