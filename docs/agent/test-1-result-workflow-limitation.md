# 测试1结果：发现现有流程的限制

**日期：** 2026-07-12  
**测试对象：** SailorMizu Chiffon 替换 DS-42 (Main/patch_1)

---

## 🔍 测试过程

### 输入文件
- **源patch：** `材质拆分版测试对象\【SailorMizu】Chiffon 替换 DS-42 (1)\Main\9ba626afa44a3aa3.patch_1`
- **大小：** 0.2 MB TOC + 16.7 MB GPU
- **类型：** 基础包（模型 + 基础材质）

### 执行命令
```bash
dotnet run --project AdaptationPatchSmoke -- 
  "D:\SteamLibrary\steamapps\common\Helldivers 2\data" 
  "...\Main\9ba626afa44a3aa3.patch_1" 
  ".\out\sailormizu-test" 
  --broad-coverage
```

> ⛔ 已过时（2026-11-08）：`AdaptationPatchSmoke` 这个工程在仓库里不存在（也无对应 csproj，从未入库），
> 所以上面这条命令今天跑不起来。它是 2026-07-12 的一次性实验壳；等效的诊断入口现在是
> `HD2ModCli`（`vpk2patch` / `vpk rekey`）与 `HD2ModAdaptation` 的测试工程。
> 下面的输出数字是当时那一次运行的取证，保留。

### 输出结果
- **生成文件：** 0.22 MB TOC + **0.02 MB GPU** ⚠️
- **transferUnits：** 0（没有任何转换）
- **skippedNoSourceUnits：** 32（所有 Units 都跳过）
- **所有 Units：** `sourceSameKey=False`, `mode=minify-only-no-source`

---

## ❌ 问题确认

### 现有流程的限制

**当前行为：**
1. 扫描源 patch 寻找 Unit
2. 尝试匹配目标 Unit 的 AssetKey
3. 如果找不到匹配 → 跳过 → 生成占位符

**对于材质分离 patch：**
- ❌ 无法识别或处理
- ❌ 没有进行任何转换
- ❌ 只生成了最小化占位符（0.02 MB）

### 根本原因

**可能的原因：**

#### 原因1：AssetKey 不匹配
- patch_1 包含 DS-42 的 Unit
- broad-coverage 默认只处理 CW-22/CW-36
- 即使扫描到了，也因为 key 不匹配而跳过

#### 原因2：patch 结构不同
- 材质分离的 patch 可能使用不同的结构
- 材质引用可能是外部的
- 现有流程假设材质是内嵌的

#### 原因3：读取逻辑不支持
- `PatchUnitMeshReader` 可能无法正确读取这种 patch
- 材质数据缺失导致读取失败
- 被当作"无效"patch 跳过

---

## 💡 解决方案设计

### 方案1：增强检测（推荐）

**你的建议：加强现有线路**

```csharp
// 1. 检测 patch 类型
var patchType = await DetectPatchTypeAsync(sourcePatchPath);

if (patchType == PatchType.SeparatedMaterial)
{
    // 2. 要求用户提供材质包
    var materialPatches = await RequestMaterialPatchesAsync();
    
    // 3. 合并处理
    var combined = await CombineModelAndMaterialsAsync(
        sourcePatchPath, 
        materialPatches);
    
    // 4. 使用合并后的数据进行转换
    return await ProcessAsync(combined);
}
else
{
    // 正常流程
    return await ProcessAsync(sourcePatchPath);
}
```

**优点：**
- ✅ 保持现有流程不变
- ✅ 明确要求用户提供材质
- ✅ 避免隐式假设

**实现步骤：**
1. 实现 `DetectPatchTypeAsync`（检测材质完整性）
2. 实现 `RequestMaterialPatchesAsync`（UI 交互）
3. 实现 `CombineModelAndMaterialsAsync`（临时合并）
4. 集成到现有流程

### 方案2：自动查找材质包

