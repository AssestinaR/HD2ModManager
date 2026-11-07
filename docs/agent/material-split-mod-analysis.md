# 材质拆分版 Mod 结构分析

**案例：** 【SailorMizu】Chiffon 替换 DS-42  
**日期：** 2026-07-12  
**目的：** 理解实际的材质拆分模式，指导 Phase 2 材质管理功能设计

---

## 📁 目录结构

```
【SailorMizu】Chiffon 替换 DS-42 (1)/
├── Main/                           # 模型 + 基础材质
│   ├── 9ba626afa44a3aa3.patch_1              (0.2 MB)
│   ├── 9ba626afa44a3aa3.patch_1.gpu_resources (16.7 MB)
│   └── 9ba626afa44a3aa3.patch_1.stream        (0 MB)
│
├── 材质包/                         # 颜色变体目录
│   ├── Black/                      # 黑色变体
│   │   ├── 9ba626afa44a3aa3.patch_56              (0.01 MB)
│   │   ├── 9ba626afa44a3aa3.patch_56.gpu_resources (48.4 MB)
│   │   ├── 9ba626afa44a3aa3.patch_56.stream        (0.5 MB)
│   │   └── icon.png                                (1.94 MB)
│   ├── Blue/                       # 蓝色变体
│   │   ├── 9ba626afa44a3aa3.patch_56              (0.01 MB)
│   │   ├── 9ba626afa44a3aa3.patch_56.gpu_resources (48.4 MB)
│   │   ├── 9ba626afa44a3aa3.patch_56.stream        (0.5 MB)
│   │   └── icon.png                                (2.08 MB)
│   └── White/                      # 白色变体
│       ├── 9ba626afa44a3aa3.patch_56              (0.01 MB)
│       ├── 9ba626afa44a3aa3.patch_56.gpu_resources (48.4 MB)
│       ├── 9ba626afa44a3aa3.patch_56.stream        (0.5 MB)
│       └── icon.png                                (1.61 MB)
├── icon.png                        # Mod 预览图
└── manifest.json                   # Mod 清单
```

---

## 🔍 关键发现

### 1. 模型与材质组织方式

**模型 + 基础材质（Main 目录）：**
- 位置：`Main/`
- 包含：`9ba626afa44a3aa3.patch_1`（0.2 MB TOC + 16.7 MB GPU）
- 内容：完整的模型 Unit + 基础材质
- 这是必须安装的基础包

**颜色变体（材质包子目录）：**
- 位置：`材质包/Black/`, `材质包/Blue/`, `材质包/White/`
- 包含：`9ba626afa44a3aa3.patch_56`（0.01 MB TOC + 48.4 MB GPU + 0.5 MB Stream）
- 内容：替换材质的颜色变体
- 这是可选安装的换色包

**组织特点：**
- ✅ 基础包（patch_1）在 Main 目录
- ✅ 换色包（patch_56）在材质包子目录
- ✅ 玩家必须先安装 Main，然后选择一个颜色变体

### 2. 使用流程

**安装步骤：**
1. 安装 `Main/patch_1` → 获得完整模型和基础材质
2. 选择一个颜色变体（Black/Blue/White）
3. 安装对应的 `patch_56` → 替换为选定的颜色

**Patch 加载顺序：**
```
游戏启动
  ↓
加载 patch_1（基础模型和材质）
  ↓
加载 patch_56（颜色变体，覆盖材质）
  ↓
最终显示：模型 + 选定的颜色
```

### 3. Patch 编号规则

**patch_1（基础包）：**
- 编号较小
- 包含模型数据
- 必须安装

**patch_56（变体包）：**
- 编号较大
- 只包含材质替换
- 可选安装
- 后加载，覆盖 patch_1 的材质

**编号的意义：**
- 编号决定加载顺序
- 大编号覆盖小编号
- patch_56 的材质会覆盖 patch_1 的材质

### 4. 文件大小分析

**基础包（patch_1，在 Main 目录）：**
- TOC: 0.2 MB
- GPU: 16.7 MB
- Stream: 0 MB
- **总计：16.9 MB**
- **内容：** 模型几何数据 + 基础材质

