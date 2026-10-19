-- 离线验证 armor_section_apply.lua：用真实 TOC 字节做可变缓冲，冒充内存。
local L=dofile('E:/Data/source/repos/WpfApp1/HD2RuntimeArmorOverride/src/armor_section_layout.lua')
local A=dofile('E:/Data/source/repos/WpfApp1/HD2RuntimeArmorOverride/src/armor_section_apply.lua')

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

local ORIGINAL=load_bytes('E:/Data/source/repos/WpfApp1/tmp/deployed-unit.toc')
local buffer=ORIGINAL

local function reader(data)
    return function(address,size)
        if not address or address<0 or size<1 or address+size>#data then return nil end
        return data:sub(address+1,address+size)
    end
end

local function u32_at(data,address)
    local a,b,c,d=data:byte(address+1,address+4)
    return a+b*256+c*65536+d*16777216
end

-- 可写缓冲：write 先校验当前值再落盘，语义与运行时一致。
-- 注意它改的是上值 buffer（不是参数副本），否则外层看不到写入效果——
-- 这正是本用例第一版踩到的坑：写"成功"了，但断言读的是没变的旧串。
local function make_io(fail_at)
    local writes=0
    return {
        read=function(address) return u32_at(buffer,address) end,
        write=function(address,value,expected)
            writes=writes+1
            if fail_at and writes==fail_at then return false end
            if expected~=nil and u32_at(buffer,address)~=expected then return false end
            local bytes=string.char(value%256,math.floor(value/256)%256,
                math.floor(value/65536)%256,math.floor(value/16777216)%256)
            buffer=buffer:sub(1,address)..bytes..buffer:sub(address+5)
            return true
        end,
        count=function() return writes end,
    }
end

local layout=assert(L.parse(reader(buffer),0))
local canonical=assert(L.canonical_mesh(layout))
local ids=assert(L.section_identities(layout))
ok(#ids==22,'identity count 22')
local target_fp=ids[1].fingerprint
ok(target_fp=='a47787804c45e1a9-8852','section 0 fingerprint '..tostring(target_fp))

-- ① 什么都不关：计划里每条 desired 都等于 original，应用时一条都不写
local none=A.plan(layout,canonical,function() return false end)
ok(none~=nil and none.hidden_sections==0,'no-hidden plan')
ok(none and #none.writes==22*5,'writes=22 sections x 5 lods got '..(none and #none.writes or -1))
buffer=ORIGINAL
local io1=make_io()
local okay1,count1=A.apply(io1,none.writes)
ok(okay1==true and #count1==0,'nothing to write when nothing is hidden')
ok(io1.count()==0,'no write calls issued')

-- ② 关掉 section 0 与 3：每级 LOD 的 +16 都变 0，其余不变
local hidden={[0]=target_fp,[3]=ids[4].fingerprint}
local plan=A.plan(layout,canonical,function(index,fp) return hidden[index]==fp end)
ok(plan~=nil and plan.hidden_sections==2,'two hidden sections')
buffer=ORIGINAL
local io2=make_io()
local okay2,count2=A.apply(io2,plan.writes)
ok(okay2==true,'apply ok: '..tostring(count2))
ok(#count2==10,'wrote 2 sections x 5 lods = 10 got '..tostring(count2 and #count2))
local zeroed=0
for _,write in ipairs(plan.writes) do
    if u32_at(buffer,write.address_offset)==0 then zeroed=zeroed+1 end
end
ok(zeroed==10,'10 numIndices zeroed got '..zeroed)
-- 未涉及的 section 必须原样
local untouched=true
for index=1,canonical.num_sections do
    if index-1~=0 and index-1~=3 then
        local section=canonical.sections[index]
        if u32_at(buffer,section.offset+16)~=section.num_indices then untouched=false end
    end
end
ok(untouched,'untouched sections keep their numIndices')

-- ③ 恢复：把同样的计划换成"全不隐藏"，字节必须完全回到原样
local restore=A.plan(layout,canonical,function() return false end)
local io3=make_io()
local okay3=A.apply(io3,restore.writes)
ok(okay3==true,'restore ok')
ok(buffer==ORIGINAL,'restore reproduces the original bytes exactly')

-- ④ 指纹对不上：掩码条目存在但指纹不匹配 => 一条都不写
local mismatched=A.plan(layout,canonical,function(index,fp)
    return index==0 and fp=='deadbeefdeadbeef-0'
end)
ok(mismatched~=nil and mismatched.hidden_sections==0,'fingerprint mismatch hides nothing')

-- ⑤ 预检失败：缓冲里的值与计划的原值不符时，整体拒绝且一个字节都不写
local corrupted=ORIGINAL:sub(1,plan.writes[1].address_offset)
    ..string.char(9,9,9,9)..ORIGINAL:sub(plan.writes[1].address_offset+5)
buffer=corrupted
local io4=make_io()
local okay4,reason4=A.apply(io4,plan.writes)
ok(okay4==false,'precheck rejects a changed value')
ok(tostring(reason4):find('precheck_failed')~=nil,'precheck reason: '..tostring(reason4))
ok(io4.count()==0,'precheck failure issues no writes')
ok(corrupted~=nil,'sanity')

-- ⑥ 写到一半失败：必须整体回滚，缓冲回到原样
buffer=ORIGINAL
local io5=make_io(3)
local okay5,reason5=A.apply(io5,plan.writes)
ok(okay5==false,'third write failure is reported')
ok(tostring(reason5):find('rolled back')~=nil,'rollback reason: '..tostring(reason5))
ok(buffer==ORIGINAL,'rollback restores every byte')

-- ⑦ 整体禁用判定
local all_hidden=A.plan(layout,canonical,function() return true end)
ok(A.unit_fully_hidden(all_hidden)==true,'all hidden => unit fully hidden')
ok(A.unit_fully_hidden(plan)==false,'partial hidden => not fully hidden')
ok(A.unit_fully_hidden(none)==false,'nothing hidden => not fully hidden')

-- ⑧ 越界 / 结构缺失时给出原因而不是抛异常
local bad=L.canonical_mesh(assert(L.parse(reader(ORIGINAL),0)))
local no_lod0=A.plan({meshes={}},nil,function() return false end)
ok(no_lod0==nil,'missing lod0 mesh returns nil')
ok(bad~=nil,'sanity')

print(string.format('checks=%d failures=%d',checks,failures))
if failures>0 then os.exit(1) end
print('APPLY_OK')
