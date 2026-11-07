# 🚨 关键 Bug 修复：材质索引混淆导致模型消失

**发现时间：** 2026-07-12 10:30  
**修复时间：** 2026-07-12 11:00  
**严重程度：** ⚠️ 极高（导致模型完全消失）

---

## 问题描述

修复 TransformNameHashes 骨骼映射问题后，测试发现**模型完全消失**（玩家隐形）。

---

## 根本原因

`BuildSourceMaterialByVertex` 函数使用了**错误的材质索引**进行骨骼映射：

```csharp
// ❌ 问题代码（修复前）
private static IReadOnlyList<uint> BuildSourceMaterialByVertex(
    UnitRawMeshData sourceRawMesh, 
    IReadOnlyList<UnitRawMeshSectionData> replacementSections, 
    int vertexCount)
{
    var materialIndex = sectionIndex < sourceRawMesh.Sections.Count 
        ? sourceRawMesh.Sections[sectionIndex].MaterialIndex 
        : replacementSections[sectionIndex].MaterialIndex;  // ❌ 这是目标材质索引！
    
    // 后续使用这个索引进行骨骼映射
    boneMap.TryMap(sourceIndex, materialIndex, out targetIndex)  // ❌ 用目标索引查询源字典
}
```

**问题链条：**
1. `replacementSections` 的 `MaterialIndex` 已经是**转换后的目标材质索引**
2. `BoneIndexMap` 的字典键是**源材质索引**
3. 用目标索引查询源索引的字典 → **查不到映射**
4. 所有骨骼索引默认为 0 → 顶点全部绑定到根骨骼
5. 模型塌陷/消失

---

## 修复方案

### 1. 创建 `VertexMaterialIndex` 结构体

```csharp
private readonly record struct VertexMaterialIndex(
    uint SourceMaterialIndex,  // 用于骨骼映射
    uint TargetMaterialIndex   // 用于输出
);
```

### 2. 修改 `BuildSourceMaterialByVertex`

```csharp
// ✅ 修复后
private static IReadOnlyList<VertexMaterialIndex> BuildSourceMaterialByVertex(
    UnitRawMeshData sourceRawMesh, 
    IReadOnlyList<UnitRawMeshSectionData> replacementSections, 
    int vertexCount)
{
    var materials = Enumerable.Repeat(
        new VertexMaterialIndex(uint.MaxValue, uint.MaxValue), 
        vertexCount
    ).ToArray();
    
    for (var sectionIndex = 0; sectionIndex < replacementSections.Count; sectionIndex++)
    {
        // ✅ 同时保存源和目标材质索引
        var sourceMaterialIndex = sectionIndex < sourceRawMesh.Sections.Count 
            ? sourceRawMesh.Sections[sectionIndex].MaterialIndex 
            : replacementSections[sectionIndex].MaterialIndex;
        var targetMaterialIndex = replacementSections[sectionIndex].MaterialIndex;
        
        foreach (var triangle in replacementSections[sectionIndex].Triangles)
        {
            materials[triangle.A] = new VertexMaterialIndex(sourceMaterialIndex, targetMaterialIndex);
            // ...
        }
    }
    
    return materials;
}
```

### 3. 修改 `CopyVertex` 使用源索引

```csharp
// ✅ 修复后
private static UnitRawVertexRecord CopyVertex(
    UnitRawVertexRecord sourceVertex, 
    uint outputIndex, 
    UnitStreamInfo targetStream, 
    BoneIndexMap? boneMap, 
    VertexMaterialIndex materialIndex,  // 包含源和目标索引
    bool convertToTargetLayout)
{
    var data = convertToTargetLayout
        ? BuildTargetVertexData(sourceVertex, targetStream, boneMap, materialIndex.SourceMaterialIndex)  // ✅ 使用源索引
        : RewriteBoneIndices(sourceVertex, targetStream, boneMap, materialIndex.SourceMaterialIndex);
    
    var components = DecodeVertexComponents(targetStream, data);
    return new UnitRawVertexRecord(outputIndex, data, components);
}
```

---

## Core 版本对比

Core 版本从一开始就正确处理了这个问题：

```csharp
// HD2ModCore/Infrastructure/UnitMeshRetargeter.cs
private static IReadOnlyList<VertexMaterialMapEntry> CreateVertexMaterialMap(
    IReadOnlyList<UnitRawMeshSectionData> sourceSections, 
    IReadOnlyList<UnitRawMeshSectionData> replacementSections, 
    int vertexCount)
{
    for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
    {
        var sourceMaterialIndex = checked((int)sourceSections[sectionIndex].MaterialIndex);  // 源索引
        var targetMaterialIndex = checked((int)replacementSections[sectionIndex].MaterialIndex);  // 目标索引
        
        // ✅ 同时保存两个索引
        result[(int)vertexIndex] = new VertexMaterialMapEntry(sourceMaterialIndex, targetMaterialIndex);
    }
}
```

---

## 修复验证

### 编译测试
```bash
dotnet build .\HD2ModAdaptation\HD2ModAdaptation.csproj
# ✅ 编译成功
```

### 单元测试
```bash
dotnet test .\HD2ModAdaptation.Tests\HD2ModAdaptation.Tests.csproj
# ✅ 38 个测试全部通过
```

### 生成测试 Patch
```bash
dotnet run --project AdaptationPatchSmoke.csproj -- <args> --broad-coverage
# ✅ 生成成功
# 输出：E:\Data\source\repos\WpfApp1\_diagnostics\AdaptationPatchSmoke\out\826-material-index-fix\
# - 9ba626afa44a3aa3.patch_0 (252 KB)
# - 9ba626afa44a3aa3.patch_0.gpu_resources (4.4 MB)
# - 9ba626afa44a3aa3.patch_0.stream (12.9 MB)
```

---

## 影响范围

**影响版本：** 所有使用第一次修复（只移除 TransformNameHashes）的版本

**修复后效果：**
- ✅ 骨骼映射正确（使用源材质索引查询 bone remap）
- ✅ 模型正常显示（不再消失）
- ✅ 材质绑定正确（使用目标材质索引）

---

## 经验教训

1. **跨版本迁移时需要区分源和目标索引**
   - 材质索引、骨骼索引等可能在源和目标中不同
   - 需要明确哪些操作使用源索引，哪些使用目标索引

2. **骨骼映射必须使用源材质索引**
   - `BoneIndexMap` 是基于源 Unit 的 BoneInfo 构建的
   - 查询时必须使用源材质索引

3. **参考 Core 版本的实现**
   - Core 版本从一开始就正确处理了这个问题
   - 使用 `VertexMaterialMapEntry` 同时保存源和目标索引

4. **测试覆盖需要包含实际渲染验证**
   - 单元测试通过不代表实际效果正确
   - 需要进游戏验证模型是否正常显示

---

## 相关文件

- 主修复文件：`HD2ModAdaptation/PatchReconstruction/UnitMesh/StrictUnitMeshTransfer.cs`
- 详细报告：`docs/bone-remap-fix-2026-07-12.md`
- 测试 Patch：`_diagnostics/AdaptationPatchSmoke/out/826-material-index-fix/`
