# 材质分离模组处理流程设计

**日期：** 2026-07-12  
**基于：** 材质拆分分析 + 实际需求讨论

---

## 🎯 核心场景

### 场景1：更新过时模组

**情况A：内嵌材质模组（当前支持）**
```
输入：old_armor.patch_0（模型+材质内嵌）
处理：使用现有流程更新
输出：new_armor.patch_0（模型+材质内嵌）
```
✅ 已支持，无需修改

**情况B：分离材质模组（需要新支持）**
```
输入：
  - model.patch_0（模型，可能有基础材质）
  - material_red.patch_1（红色材质）
  
问题：
  1. 只更新模型，不更新材质？
  2. 材质索引会不会错位？
  3. 旧材质能否继续使用？
```

**关键问题：模型更新后，材质索引是否稳定？**

### 场景2：替换到其他护甲

**当前模组：**
```
- model.patch_0（模型）
- material_red.patch_1（红色材质）
- material_blue.patch_2（蓝色材质）
```

**目标：替换到 CW-36**

**问题：材质要不要一起替换？**
- 选项A：只替换模型 → 材质可能不匹配
- 选项B：一起替换 → 保持完整性

**我的建议：** 材质也要一起替换，因为材质是为这个模型设计的

### 场景3：内嵌 → 分离

**输入：**
```
armor_mod.patch_0
├── Unit（模型）
└── Materials（内嵌材质）
```

**输出：**
```
模型/
└── armor_mod.patch_0
    └── Unit（模型，材质引用）

材质1/
└── armor_mod.patch_1
    └── Materials（原始材质）
```

### 场景4：分离 → 内嵌

**输入：**
```
model.patch_0（模型）
material_red.patch_1（红色材质）
```

**输出：**
```
armor_mod.patch_0
├── Unit（模型）
└── Materials（合并的材质）
```

---

## 🔬 测试问题1：无材质patch能否直接更新？

### 实验设计

**假设：** Main/patch_1 只包含模型，没有材质

**测试步骤：**
1. 从 Main/patch_1 提取模型
2. 使用现有流程更新到新版游戏
3. 生成新的 patch_0
4. 尝试加载：new_patch_0 + old_material.patch_56

**预期结果：**
- ✅ 如果材质索引不变 → 可以继续用旧材质
- ❌ 如果材质索引改变 → 旧材质失效

**风险：**
- 材质索引可能由目标Unit决定
- 更新后索引可能重新分配
- **需要实际测试验证**

---

## 🔧 操作流程设计

### 流程1：更新（Update）

#### 1.1 检测模组类型

```csharp
public enum ModType
{
    Embedded,        // 内嵌材质（模型+材质在一起）
    SeparatedBase,   // 分离式基础包（模型+基础材质）
    SeparatedVariant // 分离式变体（只有材质）
}

public async Task<ModType> DetectModTypeAsync(string patchPath)
{
    var unitCount = await CountUnitsAsync(patchPath);
    var materialCount = await CountMaterialsAsync(patchPath);
    
    if (unitCount > 0 && materialCount > 0)
        return ModType.Embedded;
    
    if (unitCount > 0 && materialCount == 0)
        return ModType.SeparatedBase; // 或者基础包
    
    if (unitCount == 0 && materialCount > 0)
        return ModType.SeparatedVariant;
    
    throw new InvalidOperationException("Unknown mod type");
}
```

#### 1.2 更新策略

**策略A：只更新模型（推荐用于测试）**
```csharp
public async Task<UpdateResult> UpdateModelOnlyAsync(
    string modelPatchPath,
    string gameDataPath,
    string outputPath)
{
    // 1. 提取模型（不提取材质）
    var modelCache = await ExtractModelAsync(modelPatchPath, includeMaterials: false);
    
    // 2. 读取目标Unit
    var targetUnit = await LoadTargetUnitAsync(gameDataPath);
    
    // 3. 转换模型
    var result = await meshTransfer.Transfer(targetUnit, modelCache);
    
    // 4. 写入patch（不包含材质）
    await WritePatchAsync(result, outputPath);
    
    return new UpdateResult
    {
        UpdatedModelPatch = outputPath,
        MaterialPatchesStillValid = true, // 假设旧材质仍可用
        Warning = "请测试旧材质是否仍然工作"
    };
}
```

