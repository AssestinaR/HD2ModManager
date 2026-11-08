# HD2ModAdaptation 实际需求与架构细化

**日期：** 2026-07-12  
**基于：** adaptation-refactoring-plan.md  
**重点：** 材质管理、部件共享、实际可行性

> ⛔ 状态（2026-11-08）：本篇是 2026-07-12 的规划稿，不是现有实现清单。文中
> `StrictUnitMeshTransfer` / `BoneRemapper` / `MaterialMapper` / `MeshTransfer` 以及 `Foundation/`、`Tools/`
> 两层目录规划**在源码里都已 0 命中**（2026-08-30「代码清理阶段1」删掉了
> `HD2ModAdaptation/Processing/{BoneRemapper,MaterialMapper,MeshTransfer}.cs` 等；`HD2ModAdaptation` 现在的主干是
> `Analysis/` 与 `PatchReconstruction/`）。
> 仍然有效的是**问题分析**（材质分离 patch、材质包按需匹配、部件共享 Unit）。现行形状是：
> 转换段按 planner 把「模型 + 它自己的材质包」分组成 patch 组，基底 `sectional/` 里 unit patch 恒为
> `patch_0`、材质变体 `patch_1..N` 平铺 + `patches.json` 记号；编译段按 Mod 设置里的
> `enabledMaterialPatches` 选择性物化进 `Overwrite/`。链路见
> `docs/agent/sectional-compile-base-20261108.md`。

---

## 🎯 实施策略澄清

### 当前可行 vs 需要验证

**✅ Phase 1: 立即可实施（已验证）**
```
Patch 重构流程
├── 读取源 patch Units
├── 提取模型+材质到临时缓存
├── 读取目标 Units（同一 Unit 的新版本）
├── 转换（BoneRemapper + MaterialMapper + MeshTransfer）
└── 写入新 patch
```

**✅ Phase 2: 可以独立实施（低风险）**
```
模型提取工具
├── 扫描 patch 文件
├── 识别模型类型
├── 提取模型数据
├── 提取材质引用
└── 保存到缓存
```

**❓ Phase 3: 需要验证后实施（高风险）**
```
跨护甲替换
├── 骨骼结构兼容性？
├── 材质槽位兼容性？
├── 部件尺寸适配？
└── 动画兼容性？
```

**实施顺序：**
1. 先实现 Phase 1（Patch 重构，不依赖跨护甲替换）
2. 再实现 Phase 2（模型提取，独立验证）
3. 最后验证 Phase 3（跨护甲替换，实验性）
4. 整合流程（基于验证结果调整）

---

## 📦 材质与模型关系设计

### 场景分类

#### 场景 A: 一体化 Patch（最常见）

**结构：**
```
armor_mod.patch_0
├── Unit: 0xe0a48d0be9a7453f/0x4298e09c7670258d (护甲模型)
│   ├── Meshes (RawMeshData)
│   └── Material References (MaterialIds)
│
└── Material Assets (实际材质文件)
    ├── 0x1234567890abcdef (Diffuse 贴图)
    ├── 0x2345678901bcdef0 (Normal 贴图)
    └── 0x3456789012cdef01 (Specular 贴图)
```

**特点：**
- ✅ 模型和材质在同一个 patch
- ✅ 提取时可以完整获取
- ✅ 自包含，不依赖外部

#### 场景 B: 分离式 Patch（换色包）

**结构：**
```
armor_model.patch_0          # 模型 patch
└── Unit: 0xe0a48d0be9a7453f/0x4298e09c7670258d
    ├── Meshes (RawMeshData)
    └── Material References: [0xAAA, 0xBBB, 0xCCC] ← 引用但不包含

armor_material_red.patch_0   # 红色材质包
├── Material 0xAAA (Red Diffuse)
├── Material 0xBBB (Red Normal)
└── Material 0xCCC (Red Specular)

armor_material_blue.patch_0  # 蓝色材质包
├── Material 0xAAA (Blue Diffuse)
├── Material 0xBBB (Blue Normal)
└── Material 0xCCC (Blue Specular)
```

**特点：**
- ⚠️ 模型 patch 缺少材质文件
- ✅ 多个材质包可替换使用
- ✅ 更新模型不影响材质