**颜色变体（patch_56，在材质包子目录）：**
- TOC: 0.01 MB
- GPU: 48.4 MB
- Stream: 0.5 MB
- **总计：48.9 MB**
- **内容：** 完整的替换材质（高分辨率贴图）

**为什么变体更大？**
- ✅ 基础包包含模型数据（占用空间）
- ✅ 变体包只有材质，但是高质量完整贴图
- ✅ 颜色变体可能包含 Diffuse + Normal + Specular 等多张贴图
- ✅ 基础包可能只有简化材质或占位符

### 4. 预览图（icon.png）

每个颜色变体都有自己的预览图：
- Black: 1.94 MB
- Blue: 2.08 MB
- White: 1.61 MB

**用途：**
- 在 Mod 管理器中显示
- 帮助玩家选择喜欢的颜色

---

## 📋 对应我们文档中的场景

### 实际上是场景 C：共享材质的变体

**更准确的描述：**

```
Main/patch_1                     # 基础包（模型 + 基础材质）
├── Unit: Chiffon 模型
└── Materials: 基础材质（可能是默认颜色）

材质包/Black/patch_56           # 黑色变体
└── Materials: 黑色贴图（覆盖基础材质）

材质包/Blue/patch_56            # 蓝色变体
└── Materials: 蓝色贴图（覆盖基础材质）

材质包/White/patch_56           # 白色变体
└── Materials: 白色贴图（覆盖基础材质）
```

**加载机制：**
1. patch_1 提供模型和基础材质
2. patch_56（任选一个）覆盖材质
3. 最终效果：同一模型 + 不同颜色

**与场景 B 的区别：**
- 场景 B：模型和材质完全分离，模型不带材质
- 实际案例：模型带基础材质，变体覆盖材质
- 这是一种更实用的方式（fallback 到基础材质）

---

## 🎯 对 Phase 2 的启示

### 必须实现的功能

#### 1. Patch 依赖关系检测

```csharp
// 检测 patch_56 依赖 patch_1
var dependencies = await detector.DetectDependenciesAsync(
    @"材质包\Black\patch_56");

// 结果：
// - RequiresBasePackage: true
// - BasePackagePatchNumber: 1
// - DependencyType: MaterialOverride
```

#### 2. Patch 加载顺序验证

```csharp
// 验证安装顺序
var validation = await validator.ValidateInstallOrderAsync(
    new[]
    {
        @"Main\patch_1",      // 基础包
        @"材质包\Black\patch_56"  // 变体
    });

// 结果：
// - IsValid: true
// - LoadOrder: [patch_1, patch_56]
// - OverriddenMaterials: [材质ID列表]
```

#### 3. 变体冲突检测

```csharp
// 检测同时安装多个变体的冲突
var conflict = await detector.DetectConflictAsync(
    new[]
    {
        @"材质包\Black\patch_56",
        @"材质包\Blue\patch_56"
    });

// 结果：
// - HasConflict: true
// - ConflictType: SamePatchNumber
// - Message: "只能安装一个 patch_56 变体"
```

#### 4. 材质覆盖分析

```csharp
// 分析哪些材质被覆盖
var analysis = await analyzer.AnalyzeMaterialOverridesAsync(
    basePatch: @"Main\patch_1",
    variantPatch: @"材质包\Black\patch_56");

// 结果：
// - OverriddenMaterialIds: [0x1234, 0x5678, ...]
// - OverrideCount: 12
// - PreservedMaterialIds: [0x9abc, ...]
```

### 新的核心需求

#### 5. Patch 编号解析

**需要识别：**
- Patch 文件名中的编号（`_1`, `_56` 等）
- 编号决定加载顺序
- 编号相同会产生冲突

**实现：**
```csharp
public sealed class PatchNumberParser
{
    public PatchNumberInfo Parse(string patchPath)
    {
        // 从 "9ba626afa44a3aa3.patch_56" 提取 56
        var match = Regex.Match(Path.GetFileName(patchPath), @"\.patch_(\d+)$");
        
        return new PatchNumberInfo
        {
            Number = match.Success ? int.Parse(match.Groups[1].Value) : 0,
            CanCoexist = true // 不同编号可以共存
        };
    }
}
```

