# 护甲映射页 2026-10-11 更新回归分析

> 范围：**只分析，不改任何生产代码**。
> 对比：`30a4f83`（20260928 发布）→ `HEAD`（20261011 发布）。
> 本文所有结论都标了证据来源；**未证实的一律标为"嫌疑"**，不当事实。
>
> **【2026-10-11 21:20 修正】** 用户按 §4.2 的复现办法实测（把 `asset-index.sqlite` 改名后启动），
> 结果**不是**"全未知护甲"，而是**启动即未处理异常崩溃**：
> `KeyNotFoundException: The given key '29533812' was not present in the dictionary.`
> 日志堆栈直指 `ApplySnapshot` **line 519**。据此本文已修正 §0 / 新增 §3.5 / 修正 §4 与 §9。
> **核心触发判断（护甲目录读到 0 条）被实测证实；但症状描述原先说错了，且真实缺陷比原先判断更严重。**

---

## 0. 结论摘要（先看这段）

1. **"护甲页卡" 是真修好了**：把"整包构建 GameData 归档浏览器"换成只请求 `ArmorCoverage` 一个事实。
   同一台机实测：**960 ms → 425/442 ms → 230 ms**。

2. **"未知护甲 {id}" 与本次改动的名称解析逻辑没有直接因果关系。**
   `RuntimeArmorRegistryService`（目录读取 / 协议解析 / 工作区解析 / 迁移守卫）在 `30a4f83..HEAD`
   区间内 **逐字节未变**；`ArmorRegistryPageViewModel.ApplySnapshot`（`Choices` 的唯一构建处）也**未变**。
   症状只可能来自一个地方：**护甲目录读到了 0 条**。

3. **一个根因，两种症状** —— 判据是"工作区里有没有 `map_model`（自定义素体映射）"：

   | 工作区内容 | 症状 | 例 |
   |---|---|---|
   | **有** `map_model` | **启动即未处理异常崩溃**（`KeyNotFoundException`） | 用户实测（本机 293 条 `map_model`） |
   | **无** `map_model`（全是 `map=`） | 优雅地全显示 `未知护甲 {id}` | 截图 B（`revision 5 · 14 条映射`，8 条"护甲视觉来源"） |

   机制：`ApplySnapshot` 的第一个循环用 `if (mapping.SourceItemId is not { } sourceItemId) continue;`
   **跳过所有模型映射**（模型映射的 `SourceItemId` 恒为 `null`），因此它们的 `TargetItemId`
   **永远不会被补上 `Unknown` 兜底**；随后的第二个循环却用**直接索引器** `Choices[mapping.TargetItemId]`
   取值（**line 519**）→ 目录里没有该 `ItemId` 时**抛异常**。

4. **本次更新把它从"点开护甲页才炸"变成"启动就炸"**（这才是"新版把自定义素体弄坏了"的观感来源）：
   护甲页的**视图**现在会在启动时被创建（`PrewarmArmorPageHost` + `GetOrCreateWorkspaceVisualPrewarmPages`），
   触发 `ArmorRegistryPageView.OnLoaded` —— 它是**没有 try/catch 的 `async void`**，
   而 `RefreshAsync` 的 catch 过滤器**不含 `KeyNotFoundException`** → 异常直接逃到全局处理器。

5. **本次更新还新增了另一条破坏性回归**：启动同步里**递归删除模组库目录**
   （`ModLibraryResidualCleanupService`），**没有任何迁移/首次运行保护**。
   → 这是截图 A 的 **"模型包部署失败，尚未编译护甲注册"** 的头号嫌疑。

6. **用户猜测的"忘记做旧版兼容 / 更新到新版（迁移）"**：
   工作区文件（`armor-registry.workspace.json`）与协议文件（`armor-registry.protocol`）的
   **兼容处理在本区间内没有任何改动**，所以**它不是本次回归的原因**。
   但**新增的残留清理确实完全没有迁移保护** —— 用户对"缺迁移"的直觉是对的，只是缺在另一处。

7. **仍未解决的关键问题**：**为什么那些用户的护甲目录会读到 0 条**（或某个 target 不在索引里）。
   用户实测只复现了"目录读不到"这一支；**这不是"文件被改名"能解释的现场成因**，见 §10。

---

## 1. 取证：提交范围

| 项 | 值 |
|---|---|
| 20260928 发布切点 | `30a4f83` "Fix runtime helmet mapping classification"，2026-09-28 19:03:56 |
| 30a4f83..HEAD | **43 个提交** |
| 非 ManagedMain 差异 | **181 文件，+10399 / -4012** |
| 与护甲页直接相关的提交 | 只有 2 个：`02f5423`（只请求覆盖事实）、`8c720c4`（预热 + 护栏） |

---

## 2. "到底哪些文件变了" —— 逐文件核对表

| 文件 | 区间内 | 对"护甲名称"的影响 |
|---|---|---|
| `HD2ModCore/Infrastructure/RuntimeArmorRegistryService.cs` | **未变** | 目录读取 / 协议解析 / 工作区解析 / 迁移守卫 **完全同版** |
| `HD2ModCore/Infrastructure/StoragePaths.cs` | **未变** | `DbPath` 位置同版 |
| `HD2ModManager/Services/SettingsService.cs` 的 `CreateStoragePaths()` | **未变** | `AppRoot` 仍是 `AppDomain.CurrentDomain.BaseDirectory` |
| `HD2ModCore/Infrastructure/AssetArchiveIndexService.cs` | **未变** | `game_data_authority_kits` 的**写入方**同版 |
| `HD2ModCore/Infrastructure/GameDataAuthorityProvider.cs` | **未变** | 护甲名/套装的来源同版 |
| `HD2ModCore/Infrastructure/Sqlite/SqliteSchema.cs` | **未变** | 表结构同版 |
| 发布包 `data/resources/*`（archivehashes / friendlynames / typehash / bonehash / asset-metadata-manifest） | **逐字节相同** | 索引指纹来源同版 |
| 发布包结构 | **相同**（都是 533 条目；都**不含** `data/indexes/`、**不含** `settings.json`） | 安装布局同版 |
| `HD2RuntimeArmorOverride/src/*.lua` 与 `HD2ModCore.csproj` 的 `EmbeddedResource` 清单 | **未变** | 运行时协议契约同版 |
| `HD2ModManager/ViewModels/ArmorRegistryPageViewModel.cs` | **+102 / -46** | **唯一实质变化** |
| `HD2ModManager/ViewModels/ShellViewModel.cs` | **+82** | 预热 + 取数闸门 |
| `HD2ModManager/Services/ModLibraryService.cs` | **+32 / -10** | **新增残留清理（会删除）** |

> 核对方法：`git diff --stat 30a4f83..HEAD -- <path>`；发布包用 Python `zipfile` 逐条比对 sha256。

**推论**：本次更新的代码**不可能**改变护甲目录的内容（写方同版、资源同版、表结构同版）。
唯一能变的是"**什么时候读**"。

---

## 3. `未知护甲 {id}` 的确切产生条件（代码证据）

```
ArmorRegistryPageViewModel.cs:1836
  public static RuntimeArmorChoiceViewModel Unknown(uint itemId)
      => new(itemId, itemId.ToString(...), $"未知护甲 {itemId}", isUnknown: true);
```

**只有一条路径能产生它**：

1. `Providers` / `AvailableArmors` / `ProviderDetailItems` 都由 `BuildDisplayChoices(ids)` 构建，
   而 `BuildDisplayChoices` 只做 `Choices.ContainsKey(id) ? Choices[id] : (缺席)`（`:1078-1084`）。
