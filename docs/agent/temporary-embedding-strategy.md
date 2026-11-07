# 材质分离模组更新方案：临时内嵌策略

**日期：** 2026-07-12  
**基于：** 测试1发现 + 现有流程分析

---

## 🎯 核心思路

**将材质分离模组"临时模拟"为内嵌材质模组进行更新**

```
输入：
  - 模型 patch（无材质或基础材质）
  - 用户选择的材质包（可选）

处理：
  - 临时合并为内嵌材质版本
  - 使用现有流程更新
  - 输出时保持分离状态

输出：
  - 更新后的模型 patch（仍然是分离状态）
  - 材质包保持不变（不需要更新）
```

**关键：避免材质变为内嵌！**

---

## 💡 方案设计

### 流程图

```
┌─────────────────────────────────────────┐
│ 1. 检测 patch 类型                      │
└─────────────────┬───────────────────────┘
                  │
         ┌────────▼────────┐
         │ 是否内嵌材质？   │
         └────┬────────┬───┘
              │        │
        Yes   │        │ No
              │        │
              │        ▼
              │   ┌─────────────────────────┐
              │   │ 2. 请求用户提供材质包    │
              │   │   （可选，用于引用完整性）│
              │   └──────────┬──────────────┘
              │              │
              │              ▼
              │   ┌─────────────────────────┐
              │   │ 3. 临时合并（内存中）    │
              │   │   - 读取模型            │
              │   │   - 读取材质            │
              │   │   - 合并为完整 Unit     │
              │   └──────────┬──────────────┘
              │              │
              ▼              ▼
         ┌────────────────────────────────┐
         │ 4. 使用现有流程更新             │
         │   - MeshTransfer              │
         │   - 材质索引映射               │
         │   - 骨骼重映射                 │
         └────────────┬───────────────────┘
                      │
                      ▼
         ┌────────────────────────────────┐
         │ 5. 输出处理                    │
         └────┬───────────────────┬───────┘
              │                   │
        原本内嵌               原本分离
              │                   │
              ▼                   ▼
    ┌──────────────────┐  ┌──────────────────┐
    │ 输出内嵌版        │  │ 输出分离版        │
    │ patch_0          │  │ - 模型 patch_0   │
    │ (模型+材质)       │  │ - 材质引用保持   │
    └──────────────────┘  └──────────────────┘
```

---

## 🔧 实现设计

### 步骤1：检测 patch 类型

```csharp
public enum PatchMaterialMode
{
    Embedded,    // 内嵌材质
    Separated,   // 材质分离
    Unknown
}

public sealed class PatchMaterialDetector
{
    public async Task<PatchMaterialMode> DetectAsync(string patchPath)
    {
        var scanner = new PatchTocScanner();
        var entries = await scanner.ScanEntriesAsync(patchPath);
        
        // 检查是否包含 Unit
        var unitEntries = entries.Where(e => e.AssetKey.TypeId == UnitTypeId).ToList();
        if (unitEntries.Count == 0)
        {
            return PatchMaterialMode.Unknown;
        }
        
        // 读取 Unit 并检查材质
        foreach (var unitEntry in unitEntries)
        {
            var unit = await ReadUnitAsync(unitEntry, entries);
            
            // 检查材质引用是否在 patch 内
            var materialIds = unit.Model.Materials.Select(m => m.MaterialId).ToHashSet();
            var materialEntries = entries.Where(e => e.AssetKey.TypeId == MaterialTypeId).ToList();
            
            var missingMaterials = materialIds.Except(
                materialEntries.Select(e => e.AssetKey.FileId)).ToList();
            
            if (missingMaterials.Count > 0)
            {
                return PatchMaterialMode.Separated; // 有材质引用但不在 patch 内
            }
        }
        
        return PatchMaterialMode.Embedded; // 所有材质都在 patch 内
    }
}
```

### 步骤2：请求用户提供材质包

