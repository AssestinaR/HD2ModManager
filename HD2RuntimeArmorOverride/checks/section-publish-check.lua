-- 离线端到端：定位 → 计划 → 应用，用假内存跑多目标/冲突/缺失。
local L=dofile('E:/Data/source/repos/WpfApp1/HD2RuntimeArmorOverride/src/armor_section_layout.lua')
local A=dofile('E:/Data/source/repos/WpfApp1/HD2RuntimeArmorOverride/src/armor_section_apply.lua')
local LOC=dofile('E:/Data/source/repos/WpfApp1/HD2RuntimeArmorOverride/src/armor_section_locate.lua')
local M=dofile('E:/Data/source/repos/WpfApp1/HD2RuntimeArmorOverride/src/armor_wardrobe_masks.lua')
local P=dofile('E:/Data/source/repos/WpfApp1/HD2RuntimeArmorOverride/src/armor_section_publish.lua')

local failures,checks=0,0
local function ok(condition,label)
    checks=checks+1
    if not condition then failures=failures+1;print('FAIL '..label) end
end

local handle=assert(io.open('E:/Data/source/repos/WpfApp1/tmp/deployed-unit.toc','rb'))
local TOC=handle:read('*a');handle:close()
local UNIT_A='af431352d9b95e83'
local UNIT_B='1122334455667788'