2. `Choices` 只在 `ApplySnapshot(rebuildChoices: true)` 里重建，来源**唯一**：`cachedCatalog`（`:471-483`）。
3. `cachedCatalog` 只在 `RefreshAsync` 里赋值，来源**唯一**：`service.ReadArmorCatalogAsync()`（`:435`）。
4. `ReadArmorCatalogAsync()`（`RuntimeArmorRegistryService.cs:101-134`）读 SQLite：
   ```csharp
   if (!File.Exists(paths.DbPath)) return Array.Empty<RuntimeArmorCatalogEntry>();
   ... SELECT ... FROM game_data_authority_kits WHERE kit_kind IN (0, 1) ...
   ```
   `paths.DbPath = <AppRoot>/data/indexes/asset-index.sqlite`。

⇒ **"未知护甲 {id}" = 索引库文件不存在（或该表没有这些行）。**
它不是"覆盖事实算错"，也不是"图标丢了"。

**本机反证（说明目录本身没问题）**：

| 索引 | 大小/时间 | `game_data_authority_kits` | 截图 2 的 8 个 ID |
|---|---|---|---|
| `bin/Debug/.../data/indexes/asset-index.sqlite` | Oct 11 20:36 | 411 行（kind 0:135 / 1:158 / 2:118） | **全部命中** |
| `bin/Release/.../data/indexes/asset-index.sqlite` | Sep 22 09:10 | 402 行（kind 0:132 / 1:155 / 2:115） | **全部命中** |

截图 2 的 8 个 ID 在两个索引里都查得到：
`1561165826`/`191298645` = TG-8"神枪手"（护甲/头盔）、`2366040666`/`4159107367` = O-3"自由之魂"、
`2756937165`/`3244366537` = TG-122"爆破兵"、`2890020275`/`2928638796` = AC-1"恪尽职守"。

⇒ **那些用户看到的"未知护甲"不是"目录里没有这条护甲"，而是"目录整体读到了 0 条"。**

---

## 3.5 【已实测证实】崩溃路径：`ApplySnapshot` 对模型映射没有兜底

### 3.5.1 复现

把 `<管理器目录>/data/indexes/asset-index.sqlite` 改名为 `asset-index1.sqlite` → 启动管理器。

### 3.5.2 实测结果（日志原文）

```
[21:19:12.124] INFO 工作区视觉预热完成：已布局真实配置页 / 模组库页，缓存视图=2。
[21:19:12.137] ERROR Unhandled UI exception: System.Collections.Generic.KeyNotFoundException:
               The given key '29533812' was not present in the dictionary.
   at System.Collections.Generic.Dictionary`2.get_Item(TKey key)
   at HD2ModManager.ViewModels.ArmorRegistryPageViewModel.ApplySnapshot(
        RuntimeArmorRegistrySnapshot snapshot, Boolean rebuildChoices, Boolean resetNavigation)
        in ...ArmorRegistryPageViewModel.cs:line 519
   at HD2ModManager.ViewModels.ArmorRegistryPageViewModel.RefreshAsync(Boolean isWarmUp)
        in ...ArmorRegistryPageViewModel.cs:line 403
   at HD2ModManager.ViewModels.ArmorRegistryPageViewModel.LoadCatalogOnceAsync(Boolean isWarmUp)
        in ...ArmorRegistryPageViewModel.cs:line 351
   at HD2ModManager.Views.ArmorRegistryPageView.OnLoaded(Object sender, RoutedEventArgs e)
        in ...ArmorRegistryPageView.xaml.cs:line 31
   at System.Threading.Tasks.Task.<>c.<ThrowAsync>b__124_0(Object state)
   at System.Windows.Threading.ExceptionWrapper.InternalRealCall(...)
[21:19:12.212] WARN 预热护甲映射页失败：The given key '29533812' was not present in the dictionary.
[21:19:15.990] ERROR Unhandled UI exception: KeyNotFoundException: ... '29533812' ...
   at ...ArmorRegistryPageViewModel.ApplySnapshot(...) ...:line 519
   at ...ArmorRegistryPageViewModel.RefreshRuntimeStatusAsync() ...:line 451   ← 第二次打开又炸
   at ...ArmorRegistryPageView.OnLoaded(...) ...ArmorRegistryPageView.xaml.cs:line 31
