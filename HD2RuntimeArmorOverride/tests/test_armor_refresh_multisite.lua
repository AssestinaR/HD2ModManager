local P=assert(loadfile('HD2RuntimeArmorOverride/src/armor_refresh_multisite.lua'))()
local b,root,avatar,obj=0x180000000,0x200000000,0x210000000,0x220000000
local function p32(n)local s='' for _=1,4 do s=s..string.char(n%256);n=math.floor(n/256) end return s end
local function p64(n)return p32(n%4294967296)..p32(math.floor(n/4294967296))end
local function put(s,o,v)return s:sub(1,o)..v..s:sub(o+#v+1)end
local function u32(s,o)local a,b,c,d=s:byte(o+1,o+4);return a and d and a+b*256+c*65536+d*16777216 end
local mem={}
mem[b]=put(string.rep('\0',64),0x3c,p32(0x80))
mem[b+0x80]=put(put('PE\0\0'..string.rep('\0',84),8,p32(1790161983)),0x50,p32(74727424))
mem[b+0x3326e68]=p64(root)
mem[root]=string.rep('\0',256);mem[avatar]=string.rep('\0',0x1e8)
mem[obj]=put(string.rep('\0',256),8,p64(avatar))
for _,at in ipairs({0x6240,0x65b8,0x7380}) do mem[root+at]=p32(0);mem[root+at+8]='' end
mem[root+0x6240]=p32(1);mem[root+0x6248]=p64(obj)..p32(0xdf)..p32(0)
local now=1
local probe=P.new({base=function()return b end,clock=function()return now end,u32=u32,
    read=function(at,n)return mem[at] and mem[at]:sub(1,n)end})
local function run()return probe.tick('0x0000000210000000',{}, {encoded_handle=17})end
local original_tonumber=tonumber
tonumber=function(v,radix)
    local n=original_tonumber(v,radix)
    if radix==16 and n and n>0xffffffff then return 0xffffffff end
    return n
end
local a=run();assert(a.state=='captured')
tonumber=original_tonumber
local found=false
for _,r in ipairs(a.rows) do
    if r.site=='avatar' then assert(r.state=='captured' and r.object=='0x0000000210000000') end
    if r.site=='list_6240_kind_df_0' then assert(r.value_equalities=='8:0x0000000210000000');found=true end
end
assert(found)
mem[obj]=put(mem[obj],0x20,p32(17));now=1.02
local c=run();found=false
for _,r in ipairs(c.rows) do if r.site=='list_6240_kind_df_0' then
    assert(r.changed and r.changed_dwords=='20:00000011'
        and r.value_equalities:find('encoded_avatar',1,true));found=true end end
assert(found)
mem[root+0x6240]=p32(65);now=1.04
local bad=run();found=false
for _,r in ipairs(bad.rows) do if r.site=='directory_6240' then assert(r.state=='unavailable_or_unstable');found=true end end
assert(found)
print('PASS: multisite bounds, separate candidates, scalar equality and delta; no pointer recursion')
