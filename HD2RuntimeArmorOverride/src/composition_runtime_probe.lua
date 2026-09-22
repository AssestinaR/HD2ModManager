-- Read-only composition probe.
-- It correlates authoritative item/archive signatures with nearby runtime
-- bytes and candidate pointers. It does not infer a writable layout.
local existing = rawget(_G, 'HD2ArmorCompositionProbe')
if existing then return existing end
local M = {version = '0.10.0', status = 'starting'}
rawset(_G, 'HD2ArmorCompositionProbe', M)
local loader = rawget(_G, 'CowboyBingusModLoader')
if type(loader) ~= 'table' or loader.api ~= 1 or type(loader.open_log) ~= 'function' then
    M.status = 'loader_unavailable'; return M
end
local opened, file = pcall(loader.open_log, 'ArmorCompositionProbe_runtime.log')
if not opened or not file then M.status = 'log_open_failed'; return M end
local function q(v)
    return '"' .. tostring(v):gsub('[%z\1-\31\\"]', function(c)
        if c == '\\' then return '\\\\' end
        if c == '"' then return '\\"' end
        return string.format('\\u%04x',string.byte(c))
    end) .. '"'
end
local function val(v)
    if v == nil then return 'null' end
    if type(v) == 'boolean' then return v and 'true' or 'false' end
    if type(v) == 'number' then return tostring(v) end
    if type(v) == 'table' then
        local a, array = {}, true
        for k in pairs(v) do if type(k) ~= 'number' then array=false; break end end
        if array then
            for i=1,#v do a[#a+1] = val(v[i]) end
            return '[' .. table.concat(a, ',') .. ']'
        end
        for k,x in pairs(v) do a[#a+1] = q(k) .. ':' .. val(x) end
        return '{' .. table.concat(a, ',') .. '}'
    end
    return q(v)
end
local function emit(kind, fields)
    local a = {'{"record_type":' .. q(kind)}
    for k,v in pairs(fields or {}) do a[#a+1] = ',' .. q(k) .. ':' .. val(v) end
    file:write(table.concat(a) .. '}\n'); file:flush()
end
local ok, ffi = pcall(require, 'ffi')
if not ok or ffi.os ~= 'Windows' or not ffi.abi('64bit') then
    M.status='unsupported'
    emit('probe_error', {reason='windows_x64_ffi_required'}); file:close(); return M
end
ffi.cdef [[
void *GetModuleHandleA(const char *);
void *GetCurrentProcess(void);
int ReadProcessMemory(void *, const void *, void *, size_t, size_t *);
size_t VirtualQuery(const void *, void *, size_t);
]]
local kernel = ffi.load('kernel32')
local process = kernel.GetCurrentProcess()
local base = tonumber(ffi.cast('uintptr_t', kernel.GetModuleHandleA('game.dll')))
local function read(address, size)
    if not address or address < 65536 or size <= 0 or size > 0x10003f then return nil end
    local out, got = ffi.new('uint8_t[?]', size), ffi.new('size_t[1]')
    if kernel.ReadProcessMemory(process, ffi.cast('const void *', address), out, size, got) == 0
        or tonumber(got[0]) ~= size then return nil end
    return ffi.string(out, size)
end
local function u32(s, at)
    if not s or at < 0 or #s < at + 4 then return nil end
    local a,b,c,d = s:byte(at+1,at+4)
    return a + b*256 + c*65536 + d*16777216
end
local function u64(s, at)
    if not s or at < 0 or #s < at + 8 then return nil end
    local n = ffi.new('uint64_t[1]'); ffi.copy(n, s:sub(at+1,at+8), 8)
    return tonumber(n[0])
end
local function hex(s)
    return s and (s:gsub('.', function(c) return string.format('%02x', string.byte(c)) end)) or nil
end
local function le32(n)
    local t = {}; for _=1,4 do t[#t+1] = string.char(n % 256); n=math.floor(n/256) end
    return table.concat(t)
end
local function le64(hex_digits)
    local t = {}; for i=#hex_digits,1,-2 do t[#t+1] = string.char(tonumber(hex_digits:sub(i-1,i),16)) end
    return table.concat(t)
end
local function pack64(v)
    local t={};for _=1,8 do t[#t+1]=string.char(v%256);v=math.floor(v/256) end
    return table.concat(t)
end
local function region_number(v)
    return string.format('0x%016x',v or 0)
end
local function hex_number(text)
    local n=0
    for digit in text:sub(3):gmatch('.') do
        local value=('0123456789abcdef'):find(digit,1,true)
        if not value then return nil end
        n=n*16+value-1
    end
    return n
end
local function ptr(v) return v and v > 0x10000 and v < 0x0000800000000000 end
local targets = {
    {name='B-08', item=488691438, archive='dfa7c28c3b490a8c', pieces=19},
    {name='RE-1861', item=3340614564, archive='39fe1f0308f4514a', pieces=15},
    {name='RE-2310', item=820333399, archive='d923906f8b815987', pieces=23},
    {name='CE-81', item=2546928266, archive='88ccc33c4cca4d20', pieces=29}
}
local patterns, expected_by_name = {}, {}
for _,t in ipairs(targets) do
    patterns[#patterns+1] = {name=t.name, bytes=le32(t.item)}
    expected_by_name[t.name] = {
        archive=le64(t.archive), archive_text=t.archive, bodies=3, pieces=t.pieces
    }
end
local function find_all(data, needle, max, limit)
    local out, at = {}, 1
    limit = limit or #data
    while #out < (max or 64) do
        local p = data:find(needle, at, true)
        if not p or p > limit then break end
        out[#out+1] = p-1; at = p + 1
    end
    return out
end
local function candidate_qwords(data, center)
    local result, seen = {}, {}
    local first = math.max(0, center - 0x100)
    local last = math.min(#data - 8, center + 0x100)
    for at=first,last,8 do
        local v = u64(data, at)
        if ptr(v) then
            local key = string.format('0x%016x', v)
            if not seen[key] then seen[key] = true; result[#result+1] = {offset=at-center,value=key} end
        end
    end
    return result
end
local function relation_probe(root, region, region_size, chain)
    local function address_number(text)
        local n=0
        for digit in text:sub(3):gmatch('.') do
            n=n*16+assert(('0123456789abcdef'):find(digit,1,true))-1
        end
        return n
    end
    local lower=math.max(region,math.floor((root-0x70000)/4)*4)
    local upper=math.min(region+region_size,root+0x70000)
    local size=upper-lower
    if size<=0 or size>0x100000 then return nil end
    local data=read(lower,size)
    if not data then return {readable=false} end
    local wanted={[root]=true}
    local labels={[root]='root'}
    local body=address_number(chain.bodies)
    if body then wanted[body]=true;labels[body]='bodies' end
    for _,row in ipairs(chain.arrays or {}) do
        local a=address_number(row.address)
        if a then wanted[a]=true;labels[a]='body_'..tostring(row.body_type) end
    end
    local refs={}
    -- LDLD payloads can be four-byte aligned, including pointer fields.
    for at=0,#data-8,4 do
        local v=u64(data,at)
        if wanted[v] then
            local key=labels[v]
            refs[key]=refs[key] or {count=0,locations={}}
            refs[key].count=refs[key].count+1
            if #refs[key].locations<16 then
                refs[key].locations[#refs[key].locations+1]=
                    string.format('0x%016x',lower+at)
            end
        end
        if at>0 and at%0x10000==0 then coroutine.yield() end
    end
    local ldld={}
    for _,at in ipairs(find_all(data,'LDLD',64)) do
        ldld[#ldld+1]=string.format('0x%016x',lower+at)
    end
    return {readable=true,window_start=string.format('0x%016x',lower),
        window_size=size,backrefs=refs,ldld_headers=ldld,
        local_window_only=true,consumer_proven=false}
end
local function memory_regions()
    local mbi = ffi.new('uint8_t[48]')
    local cursor, queried, result = 0, 0, {}
    while queried < 32768 and cursor < 0x0000800000000000 do
        local got = kernel.VirtualQuery(ffi.cast('const void *',cursor), mbi, 48)
        if got == 0 then break end
        local region = tonumber(ffi.cast('uintptr_t *', mbi)[0])
        local size = tonumber(ffi.cast('size_t *', mbi + 24)[0]) or 0
        local state = tonumber(ffi.cast('uint32_t *', mbi + 32)[0]) or 0
        local protect = tonumber(ffi.cast('uint32_t *', mbi + 36)[0]) or 0
        local region_type = tonumber(ffi.cast('uint32_t *', mbi + 40)[0]) or 0
        local protection = protect % 256
        local readable = state == 0x1000 and math.floor(protect/256)%2 == 0
            and (protection == 2 or protection == 4 or protection == 8
                or protection == 32 or protection == 64 or protection == 128)
        if readable and size > 0 then
            result[#result+1] = {base=region,size=size,kind=region_type,
                protect=protection,state=state}
        end
        queried = queried + 1
        cursor = region + math.max(size, 0x1000)
        if queried % 2048 == 0 then coroutine.yield() end
    end
    local priority = {[0x40000]=1,[0x20000]=2,[0x1000000]=3}
    table.sort(result,function(a,b)
        local pa,pb=priority[a.kind] or 4,priority[b.kind] or 4
        if pa~=pb then return pa<pb end
        return a.base>b.base
    end)
    return result,queried
end
local classify_holder_context
local function holder_probe(read, available, targets, max_region_size)
    local holders={}
    for _,entry in ipairs(available) do
        if entry.size>0 and entry.size<=max_region_size then
            local consumed=0
            while consumed<entry.size do
                local amount=math.min(0x100000,entry.size-consumed)
                local address=entry.base+consumed
                local overlap=math.min(7,entry.size-consumed-amount)
                local data=read(address,amount+overlap)
                if data then
                    for _,target in ipairs(targets) do
                        for _,at in ipairs(find_all(data,target.bytes,64,amount)) do
                            local absolute=address+at
                            if not target.exclude[absolute] then
                                local key=target.name
                                holders[key]=holders[key] or {}
                                if #holders[key]<128 then
                                    local context=read(math.max(65536,absolute-64),192)
                                    holders[key][#holders[key]+1]={
                                        address=region_number(absolute),
                                        target=region_number(target.value),
                                        aligned8=absolute%8==0,
                                        region=region_number(entry.base),
                                        region_type=region_number(entry.kind),
                                        protection=entry.protect,
                                        context=hex(context),
                                        context_class=classify_holder_context(context,target.value)
                                    }
                                end
                            end
                        end
                    end
                end
                consumed=consumed+amount
                coroutine.yield()
            end
        end
    end
    return holders
end
local function locate_region(available, address)
    for _,entry in ipairs(available) do
        if address>=entry.base and address<entry.base+entry.size then
            return {
                base=region_number(entry.base),size=entry.size,
                end_address=region_number(entry.base+entry.size),
                region_type=region_number(entry.kind),
                protection=entry.protect,state=entry.state
            }
        end
    end
    return nil
end
local function read_qword_summary(data)
    local values={}
    if not data then return values end
    for at=0,#data-8,8 do
        local value=u64(data,at)
        if value then values[#values+1]=region_number(value) end
    end
    return values
end
classify_holder_context=function(data, target_value)
    local count_offsets, target_offsets, pointer_count = {}, {}, 0
    if not data then return {readable=false} end
    for at=0,#data-8,8 do
        local value=u64(data,at)
        if value then
            if value==411 then count_offsets[#count_offsets+1]=at-64 end
            if target_value and value==target_value then
                target_offsets[#target_offsets+1]=at-64
            end
            if ptr(value) then pointer_count=pointer_count+1 end
        end
    end
    local score=0
    if #count_offsets>0 then score=score+3 end
    if #target_offsets>0 then score=score+2 end
    if pointer_count>=2 then score=score+1 end
    return {
        readable=true,
        count_411_offsets=count_offsets,
        target_offsets=target_offsets,
        pointer_qwords=pointer_count,
        structural_score=score,
        likely_container=score>=5
    }
end
local function scan()
    local available, queried = memory_regions()
    local hits, scanned, scanned_regions = {}, 0, 0
    local raw_item_hits, header_anchor_hits = 0, 0
    local successful_bytes, failed_bytes, skipped_bytes = 0, 0, 0
    local max_scanned, chunk_size = 0x200000000, 0x100000
    local max_region_size = 0x08000000
    local bytes_by_type = {}
    local verified_roots = {}
    local layouts,index_candidates = {},{}
    emit('composition_region_inventory', {
        queried=queried,readable_regions=#available,max_attempted_bytes=max_scanned,
        max_region_size=max_region_size
    })
    if COMPOSITION_INDEX then
        local fast_layout
        for _,entry in ipairs(available) do
            if entry.kind==0x20000 and entry.size>=384196 and entry.size<=0x80000 then
                local prefix=read(entry.base,4)
                if prefix and u32(prefix,0)==411 then
                    local checked=COMPOSITION_INDEX.library(
                        read,entry.base,entry.size,nil)
                    if checked and checked.count==411 and checked.bytes==384196 then
                        fast_layout=checked
                        break
                    end
                end
            end
        end
        if fast_layout then
            local needle=pack64(fast_layout.roots[1])
                ..pack64(fast_layout.roots[2])..pack64(fast_layout.roots[3])
            for _,entry in ipairs(available) do
                if entry.kind==0x20000 and entry.size>=fast_layout.count*8
                    and entry.size<=0x20000 then
                    local data=read(entry.base,entry.size)
                    if data then
                        for _,at in ipairs(find_all(data,needle,8)) do
                            local candidate=COMPOSITION_INDEX.check(
                                read,fast_layout,entry.base+at,fast_layout.roots[1])
                            if candidate then
                                local roots={}
                                for index,root in ipairs(fast_layout.roots) do
                                    local header=read(root,4)
                                    local item=header and u32(header,0)
                                    if item==488691438 or item==3340614564
                                        or item==820333399 or item==2546928266 then
                                        roots[tostring(item)]={
                                            address=root,index=index-1
                                        }
                                    end
                                end
                                if roots['488691438'] and roots['3340614564']
                                    and roots['820333399'] and roots['2546928266'] then
                                    M.result={
                                        index_base=hex_number(candidate.index_base),
                                        blob_base=fast_layout.base,
                                        matched_entries=411,roots=roots,
                                        fast_path=true,read_only_probe=true,
                                        consumer_proven=false,ownership_proven=false
                                    }
                                    emit('composition_fast_registry',{
                                        index_base=candidate.index_base,
                                        blob_base=region_number(fast_layout.base),
                                        blob_bytes=fast_layout.bytes,
                                        matched_entries=411,roots=roots,
                                        read_only=true,consumer_proven=false
                                    })
                                    emit('composition_scan',{
                                        game_dll_base=region_number(base),
                                        fast_path=true,matched_entries=411,
                                        complete_address_space=false,
                                        consumer_proven=false})
                                    return {}
                                end
                            end
                        end
                    end
                end
                coroutine.yield()
            end
        end
    end
    for _,entry in ipairs(available) do
        if scanned >= max_scanned then break end
        local region,size,region_type=entry.base,entry.size,entry.kind
        if size > 0 and size <= max_region_size then
            local consumed = 0
            while consumed < size and scanned < max_scanned do
                local amount = math.min(chunk_size, size-consumed, max_scanned-scanned)
                local chunk_address = region+consumed
                -- The anchor includes fields through +63, so retain enough
                -- overlap to validate a header crossing a chunk boundary.
                local overlap = math.min(63, size-consumed-amount)
                local data = read(chunk_address, amount+overlap)
                scanned = scanned + amount
                local type_key=string.format('0x%x',region_type)
                bytes_by_type[type_key]=(bytes_by_type[type_key] or 0)+amount
                if data then
                successful_bytes=successful_bytes+amount
                for _,p in ipairs(patterns) do
                    for _,at in ipairs(find_all(data,p.bytes,32,amount)) do
                        raw_item_hits=raw_item_hits+1
                        local absolute = chunk_address + at
                        local want=expected_by_name[p.name]
                        local header=data:sub(at+1,at+64)
                        if raw_item_hits<=100 then
                            emit('composition_item_context', {
                                target=p.name,address=string.format('0x%016x',absolute),
                                region_type=type_key,
                                window=hex(read(math.max(65536,absolute-0x100),0x300)),
                                consumer_proven=false,self_copy_excluded=false
                            })
                        end
                        if #header==64 and header:sub(33,40)==want.archive
                            and u32(header,40)==0 and u64(header,56)==want.bodies then
                            header_anchor_hits=header_anchor_hits+1
                            local verified
                            if COMPOSITION_VERIFY then
                                local checked, result = pcall(COMPOSITION_VERIFY, read, absolute)
                                if checked and result then
                                    result.relations=relation_probe(
                                        absolute,region,size,result)
                                    emit('composition_chain_candidate', result)
                                    verified=result
                                    if result.storage and result.storage.wrapper_valid then
                                        if COMPOSITION_INDEX and not layouts[region] then
                                            layouts[region]=COMPOSITION_INDEX.library(
                                                read,region,size,absolute)
                                        end
                                        verified_roots[#verified_roots+1]={
                                            item=result.item,address=absolute,
                                            bytes=pack64(absolute),layout=layouts[region]}
                                    end
                                end
                            end
                            hits[#hits+1] = {
                                target=p.name,
                                address=string.format('0x%016x',absolute),
                                region=string.format('0x%016x',region),
                                region_type=type_key, offset=consumed+at,
                                expected_archive=want.archive_text,
                                expected_pieces=want.pieces,
                                chain_verified=verified~=nil,
                                window=hex(read(math.max(65536,absolute-0x100),0x300)),
                                nearby_qwords=candidate_qwords(data,at)
                            }
                        end
                    end
                end
                else
                    failed_bytes=failed_bytes+amount
                end
                consumed = consumed + amount
                coroutine.yield()
            end
        else
            skipped_bytes=skipped_bytes+size
        end
        scanned_regions=scanned_regions+1
        if scanned_regions % 128 == 0 then
            emit('composition_scan_progress', {
                scanned_regions=scanned_regions,readable_regions=#available,
                attempted_bytes=scanned,raw_item_hits=raw_item_hits,
                successful_bytes=successful_bytes,failed_bytes=failed_bytes,
                skipped_bytes=skipped_bytes,
                header_anchor_hits=header_anchor_hits,hit_count=#hits
            })
        end
        coroutine.yield()
    end
    local root_refs={}
    if #verified_roots>0 then
        for _,entry in ipairs(available) do
            if entry.kind==0x20000 and entry.size<=max_region_size then
                local consumed=0
                while consumed<entry.size do
                    local amount=math.min(chunk_size,entry.size-consumed)
                    local address=entry.base+consumed
                    local overlap=math.min(7,entry.size-consumed-amount)
                    local data=read(address,amount+overlap)
                    if data then
                        for _,root in ipairs(verified_roots) do
                            for _,at in ipairs(find_all(data,root.bytes,32,amount)) do
                                local ref=address+at
                                -- The root's own Item ID is not a pointer reference.
                                if ref~=root.address then
                                    if root.layout then
                                        local checked=COMPOSITION_INDEX.check(
                                            read,root.layout,ref,root.address)
                                        if checked and not index_candidates[checked.index_base] then
                                            index_candidates[checked.index_base]=checked
                                            local at=ref-root.layout.indices[root.address]*8
                                            checked.prefix=hex(read(at-64,64))
                                            checked.suffix=hex(read(at+checked.index_bytes,32))
                                            emit('composition_full_index',checked)
                                        end
                                    end
                                    local key=tostring(root.item)
                                    root_refs[key]=root_refs[key] or {}
                                    if #root_refs[key]<64 then
                                        root_refs[key][#root_refs[key]+1]={
                                            address=string.format('0x%016x',ref),
                                            aligned8=ref%8==0,
                                            context=hex(read(math.max(65536,ref-32),96))}
                                    end
                                end
                            end
                        end
                    end
                    consumed=consumed+amount
                    coroutine.yield()
                end
            end
        end
        emit('composition_root_backrefs',{
            roots=#verified_roots,references=root_refs,
            private_regions_only=true,consumer_proven=false})
    end
    local holder_targets, target_seen = {}, {}
    for _,candidate in pairs(index_candidates) do
        for _,spec in ipairs({
            {name='index_base',value=hex_number(candidate.index_base)},
            {name='blob_base',value=hex_number(candidate.blob_base)}
        }) do
            if spec.value and not target_seen[spec.name..':'..tostring(spec.value)] then
                target_seen[spec.name..':'..tostring(spec.value)]=true
                holder_targets[#holder_targets+1]={
                    name=spec.name,value=spec.value,bytes=pack64(spec.value),exclude={}
                }
            end
        end
    end
    if #holder_targets>0 then
        for _,candidate in ipairs(index_candidates) do
            local index_value=tonumber(candidate.index_base:sub(3),16)
            local blob_value=tonumber(candidate.blob_base:sub(3),16)
            for _,target in ipairs(holder_targets) do
                if target.value==index_value or target.value==blob_value then
                    target.exclude[index_value]=true
                    target.exclude[blob_value]=true
                end
            end
        end
        local holders=holder_probe(read,available,holder_targets,max_region_size)
        local target_regions={}
        for _,target in ipairs(holder_targets) do
            target_regions[target.name]=locate_region(available,target.value)
        end
        for _,hit in ipairs(holders.blob_base or {}) do
            local hit_address=hex_number(hit.address)
            if hit_address and hit_address>=0x100000000 and hit.context then
                local context={}
                for i=1,#hit.context,2 do
                    context[#context+1]=string.char(tonumber(hit.context:sub(i,i+1),16))
                end
                hit.linked_blocks={}
                for _,at in ipairs({56,64,72}) do
                    local value=u64(table.concat(context),at)
                    hit.linked_blocks[#hit.linked_blocks+1]={
                        offset=at-64,value=region_number(value),
                        region=ptr(value) and locate_region(available,value) or nil
                    }
                end
            end
        end
        local public_targets={}
        for _,target in ipairs(holder_targets) do
            public_targets[#public_targets+1]={
                name=target.name,
                value=region_number(target.value),
                bytes_hex=hex(target.bytes)
            }
        end
        emit('composition_holder_graph',{
            targets=public_targets,target_regions=target_regions,holders=holders,
            consumer_proven=false,ownership_proven=false,
            process_wide=false,read_only=true})
        local temporal={}
        for _,target in ipairs(holder_targets) do
            local rows=holders[target.name] or {}
            for _,hit in ipairs(rows) do
                local address=hex_number(hit.address)
                if address and address>=0x100000000 then
                    temporal[#temporal+1]={
                        name=target.name,address=hit.address,
                        region=hit.region
                    }
                end
            end
        end
        if #temporal>0 then
            local previous={}
            for sample=1,8 do
                local snapshot,changed={},false
                for _,target in ipairs(temporal) do
                    local address=hex_number(target.address)
                    local bytes=read(address,64)
                    local key=target.name..':'..target.address
                    if previous[key] and previous[key]~=bytes then changed=true end
                    previous[key]=bytes
                    snapshot[#snapshot+1]={
                        name=target.name,address=target.address,
                        bytes=hex(bytes),
                        qwords=read_qword_summary(bytes)
                    }
                end
                for _,target in ipairs(holder_targets) do
                    local bytes=read(target.value,64)
                    local key='target:'..target.name
                    if previous[key] and previous[key]~=bytes then changed=true end
                    previous[key]=bytes
                    snapshot[#snapshot+1]={
                        name='target_'..target.name,
                        address=region_number(target.value),
                        bytes=hex(bytes),
                        qwords=read_qword_summary(bytes)
                    }
                end
                emit('composition_holder_temporal',{
                    sample=sample,resumes_after_graph=(sample-1)*120,
                    entries=snapshot,read_only=true,
                    consumer_proven=false,changed_since_previous=changed
                })
                for _=1,120 do coroutine.yield() end
            end
        end
    end
    for _,candidate in pairs(index_candidates) do
        if candidate.matched_entries==411 then
            local roots={}
            for _,root in ipairs(verified_roots) do
                if root.layout and root.layout.indices[root.address] then
                    roots[tostring(root.item)]={
                        address=root.address,
                        index=root.layout.indices[root.address]
                    }
                end
            end
            M.result={
                index_base=hex_number(candidate.index_base),
                blob_base=hex_number(candidate.blob_base),
                matched_entries=candidate.matched_entries,
                roots=roots,
                read_only_probe=true,
                consumer_proven=false,
                ownership_proven=false
            }
            break
        end
    end
    emit('composition_scan', {game_dll_base=string.format('0x%016x',base or 0),
        attempted_bytes=scanned, queried_regions=queried,
        successful_bytes=successful_bytes,failed_bytes=failed_bytes,skipped_bytes=skipped_bytes,
        readable_regions=#available,scanned_regions=scanned_regions,
        bytes_by_type=bytes_by_type,raw_item_hits=raw_item_hits,
        header_anchor_hits=header_anchor_hits,hit_count=#hits,hits=hits,
        verified_root_count=#verified_roots,root_backrefs=root_refs,
        full_indices=index_candidates,
        complete_address_space=false,
        inventory_processed=scanned_regions==#available,
        consumer_proven=false,self_buffer_matches_possible=true})
    return hits
end
emit('probe_start', {version=M.version, read_only=true})
local previous = rawget(_G,'update')
if type(previous) == 'function' then
    local elapsed = 0
    local worker = coroutine.create(scan)
    local wrapper
    local function finish(status)
        M.status=status
        if rawget(_G,'update') == wrapper then rawset(_G,'update',previous) end
        file:close()
    end
    wrapper = function(dt,...)
        if type(dt)=='number' and dt >= 0 and dt < 10 then elapsed=elapsed+dt end
        if M.status == 'active' and elapsed >= 15 then
            local success, failure = coroutine.resume(worker)
            if not success then
                pcall(emit,'probe_error',{reason=tostring(failure)})
                finish('failed')
            elseif coroutine.status(worker) == 'dead' then
                finish('complete')
            elseif elapsed >= 720 then
                pcall(emit,'probe_timeout',{seconds=elapsed})
                finish('incomplete')
            end
        end
        return previous(dt,...)
    end
    M.status='active'
    rawset(_G,'update', wrapper)
else
    M.status='update_unavailable'; file:close()
end
return M
