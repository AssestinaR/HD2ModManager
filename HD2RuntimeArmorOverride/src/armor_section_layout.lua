-- unit TOC 的只读结构解析。给定一个 read(address,size) 与 TOC 基址，走出
-- 结构头 / mesh 表 / 节表 / 材质表。
--
-- 为什么运行时需要这个：掩码里只有语义身份 (unit, sectionIndex, fingerprint)，
-- 偏移属于构建产物。若运行时能自己走 TOC，就不必把 sections.json 部署到游戏读得到
-- 的地方。实测依据见 notes/section-toc-memory-header-differs-only-in-unit-id-20261019.md：
-- 内存副本与文件只在 +0 的 8 字节（unit FileId）不同，0x5c/0x64/0x70 三个结构偏移一致。
--
-- 偏移来源是权威读取器 HD2ModAdaptation/PatchReconstruction/UnitMesh/UnitMeshReader.cs：
--   toc+0x5c StreamInfo  toc+0x64 MeshInfo  toc+0x70 Materials
--   mesh+56 lod  mesh+104 numMaterials  mesh+120 numSections  mesh+124 sectionsOffset
--   mesh+132 materialSlotIds[]   节记录 24 字节：+0 matIdx +4 vOff +8 nVtx
--                                +12 iOff +16 nIndices +20 groupIndex
--   materials 表：u32 数量，随后 数量×u32 sectionId，随后 数量×u64 materialId
--
-- 本模块**不做任何内存访问**：read 由调用方注入，所以能离线用文件读取来测。
local M={}

local MAX_MESHES=64
local MAX_SECTIONS=4096
local MAX_MATERIALS=4096
local MAX_OFFSET=0x1000000

local function u32(data,at)
    if not data or at<0 or at+4>#data then return nil end
    local a,b,c,d=data:byte(at+1,at+4)
    return a+b*256+c*65536+d*16777216
end

local function i32(data,at)
    local value=u32(data,at)
    if value and value>=2147483648 then return value-4294967296 end
    return value
end