**检测逻辑：**
```csharp
public sealed class MaterialDetectionResult
{
    public bool HasEmbeddedMaterials { get; init; }
    public IReadOnlyList<ulong> ReferencedMaterialIds { get; init; }
    public IReadOnlyList<ulong> MissingMaterialIds { get; init; }
    
    public bool IsComplete => MissingMaterialIds.Count == 0;
    public bool RequiresExternalMaterials => !IsComplete;
}
```

#### 场景 C: 共享材质包（护甲套装）

**结构：**
```
armor_set.patch_0            # 多个护甲共享
├── Unit A (CW-22 上身)
│   └── Material Refs: [0xAAA, 0xBBB]
├── Unit B (CW-22 下身)
│   └── Material Refs: [0xAAA, 0xBBB]
└── Unit C (CW-22 头盔)
    └── Material Refs: [0xCCC]

shared_materials.patch_0     # 共享材质
├── Material 0xAAA (通用 Diffuse)
├── Material 0xBBB (通用 Normal)
└── Material 0xCCC (头盔专用)
```

**检测逻辑：**
```csharp
public sealed class MaterialSharingAnalysis
{
    public Dictionary<ulong, List<AssetKey>> MaterialUsage { get; init; }
    // Key: MaterialId, Value: 使用该材质的 Units
    
    public IReadOnlyList<MaterialGroup> SharedMaterialGroups { get; init; }
    // 材质组：哪些 Units 共享哪些材质
}
```

---

## 🔍 材质检测与管理

### 核心功能

#### 1. 材质完整性检测

```csharp
// Tools/Validators/MaterialValidator.cs
public sealed class MaterialValidator
{
    public async Task<MaterialDetectionResult> DetectAsync(
        string patchPath,
        AssetKey unitKey)
    {
        // 1. 读取 Unit，获取材质引用
        var unit = await unitReader.ReadAsync(patchPath, unitKey);
        var referencedIds = ExtractMaterialReferences(unit);
        
        // 2. 检查 patch 中是否包含这些材质
        var availableIds = await ScanMaterialsInPatch(patchPath);
        var missingIds = referencedIds.Except(availableIds).ToList();
        
        return new MaterialDetectionResult
        {
            HasEmbeddedMaterials = availableIds.Count > 0,
            ReferencedMaterialIds = referencedIds,
            MissingMaterialIds = missingIds
        };
    }
}
```

#### 2. 材质包识别

```csharp
// Tools/Analyzers/MaterialPackageAnalyzer.cs
public sealed class MaterialPackageAnalyzer
{
    public async Task<MaterialPackageInfo> AnalyzeAsync(
        string patchPath)
    {
        // 1. 扫描 patch 中的所有资源
        var entries = await ScanPatchEntries(patchPath);
        
        // 2. 识别材质资源（基于类型ID）
        var materials = entries
            .Where(e => IsMaterialAsset(e.AssetKey.TypeId))
            .ToList();
        
        // 3. 识别 Unit 资源
        var units = entries
            .Where(e => e.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId)
            .ToList();
        
        // 4. 判断是否为材质包
        var isMaterialPack = materials.Count > 0 && units.Count == 0;
        
        return new MaterialPackageInfo
        {
            IsMaterialPackage = isMaterialPack,
            MaterialIds = materials.Select(m => m.AssetKey.FileId).ToList(),
            UnitCount = units.Count,
            MaterialCount = materials.Count
        };
    }
}
```

#### 3. 材质匹配分析