#### 6. 基础包与变体关系

**需要识别：**
- 哪个是基础包（通常编号小）
- 哪些是变体包（通常编号大）
- 变体依赖哪个基础包

**数据模型：**
```json
{
  "base_package": {
    "path": "Main/patch_1",
    "patch_number": 1,
    "type": "base",
    "contains": ["model", "base_materials"]
  },
  "variants": [
    {
      "path": "材质包/Black/patch_56",
      "patch_number": 56,
      "type": "material_variant",
      "requires_base": 1,
      "variant_name": "Black"
    }
  ]
}
```

#### 7. 冲突检测规则

**规则 1：同编号冲突**
- 同时安装 `材质包/Black/patch_56` 和 `材质包/Blue/patch_56` 会冲突
- 只能选择一个 patch_56

**规则 2：缺少基础包**
- 安装 `patch_56` 但没有 `patch_1` → 警告或错误

**规则 3：覆盖验证**
- patch_56 应该覆盖 patch_1 的材质
- 如果没有覆盖关系，可能配置错误

---

## 🔬 下一步分析

### 建议使用我们的工具分析

1. **扫描基础材质包（patch_1）**
   ```bash
   # 查看包含哪些材质资源
   PatchTocScanner scan "材质包\9ba626afa44a3aa3.patch_1"
   ```

2. **扫描颜色变体（patch_56）**
   ```bash
   # 查看 Black/Blue/White 的材质资源
   PatchTocScanner scan "材质包\Black\9ba626afa44a3aa3.patch_56"
   ```

3. **对比分析**
   - patch_1 和 patch_56 包含的材质 ID 是否相同？
   - 如果相同，说明是完整替换
   - 如果不同，说明是增量或部分替换

4. **材质 ID 分析**
   - 提取所有材质的 AssetKey
   - 与原版游戏对比，找出这些材质原本属于哪个 Unit

---

## 📝 总结

### 验证和纠正的认知

❌ **错误假设：** Main 目录为空，材质包是独立的
✅ **实际情况：** Main 包含基础包，材质包是覆盖变体

❌ **错误假设：** 材质和模型完全分离（场景 B）
✅ **实际情况：** 基础包带模型和材质，变体覆盖材质（场景 C 变种）

✅ **正确假设：** 多个颜色变体确实存在
✅ **正确假设：** 使用不同 patch 编号管理

### 核心发现

**组织方式：基础包 + 可选变体**
- Main/patch_1: 模型 + 基础材质（必须）
- 材质包/*/patch_56: 颜色变体（可选，互斥）

**加载机制：编号决定覆盖**
- 小编号先加载（patch_1）
- 大编号后加载并覆盖（patch_56）
- 同编号互斥（只能装一个）

**用户体验：基础 + 选择**
- 必须安装 Main（基础包）
- 选择一个颜色变体
- 变体提供预览图辅助选择

### 对设计的影响

**Phase 2 必须实现：**
1. ✅ Patch 编号解析和管理
2. ✅ 基础包与变体关系检测
3. ✅ 冲突检测（同编号互斥）
4. ✅ 依赖检测（变体依赖基础包）
5. ✅ 材质覆盖分析

**Phase 2 优先级调整：**

**P0 - 核心（必须）：**
- Patch 编号解析器
- 基础包与变体检测器
- 冲突检测器

**P1 - 重要（推荐）：**
- 材质覆盖分析器
- 依赖关系管理
- 变体元数据生成

**P2 - 增强（可选）：**
- 预览图提取
- 批量处理
- 自动修复建议

### 修正后的实施计划

**原计划：**
- 材质检测、识别、匹配（针对分离式）

**新计划：**
- Patch 编号管理（优先）
- 基础包与变体关系（优先）
- 冲突和依赖检测（优先）
- 材质覆盖分析（次要）

---

**结论：** 实际案例与预期不同，但提供了更有价值的洞察。基础包+变体的模式比完全分离更实用，我们的 Phase 2 应该优先支持这种模式！
