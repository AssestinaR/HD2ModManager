local C=assert(loadfile('HD2RuntimeArmorOverride/src/armor_refresh_component.lua'))()
local base,root,obj=0x180000000,0x200000000,0x210000000
local function p32(n)
    local s=''
    for _=1,4 do s=s..string.char(n%256);n=math.floor(n/256) end
    return s
end
local function p64(n)return p32(n%4294967296)..p32(math.floor(n/4294967296))end
local function put(s,o,v)return s:sub(1,o)..v..s:sub(o+#v+1)end
local function u32(s,o)
    local a,b,c,d=s:byte(o+1,o+4)
    return a and d and a+b*256+c*65536+d*16777216
end
local mem={}
mem[base]=put(string.rep('\0',64),0x3c,p32(0x80))
local pe=put('PE\0\0'..string.rep('\0',84),8,p32(1790161983))
mem[base+0x80]=put(pe,0x50,p32(74727424))
mem[base+0x3326e68]=p64(root)
mem[root+0x7380]=p32(1)
mem[root+0x7388]=p64(obj)..p32(0x124)..p32(0)
mem[obj]=put(string.rep('\0',0xc30),0x10,p64(0x220000000))
local now=10
local probe=C.new({base=function()return base end,clock=function()return now end,u32=u32,
    read=function(at,n)return mem[at] and mem[at]:sub(1,n)end})
local r=probe.tick('0x0000000220000000',{})
assert(r.state=='captured' and r.first_or_new_object and r.object_equalities:find('avatar',1,true))
now=10.02;r=probe.tick('',{});assert(not r.changed and r.record_u32_at18==nil)
mem[obj]=put(mem[obj],0x18,p32(42))
now=10.04;r=probe.tick('',{});assert(r.changed and r.changed_dwords=='18:0000002a')
mem[root+0x7380]=p32(257)
assert(probe.tick('',{}).state=='list_bound')
local bad=C.new({base=function()return base end,clock=function()return now end,u32=u32,read=function()end})
assert(bad.tick('',{}).state=='build_mismatch')
print('PASS: component fingerprint, exact equality, bounded reads, delta and list rejection')