```csharp
// Tools/Analyzers/MaterialMatcher.cs
public sealed class MaterialMatcher
{
    public async Task<MaterialMatchResult> FindMatchingMaterialsAsync(
        string modelPatchPath,
        IReadOnlyList<string> candidateMaterialPaths)
    {
        // 1. 分析模型需要的材质ID
        var required = await GetRequiredMaterials(modelPatchPath);
        
        var matches = new List<MaterialPackageMatch>();
        
        // 2. 检查每个候选材质包
        foreach (var matPath in candidateMaterialPaths)
        {
            var available = await GetAvailableMaterials(matPath);
            
            // 3. 计算匹配度
            var matched = required.Intersect(available).ToList();
            var coverage = (double)matched.Count / required.Count;
            
            if (coverage > 0)
            {
                matches.Add(new MaterialPackageMatch
                {
                    PackagePath = matPath,
                    MatchedMaterialIds = matched,
                    Coverage = coverage,
                    IsComplete = coverage >= 1.0
                });
            }
        }
        
        return new MaterialMatchResult
        {
            RequiredMaterials = required,
            Matches = matches.OrderByDescending(m => m.Coverage).ToList()
        };
    }
}

public sealed class MaterialPackageMatch
{
    public string PackagePath { get; init; }
    public IReadOnlyList<ulong> MatchedMaterialIds { get; init; }
    public double Coverage { get; init; }  // 0.0 ~ 1.0
    public bool IsComplete { get; init; }
}
```

---

## 🧩 原版游戏部件共享分析

### 问题描述

**CW-22 和 CW-36 共享胯部 Unit：**
```
CW-22 Archive: 1d9e8acfc3ee3ace
├── 胸部: 0x2bcee0cabdd012ed
├── 胯部: 0x40c2c1507a97529b ← 共享
└── ...

CW-36 Archive: 7b19ad7f21d347b4
├── 胸部: 0x608fe93ecd7e6e7e
├── 胯部: 0x40c2c1507a97529b ← 共享
└── ...
```

**影响：**
- 修改胯部 Unit → 两个护甲都改变
- 需要检测"哪些护甲使用了这个 Unit"

### 部件共享数据库

#### 1. 构建共享关系

```csharp
// Foundation/Analysis/UnitSharingDatabase.cs
public sealed class UnitSharingDatabase
{
    // Key: Unit FileId, Value: 使用该 Unit 的护甲列表
    private readonly Dictionary<ulong, List<ArmorReference>> unitToArmors;
    
    public async Task BuildAsync(string gameDataPath)
    {
        // 1. 扫描所有护甲档案
        var armorArchives = new[]
        {
            "1d9e8acfc3ee3ace", // CW-22
            "7b19ad7f21d347b4", // CW-36
            // ... 更多
        };
        
        foreach (var archive in armorArchives)
        {
            // 2. 读取档案中的所有 Units
            var units = await ScanUnitsInArchive(gameDataPath, archive);
            
            // 3. 记录关系
            foreach (var unit in units)
            {
                if (!unitToArmors.ContainsKey(unit.FileId))
                    unitToArmors[unit.FileId] = new List<ArmorReference>();
                
                unitToArmors[unit.FileId].Add(new ArmorReference
                {
                    ArchiveName = archive,
                    ArmorName = DetectArmorName(archive), // CW-22, CW-36, etc.
                    Role = DetectUnitRole(unit) // chest, waist, legs, etc.
                });
            }
        }
    }
    
    public UnitSharingInfo GetSharingInfo(ulong unitFileId)
    {
        if (!unitToArmors.TryGetValue(unitFileId, out var armors))
        {
            return new UnitSharingInfo
            {
                IsShared = false,
                Armors = Array.Empty<ArmorReference>()
            };
        }
        
        return new UnitSharingInfo
        {
            IsShared = armors.Count > 1,
            Armors = armors,
            SharedCount = armors.Count
        };
    }
}

public sealed class ArmorReference
{
    public string ArchiveName { get; init; }
    public string ArmorName { get; init; }   // "CW-22", "CW-36"
    public string Role { get; init; }         // "chest", "waist", "legs"
}

public sealed class UnitSharingInfo
{
    public bool IsShared { get; init; }
    public int SharedCount { get; init; }
    public IReadOnlyList<ArmorReference> Armors { get; init; }
}
```

#### 2. 护甲关联查询