-- 8 字节读成 16 位 hex，按 FileId 的打印习惯（高字节在前）。
local function hex64(data,at)
    if not data or at<0 or at+8>#data then return nil end
    local out={}
    for index=8,1,-1 do out[#out+1]=string.format('%02x',data:byte(at+index)) end
    return table.concat(out)
end

function M.hex32(data,at)
    local value=u32(data,at)
    if not value then return nil end
    return string.format('%08x',value)
end

-- TOC 起点前 8 字节就是 unit 自己的 FileId（文件里是 0，内存里被填上了）。
-- 定位器拿它做最后一道确认，比"节记录签名"稳：后者与构建绑定，换包就失效。
function M.unit_id_at(read,base)
    return hex64(read(base,8),0)
end

function M.parse(read,base)
    assert(type(read)=='function','read function is required')
    -- base 允许为 0：离线测试时"内存"就是文件本身，基址取 0。
    assert(type(base)=='number' and base>=0,'toc base is required')

    local header=read(base,0x80)
    if not header or #header<0x80 then return nil,'header_unreadable' end
    local stream_info=u32(header,0x5c)
    local mesh_info=u32(header,0x64)
    local materials_offset=u32(header,0x70)
    if not mesh_info or mesh_info==0 or mesh_info>=MAX_OFFSET then
        return nil,'mesh_info_offset_invalid'
    end

    local count=u32(read(base+mesh_info,4),0)
    if not count or count<1 or count>MAX_MESHES then return nil,'mesh_count_invalid' end
    local table_data=read(base+mesh_info+4,count*4)
    if not table_data then return nil,'mesh_table_unreadable' end

    local meshes={}
    for index=0,count-1 do
        local relative=u32(table_data,index*4)
        if not relative or relative>=MAX_OFFSET then return nil,'mesh_offset_invalid' end
        local mesh_base=base+mesh_info+relative
        local record=read(mesh_base,132)
        if not record then return nil,'mesh_record_unreadable' end
        local num_materials=u32(record,104)
        local num_sections=u32(record,120)
        local sections_offset=u32(record,124)
        if not num_materials or num_materials>MAX_MATERIALS then return nil,'num_materials_invalid' end
        if not num_sections or num_sections>MAX_SECTIONS then return nil,'num_sections_invalid' end
        if not sections_offset or sections_offset>=MAX_OFFSET then return nil,'sections_offset_invalid' end

        local slots={}
        if num_materials>0 then
            -- 槽位数组从 +128 开始：ReadMesh 读完 sectionsOffset 后只 cursor+=4，
            -- 而 128 + numMaterials*4 == sectionsOffset（实测 128+88=216）。
            -- 之前写成 +132 会让整个数组偏一位，材质 ID 全错但两边一致时看不出。
            local slot_data=read(mesh_base+128,num_materials*4)
            if not slot_data then return nil,'material_slots_unreadable' end
            for slot_index=0,num_materials-1 do slots[slot_index+1]=u32(slot_data,slot_index*4) end
        end

        local sections={}
        if num_sections>0 then
            local section_data=read(mesh_base+sections_offset,num_sections*24)
            if not section_data then return nil,'section_table_unreadable' end
            for section_index=0,num_sections-1 do
                local at=section_index*24
                sections[section_index+1]={
                    offset=mesh_base+sections_offset+at,
                    material_index=u32(section_data,at),
                    vertex_offset=u32(section_data,at+4),
                    num_vertices=u32(section_data,at+8),
                    index_offset=u32(section_data,at+12),
                    num_indices=u32(section_data,at+16),
                    group_index=u32(section_data,at+20),
                }
            end
        end

        meshes[#meshes+1]={index=index,offset=relative,lod=i32(record,56),
            num_materials=num_materials,num_sections=num_sections,
            sections_offset=sections_offset,sections_at=mesh_base+sections_offset,
            material_slots=slots,sections=sections}
    end

    local materials={}
    local material_by_slot={}
    local material_count=materials_offset and u32(read(base+materials_offset,4),0) or 0
    if material_count and material_count>0 and material_count<=MAX_MATERIALS then
        local ids=read(base+materials_offset+4,material_count*4)
        local values=read(base+materials_offset+4+material_count*4,material_count*8)
        if ids and values then
            for index=0,material_count-1 do
                local section_id=u32(ids,index*4)
                local material_id=hex64(values,index*8)
                materials[#materials+1]={section_id=section_id,material_id=material_id}
                if section_id and not material_by_slot[section_id] then
                    material_by_slot[section_id]=material_id
                end
            end
        end
    end

    return {stream_info=stream_info,mesh_info=mesh_info,materials_offset=materials_offset,
        mesh_count=count,meshes=meshes,materials=materials,material_by_slot=material_by_slot}
end

-- 一个 unit 里承载装饰的那一族 mesh（lod>=0），按 lod 升序。
function M.lod_family(layout)
    local family={}
    for _,mesh in ipairs(layout.meshes) do
        if mesh.lod and mesh.lod>=0 then family[#family+1]=mesh end
    end
    table.sort(family,function(a,b) return a.lod<b.lod end)
    return family
end

-- 掩码里的 section 下标以 LOD0 为锚。取"section 最多"的那个 lod0 mesh 作为基准；
-- 一个 unit 有多级 LOD 但只有 0 级才可能有额外结构，这里不做假设。
function M.canonical_mesh(layout)
    local best
    for _,mesh in ipairs(layout.meshes) do
        if mesh.lod==0 then
            if not best or mesh.num_sections>best.num_sections then best=mesh end
        end
    end
    return best
end

function M.section_fingerprint(layout,mesh,section)
    local slot
    if section.material_index and mesh.material_slots[section.material_index+1] then
        slot=mesh.material_slots[section.material_index+1]
    end
    local material=slot and layout.material_by_slot[slot] or nil
    local triangles=math.floor((section.num_indices or 0)/3)
    if not material then return 'none-'..tostring(triangles) end
    return material..'-'..tostring(triangles)
end

-- 掩码用的 section 身份列表（基于基准 mesh）。
function M.section_identities(layout)
    local mesh=M.canonical_mesh(layout)
    if not mesh then return nil,'no_lod0_mesh' end
    local list={}
    for index,section in ipairs(mesh.sections) do
        list[#list+1]={index=index-1,fingerprint=M.section_fingerprint(layout,mesh,section)}
    end
    return list
end

-- 某个 section 下标在各级 LOD 上的写入位置。
-- 只收 numSections 与基准 mesh 相等的 LOD——数量不等说明节序不可对齐，
-- 强行按同一下标写会关错几何（原版 unit 就有 lod3 只有 1 个 section 的情况）。
function M.section_toggles(layout,section_index)
    local canonical=M.canonical_mesh(layout)
    if not canonical then return nil,'no_lod0_mesh' end
    local toggles={}
    for _,mesh in ipairs(M.lod_family(layout)) do
        if mesh.num_sections==canonical.num_sections then
            local section=mesh.sections[section_index+1]
            if section then
                toggles[#toggles+1]={lod=mesh.lod,mesh_index=mesh.index,
                    offset=section.offset,num_indices=section.num_indices}
            end
        end
    end
    return toggles
end

return M