```

`29533812` = `AD-26"血刃科技"`，正是本机工作区里 `map_model=29533812,custom-cc267…` 的**目标护甲**。

### 3.5.3 逐步解释（每一跳都有行号对应）

1. **启动**：`MainWindow` 的视觉预热把护甲页视图放进 `PrewarmArmorPageHost`（新增），
   视图 `Loaded` → `ArmorRegistryPageView.OnLoaded`（**line 31**）`await viewModel.InitializeAsync()`。
2. `InitializeAsync()` → `EnsureCatalogLoadedAsync(false)` → `LoadCatalogOnceAsync(false)`（**line 351**）
   → `RefreshAsync(false)`。
3. `RefreshAsync` 读目录得到 **0 条**（`asset-index.sqlite` 不在），
   `hasLoadedCatalog = true`（**line 402**），然后调 `ApplySnapshot(..., rebuildChoices: true, ...)`（**line 403**）。
4. `ApplySnapshot`（**line 471-483**）用空目录重建 `Choices` → `Choices` 里只有后面补进来的项。
5. **第一个循环（line 485-492）跳过模型映射**：
   ```csharp
   foreach (var mapping in snapshot.Document.Mappings)
   {
       if (mapping.SourceItemId is not { } sourceItemId) continue;   // ← 模型映射的 SourceItemId 恒为 null → 直接跳过
       if (!Choices.ContainsKey(mapping.TargetItemId)) Choices[mapping.TargetItemId] = Unknown(...);
       ...
   }
   ```
   模型映射由 `ParseProtocol` 构造成 `new RuntimeArmorRegistryMapping(modelTarget, null, targetModelKey)`
   —— **`SourceItemId == null`**，所以 `29533812` **永远拿不到 `Unknown` 兜底**。
6. **第二个循环（line 512-529）用直接索引器取值**：
   ```csharp
   if (mapping.IsModelMapping)
   {
       ...
       Mappings.Add(new RuntimeArmorMappingRowViewModel(
           Choices[mapping.TargetItemId],        // ← line 519：KeyNotFoundException
           Choices[SyntheticModelItemId(model.Key)], model.Key));
   ```
7. **异常为什么变成"未处理错误"**：
   - `RefreshAsync` 的 catch 过滤器是 `IOException or UnauthorizedAccessException or InvalidDataException
     or InvalidOperationException` —— **`KeyNotFoundException` 不在其中**，直接穿透。
   - `ArmorRegistryPageView.OnLoaded` 是 **`async void` 且没有 try/catch** → 异常逃到
     `Task.ThrowAsync` → 全局 UI 未处理异常处理器 → "管理器遇到未处理错误，详情已写入logs"。
   - 同一时刻的 `QueueArmorRegistryPrewarm` 那条路径**有** try/catch，所以只留下一条 `WARN 预热护甲映射页失败`。
     两条路径共用同一个 `catalogLoadTask`，**先到的那条（视图）决定谁炸**。
8. **为什么第二次打开还炸**：`ApplySnapshot` 在 **line 519 抛错**，所以它后面的
   `fingerprint = snapshot.Fingerprint;`（**line 556**）**从未执行** → `fingerprint` 仍是旧值
   → 下次 `RefreshRuntimeStatusAsync`（**line 451**）判断 `fingerprint != snapshot.Fingerprint` 成立
   → 又调 `ApplySnapshot(rebuildChoices: hasLoadedCatalog /* true */)` → 再炸。
   所以**不重启就无法恢复**（与实测一致：改名回去 + 重启才恢复）。

### 3.5.4 这条缺陷是不是本次引入的？

**不是。** `ApplySnapshot` 与 `RefreshAsync` 的 catch 过滤器在 `30a4f83..HEAD` 区间内**都未改动**，
`ArmorRegistryPageView.OnLoaded` 也未改动。⇒ **旧版同样会炸，只是要等用户手动点开护甲页。**

**本次改变的是"什么时候炸"**：

| | 旧版（30a4f83） | 新版（HEAD） |
|---|---|---|
| 护甲页视图在启动时创建？ | ❌ 否（`GetOrCreateWorkspaceVisualPrewarmPages` 只返回 Profile/Library） | ✅ 是（新增 `PrewarmArmorPageHost`，三元组加了 Armor） |
| 目录读到 0 条时 | 用户点开护甲页 → 崩 | **启动即崩**，用户什么都没做 |

⇒ 用户看到的是"**更新后管理器一打开就报未处理错误**"，自然会归因到新版本。

---

## 4. 为什么"目录读到 0 条"会变得容易命中（时机问题）

### 4.1 目录首次读取的时机被前移

```csharp
// :334  首次打开：只在"从未加载过"时才做完整加载
public Task InitializeAsync() => hasLoadedCatalog ? RefreshRuntimeStatusAsync() : EnsureCatalogLoadedAsync(isWarmUp: false);

// :399-403  RefreshAsync 尾部
var inputs = isWarmUp ? await Task.Run(LoadCatalogInputsAsync) : await LoadCatalogInputsAsync();
if (inputs is not { } loaded) return;      // 只有"读失败"才返回
cachedCatalog = loaded.Catalog;            // ← 不检查 loaded.Catalog.Count == 0
cachedArmorDisplayFacts = loaded.ArmorDisplayFacts;
hasLoadedCatalog = true;                   // ← 空目录也置 true
ApplySnapshot(loaded.Snapshot, rebuildChoices: true, resetNavigation: true);
```

- 全仓 `hasLoadedCatalog` 只有 5 处引用（`:69/334/339/402/451`），**没有任何复位路径**；
  唯一"复位"= 重启进程。
- 全仓**没有**任何"目录为空"的判据或状态（`grep` 无命中）。

`ShellViewModel.cs:419-426`（新增的 `finally`）：

```csharp
finally
{
    _armorDisplayFactsGate.TrySetResult();   // 放行取数闸门
    QueueArmorRegistryPrewarm();             // 立刻预热（ApplicationIdle）
}
```

| | 旧版（30a4f83） | 新版（HEAD） |
|---|---|---|
| 目录首次读取时机 | **只在用户点开护甲页时** | **启动维护一结束就自动读**（`QueueArmorRegistryPrewarm`），另有 `QueueWorkspacePagePrewarm` 与视觉预热触发 `InitializeAsync` |
| 首次读取时索引库几乎总已存在 | ✅ 是 | ❌ 不保证 |

⇒ **任何"启动时索引库还不在"的用户**都会命中：新解压到**新目录**（`data/indexes/` 随安装目录走）、
索引被清理、从未建立、首次使用。

### 4.2 判别方法（不改代码即可做）

1. 看 `<管理器目录>/data/indexes/asset-index.sqlite` **是否存在**，以及
   `SELECT count(*) FROM game_data_authority_kits` 是否 > 0。
   - 不存在 / 为 0 → 命中本类。
   - 存在且含护甲行 → **不一定是"目录读不到"**，转 §10 的"个别 target 不在索引里"分支。
2. 看工作区里有没有 `map_model=`：有 → 症状是**崩溃**；没有 → 症状是**全"未知护甲"**。
3. 日志里搜 `Unhandled UI exception` / `预热护甲映射页失败` / `模组库残留清理`。
4. 复现（本机已验证）：把 `asset-index.sqlite` 改名 → 启动 → **应出现未处理错误** →
   改名回去 → 重启 → 恢复。（用户实测：完全吻合。）

---

## 5. 回归 #2：启动期递归删除模组库目录（全新破坏性行为，零迁移保护）

### 5.1 代码

`HD2ModManager/Services/ModLibraryService.cs`（区间内新增，在 `SynchronizeAsync` 内、落盘快照之前）：

```csharp
var residualCleanup = CoreServices.CreateModLibraryResidualCleanupService()
    .Cleanup(_snapshot, _paths.ModsDirectory, cancellationToken);
if (residualCleanup.RemovedCount > 0)
    LogService.Info($"模组库残留清理：已删除 {residualCleanup.RemovedCount} 个无 Patch、无有效外部资源且未被库引用的残留目录。");
```

`HD2ModCore/Infrastructure/ModLibraryResidualCleanupService.cs`：对 `ModsDirectory` 的**直接子目录**，
满足以下**全部**条件就 `Directory.Delete(directory, recursive: true)`：

1. 不是 `.hd2modmanager-trash`；
2. **不在**同步后 `snapshot.Nodes` 的 `RelativePath` 集合里，也不是某个节点路径的**父目录**；
3. `IsSafeResidual` 为真：
   - 目录内（递归）**一个文件都没有** → 真；**或**
   - 目录内**每一个**文件都算"管理器元数据"：`logical marker` / `option-relation` / `manifest.json` / `*.tmp` / `icon.*`
     —— 且没有任何能被 `PatchFileNameParser` 识别的 patch、也没有 `ExternalInstallFileFactsService` 认得的外部安装文件。

### 5.2 风险点

| # | 风险 | 说明 |
|---|---|---|
| a | **完全没有迁移/首次运行保护** | 这是**新装就能跑**的破坏性操作，不是升级才有的；用户升级后第一次启动同步就会执行 |
| b | 路径规范化口径不同 | `Normalize` 只把 `\` 换成 `/` 并 trim `/`；不处理 `./`、`../`、尾部空格。`RelativePath` 与 `Path.GetRelativePath` 若口径不一致，"已被引用的目录"可能被判成"未被引用" |
| c | 只含 `manifest.json` + `icon.png` 的"壳目录" | 会被判为纯元数据 → 删除（**这是设计意图，但对自定义素体壳目录是危险的**） |
| d | 删除不可撤销 | 直接 `Directory.Delete`，**没有走回收站 / 没有走 `.hd2modmanager-trash`**（对比 `StoragePaths.DeletionTrashDirectory` 的存在） |
| e | 保守的一面 | `PatchFileNameParser` 不认识的文件名会让 `IsManagerMetadata` 返回 false → **保留**。所以"有载荷但名字不标准"的目录是安全的 |

### 5.3 与用户症状的对应

`ArmorRegistryPageViewModel.ApplyAsync`（`:1519-1524`）在应用**含自定义素体**的映射前会先部署：

```csharp
if (!IsGameRunning() && Enabled && desired.Any(mapping => mapping.IsModelMapping))
{
    var deployment = await deploymentCoordinator.FlushAsync();
    if (deployment.Stage != ProfileDeploymentStage.Completed)
        throw new InvalidOperationException(deployment.Message ?? "模型包部署失败，尚未编译护甲注册。");
}
```

**截图 1 的两条通知（20:40:31 / 20:41:00）就是这个 `throw`。**
若用户的素体 Mod 目录被残留清理删掉/内容被删，`FlushAsync()` 必然不能到 `Completed`。

⇒ 回归 #2 是"**部分用户的自定义素体出现了问题**"的**头号嫌疑**，也是截图 1 的直接解释。
（"未知护甲 / 启动崩溃"由 §3.5 / §4 解释；两者是**两个独立问题**，出现在不同用户身上完全正常。）

---

## 6. 用户猜测的"旧版兼容 / 更新到新版"：本区间**未改动**，故不是本次回归的原因

以下全部位于 `RuntimeArmorRegistryService.cs`（区间内**未变**），因此 **20260928 与 20261011 行为完全一致**：

| 位置 | 行为 |
|---|---|
| `ParseProtocol` `:615-617` | `schema` 必须是 1 或 2；缺 `revision`/`enabled` 抛错 |
| `ParseProtocol` `:618-619` | `schema == 1 && models.Count > 0` → `InvalidDataException("schema=1 不支持模型素体。")` |
| `ReadWorkspaceAsync` `:391` | `JsonUnmappedMemberHandling.Disallow` —— 工作区 JSON 出现**未知字段**直接失败 |
| `ReadWorkspaceAsync` `:411-413` | `schema` 必须是 1；`compositionMode` 必须是 `full` 或 `existing_only` |
| `ReadSnapshotAsync` `:233-238` | `workspaceBelongsToCurrentLibrary` —— 部署状态文件里的 `workspace=` 与当前 `WorkspacePath` 不一致时，退化为"从协议反推工作区" |
| `ReadSnapshotAsync` `:261-268` | "老线格式"兼容：协议里存在 8 段 `model_piece=` 就视为**待应用**（`:263` 的注释原文 *"Keep Apply available after upgrading without requiring a dummy edit."*） |

**关键时间线**：`schema=2`（自定义素体）由 `399439e`（2026-09-25 00:09）引入，
`6dda1c4`（2026-09-25 22:24）优化增量映射 —— **都早于 20260928 发布**。
⇒ 20260928 里**已经有**同样的迁移缺口。**本次更新没有让它变好，也没有让它变坏。**

> 但请注意一个**真实存在的、方向相反**的兼容问题：
> **新版本写出的工作区/协议，旧版本不一定读得回来。**
> 用户"回退到上一版"这个动作本身，可能让他们面对"旧版读不了新版写的工作区"。
> 这不是本次故障的原因，但会**放大**回退后的混乱。

---

## 7. 其它值得记录的观察

1. **本机的"未下发"状态**：
   - `%LOCALAPPDATA%/CowboyBingus/Helldivers2/ArmorRegistry/armor-registry.protocol`
     = **Oct 11 20:42**，`schema=2`，`revision=92`，`enabled=true`，`model=` 1 条，`map_model=` **293** 条。
   - `runtime.status` = **Oct 2 19:09**，`revision=90`，`state=ready`，`pid=14140`，`active_mappings=293`。
   - ⇒ 本机最近两次改动（rev 91/92）**从未下发到游戏**；游戏自 Oct 2 起没启动过。
     `refresh-auto-14140.log` 也是 Oct 2。**这与截图 1 的失败通知是一致的。**

2. **护甲覆盖事实本身是成功的**（本机日志）：
   `信息中心诊断：类型=GameDataIndex ... 状态=Fresh，缓存命中=False，耗时=173~438ms`，
   20:36 那次 `ArmorCoverage` 生产 **230 ms**；日志里**没有** `护甲覆盖事实失败` / `预热运行时护甲注册失败`。
   ⇒ "未知护甲"**不是**覆盖事实生产失败造成的。

3. `ShellViewModel` 的 `finally` 顺序**正确**：先 `_armorDisplayFactsGate.TrySetResult()`，再 `QueueArmorRegistryPrewarm()`。
   注释也说明了原因（库快照与启动部署必须先落定）。这一处没有问题。

4. **一处潜在隐患**：`RefreshRuntimeStatusAsync`（`:444-446`）**检查** `IsBusy` 但**从不设置**它，
   因此它可以与 `RefreshAsync` 并发执行，两者都会调 `ApplySnapshot`。
   当前唯一调用点（`:334`）保证 `rebuildChoices == true`，所以暂时不致命；
   但一旦有人在"未加载完成"时调用它，`ApplySnapshot(rebuildChoices: false)` 会拿**空的** `Choices`
   给每条映射填 `Unknown` —— 正是"全部未知护甲"的另一个入口。

5. **`settings.json` 有两个**：
   - 管理器设置 = `<管理器目录>/settings.json`（`SettingsService` 私有常量）
   - `StoragePaths.SettingsPath` = `<AppRoot>/data/settings.json`
   `GameDataAuthorityProvider` 的 **locale** 从**后者**读，而后者**不在发布包里、管理器也不写它**
   → locale 恒为默认值（本机 Debug 索引里是中文名，说明这条链路当前无害）。
   这是**既有的**环境差异，不是本次引入。

6. **`GameDataLocator` 被重写，但不是本次回归的原因**：
   生产调用点只有 `SettingsService.TryDetectAndSetGameDataFolder()`（且**只在 `GameDataFolder` 为空时才写**，
   见 `ShellViewModel.cs:1064-1071`、`:396-397`）与 CLI 的 `game locate`。
   ⇒ **不会覆盖用户已有设置**；索引构建走的是 `SettingsService.GetGameDataFolder()`。

7. **`data/` 目录随安装目录走**：`StoragePaths.DbPath` 相对 `AppDomain.CurrentDomain.BaseDirectory`。
   两个发布包都**不含** `data/indexes/`，也不含 `settings.json`。
   ⇒ **用户把新版解压到新目录 = 索引与设置一起"消失"**。这是 §10 分支 A1 最可能的触发场景。

---

## 8. 修复方向（本轮**不实施**，仅记录）

按"会不会丢数据 / 会不会崩"排序：

| 优先级 | 针对 | 方向 |
|---|---|---|
| **P0** | 崩溃本身 | **`ApplySnapshot` 不许对 `Choices` 用直接索引器。** 把 line 519 改成 `Choices.TryGetValue(mapping.TargetItemId, out var target) ? target : Unknown(mapping.TargetItemId)`（并同样处理 line 521 的合成 ID）。这一条与"目录为什么是空的"**完全独立**，无论根因是什么都必须修 |
| **P0** | 崩溃外溢 | `ArmorRegistryPageView.OnLoaded` 的 `async void` 必须包 try/catch；或让 `RefreshAsync` 的 catch 过滤器包含 `KeyNotFoundException`（更稳的做法是"任何异常都不该从 UI 事件里逃出去"） |
| **P1** | 症状不可读 | 把"目录 0 条"与"读失败"同等对待：不要置 `hasLoadedCatalog`；或引入显式状态 `CatalogState { Unavailable, Empty, Loaded }`，`Empty` 时允许重试 |
| **P1** | 时机 | 预热前先问 `IAssetArchiveIndexService.IndexExistsAsync()`；索引不存在就**不预热**，改为"索引建成后触发一次预热" |
| **P1** | 症状不可读 | 页面给一个显式的"未索引 / 索引过时"空状态，而不是渲染成 `未知护甲 {id}` 或崩溃 |
| **P0** | 残留清理 | 加**首次运行 / 迁移保护**（例如必须曾经见过该路径、或写入过一次 marker 之后才允许删）；把删除清单写进日志并通知用户；**至少降级为移动到 `.hd2modmanager-trash`** 而不是 `Directory.Delete` |
| **P2** | 并发 | `RefreshRuntimeStatusAsync` 与 `RefreshAsync` 之间补上互斥（补 `IsBusy = true`，或共用同一把锁） |

> 注意 P0 第一条的性质：它是**防御性修复**。即使我们最终查明"目录为空"的现场成因并堵住它，
> `ApplySnapshot` 仍然会因为任何**个别** `ItemId` 不在目录里而崩溃（见 §10 分支 B）。

---

## 9. 一句话回答用户的问题

- **"为啥点开来自定义素体变成未知护甲了？"**
  → 护甲目录读到了 **0 条**（最直接的原因：`data/indexes/asset-index.sqlite` 不在）。
  然后分两种：
  **工作区里没有 `map_model`** → 优雅地全显示 `未知护甲 {itemId}`（截图 B 那台）；
  **有 `map_model`（即真的配了自定义素体）** → `ApplySnapshot` line 519 直接抛
  `KeyNotFoundException`，而视图的 `async void OnLoaded` 没有 try/catch → **启动即"未处理错误"**。
  **重启管理器**（在索引建好之后）即可恢复。

- **"是不是更新时忘了做旧版兼容 / 迁移？"**
  → 工作区与协议格式的兼容代码**这次一行没改**（所以不是它）。
  但**这次新增的"启动期残留清理会递归删除模组库目录"完全没有迁移保护** ——
  这条更危险，且更可能是"自定义素体坏掉 / 模型包部署失败"的原因。

---

## 10. 仍未解决的关键问题（下一轮要查的）

崩溃路径已经**证实**，但"**目录为什么会是空的**"仍未有现场证据。用户实测只证明了
"目录读不到 → 崩溃"这条因果，**没有解释现场成因**。候选分支：

**分支 A：目录整体读到 0 条**
- A1 `data/indexes/asset-index.sqlite` **不存在**（新解压到新目录、被清理、从未建立）。
- A2 文件存在但 `game_data_authority_kits` **为 0 行**（索引建到一半失败、或建索引时 authority 加载失败）。
- A3 `ReadArmorCatalogAsync` 抛异常被吞掉（当前它**不吞异常**，所以可能性低）。

**分支 B：目录正常，但**某个** `map_model` 的 target 不在索引里**
- B1 目标护甲来自**更新的游戏版本 / DLC**，而用户的索引是旧的（本机 Release 索引 402 行 vs Debug 411 行，
  说明索引内容会随构建变化）。
- B2 目标是"已下架 / 改名"的 item。
- ⚠️ 这一支**不需要目录为空**，也能产生一模一样的崩溃 —— **目前无法排除，且它更容易解释"只有部分用户"**。

**下一轮的最小取证清单（找一位出问题的用户，只取文件不改代码）**：
1. `<管理器目录>/data/indexes/asset-index.sqlite` 是否存在、大小、`count(*) from game_data_authority_kits`。
2. `<模组库>/.hd2modmanager/armor-registry.workspace.json` 里的 `map_model` 条目数与 target 列表。
3. `logs/manager_YYYYMMDD.log` 里 `Unhandled UI exception` 那一行**括号里的数字** ——
   它就是缺失的 `ItemId`；拿它去 1 里的索引查一次，就能直接判定是分支 A 还是分支 B。
4. `logs/` 里是否有 `模组库残留清理：已删除 N 个…`（回归 #2 是否真的发生过）。

> 第 3 条是最关键的一步：**异常消息自带缺失的 ItemId**，等于现场自动留下了判定依据。

---

## 11. 代码层面的成因清单（2026-10-11 22:10 补充；用户未再回复，改为纯代码追溯）

> 本节不依赖现场证据，全部是从代码里读出来的**可达路径**，每条都带行号。

### 11.1 【新发现 · 头号候选】协议在 `%LocalAppData%`，守卫却在 `data/` —— 换个目录解压就中

这是本轮最重要的发现。它能**同时**解释"只有部分用户""只有新版才炸""症状还分两种"。

两个文件的生命周期完全不同：

| 文件 | 路径 | 解压到新目录后 |
|---|---|---|
| `armor-registry.protocol`（**装着全部 `map_model`**） | `%LocalAppData%\CowboyBingus\Helldivers2\ArmorRegistry\` | **还在**（与程序目录无关） |
| `runtime-armor-registry.deployment`（**唯一的归属守卫**） | `<AppRoot>\data\` | **没了**（跟着程序目录走） |

`RuntimeArmorRegistryService.ReadSnapshotAsync`（:227-244）：

```csharp
var protocolExists = File.Exists(protocolPath);
var deployment = await ReadKeyValueFileAsync(deploymentPath, ct);
var workspaceBelongsToCurrentLibrary = !deployment.Exists            // ← 新目录里 deployment 不存在
    || !deployment.Values.TryGetValue("workspace", out var deployedWorkspace)
    || string.Equals(Path.GetFullPath(deployedWorkspace), Path.GetFullPath(WorkspacePath), OrdinalIgnoreCase);
var document = File.Exists(WorkspacePath)
    ? await ReadWorkspaceAsync(ct)
    : protocolExists && workspaceBelongsToCurrentLibrary              // ← 守卫被绕过
        ? ParseProtocol(await ReadBoundedTextAsync(protocolPath, …))  // ← 于是采用了旧协议
        : new RuntimeArmorRegistryDocument(0, false, Array.Empty<RuntimeArmorRegistryMapping>());
```

而 `armor-registry.protocol` **全仓没有任何一处删除它**（只有 :363 `File.Move(..., overwrite: true)` 覆盖写）。

**完整因果链：**

1. 用户把新版**解压到一个新文件夹**（而不是覆盖旧目录）→ 新 `AppRoot` 里 `data/` 是空的。
2. `data/indexes/asset-index.sqlite` **不存在** → `ReadArmorCatalogAsync` :104 直接 `return Array.Empty<>()` → 目录 0 条。
   ⚠️ **索引不会自动重建**：设置页原文就是"索引过期或缺失；不会自动重建，请确认后手动启动重建"，
   `CheckGameDataIndexOnStartupAsync`（:468-504）也确实**只检查、不重建** → 这个状态会**一直持续**。
3. `data/runtime-armor-registry.deployment` 同样不存在 → `workspaceBelongsToCurrentLibrary = true` → **守卫失效**。
4. `<新 mods 目录>/.hd2modmanager/armor-registry.workspace.json` 不存在（新目录）→ 于是走 `ParseProtocol`。
5. 旧协议里带着 `map_model` → 快照里就有这些模型映射。
6. `ApplySnapshot` 第一循环跳过模型映射 → 第二循环 `Choices[target]`（**:520**）→ `KeyNotFoundException`。
7. 本次更新把护甲页**视图**提前到启动创建（`PrewarmArmorPageHost`）→ **用户什么都没做就崩**。

**"只有部分用户"的判据**：必须同时满足
(a) 以前点过"应用"（协议存在，且里面有 `map_model`），**并且**
(b) 解压到新目录 / `data/` 丢失 / 索引从未建立。
只满足 (a) 而目录完好 → 不炸；只满足 (b) 而没有 `map_model` → 全"未知护甲"（= 截图 B）。

### 11.2 分支 A 的其他可达路径（目录整体 0 条）

- **A1** `DbPath` 不存在 → :104 空数组。除"新目录"外还有：杀毒隔离、用户手删 `data/`、
  装在 U 盘/网络盘后盘符变化。
- **A2** 文件在但表为空：`SaveAuthoritySnapshotAsync` 的删除+插入**在同一个事务里**
  （:330 `BeginTransactionAsync` → :332 `ClearIndexDataAsync`），所以**正常重建期间读不到空表**；
  但重建中途失败回滚、或从未成功建立过，表就是空的。
- **A3** 建索引时 `GameDataFolder` 为空/错 → 本次重写的 `GameDataLocator` **要求 `data` 目录真实存在**、
  只探测已知库根、**不递归** → 非 Steam / 自定义安装位置的用户可能**探测失败** →
  `GameDataFolder` 一直为空 → `CheckGameDataIndexOnStartupAsync` :476 直接 `return`
  → 索引永远不会被建、也不会被修 → **长期停在"目录 0 条"**。这条会**放大** A1。
- **A4** 旧版本留下的 `asset-index.sqlite` 缺 `game_data_authority_kits` 表 → `SqliteException`。
  ✅ 核对后**排除外溢**：`RefreshAsync` 的 catch 过滤器（:405-409）**已包含 `Microsoft.Data.Sqlite.SqliteException`**，
  只会显示"读取失败"，不会崩。

### 11.3 分支 B 的可达路径（目录非空，但**某个** target 缺失）

- **B1** 协议/工作区来自**另一台机器或另一个模组库**被顺手拷过来（协议在 LocalAppData、工作区在模组库，
  两者都可能被单独搬运）。
- **B2** 目标来自**更新的游戏版本 / DLC**，而用户的索引是旧的 —— 索引内容确实会随构建/游戏版本变化
  （本机 Release 402 行 vs Debug 411 行）。
- **B3** `WHERE kit_kind IN (0, 1)` 把 `kit_kind = 2`（"其它"）过滤掉。
  ✅ 核对后**基本排除**：UI 的目标候选只来自 `Choices`，而 `Choices` 只由该查询的结果构建（:471-483），
  正常路径下用户**选不到** `kit_kind = 2` 的目标。
  ⚠️ 唯一残留口子：第一循环补进 `Choices` 的 `Unknown` 项会**留在** `Choices` 里，
  若之后把它选成新映射的目标，就会造出一个"天生不在目录里"的目标。概率低，但代码上成立。

### 11.4 已核对并**排除**的怀疑

- **索引重建与护甲页抢跑**：重建的删除+插入在**同一事务**内，且 `CheckGameDataIndexOnStartupAsync`
  **只检查不重建** → 排除"启动时读到空表"。
- **残留清理误删 `.hd2modmanager` 或 `data`**：`IsSafeResidual` 要求目录里**每一个**文件都是"管理器元数据"
  （logical marker / option-relation / `manifest.json` / `*.tmp` / `icon.*`）；
  `armor-registry.workspace.json` 不在其中 → 该目录被判为"有载荷" → **不会删**。
  且 `Cleanup` 只遍历**模组库根的直接子目录**，够不到程序目录下的 `data/`。**排除**。
- **`RefreshRuntimeStatusAsync` 拿着空 `Choices` 提前跑**：全仓只有 `InitializeAsync` :334 调用它，
  而 :334 用 `hasLoadedCatalog ? … : EnsureCatalogLoadedAsync(...)` 设了闸门，
  `EnsureCatalogLoadedAsync` 又是单飞（:341-345）→ **排除**。
- **工作区/协议格式的兼容迁移**：区间内一行未改（见 §6）。

### 11.5 由此得到的修复优先级（仍不改代码，仅记录）

| 优先级 | 位置 | 改法 |
|---|---|---|
| **P0（真正的根因）** | `ReadSnapshotAsync` :233-244 | 归属守卫**不能只依赖 `data/` 里的文件**。现在"`deployment` 不存在就放行"等于"新目录 = 无主协议一律采用"。应改为：**归属不明或协议过旧时不采用协议里的映射**；至少"目录读到 0 条"时拒绝采用。 |
| **P0（兜底，与根因无关）** | `ApplySnapshot` :520 / `OnLoaded` :31 | `Choices` 一律 `TryGetValue` + `Unknown` 兜底；`async void OnLoaded` 包 try/catch。两条都能把"启动即崩"降级为"显示未知护甲"。 |
| **P1** | `RefreshAsync` :399-403 | `catalog.Count == 0` 时**不要**置 `hasLoadedCatalog = true`，并给出可操作提示（"索引缺失，请到设置页建立资产索引"），而不是静默进入"全未知 / 崩溃"。 |
| **P1** | 护甲页 / 设置页 | 把"`asset-index.sqlite` 缺失"与"解压到新目录"绑定提示；或在目录为空时**不加载映射**。 |

---

## 12. 已落实的修复（2026-10-11 22:5x；仅 P0 兜底两条，根因未动）

用户授权："如果是纯正面修复，那么我们可以直接落实，即使主要问题不是这个，我们也提前修了一个可能暴露的问题。"

按这个标准逐条评估后，**只落实了与根因无关的两条兜底**（§11.5 表里的第二行），**根因那条（归属守卫）没有动** —— 理由见 §12.3。

### 12.1 改了什么

**文件：`HD2ModManager/ViewModels/ArmorRegistryPageViewModel.cs`（+35 / −4）**

1. **`ResolveChoice(uint itemId)`（新增私有方法）** —— 把"映射引用的护甲 id"解析成可显示项，**永不抛**：
   - 命中 `Choices` 直接返回；
   - 未命中 → 记一条 `LogService.Warn`，用 `RuntimeArmorChoiceViewModel.Unknown(itemId)` 兜底，**并把它补进 `Choices`**（所以同一个 id 只会记一条 WARN，也顺带保住"每个 ItemId 至多一个 VM"的不变式）。

2. **`ApplySnapshot` 里 4 处裸索引器读取全部改走 `ResolveChoice`**（原 :520/:521 与 :527 的读取位置）：

   | 原写法 | 现写法 | 原来为什么没炸 |
   |---|---|---|
   | `Choices[mapping.TargetItemId]`（模型映射行） | `ResolveChoice(mapping.TargetItemId)` | **它从来没被兜底过** ← 本次崩溃点 |
   | `Choices[SyntheticModelItemId(model.Key)]` | `ResolveChoice(SyntheticModelItemId(model.Key))` | 靠上面 `modelDefinitions` 循环刚写过 |
   | `Choices[mapping.TargetItemId]`（普通映射行） | `ResolveChoice(mapping.TargetItemId)` | 靠第一循环补过 `Unknown` |
   | `Choices[sourceItemId]` | `ResolveChoice(sourceItemId)` | 靠第一循环补过 `Unknown` |

   后三处**在当前代码里是安全的**，改它们纯粹是为了让"`ApplySnapshot` 对 `Choices` 的读取一律走兜底"成为一条**可校验的整齐不变式**（行为完全等价：命中即返回同一个 VM）。**写入**（`Choices[x] = ...`，共 3 处）保持不变。

3. **`LoadCatalogOnceAsync` 加 `catch (Exception)`** —— 记 `LogService.Error` + 把失败写成页面状态（`StatusTitle = "读取失败"` / `StatusDetail = exception.Message`）；`finally` 里复位 `catalogLoadTask`，让下次打开还能重试。它堵的是"`async void OnLoaded` → 全局未处理错误"这条路：以后**任何**没被 `RefreshAsync` 认识的异常都只会显示成页面状态，不再弹全局错误。

### 12.2 为什么这两条是纯正面

- **不改变任何正常路径的行为**：`ResolveChoice` 命中时与索引器逐字节等价；`catch` 只在"本来就要逃到全局"的路径上执行。
- **不掩盖真 bug**：缺一条护甲会留下 `LogService.Warn`（且日志里有具体 id），页面上照实显示"未知护甲 {id}" —— 这正是护甲页**已有**的降级语义，不是新发明的。
- **保住 4 条既有不变式**：`Choices` 不删项；每个 `ItemId` 至多一个 VM（`ResolveChoice` 与第一循环用同一个 `TryGetValue`+补项写法）；工厂返回非 null；`hasLoadedCatalog` 语义不变（`catch` 里不碰它，仍为 `false` → 走 `EnsureCatalogLoadedAsync` 重试）。
- **降级而非崩溃**：即使 §11.1 的完整因果链**一字未改**，用户现在看到的也只会是"读取失败 + 未知护甲"，而不是"管理器遇到未处理错误"整个弹窗。**根因仍在，只是不再致命。**

### 12.3 为什么**没有**动根因（归属守卫）

`ReadSnapshotAsync` :227-244 的 `workspaceBelongsToCurrentLibrary`（`!deployment.Exists || …`）确实是真 bug，但**它不是纯正面修复**：

- 它改的是**用户可见语义** —— "哪些用户能看到自己的映射"。
- ⚠️ **本节初稿写错过一次，2026-10-11 22:3x 由用户指出并更正**：初稿写的是"**没点过"应用"**但只有旧协议的用户"，
  **这在逻辑上不成立** —— `armor-registry.protocol` 全仓只有 `ApplyAsync` :354-363 写它（CLI 侧也只有
  `armor-registry apply`），**没点过"应用"就不可能存在协议**。正确说法见 §13.3。
- 还牵扯：`applied.json` + `workspace.json` 存在但 `mods/.hd2modmanager/` 缺失的情形怎么判；设置项（`GameDataFolder` 之类）缺失时要不要**主动写入**再判归属（否则"配置读不出来"会**静默丢掉全部协议回退**，比现在还糟）。
- 需要一次明确的产品决定 + 至少一条架构测试钉住。

**结论：留待用户拍板，本次不碰。** 用户 2026-10-11 22:2x 明确表态：**不接受"老用户的映射看不见"**，
因此"收紧回退"这个方向已被否决，替代方向见 §13.4。

### 12.4 配套测试（新增）

**文件：`HD2ModCore.Tests/ArmorRegistryPageResilienceArchitectureTests.cs`（新，3 条）**

按仓库既有风格（`StartupPrewarmArchitectureTests` 那套"读源码文本 + 断言"）钉住上面两条修复：

| 用例 | 钉什么 |
|---|---|
| `ApplySnapshotNeverReadsChoicesThroughTheRawIndexer` | `ApplySnapshot` 里对 `Choices` 的**读取**必须走 `ResolveChoice`；裸索引器只允许出现在赋值左侧。判据是"`]` 之后（跳过空格）是不是单个 `=`"。 |
| `MissingArmorFallbackLogsInsteadOfSwallowing` | `ResolveChoice` 必须 `TryGetValue` + `Unknown` 兜底 + `LogService.Warn`。 |
| `CatalogLoadFailureBecomesPageStateInsteadOfAGlobalCrash` | `LoadCatalogOnceAsync` 必须有 `catch (Exception)` + Error 日志 + 写页面状态 + `finally` 复位单飞账本，且 catch 里**不**置 `hasLoadedCatalog`。 |

⚠️ **测试自身的坑（已踩并修正）**：第一版判据写的是"含 `Choices[` 但不是**行首**赋值"，结果**抓不到真正的历史 bug** —— 崩溃那一行恰好就是行首的 `Choices[mapping.TargetItemId],`。按项目铁律"主动破坏被保护行再跑"，这次破坏**没有**让用例变红，才发现用例是假的；改成"看右括号后面是不是 `=`"之后，同样的破坏立刻变红。**凡是靠文本形状判断的用例，都必须做这一步反证。**

---

## 13. 根因的**形状**：升级路径的三重生命周期不对称（2026-10-11 22:3x）

> 由用户提出："如果老用户没有点过应用，那 LocalAppData 里不可能存在旧协议……除非他新安装的管理器没有覆盖掉旧管理器。是不是重点在这里！？也就是说新管理器想要对接旧管理器，但旧管理器的数据新管理器没收到？"
>
> **结论：方向完全正确，而且这比 §11.1 的"某一步绕过守卫"更根本 —— 它解释了为什么这个守卫在设计上就不可能可靠。**

### 13.1 护甲注册相关的数据其实住在**三个**地方，生命周期各不相同

| 数据 | 位置（代码） | 生命周期 | 换新目录解压后 |
|---|---|---|---|
| `armor-registry.protocol`、`runtime.status`、`locator.cache`、`ArmorRegistryRuntime.log` | `%LocalAppData%\CowboyBingus\Helldivers2\{ArmorRegistry,Logs}`（`ResolveRegistryRoot` :89-99） | **跟用户走** | ✅ **还在** |
| `settings.json`（含 `ModLibraryFolder`/`GameDataFolder`）、`data\indexes\asset-index.sqlite`、`data\runtime-armor-registry.deployment` | **exe 旁边**（`SettingsService.SettingsPath` = `AppDomain.CurrentDomain.BaseDirectory\settings.json`；`StoragePaths.DataDirectory` = `AppRoot\data`） | **跟安装走** | ❌ **全丢** |
| `armor-registry.workspace.json`、`applied.json` | `<ModLibraryFolder>\.hd2modmanager\`（`WorkspaceRoot` :35） | **跟模组库走** | ⚠️ **取决于模组库路径有没有变** |

**发布包本身不含这三类中的任何一类**（`tools/PublishRelease.ps1` 只 stage 二进制 + `bin/`（CLI + 私有运行时）+ `hd2mod-docs/` + 根 README + `Armor-Diagnostics.cmd`，见 :302-401）。所以"覆盖安装"能保住第 2、3 类，"**解压到新目录**"则必然丢掉第 2 类。

**本机实测（2026-10-11 22:2x，可直接复现）：**

```
<AppRoot>\settings.json          → "ModLibraryFolder": "E:\\mod"        ← 自定义库，只存在于这里
<AppRoot>\data\runtime-armor-registry.deployment
                                 → workspace=E:\mod\.hd2modmanager\armor-registry.workspace.json
E:\mod\.hd2modmanager\           → armor-registry.workspace.json + applied.json（各 46570 B）
%LocalAppData%\...\ArmorRegistry\armor-registry.protocol
                                 → schema=2 / revision=92 / 293 条 map_model=   ← 用户级，换目录也在
```

### 13.2 为什么守卫**必然**失效：用"升级时会被摧毁的凭据"，去判断"升级时会被保留的文件"归谁

`ReadSnapshotAsync` :233-238 用 `<AppRoot>\data\runtime-armor-registry.deployment` 里的 `workspace=` 判断协议归属。
但 `deployment` 是**安装级**的，而 `protocol` 是**用户级**的。解压到新目录后：

- `protocol` **在**（用户级，跟着走）；
- `deployment` **不在**（安装级，丢了）→ `!deployment.Exists` 为真 → `workspaceBelongsToCurrentLibrary = true` → **守卫退化成"没有凭据 ⇒ 一律相信"**。

**一句话：守卫拿一个"升级必然摧毁"的凭据，去守护一个"升级必然保留"的文件。** 这不是某一版写错了一行，是**两个生命周期被放在了同一个判断里**。

### 13.3 修正后的"谁会受影响"（§12.3 的错误说法在此更正）

必须**同时**满足三条才会崩：

1. **点过"应用"**（否则协议不存在，不可能走到 `ParseProtocol`）；
2. **当前模组库里没有 `workspace.json`**（:239 优先读工作区；有工作区时协议根本不参与）——
   即模组库路径**变了**，最常见的原因见 §13.5；
3. **`<AppRoot>\data\runtime-armor-registry.deployment` 不存在**（新目录解压）或指向别的库。

⇒ 也就是**恰好就是现在正在崩溃的那批人**。所以"收紧守卫"对他们来说 = 从崩溃变成空列表：**严格更好，但仍然丢了可见性** —— 这正是用户拒绝的方向。

### 13.4 替代方向（用户约束：**绝不允许老用户看不见映射**）

根因是"凭据与被守护对象的生命周期不一致"，所以修法应该是**让两者同生共死**，而不是收紧判断：

| 方向 | 做法 | 评价 |
|---|---|---|
| **① 换凭据（推荐）** | 归属判断改用**同库**的 `<ModLibraryFolder>\.hd2modmanager\armor-registry.applied.json`（`ApplyAsync` :370 与 protocol 同时写）。它与被守护的工作区**同库同生共死**，换安装目录不受影响 | **不误伤老用户**：他们的 `applied.json` 就在自己库里。且语义更准 —— "这个库自己应用过"才是"协议属于这个库"的证据 |
| **② 补凭据** | 在协议旁边（用户级目录）也写一份 `workspace=` 标记，读取时**同时**接受旧的 `<AppRoot>\data\` 位置 | 只对**未来**的升级有效；对已经坏掉的那批人仍无凭据 |
| **③ 不 gate，改"说清楚"** | 保持现有回退（**永远不隐藏映射**，配合 §12 的兜底已经不崩），但把"目录 0 条 / 工作区缺失 / 协议归属可疑"变成**页面上可见、可操作**的状态（"资产索引缺失 → 一键建立"） | 完全满足用户约束，**纯正面**；是 ① 之外应该**同时**做的一层 |
| ~~④ 收紧守卫~~ | ~~`deployment` 不存在就不采用协议~~ | **已否决**（老用户看不见映射） |

**建议：① + ③ 一起做。** ① 让归属判断在有证据时可靠、无证据时**不误伤**；③ 让"索引缺失"这类真实故障**自己浮出水面**，而不是伪装成"未知护甲"。

### 13.5 连带发现（独立于护甲页，值得单独排查）

模组库路径本身也是**安装级**的（只存在 `<AppRoot>\settings.json` 里），所以换新目录解压后 `ModLibraryFolder` 会**被静默重新推导**：

- `SettingsService.InitializeDefaultPaths()` → 先 `TryDetectAndSetGameDataFolder()`，再 `EnsureDefaultModLibraryFolder()`；
- `GetRecommendedModLibraryFolder()` 依赖**游戏 data 目录探测成功**（`<steamapps>\common\HD2ModManager\mods`），
  探测失败则回退 `<exe>\mods`（`GetPortableModLibraryFolder()` :315）。

⇒ **游戏目录探测失败会级联成"模组库悄悄换了一个空目录"**，而库一换，`workspace.json` 就找不到了（§13.3 条件 2）。
这把 §11.2 的 **A3（`GameDataLocator` 探测失败）** 从"放大器"升级为**可能的触发条件本身**。

**推论**：如果受影响的用户是"自定义模组库 + 新目录解压"，那么他**看到的模组列表应该也是空的**（库被换掉了）。
这一条是**可验证的判别点**：去问用户"你的模组列表还在吗"。在吗 ⇒ 走的是"库没变但工作区缺失"（另找原因）；
不在 ⇒ 与本节模型一致。

---

## 14. 已落实的修复：归属凭据搬到协议旁边 + 把异常状态说出来（2026-10-11 22:5x）

用户 22:37 拍板"可行"，同时给出一条**项目级硬约束**（已写入 `MEMORY.md` 的「工作约定」）：

> 任意自定义的数据应该保存在 mod 库，除了设置页面的简单设置等保存在 `settings.json` 中，
> 以及能直接从游戏 data 等获取的纯缓存类信息直接保存管理器目录。
> 这样用户即使更换了一个管理器，也能无痛继承 mod 库，随便迁移 mod 库也能无痛继承，只需改变 mod 库地址即可。

### 14.1 ⚠️ 我提的 ① 被自己证伪：`applied.json` **不能**当归属凭据

§13.4 里我推荐"改用同库的 `armor-registry.applied.json` 当归属凭据"。**这条是错的，落实前核对代码时发现**：

- `ApplyAsync` :369 写 `WorkspaceRoot\armor-registry.workspace.json`，:370 **紧接着**写 `WorkspaceRoot\armor-registry.applied.json`。
- ⇒ **`applied.json` 存在 ⟹ `workspace.json` 必然存在。**
- 而协议回退分支的前提正是"`workspace.json` 不存在"（:239 优先读工作区）。
- ⇒ 用 `applied.json` 当 gate，回退分支**永远不会触发** ⇒ 受影响的用户从"崩溃"变成"**空列表**"——
  **正是用户明确否决的那个结果。**

**教训**：想用某个文件当"证据"之前，先确认它**能不能在需要它的那个分支里存在**。
这次是靠"落实前把代码再读一遍"拦住的；如果直接照 §13.4 的表动手，就会写出一个"看起来修了、其实把映射藏了"的补丁。

### 14.2 实际落实了什么

| 改动 | 位置 | 作用 |
|---|---|---|
| **归属标记搬到协议旁边** | `RuntimeArmorRegistryService.OwnerFileName = "armor-registry.owner"`；`ApplyAsync` 写 `<RegistryRoot>\armor-registry.owner`（`schema=1` / `workspace=` / `revision=`） | 归属凭据与协议**同目录、同生命周期**。换目录解压后它仍在 ⇒ 页面能说出"这份协议属于哪个库"。旧的安装级 `deployment` **照写不删**（同一安装内的库切换仍靠它） |
| **读取回退链** | `ReadSnapshotAsync` | 归属取值顺序：协议旁的 `owner` → 旧的安装级 `deployment` → `null`（无从判断，如实说"未知"） |
| **来源标记** | `RuntimeArmorRegistrySnapshot.DocumentSource`（新增 `RuntimeArmorRegistryDocumentSource` 枚举：`Workspace` / `ProtocolFallback` / `Empty`） | 让"这份文档是从工作区读的，还是退回协议副本"成为**可断言的事实**，而不是只能靠猜 |
| **页面说出来** | `ArmorRegistryPageViewModel.UpdateStatus` | ① `ProtocolFallback` 时："当前模组库没有工作区文件，正在显示协议副本；该协议上次由 `<owner>` 应用。如果这不是你要用的模组库，请到设置页确认模组库地址。" ② 已加载目录但条数为 0 时："资产索引缺失或为空，护甲名称无法解析（映射会显示为「未知护甲」）；请到设置页建立资产索引。" |

### 14.3 刻意**没有**做的事

- **没有收紧归属判断。** `workspaceBelongsToCurrentLibrary`（`!deployment.Exists || …`）原样保留。
  ⇒ 回退行为、以及"哪些用户能看到映射"**完全没变**——只有"看得见但不知道为什么"变成了"看得见且知道为什么"。
  这是本次改动**唯一的正确性目标**：**绝不隐藏，只解释**。
- **没有新增 gate、没有新增状态机。** `DocumentSource` / `ProtocolOwnerWorkspace` 都是**只读展示**字段，
  不参与任何"要不要读/要不要显示"的决定。
- **没有动 `hasLoadedCatalog` 语义**（目录 0 条仍会置 `true`）。那是 §11.5 的 P1，会改状态机语义，单独做。

### 14.4 测试（+5 条，全部做过破坏性反证）

- `RuntimeArmorRegistryServiceTests`（+3）：
  - `MissingWorkspace_FallsBackToTheProtocolAndRecordsThatItDid` —— 回退真的发生（映射没被藏），且 `DocumentSource == ProtocolFallback`、无归属记录时 `ProtocolOwnerWorkspace == null`。
  - `ProtocolOwnership_IsRecordedNextToTheProtocolSoItSurvivesANewInstallDirectory` —— **忠实模拟"换目录解压 + 指向另一个库"**（新建一个 `StoragePaths`，AppRoot 与库都换掉，用户目录照旧），断言仍能说出归属。
  - `ProtocolOwnership_FallsBackToTheOldInstallLevelRecordWhenTheNewMarkerIsAbsent` —— 旧版本用户不丢。
- `ArmorRegistryPageResilienceArchitectureTests`（+1）：`ThePageSaysWhyItMightBeShowingLessThanExpected` —— 钉住页面确实会说这两句话。

**破坏性反证**：把 `documentSource` 强制成 `Workspace` + 把归属标记写到 `armor-registry.owner.disabled`
⇒ 前两条用例**立刻变红**（`失败: 2，通过: 20`），恢复后全绿。

### 14.5 关于"这次要提醒用户覆盖安装"

发布包（`tools/PublishRelease.ps1`）**不含** `settings.json` / `data/` / `mods/`，所以**覆盖安装不会丢任何用户数据**；
而**解压到新目录**会丢 `settings.json`（⇒ 模组库地址被重新推导）与 `data/`（⇒ 索引 + 旧归属凭据）。
**这次发布要显式提醒"请覆盖安装到原目录"**；本节 14.2 的归属标记则让**以后**即使解压到新目录也能自查。