```csharp
// Tools/Analyzers/ArmorRelationshipAnalyzer.cs
public sealed class ArmorRelationshipAnalyzer
{
    private readonly UnitSharingDatabase sharingDb;
    
    public async Task<ArmorRelationshipResult> AnalyzeAsync(
        string targetArmorArchive,
        string gameDataPath)
    {
        // 1. 获取目标护甲的所有 Units
        var targetUnits = await ScanUnitsInArchive(gameDataPath, targetArmorArchive);
        
        var relatedArmors = new HashSet<string>();
        var sharedParts = new List<SharedPartInfo>();
        
        // 2. 检查每个 Unit 是否被其他护甲共享
        foreach (var unit in targetUnits)
        {
            var sharingInfo = sharingDb.GetSharingInfo(unit.FileId);
            
            if (sharingInfo.IsShared)
            {
                sharedParts.Add(new SharedPartInfo
                {
                    UnitId = unit.FileId,
                    Role = DetectUnitRole(unit),
                    SharedWith = sharingInfo.Armors
                        .Where(a => a.ArchiveName != targetArmorArchive)
                        .ToList()
                });
                
                // 记录关联护甲
                foreach (var armor in sharingInfo.Armors)
                {
                    if (armor.ArchiveName != targetArmorArchive)
                        relatedArmors.Add(armor.ArmorName);
                }
            }
        }
        
        return new ArmorRelationshipResult
        {
            TargetArmor = DetectArmorName(targetArmorArchive),
            RelatedArmors = relatedArmors.ToList(),
            SharedParts = sharedParts,
            HasSharedParts = sharedParts.Count > 0
        };
    }
}

public sealed class SharedPartInfo
{
    public ulong UnitId { get; init; }
    public string Role { get; init; }  // "waist", "chest", etc.
    public IReadOnlyList<ArmorReference> SharedWith { get; init; }
}

public sealed class ArmorRelationshipResult
{
    public string TargetArmor { get; init; }
    public IReadOnlyList<string> RelatedArmors { get; init; }
    public IReadOnlyList<SharedPartInfo> SharedParts { get; init; }
    public bool HasSharedParts { get; init; }
}
```

---

## 🔄 更新后的架构

### Foundation 层增强

```
Foundation/
├── Models/
│   ├── UnitMesh/
│   ├── Material/
│   │   ├── MaterialReference.cs
│   │   ├── MaterialPackageInfo.cs
│   │   └── MaterialDetectionResult.cs
│   └── Armor/
│       ├── ArmorReference.cs
│       ├── UnitSharingInfo.cs
│       └── ArmorRelationshipResult.cs
│
├── Analysis/                      # 新增：分析工具
│   ├── UnitSharingDatabase.cs    # 部件共享数据库
│   ├── MaterialMatcher.cs         # 材质匹配器
│   └── ArmorRelationshipAnalyzer.cs
│
└── Cache/
    ├── ModelCache.cs
    └── MaterialCache.cs           # 新增：材质缓存
```

### Tools 层增强

```
Tools/
├── Extractors/
│   ├── ModelExtractor.cs
│   └── MaterialExtractor.cs       # 新增：材质提取
│
├── Validators/
│   └── MaterialValidator.cs       # 新增：材质验证
│
└── Analyzers/                     # 新增：分析工具
    ├── MaterialPackageAnalyzer.cs
    ├── MaterialMatcher.cs
    └── ArmorRelationshipAnalyzer.cs
```

> ⛔ 已过时（2026-11-08）：上面 `Foundation/`、`Tools/` 两层与其中列出的文件名在仓库里都不存在
> （`ModelExtractor` / `MaterialExtractor` / `MaterialValidator` / `MaterialMatcher` / `UnitSharingDatabase` 等
> 全部 0 命中）。现行对应物是 `HD2ModAdaptation/Analysis/PatchGroupAnalyzer.cs`、
> `HD2ModAdaptation/PatchReconstruction/MaterialDependencyResolver.cs` 与
> `HD2ModAdaptation/PatchReconstruction/MaterialPackagingService.cs`。
> 下面「实际使用场景」的代码是按这套不存在的 API 写的示意，读的时候按上面的对应物换算。

---

## 📋 实际使用场景

### 场景 1: 检测 Patch 材质完整性

```csharp
var validator = new MaterialValidator(serviceProvider);

var result = await validator.DetectAsync(
    @"mods\cool_armor.patch_0",
    new AssetKey(0xe0a48d0be9a7453f, 0x4298e09c7670258d));

if (!result.IsComplete)
{
    Console.WriteLine("⚠️ 该 patch 缺少材质文件！");
    Console.WriteLine($"需要的材质: {result.ReferencedMaterialIds.Count}");
    Console.WriteLine($"缺少的材质: {result.MissingMaterialIds.Count}");
    
    // 提示用户选择材质包
    Console.WriteLine("\n请选择材质包：");
    // ... UI 提示
}
else
{
    Console.WriteLine("✅ 该 patch 包含完整材质");
}
```