-- 第二份：同样的结构，换一个 FileId
local function with_unit(data,unit_id)
    local bytes={}
    for index=15,1,-2 do bytes[#bytes+1]=string.char(tonumber(unit_id:sub(index,index+1),16)) end
    return table.concat(bytes)..data:sub(9)
end

local BASE_A=0x20000000
local BASE_B=0x30000000
local regions

local function reset()
    regions={
        {base=BASE_A,bytes=with_unit(TOC,UNIT_A)},
        {base=BASE_B,bytes=with_unit(TOC,UNIT_B)},
    }
    for _,region in ipairs(regions) do region.size=#region.bytes end
end
reset()

local function read(address,size)
    for _,region in ipairs(regions) do
        local offset=address-region.base
        if offset>=0 and offset+size<=#region.bytes then
            return region.bytes:sub(offset+1,offset+size)
        end
    end
    return nil
end

local function write(address,value,expected)
    for _,region in ipairs(regions) do
        local offset=address-region.base
        if offset>=0 and offset+4<=#region.bytes then
            local a,b,c,d=region.bytes:byte(offset+1,offset+4)
            local current=a+b*256+c*65536+d*16777216
            if expected~=nil and current~=expected then return false end
            local bytes=string.char(value%256,math.floor(value/256)%256,
                math.floor(value/65536)%256,math.floor(value/16777216)%256)
            region.bytes=region.bytes:sub(1,offset)..bytes..region.bytes:sub(offset+5)
            return true
        end
    end
    return false
end

local function regions_iter()
    local index=0
    return function()
        index=index+1
        return regions[index]
    end
end

local function u32_at(region,address)
    local offset=address-region.base
    local a,b,c,d=region.bytes:byte(offset+1,offset+4)
    return a+b*256+c*65536+d*16777216
end

local function read_region_a(address,size)
    if not address or size<1 or address+size>#regions[1].bytes then return nil end
    return regions[1].bytes:sub(address+1,address+size)
end
local layout=assert(L.parse(read_region_a,0))
local canonical=assert(L.canonical_mesh(layout))
local ids=assert(L.section_identities(layout))
local fp0=ids[1].fingerprint
local fp1=ids[2].fingerprint
ok(fp0=='a648fd251c740b12-8852','section 0 fingerprint')

local function publisher()
    return P.new({read=read,write=write,regions=regions_iter,layout=L,apply=A,locate=LOC,masks=M})
end

local function mask_doc(lines)
    return M.parse(table.concat(lines,'\n')..'\n')
end

-- ① 单目标：关 section 0 和 1
reset()
local masks1=mask_doc({'schema=2','revision=1','enabled=true',
    'off=1,'..UNIT_A..',0,'..fp0,
    'off=1,'..UNIT_A..',1,'..fp1,
    'target=7,1'})
local pub=publisher()
local okay1,stats1=pub.apply_document(masks1,{[7]={UNIT_A}})
ok(okay1==true,'single target applies: '..tostring(stats1))
ok(stats1 and stats1.writes==10,'writes=10 got '..tostring(stats1 and stats1.writes))
ok(stats1 and stats1.hidden_sections==2,'hidden=2 got '..tostring(stats1 and stats1.hidden_sections))
ok(u32_at(regions[1],BASE_A+canonical.sections[1].offset+16)==0,'section 0 zeroed in memory')
ok(u32_at(regions[1],BASE_A+canonical.sections[3].offset+16)~=0,'section 2 untouched')

-- ② 两个目标指向同一 unit 且要求一致：合并不冲突
reset()
local pub2=publisher()
local masks2=mask_doc({'schema=2','revision=1','enabled=true',
    'off=1,'..UNIT_A..',0,'..fp0,
    'target=7,1','target=8,1'})
local okay2,stats2=pub2.apply_document(masks2,{[7]={UNIT_A},[8]={UNIT_A}})
ok(okay2==true,'two targets same set applies')
ok(stats2 and stats2.writes==5,'deduped to 5 writes got '..tostring(stats2 and stats2.writes))

-- ③ 两个目标对同一 section 要求不同：必须报冲突，且一个字节都不写
reset()
local pub3=publisher()
local masks3=mask_doc({'schema=2','revision=1','enabled=true',
    'off=1,'..UNIT_A..',0,'..fp0,
    'off=2,'..UNIT_A..',1,'..fp1,
    'target=7,1','target=8,2'})
local okay3,reason3=pub3.apply_document(masks3,{[7]={UNIT_A},[8]={UNIT_A}})
ok(okay3==nil,'conflict is reported: '..tostring(reason3))
ok(tostring(reason3):find('conflicting_targets')~=nil,'conflict reason: '..tostring(reason3))
ok(u32_at(regions[1],BASE_A+canonical.sections[1].offset+16)==canonical.sections[1].num_indices,
    'conflict writes nothing')

-- ④ 掩码总开关关闭：什么都不做
reset()
local pub4=publisher()
local masks4=mask_doc({'schema=2','revision=1','enabled=false',
    'off=1,'..UNIT_A..',0,'..fp0,'target=7,1'})
local okay4,stats4=pub4.apply_document(masks4,{[7]={UNIT_A}})
ok(okay4==true and stats4.writes==0,'disabled masks write nothing')

-- ⑤ unit 没加载：跳过并计数，其余目标照常生效
reset()
local pub5=publisher()
local masks5=mask_doc({'schema=2','revision=1','enabled=true',
    'off=1,'..UNIT_A..',0,'..fp0,
    'off=1,'..UNIT_B..',1,'..fp1,
    'target=7,1'})
local okay5,stats5=pub5.apply_document(masks5,{[7]={UNIT_A,'ffffffffffffffff'}})
ok(okay5==true,'missing unit does not abort')
ok(stats5 and stats5.missing==1,'missing=1 got '..tostring(stats5 and stats5.missing))
ok(stats5 and stats5.writes==5,'still applies the present unit')

-- ⑥ 两份 unit 各自生效，互不影响
reset()
local pub6=publisher()
local masks6=mask_doc({'schema=2','revision=1','enabled=true',
    'off=1,'..UNIT_A..',0,'..fp0,
    'off=1,'..UNIT_B..',2,'..ids[3].fingerprint,
    'target=7,1'})
local okay6,stats6=pub6.apply_document(masks6,{[7]={UNIT_A,UNIT_B}})
ok(okay6==true,'two units apply')
ok(stats6 and stats6.writes==10,'two units x 5 lods got '..tostring(stats6 and stats6.writes))
ok(u32_at(regions[1],BASE_A+canonical.sections[1].offset+16)==0,'unit A section 0 zeroed')
ok(u32_at(regions[2],BASE_B+canonical.sections[3].offset+16)==0,'unit B section 2 zeroed')
ok(u32_at(regions[2],BASE_B+canonical.sections[1].offset+16)~=0,'unit B section 0 untouched')

-- ⑦ 目标 → unit 列表的推导（纯函数）
local composition={unit=function(piece) return piece.u end}
local snapshot={rows={
    {body=0,pieces={{u=UNIT_B},{u=UNIT_A}}},
    {body=1,pieces={{u=UNIT_A},{u=''},{}}},
}}
local units=P.units_from_snapshot(snapshot,composition)
ok(#units==2,'units deduplicated: '..tostring(#units))
ok(units[1]==UNIT_B and units[2]==UNIT_A,'units sorted and empty dropped')
ok(#P.units_from_snapshot(nil,composition)==0,'nil snapshot yields no units')
ok(#P.units_from_snapshot({rows={{pieces={}}}},composition)==0,'empty rows yield no units')
print(string.format('checks=%d failures=%d',checks,failures))
if failures>0 then os.exit(1) end
print('PUBLISH_OK')
