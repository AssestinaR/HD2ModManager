# 🔧 第三次修复：目标 Section MaterialIndex 错误

**发现时间：** 2026-07-12 11:15  
**问题：** 修复材质索引后，胯部骨骼动画仍然不正常  
**根本原因：** `CreateBoneMap` 使用了错误的目标 MaterialIndex

---

## 问题分析

在第二次修复中，我们修复了顶点材质索引的问题（使用源索引查找 bone remap）。但还有另一个问题：

```csharp
// ❌ 第二次修复后的代码（仍有问题）
var pairs = sourceRawMesh.Sections.Zip(replacementSections, (sourceSection, replacementSection) =>
{
    var sourceRemap = FindBoneRemap(sourceBoneInfo, sourceSection.MaterialIndex);  // ✅ 源索引
    var targetRemap = FindBoneRemap(targetBoneInfo, replacementSection.MaterialIndex);  // ❌ 转换后的目标索引！
    ...
});
```

**问题：**
- `replacementSection.MaterialIndex` 是**转换后的目标材质索引**
- 但 `targetBoneInfo.Remaps` 的索引是基于**目标 mesh 的原始 section 索引**
- 用转换后的索引查询原始的 remap 数组 → 查到错误的 bone remap → 骨骼动画异常

---

## Core 版本对比

Core 版本正确处理了这个问题：

```csharp
// HD2ModCore/Infrastructure/UnitMeshRetargeter.cs
var pairs = sourceSections.Zip(replacementSections, (sourceSection, replacementSection) =>
{
    var sourceRemap = FindBoneRemap(sourceBoneInfo, sourceSection.MaterialIndex);
    var targetRemap = FindBoneRemap(targetBoneInfo, replacementSection.MaterialIndex);  // 这里也用了 replacementSection
    ...
});
```

**关键区别：** Core 版本的 `replacementSection.MaterialIndex` 保留了**目标 section 的原始 MaterialIndex**，而不是转换后的。

Core 版本的 `CopySectionIntoTargetSlot` 这样处理：
```csharp
var targetSection = sectionIndex < targetRawMesh.Sections.Count 
    ? targetRawMesh.Sections[sectionIndex]  // 获取目标 section
    : targetRawMesh.Sections.FirstOrDefault();

// 用 materialMap 转换材质，但保留了 targetSection 的 MaterialIndex 用于 bone remap
```

---

## 修复方案

在 `CreateBoneMap` 中，**通过 section 位置索引获取目标 section 的原始 MaterialIndex**：

```csharp
private static BoneIndexMap? CreateBoneMap(
    UnitMeshModel targetModel, 
    UnitRawMeshData targetRawMesh, 
    UnitMeshModel sourceModel, 
    UnitRawMeshData sourceRawMesh, 
    IReadOnlyList<UnitRawMeshSectionData> replacementSections)
{
    var sourceBoneInfo = FindBoneInfo(sourceModel, sourceRawMesh);
    var targetBoneInfo = FindBoneInfo(targetModel, targetRawMesh);
    if (sourceBoneInfo is null || targetBoneInfo is null)
    {
        return null;
    }

    var pairs = sourceRawMesh.Sections.Zip(replacementSections, (sourceSection, replacementSection) =>
    {
        var sourceRemap = FindBoneRemap(sourceBoneInfo, sourceSection.MaterialIndex);
        
        // ✅ 通过 section 位置获取目标 section 的原始 MaterialIndex
        var targetSectionIndex = sourceRawMesh.Sections.ToList().IndexOf(sourceSection);
        var targetSection = targetSectionIndex < targetRawMesh.Sections.Count 
            ? targetRawMesh.Sections[targetSectionIndex] 
            : targetRawMesh.Sections.FirstOrDefault();
        var targetRemap = targetSection is not null 
            ? FindBoneRemap(targetBoneInfo, targetSection.MaterialIndex)  // ✅ 使用原始索引
            : null;
            
        return sourceRemap is null || targetRemap is null
            ? null
            : new BoneRemapPair(sourceSection.MaterialIndex, sourceRemap, targetRemap);
    }).ToArray();

    return pairs.Length == 0 || pairs.Any(pair => pair is null)
        ? null
        : new BoneIndexMap(sourceBoneInfo, targetBoneInfo, pairs!);
}
```

**关键点：**
1. 获取 source section 在数组中的位置
2. 用相同位置获取 target section
3. 使用 target section 的**原始 MaterialIndex** 查找 bone remap

---

## 验证结果

**编译：** ✅ 成功  
**单元测试：** ✅ 38/38 通过  
**生成 Patch：** ✅ 成功

**输出：**
```
E:\Data\source\repos\WpfApp1\_diagnostics\AdaptationPatchSmoke\out\826-bone-remap-final-fix\
├── 9ba626afa44a3aa3.patch_0 (252 KB)
├── 9ba626afa44a3aa3.patch_0.gpu_resources (4.4 MB)
└── 9ba626afa44a3aa3.patch_0.stream (12.9 MB)
```

**统计：**
- 处理 32 个 Units
- 19 个完整转换
- 13 个仅最小化
- 0 个失败
- 所有骨骼映射正确 (`invalidUnique=0`)

---

## 三次修复总结

### 修复 #1: TransformNameHashes 跨版本不兼容
- **问题：** 旧 patch 的 hash 与新版游戏不匹配
- **修复：** 移除 hash 匹配，只使用 realIndex

### 修复 #2: 顶点材质索引混淆
- **问题：** 顶点使用目标材质索引查询源 bone remap
- **修复：** 创建 `VertexMaterialIndex` 分别保存源和目标索引

### 修复 #3: 目标 Section MaterialIndex 错误
- **问题：** 用转换后的目标材质索引查询原始 bone remap 数组
- **修复：** 通过 section 位置获取目标 section 的原始 MaterialIndex

---

## 测试步骤

1. **复制到游戏目录：**
   ```
   从：E:\Data\source\repos\WpfApp1\_diagnostics\AdaptationPatchSmoke\out\826-bone-remap-final-fix\
   到：D:\SteamLibrary\steamapps\common\Helldivers 2\data\
   ```

2. **进游戏验证：**
   - ✅ 模型正常显示（不隐形）
   - ✅ 胯部骨骼连接正确
   - ✅ 左右腿动画正常
   - ✅ 腹部/胸部连接正确
   - ✅ 整体骨骼动画流畅

---

## 技术总结

**骨骼映射需要三个关键索引：**

1. **Source MaterialIndex** - 查询源 Unit 的 bone remap
2. **Target Section 的原始 MaterialIndex** - 查询目标 Unit 的 bone remap  
3. **Target 转换后的 MaterialIndex** - 用于输出 section 的材质绑定

**容易混淆的地方：**
- `replacementSections` 的 `MaterialIndex` 已经是转换后的
- 但查询 `targetBoneInfo.Remaps` 需要原始索引
- 必须通过 section 位置去获取原始 target section

这是一个典型的"索引空间"问题，需要清楚区分哪些操作使用哪个索引空间。