```csharp
public sealed class MaterialPackageRequest
{
    public string ModelPatchPath { get; init; }
    public IReadOnlyList<ulong> MissingMaterialIds { get; init; }
    public bool IsOptional { get; init; } // true = 可以不提供，false = 必须提供
}

public interface IMaterialPackageProvider
{
    // UI 实现：弹出对话框让用户选择材质包
    Task<IReadOnlyList<string>?> RequestMaterialPackagesAsync(MaterialPackageRequest request);
}

// 命令行实现
public sealed class ConsoleMaterialPackageProvider : IMaterialPackageProvider
{
    public async Task<IReadOnlyList<string>?> RequestMaterialPackagesAsync(
        MaterialPackageRequest request)
    {
        Console.WriteLine($"⚠️ 检测到材质分离的模组");
        Console.WriteLine($"模型 patch: {Path.GetFileName(request.ModelPatchPath)}");
        Console.WriteLine($"缺少 {request.MissingMaterialIds.Count} 个材质引用");
        
        if (request.IsOptional)
        {
            Console.WriteLine("提供材质包可以避免引用问题（可选）");
            Console.WriteLine("留空直接回车跳过，或输入材质包路径：");
        }
        else
        {
            Console.WriteLine("必须提供材质包才能继续：");
        }
        
        var paths = new List<string>();
        while (true)
        {
            Console.Write("材质包路径（或留空完成）: ");
            var input = Console.ReadLine()?.Trim();
            
            if (string.IsNullOrEmpty(input))
            {
                break;
            }
            
            if (File.Exists(input))
            {
                paths.Add(input);
                Console.WriteLine($"✓ 已添加: {Path.GetFileName(input)}");
            }
            else
            {
                Console.WriteLine($"✗ 文件不存在: {input}");
            }
        }
        
        if (paths.Count == 0 && !request.IsOptional)
        {
            throw new InvalidOperationException("必须提供至少一个材质包");
        }
        
        return paths.Count > 0 ? paths : null;
    }
}
```

### 步骤3：临时合并（内存中）

```csharp
public sealed class TemporaryMaterialCombiner
{
    /// <summary>
    /// 在内存中临时合并模型和材质，不修改原 patch
    /// </summary>
    public async Task<UnitMeshModel> CombineInMemoryAsync(
        string modelPatchPath,
        IReadOnlyList<string>? materialPatchPaths)
    {
        // 1. 读取模型
        var modelReader = new PatchUnitMeshReader();
        var scanner = new PatchTocScanner();
        var modelEntries = await scanner.ScanEntriesAsync(modelPatchPath);
        var modelUnit = await modelReader.ReadAsync(
            modelEntries.First(e => e.AssetKey.TypeId == UnitTypeId),
            modelEntries);
        
        if (materialPatchPaths == null || materialPatchPaths.Count == 0)
        {
            // 没有提供材质包，直接返回模型
            return modelUnit.Model;
        }
        
        // 2. 读取所有材质包
        var allMaterialEntries = new List<PatchTocEntry>();
        foreach (var matPath in materialPatchPaths)
        {
            var matEntries = await scanner.ScanEntriesAsync(matPath);
            var materialEntries = matEntries.Where(e => e.AssetKey.TypeId == MaterialTypeId);
            allMaterialEntries.AddRange(materialEntries);
        }
        
        // 3. 创建临时合并的 entries（用于读取材质数据）
        var combinedEntries = modelEntries.Concat(allMaterialEntries).ToList();
        
        // 4. 重新读取 Unit（这次会找到材质）
        var combinedUnit = await modelReader.ReadAsync(
            modelEntries.First(e => e.AssetKey.TypeId == UnitTypeId),
            combinedEntries);
        
        return combinedUnit.Model;
    }
}
```

### 步骤4：使用现有流程更新