**策略B：同时更新模型和材质（安全）**
```csharp
public async Task<UpdateResult> UpdateModelAndMaterialsAsync(
    string modelPatchPath,
    IReadOnlyList<string> materialPatchPaths,
    string gameDataPath,
    string outputPath)
{
    // 1. 提取模型+所有材质
    var modelCache = await ExtractModelAsync(modelPatchPath, includeMaterials: true);
    var materialCaches = new List<MaterialCache>();
    
    foreach (var matPath in materialPatchPaths)
    {
        var matCache = await ExtractMaterialsAsync(matPath);
        materialCaches.Add(matCache);
    }
    
    // 2. 更新模型
    var updatedModel = await UpdateModelAsync(modelCache, gameDataPath);
    
    // 3. 重新映射材质索引
    var remappedMaterials = await RemapMaterialsAsync(updatedModel, materialCaches);
    
    // 4. 写入更新后的patch
    await WriteModelPatchAsync(updatedModel, Path.Combine(outputPath, "model.patch_0"));
    
    for (int i = 0; i < remappedMaterials.Count; i++)
    {
        await WriteMaterialPatchAsync(
            remappedMaterials[i], 
            Path.Combine(outputPath, $"material_{i}", $"material.patch_{i+1}"));
    }
    
    return new UpdateResult
    {
        UpdatedModelPatch = Path.Combine(outputPath, "model.patch_0"),
        UpdatedMaterialPatches = remappedMaterials.Select((m, i) => 
            Path.Combine(outputPath, $"material_{i}", $"material.patch_{i+1}")).ToList()
    };
}
```

### 流程2：提取（Extract）

#### 2.1 提取模型+所有材质

```csharp
public sealed class ModelExtractionOptions
{
    public bool ExtractEmbeddedMaterials { get; init; } = true;
    public bool ExtractSeparateMaterials { get; init; } = true;
    public IReadOnlyList<string>? SeparateMaterialPatchPaths { get; init; }
}

public async Task<ModelWithMaterialsCache> ExtractAsync(
    string modelPatchPath,
    ModelExtractionOptions options)
{
    // 1. 提取模型
    var modelCache = await modelExtractor.ExtractAsync(modelPatchPath);
    
    // 2. 提取内嵌材质
    var embeddedMaterials = options.ExtractEmbeddedMaterials
        ? await ExtractEmbeddedMaterialsAsync(modelPatchPath)
        : null;
    
    // 3. 提取分离材质
    var separateMaterials = new List<MaterialVariant>();
    if (options.ExtractSeparateMaterials && options.SeparateMaterialPatchPaths != null)
    {
        foreach (var matPath in options.SeparateMaterialPatchPaths)
        {
            var variant = await ExtractMaterialVariantAsync(matPath);
            separateMaterials.Add(variant);
        }
    }
    
    return new ModelWithMaterialsCache
    {
        Model = modelCache,
        EmbeddedMaterial = embeddedMaterials != null 
            ? new MaterialVariant { Name = "Original", Materials = embeddedMaterials, IsRequired = true }
            : null,
        SeparateMaterials = separateMaterials
    };
}
```

#### 2.2 数据模型