### 场景 2: 查找匹配的材质包

```csharp
var matcher = new MaterialMatcher(serviceProvider);

var matches = await matcher.FindMatchingMaterialsAsync(
    @"mods\armor_model.patch_0",
    new[]
    {
        @"materials\red_set.patch_0",
        @"materials\blue_set.patch_0",
        @"materials\gold_set.patch_0"
    });

Console.WriteLine($"需要 {matches.RequiredMaterials.Count} 个材质");
Console.WriteLine($"找到 {matches.Matches.Count} 个匹配的材质包：\n");

foreach (var match in matches.Matches)
{
    var status = match.IsComplete ? "✅ 完整" : "⚠️ 部分";
    Console.WriteLine($"{status} {Path.GetFileName(match.PackagePath)}");
    Console.WriteLine($"   覆盖率: {match.Coverage:P0}");
    Console.WriteLine($"   匹配: {match.MatchedMaterialIds.Count}/{matches.RequiredMaterials.Count}");
}
```

### 场景 3: 检测护甲关联关系

```csharp
var analyzer = new ArmorRelationshipAnalyzer(sharingDatabase);

var result = await analyzer.AnalyzeAsync(
    "1d9e8acfc3ee3ace",  // CW-22
    @"D:\Helldivers2\data");

Console.WriteLine($"目标护甲: {result.TargetArmor}");

if (result.HasSharedParts)
{
    Console.WriteLine("\n⚠️ 警告：该护甲与以下护甲共享部件：");
    foreach (var armor in result.RelatedArmors)
    {
        Console.WriteLine($"  - {armor}");
    }
    
    Console.WriteLine("\n共享的部件：");
    foreach (var part in result.SharedParts)
    {
        Console.WriteLine($"  - {part.Role} (Unit: 0x{part.UnitId:x16})");
        Console.WriteLine($"    共享给: {string.Join(", ", part.SharedWith.Select(a => a.ArmorName))}");
    }
    
    Console.WriteLine("\n💡 建议：同时替换这些护甲以保持一致性");
}
else
{
    Console.WriteLine("✅ 该护甲不与其他护甲共享部件");
}
```

### 场景 4: 提取模型时处理材质

```csharp
var extractor = new ModelExtractor(serviceProvider);
var validator = new MaterialValidator(serviceProvider);

// 1. 检测材质完整性
var materialCheck = await validator.DetectAsync(patchPath, unitKey);

if (materialCheck.RequiresExternalMaterials)
{
    // 2. 提示用户选择材质包
    var materialPaths = await PromptUserForMaterialPackages();
    
    // 3. 提取模型（不含材质）
    var modelCache = await extractor.ExtractAsync(
        patchPath, 
        unitKey,
        new ModelExtractionOptions
        {
            IncludeMaterials = false  // 材质从外部获取
        });
    
    // 4. 单独提取材质
    var materialExtractor = new MaterialExtractor(serviceProvider);
    var materialCache = await materialExtractor.ExtractAsync(
        materialPaths[0],  // 用户选择的材质包
        materialCheck.ReferencedMaterialIds);
    
    // 5. 组合保存
    await CacheManager.SaveCombinedAsync(
        modelCache, 
        materialCache, 
        @"caches\armor_with_red_materials.cache");
}
else
{
    // 材质完整，直接提取
    var cache = await extractor.ExtractAsync(patchPath, unitKey);
    await CacheManager.SaveAsync(cache, @"caches\armor_complete.cache");
}
```

---

## 🎯 Phase 1 实施计划（修订版）

### 目标：实现 Patch 重构 + 材质管理基础

**不依赖未验证的功能（跨护甲替换）**

### Step 1: 核心组件提取（3-4 天）

**1.1 提取骨骼映射核心**
- 从 `StrictUnitMeshTransfer` 提取 `BoneRemapper`
- 独立测试骨骼映射逻辑

**1.2 提取材质映射核心**
- 从 `StrictUnitMeshTransfer` 提取 `MaterialMapper`
- 独立测试材质映射逻辑