```csharp
public sealed class AdaptiveMeshTransfer
{
    private readonly MeshTransfer meshTransfer;
    private readonly PatchMaterialDetector detector;
    private readonly TemporaryMaterialCombiner combiner;
    private readonly IMaterialPackageProvider materialProvider;
    
    public async Task<AdaptiveMeshTransferResult> TransferAsync(
        string sourcePatchPath,
        string gameDataPath,
        int targetMeshInfoIndex)
    {
        // 1. 检测材质模式
        var mode = await detector.DetectAsync(sourcePatchPath);
        
        UnitMeshModel sourceModel;
        IReadOnlyList<string>? usedMaterialPaths = null;
        
        if (mode == PatchMaterialMode.Separated)
        {
            // 2. 请求材质包（可选）
            var missingMaterials = await DetectMissingMaterialsAsync(sourcePatchPath);
            var request = new MaterialPackageRequest
            {
                ModelPatchPath = sourcePatchPath,
                MissingMaterialIds = missingMaterials,
                IsOptional = true // 允许不提供
            };
            
            usedMaterialPaths = await materialProvider.RequestMaterialPackagesAsync(request);
            
            // 3. 临时合并
            sourceModel = await combiner.CombineInMemoryAsync(sourcePatchPath, usedMaterialPaths);
        }
        else
        {
            // 内嵌材质，直接读取
            sourceModel = await ReadModelAsync(sourcePatchPath);
        }
        
        // 4. 读取目标
        var targetModel = await ReadTargetModelAsync(gameDataPath, targetMeshInfoIndex);
        
        // 5. 使用现有流程转换
        var result = meshTransfer.Transfer(
            targetModel, 
            targetMeshInfoIndex, 
            sourceModel, 
            0); // 假设源只有一个 mesh
        
        return new AdaptiveMeshTransferResult
        {
            UpdatedModel = result.Model,
            OriginalMode = mode,
            UsedMaterialPaths = usedMaterialPaths
        };
    }
}
```

### 步骤5：输出处理（保持原有模式）

```csharp
public sealed class AdaptiveOutputWriter
{
    private readonly UnitMeshWriter writer;
    
    public async Task WriteAsync(
        AdaptiveMeshTransferResult result,
        string outputPath)
    {
        if (result.OriginalMode == PatchMaterialMode.Embedded)
        {
            // 原本就是内嵌，正常输出
            await WriteEmbeddedAsync(result.UpdatedModel, outputPath);
        }
        else
        {
            // 原本是分离，输出为分离模式
            await WriteSeparatedAsync(result.UpdatedModel, outputPath);
        }
    }
    
    private async Task WriteEmbeddedAsync(UnitMeshModel model, string outputPath)
    {
        // 标准输出：模型 + 材质都在一起
        var entries = await writer.Write(model, new byte[0]);
        await WriteToFileAsync(entries, outputPath, "patch_0");
    }
    
    private async Task WriteSeparatedAsync(UnitMeshModel model, string outputPath)
    {
        // 分离输出：移除材质，只保留模型和材质引用
        
        // 1. 创建一个只包含模型的版本
        var modelOnly = model with
        {
            Materials = model.Materials // 保留材质引用
            // 但不写入实际的材质数据到 entries
        };
        
        // 2. 写入时标记哪些是材质数据，不包含它们
        var entries = await writer.Write(modelOnly, new byte[0]);
        
        // 3. 过滤掉材质 entries（只保留 Unit 和必要数据）
        var filteredEntries = entries.Where(e => 
            e.AssetKey.TypeId != MaterialTypeId).ToList();
        
        await WriteToFileAsync(filteredEntries, 
            Path.Combine(outputPath, "model"), 
            "patch_0");
        
        Console.WriteLine("⚠️ 注意：输出为分离模式，材质包未修改");
        Console.WriteLine("请使用原有的材质包文件");
    }
}
```

---

## ⚠️ 关键问题：避免材质变为内嵌

### 问题分析

**风险：**
```
原始：
  - model.patch_0（模型，引用外部材质）
  - material.patch_56（材质包）

更新后：
  - updated_model.patch_0（模型 + 内嵌材质）❌ 错误！
  - material.patch_56（旧材质包，不再需要）
```

**期望：**
```
原始：
  - model.patch_0（模型，引用外部材质）
  - material.patch_56（材质包）

更新后：
  - updated_model.patch_0（模型，仍然引用外部材质）✅
  - material.patch_56（旧材质包，继续使用）✅
```