```csharp
public sealed class ModelWithMaterialsCache
{
    public ModelCache Model { get; init; }
    
    // 内嵌材质（如果有）
    public MaterialVariant? EmbeddedMaterial { get; init; }
    
    // 分离材质变体
    public IReadOnlyList<MaterialVariant> SeparateMaterials { get; init; }
    
    // 所有可用的材质变体
    public IReadOnlyList<MaterialVariant> AllMaterials => 
        (EmbeddedMaterial != null 
            ? new[] { EmbeddedMaterial }.Concat(SeparateMaterials) 
            : SeparateMaterials).ToList();
}

public sealed class MaterialVariant
{
    public string Name { get; init; }  // "Original", "Red", "Blue", etc.
    public IReadOnlyList<CachedMaterial> Materials { get; init; }
    public bool IsRequired { get; init; } = false; // 是否必选（内嵌材质为true）
    public string? PreviewImagePath { get; init; }
}
```

### 流程3：注入（Inject）

#### 3.1 注入选项

```csharp
public sealed class ModelInjectionOptions
{
    // 选择的材质（可以单选或多选）
    public IReadOnlyList<MaterialVariant> SelectedMaterials { get; init; }
    
    // 输出模式
    public OutputMode Mode { get; init; }
    
    // 输出路径
    public string OutputPath { get; init; }
}

public enum OutputMode
{
    EmbeddedSingle,      // 内嵌单个材质
    SeparatedSingle,     // 分离单个材质（模型patch_0 + 材质patch_1）
    SeparatedMultiple    // 分离多个材质（模型patch_0 + 多个材质patch_N）
}
```

#### 3.2 注入逻辑

```csharp
public async Task<InjectionResult> InjectAsync(
    ModelWithMaterialsCache cache,
    ModelInjectionOptions options)
{
    // 验证
    if (options.SelectedMaterials.Count > 1 && options.Mode == OutputMode.EmbeddedSingle)
    {
        throw new InvalidOperationException("多个材质只能输出为分离模式");
    }
    
    switch (options.Mode)
    {
        case OutputMode.EmbeddedSingle:
            return await InjectEmbeddedAsync(cache, options.SelectedMaterials[0], options.OutputPath);
        
        case OutputMode.SeparatedSingle:
            return await InjectSeparatedSingleAsync(cache, options.SelectedMaterials[0], options.OutputPath);
        
        case OutputMode.SeparatedMultiple:
            return await InjectSeparatedMultipleAsync(cache, options.SelectedMaterials, options.OutputPath);
        
        default:
            throw new ArgumentException("Unknown output mode");
    }
}

private async Task<InjectionResult> InjectEmbeddedAsync(
    ModelWithMaterialsCache cache,
    MaterialVariant material,
    string outputPath)
{
    // 1. 合并模型和材质
    var combined = await CombineModelAndMaterialAsync(cache.Model, material);
    
    // 2. 写入单个patch文件
    await WritePatchAsync(combined, Path.Combine(outputPath, "armor.patch_0"));
    
    return new InjectionResult
    {
        Mode = OutputMode.EmbeddedSingle,
        ModelPatch = Path.Combine(outputPath, "armor.patch_0"),
        MaterialPatches = Array.Empty<string>()
    };
}

private async Task<InjectionResult> InjectSeparatedSingleAsync(
    ModelWithMaterialsCache cache,
    MaterialVariant material,
    string outputPath)
{
    // 1. 写入模型patch（不含材质或含基础材质）
    await WriteModelPatchAsync(cache.Model, Path.Combine(outputPath, "model", "armor.patch_0"));
    
    // 2. 写入材质patch
    var materialDir = Path.Combine(outputPath, material.Name);
    await WriteMaterialPatchAsync(material, Path.Combine(materialDir, "armor.patch_1"));
    
    return new InjectionResult
    {
        Mode = OutputMode.SeparatedSingle,
        ModelPatch = Path.Combine(outputPath, "model", "armor.patch_0"),
        MaterialPatches = new[] { Path.Combine(materialDir, "armor.patch_1") }
    };
}

private async Task<InjectionResult> InjectSeparatedMultipleAsync(
    ModelWithMaterialsCache cache,
    IReadOnlyList<MaterialVariant> materials,
    string outputPath)
{
    // 1. 写入模型patch
    await WriteModelPatchAsync(cache.Model, Path.Combine(outputPath, "model", "armor.patch_0"));
    
    // 2. 写入多个材质patch
    var materialPatches = new List<string>();
    for (int i = 0; i < materials.Count; i++)
    {
        var material = materials[i];
        var materialDir = Path.Combine(outputPath, material.Name);
        var patchPath = Path.Combine(materialDir, $"armor.patch_{i + 1}");
        
        await WriteMaterialPatchAsync(material, patchPath);
        materialPatches.Add(patchPath);
    }
    
    return new InjectionResult
    {
        Mode = OutputMode.SeparatedMultiple,
        ModelPatch = Path.Combine(outputPath, "model", "armor.patch_0"),
        MaterialPatches = materialPatches
    };
}
```

