-- Exercise the actual runtime helper without a game or native binding call.
local ffi=require('ffi')
local source=assert(io.open('HD2RuntimeArmorOverride/src/armor_registry_runtime.lua','rb'))
local text=source:read('*a');source:close()
local body=assert(text:match('(local function inspect_refresh_binding.-)\nif ARMOR_REFRESH_PROBE then'))
local factory=assert(loadstring('return function(ffi,kernel,read,u32,hex)\n'..body
    ..'\nreturn inspect_refresh_binding end'))()
local base,entry=0x140000000,4267568
local address=base+entry
local reads,calls,guard={},0,false
local memory={}
local function put32(s,off,n)
    local b={}
    for _=1,4 do b[#b+1]=string.char(n%256);n=math.floor(n/256) end
    return s:sub(1,off)..table.concat(b)..s:sub(off+5)
end
local function u32(s,off)
    if not s or off+4>#s then return nil end
    local a,b,c,d=s:byte(off+1,off+4);return a+b*256+c*65536+d*16777216
end
memory[base]=put32(string.rep('\0',64),0x3c,0x80)
local header='PE\0\0'..string.rep('\0',84)
header=put32(header,8,1790149348);header=put32(header,0x50,60719104)
memory[base+0x80]=header
memory[address]=string.rep('\0',2048)
local callee=base+2117584
local delta=(callee-(address+0x53))%4294967296
memory[address+0x4e]='\xe8'..put32(string.rep('\0',4),0,delta)
memory[callee]=string.rep('\xc3',2048)
local kernel={
    GetModuleHandleA=function(name)
        return ffi.cast('void *',name=='helldivers2.exe' and base or 0)
    end,
    VirtualQuery=function(_,buf)
        ffi.cast('uintptr_t *',buf)[0]=base
        ffi.cast('size_t *',buf+24)[0]=60719104
        ffi.cast('uint32_t *',buf+32)[0]=0x1000
        ffi.cast('uint32_t *',buf+36)[0]=guard and 0x120 or 0x20
        return 48
    end
}
local function read(at,size)
    reads[#reads+1]={at=at,size=size}
    return memory[at] and memory[at]:sub(1,size)
end
local saved=package.loaded['jit.util']
package.loaded['jit.util']={funcinfo=function()return {addr=address}end}
local inspect=factory(ffi,kernel,read,u32,function(v)return string.format('0x%x',v)end)
local function target()calls=calls+1;error('must never be invoked')end
local row=inspect(target,'Unit.is_child')
assert(row.state=='captured' and row.callee_state=='captured'
    and row.callee_rva==2117584 and row.byte_count==2048
    and #row.code_hex==4096 and row.code_hex==string.rep('00',2048))
assert(calls==0)
guard=true
assert(inspect(target,'Unit.is_child').state=='not_readable_executable_region')
guard=false
memory[base+0x80]=put32(header,8,1790149349)
assert(inspect(target,'Unit.is_child').callee_state==nil,'build gate ignored')
memory[base+0x80]=header
memory[address+0x4e]='\x90'..string.rep('\0',4)
assert(inspect(target,'Unit.is_child').callee_state=='reviewed_site_mismatch')
address=base-4096
assert(inspect(target,'Unit.is_child').state=='outside_allowed_modules')
-- The reviewed link wrapper is inspected only; no link/reparent operation.
address=base+0x3eb860
local link_callee=base+0x3361c0
memory[address]=string.rep('\0',2048)
memory[address+0xb8]='\xe8'..put32(string.rep('\0',4),0,(link_callee-address-0xbd)%4294967296)
memory[link_callee]=string.rep('\xc3',2048)
memory[link_callee+0x6e]='\xe9'..put32(string.rep('\0',4),0,
    (base+0x1af240-link_callee-0x73)%4294967296)
memory[base+0x1af240]=string.rep('\xc3',2048)
row=inspect(target,'World.link_unit')
assert(row.callee_state=='captured' and row.callee_rva==0x3361c0
    and row.callee_site_offset==0xb8 and row.called==false and calls==0)
assert(row.implementation_state=='captured' and row.implementation_rva==0x1af240)
memory[link_callee+0x6e]='\xe9'..put32(string.rep('\0',4),0,0)
assert(inspect(target,'World.link_unit').implementation_state=='reviewed_destination_mismatch')
memory[link_callee+0x6e]='\x90'..string.rep('\0',4)
assert(inspect(target,'World.link_unit').implementation_state=='reviewed_tail_mismatch')
assert(inspect(target,'World.unlink_unit').callee_state==nil,'binding name gate ignored')
memory[base+0x80]=put32(header,8,1790149349)
assert(inspect(target,'World.link_unit').callee_state==nil,'link build gate ignored')
memory[base+0x80]=header
memory[address+0xb8]='\x90'..string.rep('\0',4)
assert(inspect(target,'World.link_unit').callee_state=='reviewed_site_mismatch')
-- Replay the reviewed World resolver using synthetic tables, never native calls.
address=base+4224528
local resolver,slot=base+0x9d8c0,base+0x2800000
memory[address]=string.rep('\0',2048)
memory[address+0x26]='\xe8'..put32(string.rep('\0',4),0,(resolver-address-0x2b)%4294967296)
memory[resolver]=string.rep('\0',2048)
memory[resolver+0xf]='\x48\x8b\x35'..put32(string.rep('\0',4),0,slot-resolver-0x16)
local function qword(n)
    return put32(put32(string.rep('\0',8),0,n%4294967296),4,math.floor(n/4294967296))
end
local manager,objects,generations,object,vt=0x200000000,0x200001000,0x200002000,0x200003000,base+0x2000000
local method=base+0x210000
memory[slot]=qword(manager)
memory[manager+0x98]=put32(string.rep('\0',4),0,10)
memory[manager+0x88]=qword(objects);memory[manager+0xa0]=qword(generations)
memory[generations+3]=string.char(2);memory[objects+3*8]=qword(object)
memory[object]=qword(vt);memory[vt+0x1e8]=qword(method)
memory[object+0x18]=qword(0x200004000);memory[method]=string.rep('\xc3',2048)
local handle=(2*0x400000+3)*4+1
local native=inspect(target,'Unit.world',handle)
assert(native.state=='resolved_candidate' and native.handle_index==3
    and native.handle_generation==2 and native.method_state=='captured'
    and native.method_rva==0x210000 and native.relationship_proven==false and native.called==false)
assert(inspect(target,'Unit.world',{}).state=='unsupported_unit_handle')
assert(inspect(target,'Unit.world',0x100000000).state=='unsupported_unit_handle')
memory[generations+3]=string.char(3)
assert(inspect(target,'Unit.world',handle).state=='generation_mismatch')
memory[generations+3]=string.char(2)
local slot_reads=0
local racing=factory(ffi,kernel,function(at,size)
    if at==slot then slot_reads=slot_reads+1;if slot_reads==2 then return qword(manager+16) end end
    return read(at,size)
end,u32,function(v)return string.format('0x%x',v)end)
assert(racing(target,'Unit.world',handle).state=='unstable_handle_table')
method=base+0x2beb90;memory[vt+0x1e8]=qword(method)
local code=string.rep('\0',2048)
code=code:sub(1,0xa)..'\x48\x8d\xb9\xd8\x01\x00\x00'..code:sub(0x12)
code=code:sub(1,0x65)..'\x48\x8b\x3f\x8b\x13\x48\x81\xc7\xe0\x01\x00\x00'..code:sub(0x72)
memory[method]=code
local child=0x200005000
memory[object+0x1d8]=qword(child);memory[child]=qword(vt)
memory[child+0x18]=memory[object+0x18];memory[child+0x1e0]=qword(0)
native=inspect(target,'Unit.world',handle)
assert(native.association_state=='complete' and native.association_count==1
    and native.association_objects==string.format('0x%x',child) and native.association_atomic==false)
local resolved=inspect(target,'Unit.world',handle,{},true)
assert(resolved.state=='resolved_candidate' and resolved.encoded_handle==handle
    and resolved.association_objects==nil and resolved.method_code_hex==nil)
memory[child+0x1e0]=qword(child)
assert(inspect(target,'Unit.world',handle).association_state=='cycle')
memory[child+0x1e0]=qword(0)
local links_read=0
local unstable_links=factory(ffi,kernel,function(at,size)
    if at==object+0x1d8 then
        links_read=links_read+1;if links_read==2 then return qword(0) end
    end
    return read(at,size)
end,u32,function(v)return string.format('0x%x',v)end)
assert(unstable_links(target,'Unit.world',handle).association_state=='unstable')
memory[object+0x1d8]=qword(0)
assert(inspect(target,'Unit.world',handle).association_count==0)
memory[resolver+0xf]=string.rep('\0',7)
assert(inspect(target,'Unit.world',handle).state=='resolver_instruction_mismatch')
package.loaded['jit.util']={funcinfo=function()return {}end}
assert(inspect(target,'Unit.is_child').state=='c_address_unavailable')
package.loaded['jit.util']=saved
assert(calls==0)
print('PASS: actual binding inspector, module/guard/build gates, exact hex and no target calls')