### 解决方案

**方案A：输出时剥离材质（推荐）**

```csharp
private async Task WriteSeparatedAsync(UnitMeshModel model, string outputPath)
{
    // 1. 标记哪些材质是从外部材质包来的
    var externalMaterialIds = IdentifyExternalMaterials(model);
    
    // 2. 写入完整模型（临时）
    var fullEntries = await writer.Write(model, new byte[0]);
    
    // 3. 过滤掉外部材质的数据
    var modelOnlyEntries = fullEntries.Where(entry =>
        entry.AssetKey.TypeId != MaterialTypeId ||
        !externalMaterialIds.Contains(entry.AssetKey.FileId)).ToList();
    
    // 4. 保留材质引用，但不包含材质数据
    await WriteToFileAsync(modelOnlyEntries, outputPath, "patch_0");
}
```

**方案B：使用特殊标记**

在 `AdaptiveMeshTransferResult` 中记录：
```csharp
public sealed record AdaptiveMeshTransferResult
{
    public UnitMeshModel UpdatedModel { get; init; }
    public PatchMaterialMode OriginalMode { get; init; }
    public IReadOnlyList<string>? UsedMaterialPaths { get; init; }
    
    // 新增：标记哪些材质来自外部
    public IReadOnlySet<ulong> ExternalMaterialIds { get; init; }
}
```

输出时检查：
```csharp
if (result.ExternalMaterialIds.Contains(materialId))
{
    // 这是外部材质，不写入 patch
    continue;
}
```

**方案C：双重验证**

```csharp
// 在输出前验证
var outputMode = await detector.DetectAsync(outputPatchPath);

if (result.OriginalMode == PatchMaterialMode.Separated && 
    outputMode == PatchMaterialMode.Embedded)
{
    throw new InvalidOperationException(
        "输出错误：原本分离的材质被错误地内嵌了！");
}
```

---

## 🎯 用户体验设计

### 命令行工具

```bash
# 场景1：内嵌材质模组（无需额外输入）
> UpdateMod armor_v2.3.patch_0

检测到内嵌材质模组
正在更新...
✓ 完成：armor_v2.4.patch_0

# 场景2：材质分离模组（可选提供材质包）
> UpdateMod model.patch_0

⚠️ 检测到材质分离模组
缺少 5 个材质引用

提供材质包可以避免引用问题（可选）
留空跳过，或输入材质包路径：

材质包路径: material_black.patch_56
✓ 已添加: material_black.patch_56

材质包路径: [回车跳过]

正在临时合并...
正在更新...
✓ 完成：updated_model.patch_0（分离模式）
⚠️ 请继续使用原有材质包：material_black.patch_56

# 场景3：批量更新（自动查找材质包）
> UpdateMod --auto-detect model.patch_0

⚠️ 检测到材质分离模组
正在自动查找材质包...
✓ 找到：material_black.patch_56
✓ 找到：material_blue.patch_56

使用找到的材质包？[Y/n]: Y

正在更新...
✓ 完成（分离模式）
```

### HD2ModManager UI

```
┌─────────────────────────────────────────┐
│ 更新模组: Chiffon DS-42                 │
├─────────────────────────────────────────┤
│                                         │
│ ⚠️ 检测到材质分离模组                   │
│                                         │
│ 模型文件: model.patch_0                 │
│ 缺少材质引用: 5 个                      │
│                                         │
│ □ 提供材质包（推荐，避免引用问题）       │
│                                         │
│   [选择材质包...]  material_black.patch_56 [×] │
│                                         │
│ ℹ️ 不提供材质包也可以更新，但可能导致    │
│    材质显示异常                          │
│                                         │
│         [取消]        [继续更新]         │
└─────────────────────────────────────────┘

更新后：
┌─────────────────────────────────────────┐
│ ✓ 更新完成                              │
├─────────────────────────────────────────┤
│                                         │
│ 输出文件: updated_model.patch_0          │
│ 模式: 材质分离（保持原有结构）            │
│                                         │
│ ⚠️ 材质包未修改，请继续使用：            │
│    • material_black.patch_56            │
│                                         │
│              [确定]                      │
└─────────────────────────────────────────┘
```