**1.3 简化核心转换**
- 重构 `MeshTransfer`（从 900 行→300 行）
- 使用提取的组件

> ⛔ 已过时（2026-11-08）：`StrictUnitMeshTransfer` 与这三步要产出的 `BoneRemapper` / `MaterialMapper` /
> `MeshTransfer` 在源码里都已 0 命中（相关文件 2026-08-30 删除）。今天的对应实现见
> `HD2ModAdaptation/PatchReconstruction/UnitMesh/`（骨骼重映射、材质合并都在 `Canonical/` 里）。

### Step 2: 材质管理基础（2-3 天）

**2.1 材质检测**
- 实现 `MaterialValidator`
- 检测 patch 是否包含完整材质

**2.2 材质分析**
- 实现 `MaterialPackageAnalyzer`
- 识别材质包

**2.3 材质匹配**
- 实现 `MaterialMatcher`
- 查找匹配的材质包

### Step 3: Patch 重构流程（2 天）

**3.1 保留现有流程**
- 不改动当前可工作的代码
- 作为 fallback 方案

**3.2 使用新组件重写**
- 使用提取的核心组件
- 添加材质检测和处理

**3.3 集成测试**
- 使用 2.3 版本 patch 测试
- 确保功能正常

### Step 4: 部件共享分析（2 天）

**4.1 构建共享数据库**
- 扫描原版游戏护甲档案
- 记录 Unit 共享关系

**4.2 关联查询**
- 实现 `ArmorRelationshipAnalyzer`
- 查询护甲关联关系

---

## 📊 优先级调整

### P0 - 本周（核心功能）
1. ✅ 提取 `BoneRemapper`
2. ✅ 提取 `MaterialMapper`
3. ✅ 简化 `MeshTransfer`
4. ✅ 实现材质检测和匹配

### P1 - 下周（分析工具）
1. 实现部件共享数据库
2. 实现护甲关联分析
3. 完善材质管理
4. 补充单元测试

### P2 - 2周后（实验性功能）
1. 验证跨护甲替换
2. 实现模型替换工具（如果验证通过）
3. 整合完整流程

---

## ✅ 成功指标（修订版）

### Phase 1 完成标准
- [ ] `BoneRemapper` 独立且可测试
- [ ] `MaterialMapper` 独立且可测试
- [ ] `MeshTransfer` 代码量减少 60%+
- [ ] 材质检测功能完整
- [ ] Patch 重构流程正常工作
- [ ] 所有测试通过

### Phase 2 完成标准
- [ ] 部件共享数据库完整
- [ ] 护甲关联查询准确
- [ ] 材质包识别准确率 95%+
- [ ] 文档完善

### Phase 3 完成标准（待验证）
- [ ] 跨护甲替换实验成功
- [ ] 兼容性验证通过
- [ ] 性能达标

---

## 🔗 与 HD2ModManager 集成接口

### 需要提供的 API

```csharp
// 1. 材质完整性检查
Task<MaterialDetectionResult> CheckMaterialCompleteness(string patchPath);

// 2. 查找匹配材质包
Task<MaterialMatchResult> FindMatchingMaterials(
    string modelPatch, 
    IReadOnlyList<string> candidatePaths);

// 3. 护甲关联查询
Task<ArmorRelationshipResult> GetRelatedArmors(string armorArchive);

// 4. 部件共享信息
Task<UnitSharingInfo> GetUnitSharingInfo(ulong unitFileId);
```

### 数据格式

**缓存文件格式：**
```json
{
  "type": "armor",
  "name": "Cool Armor",
  "has_materials": false,
  "required_materials": [0x1234, 0x5678, 0x9abc],
  "meshes": [...],
  "metadata": {
    "source_patch": "cool_armor.patch_0",
    "extraction_date": "2026-07-12",
    "version": "1.0"
  }
}
```

**材质包格式：**
```json
{
  "type": "material_package",
  "variant": "red",
  "materials": [0x1234, 0x5678, 0x9abc],
  "compatible_models": [
    {
      "model_name": "Cool Armor",
      "coverage": 1.0
    }
  ]
}
```

---

**下一步：** 开始实施 Phase 1，提取核心组件并实现材质管理基础
