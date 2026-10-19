-- wardrobe.masks 文法一致性检查：与 C# 侧 WardrobeMaskFileTests 用同一组用例。
-- 两边都通过 => 两种语言对同一份规格的理解一致。
local M=dofile('E:/Data/source/repos/WpfApp1/HD2RuntimeArmorOverride/src/armor_wardrobe_masks.lua')

local SLIM='cae35ee15168145f'
local STOCKY='9841b16544c8fbae'
local FACE='aa6b1bd624443008-9290'
local RIBBON='aadf05da56ef08cb-822'

local failures,checks=0,0
local function ok(condition,label)
    checks=checks+1
    if not condition then
        failures=failures+1
        print('FAIL '..label)
    end
end
local function fails(fn,label)
    checks=checks+1
    local okay,err=pcall(fn)
    if okay then
        failures=failures+1
        print('FAIL (expected error) '..label)
    else
        print('  error as expected: '..label)
    end
end
local function doc(...)
    return table.concat({...},'\n')..'\n'
end

-- 1. 正常文档
local d=M.parse(doc('schema=2','revision=7','enabled=true','# 注释',
    'off=1,'..SLIM..',0,'..FACE,
    'off=1,'..STOCKY..',0,'..FACE,
    'target=1234,1'))
ok(d.revision==7,'revision')
ok(d.enabled==true,'enabled')
ok(d.entry_count==2,'entry_count')
ok(d.target_count==1,'target_count')
ok(M.set_of(d,1234)==1,'target set')

-- 2. 拒绝项
fails(function() M.parse(doc('schema=2','revision=1','enabled=true','off_target=1,'..SLIM..',0,'..FACE)) end,'unknown key')
fails(function() M.parse(doc('schema=2','revision=1','enabled=true',
    'off=1,'..SLIM..',0,'..FACE,'off=1,'..SLIM..',0,'..FACE)) end,'duplicate entry')
fails(function() M.parse(doc('schema=2','revision=1','enabled=true','target=9,1','target=9,2')) end,'duplicate target')
fails(function() M.parse(doc('schema=2','revision=1','enabled=true','off=1,'..SLIM..',0,aa6b1bd624443008')) end,'fingerprint without triangles')
fails(function() M.parse(doc('schema=2','revision=1','enabled=true','off=1,'..SLIM..',0,aa6b-9290-extra')) end,'fingerprint extra field')
fails(function() M.parse(doc('revision=1','enabled=true')) end,'missing schema')
fails(function() M.parse(doc('schema=2','enabled=true')) end,'missing revision')
fails(function() M.parse(doc('schema=2','revision=1')) end,'missing enabled')
fails(function() M.parse(doc('schema=1','revision=1','enabled=true')) end,'schema 1 rejected')
fails(function() M.parse(doc('schema=2','revision=1','enabled=true','off=1,'..SLIM..',65536,'..FACE)) end,'section index out of range')

-- 3. unit 归一
local n=M.parse(doc('schema=2','revision=1','enabled=true','off=1,0xCAE35EE15168145F,0,AA6B1BD624443008-9290','target=1,1'))
ok(n.entry_count==1,'normalized unit accepted')
ok(M.lookup(n,1)(SLIM,0,FACE)==true,'normalized key matches')

-- 4. 没有关联掩码集 => 什么都不隐藏
local none=M.parse(doc('schema=2','revision=1','enabled=true','off=1,'..SLIM..',0,'..FACE))
ok(M.set_of(none,1234)==nil,'no set')
ok(M.lookup(none,1234)(SLIM,0,FACE)==false,'unmapped target hides nothing')

-- 5. 空集：显式例外但一条都不关
local empty=M.parse(doc('schema=2','revision=1','enabled=true','off=1,'..SLIM..',0,'..FACE,'target=1234,2'))
ok(M.set_of(empty,1234)==2,'explicit set 2')
ok(M.lookup(empty,1234)(SLIM,0,FACE)==false,'empty set hides nothing')

-- 6. 两个目标跟不同来源
local two=M.parse(doc('schema=2','revision=1','enabled=true',
    'off=1,'..SLIM..',0,'..FACE,'off=2,'..SLIM..',3,'..RIBBON,
    'target=7,1','target=9,2'))
ok(M.lookup(two,7)(SLIM,0,FACE)==true,'target 7 set 1 face')
ok(M.lookup(two,7)(SLIM,3,RIBBON)==false,'target 7 set 1 ribbon')
ok(M.lookup(two,9)(SLIM,3,RIBBON)==true,'target 9 set 2 ribbon')
ok(M.lookup(two,9)(SLIM,0,FACE)==false,'target 9 set 2 face')

-- 7. 指纹不符 => 可见
local fp=M.parse(doc('schema=2','revision=1','enabled=true','off=1,'..SLIM..',0,'..FACE,'target=1234,1'))
ok(M.lookup(fp,1234)(SLIM,0,RIBBON)==false,'fingerprint mismatch visible')
ok(M.lookup(fp,1234)(SLIM,1,FACE)==false,'section index mismatch visible')

-- 8. enabled=false
local off=M.parse(doc('schema=2','revision=1','enabled=false','off=1,'..SLIM..',0,'..FACE,'target=1234,1'))
ok(off.entry_count==1,'disabled keeps content')
ok(M.lookup(off,1234)(SLIM,0,FACE)==false,'disabled hides nothing')

-- 9. 整体禁用判定
local full=M.parse(doc('schema=2','revision=1','enabled=true',
    'off=1,'..SLIM..',0,'..FACE,'off=1,'..SLIM..',1,'..RIBBON,'target=1234,1'))
ok(M.unit_fully_disabled(full,1234,SLIM,{{index=0,fingerprint=FACE},{index=1,fingerprint=RIBBON}})==true,'unit fully disabled')
ok(M.unit_fully_disabled(full,1234,SLIM,{})==false,'empty list is not fully disabled')
ok(M.unit_fully_disabled(full,1234,SLIM,{{index=0,fingerprint=FACE},{index=2,fingerprint='a3545af6a33991cc-27525'}})==false,'partial is not fully disabled')
ok(M.unit_fully_disabled(full,1234,SLIM,{{index=0,fingerprint=RIBBON}})==false,'fingerprint mismatch is not fully disabled')

-- 10. 指纹拼接
ok(M.fingerprint('AA6B1BD624443008',9290)=='aa6b1bd624443008-9290','fingerprint format')

print(string.format('checks=%d failures=%d',checks,failures))
if failures>0 then os.exit(1) end
print('LUA_OK')