```csharp
// 自动在同目录查找材质包
var materialPatches = await FindMaterialPatchesAsync(
    Path.GetDirectoryName(sourcePatchPath));

if (materialPatches.Any())
{
    // 自动使用找到的材质包
    return await ProcessWithMaterialsAsync(sourcePatchPath, materialPatches);
}
```

**优点：**
- ✅ 用户体验好（自动化）
- ✅ 适合批量处理

**缺点：**
- ⚠️ 可能找错材质包
- ⚠️ 不够明确

### 方案3：支持材质引用（复杂）

直接修改核心流程支持外部材质引用

**缺点：**
- ❌ 改动太大
- ❌ 风险高
- ❌ 复杂度增加

---

## 🎯 推荐实施方案

### 采用方案1：增强检测 + 要求提供材质包

**Phase 2A：材质检测（P0）**

1. **实现 MaterialValidator**
   ```csharp
   public async Task<MaterialDetectionResult> DetectAsync(string patchPath)
   {
       // 检查 patch 是否包含完整材质
       // 返回：IsComplete, MissingMaterialIds
   }
   ```

2. **集成到 AdaptationPatchSmoke**
   ```csharp
   var detection = await materialValidator.DetectAsync(sourcePatchPath);
   
   if (!detection.IsComplete)
   {
       Console.WriteLine("⚠️ 检测到材质分离的 patch");
       Console.WriteLine($"缺少 {detection.MissingMaterialIds.Count} 个材质");
       Console.WriteLine("请提供材质包路径：");
       var materialPaths = ReadMaterialPathsFromUser();
       // ...处理
   }
   ```

3. **临时合并处理**
   ```csharp
   // 不修改原 patch，只在内存中合并
   var tempCombined = await CombineInMemoryAsync(model, materials);
   return await ProcessAsync(tempCombined);
   ```

**Phase 2B：UI 支持（P1）**

在 HD2ModManager 中：
- 自动检测材质分离
- 提示用户选择材质包
- 批量处理时记住选择

> ⛔ 已过时（2026-11-08）：这条"下一步"以**别的形状**做完了，没有走 `MaterialValidator` 这条路
> （该类型 0 命中，从未落地；`AdaptationPatchSmoke` 也已不存在）。现在的分工是：
> **转换段**按 planner 把「模型 + 它自己的材质包」分组成 patch 组，`sectional/` 里 unit patch 恒为 `patch_0`、
> 材质变体 `patch_1..N` 平铺 + `patches.json` 记号；**编译段**读 Mod 设置文件里的 `enabledMaterialPatches`
> 决定哪些材质物化进 `Overwrite/`。即"分离材质"是**链路事实**，不是运行时要向用户追问的缺失项。
> 见 `docs/agent/sectional-compile-base-20261108.md` §1.2 / §5。

---

## 📊 测试验证

### 验证点

**✅ 已验证：**
- [x] 现有流程不支持材质分离 patch
- [x] 会生成无效的占位符
- [x] 需要增强流程

**⏳ 待验证：**
- [ ] 材质检测逻辑是否准确
- [ ] 合并后能否正常处理
- [ ] 更新后材质索引是否兼容

### 后续测试

**测试2：材质检测**
- 实现 MaterialValidator
- 测试检测准确性

**测试3：合并处理**
- 实现临时合并逻辑
- 测试合并后的转换

**测试4：完整流程**
- 端到端测试
- 验证游戏内效果

---

## 📝 结论

### 发现的问题

1. **现有流程不支持材质分离 patch**
   - 会被跳过或生成占位符
   - 无法正常更新

2. **需要增强流程**
   - 检测材质完整性
   - 要求提供材质包
   - 临时合并处理

3. **方案已明确**
   - 方案1（增强检测）最可行
   - 保持现有流程稳定
   - 渐进式实现

### 下一步行动

**立即执行（P0）：**
1. 实现 MaterialValidator
2. 测试材质检测准确性
3. 设计临时合并逻辑

**后续执行（P1）：**
1. 集成到 AdaptationPatchSmoke
2. 实现 UI 交互
3. 端到端测试

---

**测试1成功完成！我们发现了真实问题并有了明确的解决方案。** ✅
