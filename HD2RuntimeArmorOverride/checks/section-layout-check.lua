-- 离线验证 armor_section_layout.lua：用文件读取冒充内存读取。
-- 输入是真实的 unit TOC 切片（tmp/deployed-unit.toc 的前 8 字节被填成 unit FileId，
-- 用来模拟内存副本与文件唯一的差异）。
local L=dofile('E:/Data/source/repos/WpfApp1/HD2RuntimeArmorOverride/src/armor_section_layout.lua')

local failures,checks=0,0
local function ok(condition,label)
    checks=checks+1
    if not condition then failures=failures+1;print('FAIL '..label) end
end

-- 把整个文件读进内存，然后按 (地址, 长度) 切子串——与 ReadProcessMemory 的语义一致。
local function file_reader(path)
    local handle=assert(io.open(path,'rb'))
    local data=handle:read('*a')
    handle:close()
    return function(address,size)
        if not address or address<0 or size<1 then return nil end
        if address+size>#data then return nil end
        return data:sub(address+1,address+size)
    end
end

local function check_unit(path,label,expect)
    local read=file_reader(path)
    local layout,reason=L.parse(read,0)
    ok(layout~=nil,label..' parse: '..tostring(reason))
    if not layout then return end
    ok(layout.mesh_count==expect.meshes,label..' meshes='..layout.mesh_count..' expected '..expect.meshes)
    local family=L.lod_family(layout)
    ok(#family==expect.meshes,label..' lod family size')
    local lods={}
    for _,mesh in ipairs(family) do lods[#lods+1]=mesh.lod end
    table.sort(lods)
    ok(table.concat(lods,',')==expect.lods,label..' lods='..table.concat(lods,','))
    for _,mesh in ipairs(family) do
        ok(mesh.num_sections==expect.sections,label..' lod '..mesh.lod..' nsec='..mesh.num_sections)
    end
    ok(#layout.materials==expect.materials,label..' materials='..#layout.materials)
    local ids=L.section_identities(layout)
    ok(ids~=nil,label..' identities')
    if ids then
        ok(#ids==expect.sections,label..' identity count='..#ids)
        ok(ids[1].fingerprint==expect.first_fingerprint,
            label..' s0 fp='..tostring(ids[1].fingerprint)..' expected '..expect.first_fingerprint)
    end
    local toggles=L.section_toggles(layout,0)
    ok(toggles and #toggles==expect.toggles,label..' toggles='..(toggles and #toggles or -1))
    return layout
end

-- ① 当前部署的包：与游戏内探针 PASS 的那份是同一个 unit
local layout=check_unit('E:/Data/source/repos/WpfApp1/tmp/deployed-unit.toc','deployed',{
    meshes=5,lods='0,1,2,3,4',sections=22,materials=110,
    first_fingerprint='a47787804c45e1a9-8852',toggles=5})

-- ② 文件里 +0 是 0，填过之后才等于 unit FileId —— 这正是定位器要用的特征
local read=file_reader('E:/Data/source/repos/WpfApp1/tmp/deployed-unit.toc')
ok(L.unit_id_at(read,0)=='af431352d9b95e83','unit id at +0')

-- ③ 所有 LOD 的节表数量一致时，同一 section 下标在每级 LOD 上都有落点
if layout then
    local toggles=L.section_toggles(layout,21)
    ok(toggles and #toggles==5,'last section toggles=5')
    if toggles then
        -- 各级 LOD 的落点必须互不相同（同一份几何在 5 级 LOD 里各有一份索引区间）。
        local seen={}
        local distinct=true
        for _,toggle in ipairs(toggles) do
            if seen[toggle.offset] then distinct=false end
            seen[toggle.offset]=true
        end
        ok(distinct,'toggle offsets distinct across LODs')
        -- 每个落点都必须真的能读到 24 字节记录（越界就说明偏移算错了）。
        local read_toc=file_reader('E:/Data/source/repos/WpfApp1/tmp/deployed-unit.toc')
        local in_bounds=true
        for _,toggle in ipairs(toggles) do
            if not read_toc(toggle.offset,24) then in_bounds=false end
        end
        ok(in_bounds,'every toggle record is readable')
        -- 同一级 LOD 内，节记录随下标线性推进 24 字节。
        local lod0=L.canonical_mesh(layout)
        local linear=true
        for index=2,#lod0.sections do
            if lod0.sections[index].offset~=lod0.sections[index-1].offset+24 then linear=false end
        end
        ok(linear,'section records advance by 24 bytes inside a mesh')
    end
    -- 越界的下标必须返回空表而不是报错
    local beyond=L.section_toggles(layout,999)
    ok(beyond and #beyond==0,'out-of-range section yields no toggles')
end

-- ④ 另一份真实 TOC（原版 unit，各级 LOD 的 section 数量不同）
local vanilla=file_reader('E:/tmp/vanilla-unit-test/unit_0x143a0d93d14d6587.toc')
local vanilla_layout,vanilla_reason=L.parse(vanilla,0)
ok(vanilla_layout~=nil,'vanilla parse: '..tostring(vanilla_reason))
if vanilla_layout then
    ok(vanilla_layout.mesh_count==5,'vanilla meshes=5')
    local family=L.lod_family(vanilla_layout)
    ok(#family==4,'vanilla lod family=4 (one mesh is lod=-1)')
    ok(#vanilla_layout.materials==3,'vanilla materials=3')
    -- 材质槽是 0，查不到绑定，指纹必须退化成 none-
    local ids=L.section_identities(vanilla_layout)
    ok(ids and ids[1].fingerprint~=nil,'vanilla fingerprint present')
end

-- ⑤ 结构不对时返回 nil + 原因，不能抛异常
local bogus=function() return nil end
local bad,why=L.parse(bogus,0x1000)
ok(bad==nil and why=='header_unreadable','unreadable header -> nil,reason')
local zeros=function(address,size) return string.rep('\0',size) end
local bad2,why2=L.parse(zeros,0x1000)
ok(bad2==nil and why2=='mesh_info_offset_invalid','zero mesh info offset rejected')

print(string.format('checks=%d failures=%d',checks,failures))
if failures>0 then os.exit(1) end
print('LAYOUT_OK')