---

## 🧪 测试计划

### 测试1：无材质patch能否直接更新？

**步骤：**
```
1. 提取 Main/patch_1（模型，无材质）
2. 使用现有流程更新
3. 生成 new_model.patch_0
4. 测试：new_model.patch_0 + old_material.patch_56
```

**验证点：**
- [ ] 模型显示正常
- [ ] 材质加载正常
- [ ] 材质索引匹配
- [ ] 无错误或警告

**预期：**
- 可能成功（如果索引稳定）
- 可能失败（如果索引改变）

### 测试2：材质如何分离？

**输入：** 内嵌材质的 patch

**步骤：**
```
1. 提取模型+材质
2. 使用 SeparatedSingle 模式注入
3. 生成：model/patch_0 + material/patch_1
4. 测试加载
```

**验证点：**
- [ ] 模型patch不含材质（或含基础材质）
- [ ] 材质patch包含完整材质
- [ ] 游戏内显示正常

### 测试3：材质如何合并？

**输入：** 分离的 model.patch_0 + material.patch_1

**步骤：**
```
1. 提取模型+材质
2. 使用 EmbeddedSingle 模式注入
3. 生成：combined.patch_0
4. 测试加载
```

**验证点：**
- [ ] 单个patch包含模型和材质
- [ ] 游戏内显示正常
- [ ] 文件大小合理

---

## 🎯 实施优先级

### P0 - 立即测试（验证可行性）

1. **测试1：无材质patch更新**
   - 目的：验证材质索引是否稳定
   - 如果成功 → 简化实现
   - 如果失败 → 需要重新映射材质索引

2. **测试2：材质分离**
   - 目的：验证分离机制可行性
   - 学习如何正确引用外部材质

3. **测试3：材质合并**
   - 目的：验证合并机制可行性
   - 学习如何正确内嵌材质

### P1 - 基于测试结果实现

根据测试1的结果：
- **如果索引稳定** → 实现简化的更新流程
- **如果索引不稳定** → 实现材质索引重映射

### P2 - 完整功能实现

- 提取流程（Extract）
- 注入流程（Inject）
- 多材质变体支持

---

## 🤔 关键设计决策

### 决策1：材质索引如何处理？

**问题：** 更新模型后，材质索引会不会改变？

**需要测试验证：**
- 原版Unit的材质索引是固定的吗？
- 我们重构后的材质索引能保持一致吗？

**两种方案：**
- **方案A（乐观）：** 索引稳定，旧材质可以继续用
- **方案B（保守）：** 索引可能变，需要重新映射

### 决策2：patch编号规则

你说得对，我们不管理加载顺序，只管理文件名冲突：

**规则：**
- 模型：`patch_0`
- 第一个材质：`patch_1`
- 第二个材质：`patch_2`
- 以此类推

**目录结构：**
```
output/
├── model/
│   └── armor.patch_0
├── material_red/
│   └── armor.patch_1
└── material_blue/
    └── armor.patch_2
```

### 决策3：更新策略

**推荐：先测试策略A（只更新模型）**
- 简单
- 如果可行，最省事
- 如果不行，再实现策略B

---

**下一步：** 进行测试1，验证无材质patch能否直接更新！