---

## 📊 优缺点分析

### ✅ 优点

1. **复用现有流程**
   - 不需要重写核心转换逻辑
   - MeshTransfer 可以直接使用
   - 稳定性高

2. **用户体验好**
   - 可选提供材质包（不强制）
   - 自动检测和提示
   - 保持原有模式

3. **风险可控**
   - 临时合并，不修改原文件
   - 输出时明确分离
   - 可以回退

4. **渐进式实现**
   - 先实现检测
   - 再实现合并
   - 最后实现输出

### ⚠️ 缺点与风险

1. **材质变内嵌的风险**
   - **需要特别小心处理输出**
   - 必须正确过滤材质数据
   - 需要验证机制

2. **性能开销**
   - 需要读取额外的材质包
   - 临时合并有内存开销
   - 比纯模型更新慢

3. **材质包兼容性**
   - 用户提供的材质包可能不匹配
   - 可能导致引用错误
   - 需要验证材质 ID

---

## 🔬 测试计划

### 测试2：临时合并机制

**输入：**
- model.patch_0（无材质）
- material_black.patch_56（黑色材质）

**步骤：**
1. 检测 patch 类型 → Separated
2. 请求材质包 → 提供 material_black.patch_56
3. 临时合并 → 生成完整 UnitMeshModel
4. 验证合并结果

**验证点：**
- [ ] 材质 ID 匹配
- [ ] 材质数据完整
- [ ] 内存中正确合并

### 测试3：分离模式输出

**输入：**
- 更新后的完整 UnitMeshModel（含材质）
- OriginalMode = Separated

**步骤：**
1. 识别外部材质 ID
2. 过滤材质数据
3. 输出为分离模式
4. 验证输出

**验证点：**
- [ ] 模型数据完整
- [ ] 材质引用保留
- [ ] 材质数据未写入
- [ ] 文件大小合理（不含材质数据）

### 测试4：端到端验证

**完整流程：**
```
model.patch_0 + material.patch_56
  ↓ 检测
  ↓ 临时合并
  ↓ 更新
  ↓ 分离输出
  ↓
updated_model.patch_0 + material.patch_56（未修改）
  ↓ 游戏内测试
  ↓
验证：模型、材质、骨骼
```

---

## 📝 实施计划

### Phase 2A：核心功能（P0）

1. **PatchMaterialDetector**（1-2小时）
   - 检测材质模式
   - 识别缺失材质

2. **TemporaryMaterialCombiner**（2-3小时）
   - 内存合并
   - 材质 ID 映射

3. **AdaptiveOutputWriter**（2-3小时）
   - 分离模式输出
   - 材质过滤
   - 验证机制

### Phase 2B：集成和测试（P0）

4. **AdaptiveMeshTransfer**（1-2小时）
   - 集成检测、合并、输出
   - 错误处理

5. **测试2-4**（2-3小时）
   - 临时合并测试
   - 分离输出测试
   - 端到端测试

### Phase 2C：UI 和体验（P1）

6. **MaterialPackageProvider**（2-3小时）
   - 命令行版本
   - UI 版本（HD2ModManager）

7. **自动查找材质包**（1-2小时）
   - 同目录扫描
   - 智能匹配

---

## 🎯 总结

### 核心策略

**临时内嵌 → 更新 → 保持分离**

- ✅ 复用现有流程
- ✅ 用户体验好
- ✅ 风险可控
- ⚠️ 需要小心避免材质变内嵌

### 关键点

1. **检测**：准确识别材质模式
2. **合并**：临时合并，不修改原文件
3. **输出**：正确过滤材质数据
4. **验证**：确保输出模式正确

### 下一步

**立即实施 Phase 2A（核心功能）**

预计时间：5-8 小时
风险：中低（有清晰的方案）
