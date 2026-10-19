-- 离线验证 armor_section_locate.lua：用"假内存"（若干段字节 + 基址）冒充进程内存。
local L=dofile('E:/Data/source/repos/WpfApp1/HD2RuntimeArmorOverride/src/armor_section_layout.lua')
local LOC=dofile('E:/Data/source/repos/WpfApp1/HD2RuntimeArmorOverride/src/armor_section_locate.lua')

local failures,checks=0,0
local function ok(condition,label)
    checks=checks+1
    if not condition then failures=failures+1;print('FAIL '..label) end
end

local function load_bytes(path)
    local handle=assert(io.open(path,'rb'))
    local data=handle:read('*a')
    handle:close()
    return data
end

local UNIT='af431352d9b95e83'
local TOC=load_bytes('E:/Data/source/repos/WpfApp1/tmp/deployed-unit.toc')

-- 假内存：区域列表 + 按地址切片的 read
local function make_memory(regions)
    -- 真实 regions() 会给出 size；假内存按字节长度补上，避免"忘了填 size 就整段跳过"。
    for _,region in ipairs(regions) do
        if not region.size then region.size=#region.bytes end
    end
    local reads=0
    local function read(address,size)
        for _,region in ipairs(regions) do
            local offset=address-region.base
            if offset>=0 and offset+size<=#region.bytes then
                reads=reads+1
                return region.bytes:sub(offset+1,offset+size)
            end
        end
        return nil
    end
    local function iterator()
        local index=0
        return function()
            index=index+1
            return regions[index]
        end
    end
    return read,iterator,function() return reads end
end

-- ① 特征串：FileId 小端 + 8 个零
local pattern=LOC.pattern(UNIT)
ok(#pattern==16,'pattern length 16')
ok(pattern==TOC:sub(1,16),'pattern equals the TOC prefix')

-- ② 正常定位
local regions={
    {base=0x10000000,bytes=string.rep('\170',0x2000)},
    {base=0x20000000,bytes=TOC},
    {base=0x30000000,bytes=string.rep('\0',0x2000)},
}
local read,iter,read_count=make_memory(regions)
local locator=LOC.new(read,iter,L)
local base,layout=locator.locate(UNIT)
ok(base==0x20000000,'locate returns the TOC base, got '..tostring(base))
ok(layout and layout.mesh_count==5,'layout parsed, meshes='..tostring(layout and layout.mesh_count))

-- ③ 缓存：第二次不再扫（读取次数不增加）
local before=read_count()
local again=locator.locate(UNIT)
ok(again==base,'cached locate returns the same base')
ok(read_count()==before+1,'cache hit costs one verification read, got '..(read_count()-before))

-- ④ invalidate 之后必须重扫
locator.invalidate(UNIT)
local rescanned=locator.locate(UNIT)
ok(rescanned==base,'after invalidate the same base is found again')
ok(read_count()>before+2,'invalidate forces a rescan')

-- ⑤ 不存在的 unit
local missing=locator.locate('0123456789abcdef')
ok(missing==nil,'unknown unit is not found')

-- ⑥ 损坏的副本必须被结构校验挡掉，并继续找到后面那份好的
local broken=TOC:sub(1,0x64)..string.char(0,0,0,0)..TOC:sub(0x65)
local two={
    {base=0x40000000,bytes=broken},
    {base=0x50000000,bytes=TOC},
}
local read2,iter2=make_memory(two)
local locator2=LOC.new(read2,iter2,L)
local base2,layout2=locator2.locate(UNIT)
ok(base2==0x50000000,'broken copy is rejected, good copy found at 0x50000000, got '..tostring(base2))
ok(layout2 and layout2.mesh_count==5,'good copy parses')

-- ⑦ 只有坏副本时返回 not_found（不能拿坏地址去写）
local read3,iter3=make_memory({{base=0x40000000,bytes=broken}})
local locator3=LOC.new(read3,iter3,L)
local base3,reason3=locator3.locate(UNIT)
ok(base3==nil,'only a broken copy yields nil')
ok(reason3=='not_found','reason is not_found, got '..tostring(reason3))

-- ⑧ 超过单区域上限的区域被跳过（避免一次读几 GB）
local huge={{base=0x60000000,bytes=string.rep('\0',0x2000)}}
huge[1].size=0x20000000
local read4,iter4=make_memory(huge)
local locator4=LOC.new(read4,iter4,L)
local base4=locator4.locate(UNIT)
ok(base4==nil,'oversized region is skipped')

print(string.format('checks=%d failures=%d',checks,failures))
if failures>0 then os.exit(1) end
print('LOCATE_OK')
