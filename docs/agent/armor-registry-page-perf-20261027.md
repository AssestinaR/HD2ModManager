# 护甲映射页：症状 → 根因（实测）

时间：2026-10-27
范围：`ArmorRegistryPageView` / `ArmorRegistryPageViewModel` / `ModListPanel` / `ModListTransitionController` / `ModInformationCenter` 取数链
基线：`b7bc614`

本文只做**取证与定位**，不含修改。每条结论都标了证据来源（日志行 / 代码位置），没有证据的推测单列在 §7。

---

## 0. 证据来源

**实测日志**：`HD2ModManager/bin/Debug/net10.0-windows/logs/manager_20261027.log`

当天三次打开护甲页（06:54 / 07:37 / 11:17）都留下了完整链路。以下是 11:17 那次（最完整）：

```
11:17:10.116  启动性能：阶段=库目录同步；耗时=11877ms
11:17:12.785  启动性能：阶段=启动维护就绪（全库派生已建后）；耗时=14545ms
11:17:12.794  信息中心开始生产：类型=ArmorCoverage   ← 护甲页取数真正开始
11:17:13.012  GameDataIndex     耗时=215ms
11:17:13.197  DeployedGraph     耗时=183ms
11:17:13.357  GameDataMapping   耗时=153ms
11:17:13.369  ArmorCoverage     耗时=575ms
11:17:13.394  mod-list-bulk-projection; before=0; desired=175
11:17:13.424  Thumbnail.Decode  elapsed=25ms
11:17:13.471  Thumbnail.Decode  elapsed=44ms
11:17:13.497  Thumbnail.Decode  elapsed=16ms
11:17:13.507  ModList.TransitionLayoutWait  elapsed=114ms
11:17:13.508  ModList.TransitionLayoutWait  elapsed=120ms
11:17:13.529  Thumbnail.Decode  elapsed=15ms
11:17:13.801  ArmorRegistry.Prewarm  elapsed=602ms
```

另外两次：`ArmorRegistry.Prewarm` = 378ms（06:54）、736ms（07:37）。`frame-stall gap=106ms`（11:17:12.876）。

**结论先行**：从"点开这一页"到"列表里出现护甲"，11:17 那次是 **约 1.0 秒**；但如果点得早（启动维护没跑完），要等到 **14.5 秒**之后才有数据。这两件事是不同的病，下面分开说。

---

## 1. 症状「打开要等好几秒」

### A1 — 取数闸门在启动维护结束前不放行（这是最大的一笔）

`ArmorRegistryPageViewModel.cs:478`
```csharp
if (displayFactsReady is not null) await displayFactsReady.ConfigureAwait(false);
```

`displayFactsReady` 就是 `ShellViewModel._armorDisplayFactsGate.Task`（`ShellViewModel.cs:81`），而它只在**启动维护全部跑完的 finally 里**放行（`ShellViewModel.cs:442`）：

```csharp
finally
{
    _armorDisplayFactsGate.TrySetResult();
    QueueArmorRegistryPrewarm();
}
```

实测那一天，走到 `TrySetResult()` 是启动后 **14.5 秒**（`启动维护就绪 = 14545ms`）。

所以：**启动后 15 秒内点开护甲页 = 两个列表都空着，而且没有任何"正在加载"的提示**（见 A6）。

闸门本身是**对的**（它保证图标与覆盖事实在库快照 + 启动部署之后才计算，见注释里的两个理由）。错的是**关着的时候页面没有告诉用户**。

### A2 — 每次打开都重算整条 GameData 事实链

`ModInformationCenter` 里所有"无节点事实"都是这个形状（`ModInformationCenter.cs:659`、`:625`、`:585`）：

```csharp
finally
{
    RecordDiagnostic(...);
    _gameDataTasks.TryRemove(key, out _);   // ← 生产完就移除
}
```

`TryRemove` 意味着 `_gameDataTasks` 只做**并发合并**，不做**结果缓存**。于是每次打开护甲页，`GameDataIndex` / `DeployedGraph` / `GameDataMapping` / `ArmorCoverage` 四个全部重新生产一次 —— 日志里 06:54、07:37、11:17 三次都是"缓存未命中或要求刷新"。

下层服务自己有缓存（`DeployedGraphFactCache`、`GameDataFactCache`），所以 215+183+153 已经是**热缓存**下的数字；冷启动更慢。

### A3 — 为了一个"归档 id 是否存在"跑 357k 行的聚合

`ModInformationCenter.cs:598` 的 `GetArchiveSummariesAsync()` 每次执行这段 SQL（`AssetArchiveIndexService.cs:127`）：

```sql
SELECT a.archive_id, a.display_name, a.category, COUNT(e.entry_index) AS entry_count,
       CASE WHEN EXISTS (SELECT 1 FROM archive_issues i WHERE i.archive_id = a.archive_id)
            THEN '存在问题' ELSE '已索引' END
FROM archives a
LEFT JOIN archive_entries e ON e.archive_id = a.archive_id
GROUP BY a.archive_id, a.display_name, a.category
```

`archive_entries` 是 357k 行。这就是那 215ms。

而护甲页拿 `index.Archives` 只做一件事（`ArmorCoverageProjection.cs:50`）：

```csharp
var known = index.Archives
    .Select(archive => archive.PackageName)
    .Where(archiveIds.Contains)
    .ToHashSet(...);
```

**它只要"归档 id 的集合"，不要 entry_count、不要 display_name、不要 category。** 357k 行的聚合是纯浪费。

### A4 — 首开路径没有丢到线程池

`ArmorRegistryPageViewModel.cs:442`
```csharp
var inputs = isWarmUp
    ? await Task.Run(LoadCatalogInputsAsync).ConfigureAwait(true)
    : await LoadCatalogInputsAsync().ConfigureAwait(true);
```

预热（用户看不见）丢线程池，**用户真正打开的那次不丢**。而 `LoadCatalogInputsAsync` 在第一个真正的异步让出之前是同步跑的：`ReadArmorCatalogAsync` 与 `ReadSnapshotAsync`（SQLite + 文件）大概率整段跑在 UI 线程上。

注释写的理由是"预热发生在启动空闲时刻，绝不能压到 UI 线程"——但首开比预热更该受保护。这一行写反了。

### A5 — 列表重建 = 全部容器丢弃 + 图标重新解码 + 淡入动画

`RebuildProjections()`（`:1171`）每次都用 `ListTransitionKind.Replace` 重建三个列表，而 `BulkObservableCollection.ReplaceWith`（`BulkObservableCollection.cs:54`）**无条件**发 `Reset`：

```csharp
OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
```

`Reset` 让 `ItemContainerGenerator` 状态回到 `NotStarted`，虚拟化容器全部丢弃，可见行重新实例化 → 每行重新跑 `ImagePlaceholderConverter.Convert` → 重新解码图标（实测 15~44ms/张）。

同时 `PrepareBulkVisualTransition`（`ModListTransitionController.cs:381`）在开始过渡时把 active 层设成**全透明**：

```csharp
_activePresentationLayer.Opacity = 0;
```

要等 layout 完成（`TransitionLayoutWait` 114~133ms）+ 230ms 动画才恢复。这段时间用户看到的是"空白"或"旧列表"。

### A6 — 加载中没有任何加载态，空状态文案还在撒谎

- `StatusTitle` 初值是 `"尚未读取"`（`:46`），加载中一直是它。
- `AvailableArmors` 空时显示 `EmptyMessage="所有护甲都已分配为视觉来源或映射目标。"`（`ArmorRegistryPageView.xaml:371`）。

于是"还在加载"和"真的没有护甲了"在界面上**长得一模一样**，而且后者的文案还主动告诉用户一个错误结论。这大概就是"用户反馈啥也不显示"里被描述得最让人困惑的那部分。

---

## 2. 症状「啥也不显示 / 列表是空的」

### B1 — A1 的直接后果

闸门关着 → `LoadCatalogInputsAsync` 停在 `await` → `ApplySnapshot` 没跑 → `Choices` 空 → 两个列表空。

### B2 — 过渡控制器在"面板被折叠"时会永久卡死

这是**真 bug**，与 A1 无关，会一直留在页面上直到用户切走再切回来。

`ModListTransitionController.BeginTransition`（`:211`）：
```csharp
SetPhase(TransitionPhase.AwaitingLayout);   // → TransitionStateChanged(true)
```
`ModListPanel.OnTransitionStateChanged`（`ModListPanel.xaml.cs:171`）：
```csharp
ItemsList.IsHitTestVisible = !isTransitionActive;   // ← 过渡一开始，列表立刻不可点
```

然后 `OnLayoutUpdated`（`:237`）要等 `AreContainersReady`：
```csharp
private static bool AreContainersReady(ItemsControl items)
    => items.Items.Count == 0
        || items.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated;
```

**关键**：`Reset` 会把 generator 状态打回 `NotStarted`，要**下一次 measure** 才会变成 `ContainersGenerated`。而 `Visibility=Collapsed` 的元素**永远不会被 measure**（父 Grid 跳过折叠子元素）。

于是：**任何在折叠状态下收到结构性变化的列表，会永远停在 `AwaitingLayout`** ——
- `ItemsList.IsHitTestVisible` 永远是 `false`（点不动）
- 如果是 bulk 过渡，`_activePresentationLayer.Opacity` 永远是 `0`（看不见）
- 后续所有变化都进 `_pendingDesired` 队列（`HandleDesiredChanged:186-200`），永远不应用（内容不更新）

**可达路径（已核对代码）**：

`ProviderList` 在 `IsProviderDetailOpen == true` 时被折叠（`ArmorRegistryPageView.xaml:207-216` 的 DataTrigger）。此时：
1. 用户在右侧多选几个护甲 → 底栏"转为视觉对象" → `PromoteAvailableAsProviders`（`ShellViewModel.cs:762`）
2. → `pendingProviderIds` 变大 → `RebuildProjections` → `Providers` 结构性变化
3. → `ProviderList` 的控制器 `BeginTransition`（面板是折叠的）
4. → 卡死。用户点"←"返回后，**视觉来源列表点不动**。

这正好把用户报的两个现象串起来了：**先"多选加过去"，然后"选不中条目"**。

> **诚实标注**：本条最初是**从代码推导**出来的，日志里没有抓到卡死的那一次。修复时补了一个离屏探针
> （`tmp/TransitionProbe`，见 §9），**在真 WPF 里复现了它**：折叠状态下收到结构性变化后，
> `ItemsList.IsHitTestVisible` 停在 `false`、离场层一直可见、新内容被旧内容盖住。修复后同一探针 PASS。

### B3 — `PlayPending` 早退时不清状态

`ModListTransitionController.cs:267`
```csharp
private void PlayPending()
{
    if (_disposed || !_transitionPending || !_owner.IsLoaded) return;   // ← 直接返回
    ...
    _transitionPending = false;
```

`!_owner.IsLoaded` 时直接返回，但 `_transitionPending` 仍为 `true`、phase 仍是 `AwaitingLayout`。后果是下一次 `BeginTransition`（`:213`）会跳过 `PrepareBulkVisualTransition`，复用上一轮残留的 `_before` / `_bulkVisualTransition`，动画会从错误的位置开始。

---

## 3. 症状「选不中条目」

### C1 — B2 / B3 的 `IsHitTestVisible = false`

见上。这是"点下去完全没反应"的第一嫌疑。

### C2 — `IsBusy` 期间所有命令静默返回 false

- `RefreshAsync` 一开始就 `IsBusy = true`，直到数据全部到齐才清（`:436` / `:466`）。闸门关着的 14.5 秒里它一直是 true。
- `CanAssignFromAvailable`（`:1036`）：`if (IsBusy ...) return false;`
- `CanReleaseToAvailable`（`:1110`）：同上。
- `AssignFromAvailable` / `ReleaseToAvailable` / `PromoteAvailableAsProviders` 在 `IsBusy` 时**静默 return**，没有任何提示。

拖放的 `CanAcceptRelationshipDrop`（`ArmorRegistryPageView.xaml.cs:161`）也走这两个方法 → 拖拽时连放置高亮都不出现，用户以为"拖不进去"。

---

## 4. 症状「多选加过去只变成视觉来源，不会变成映射目标」

### D1 — 底栏那条路径根本不看当前左侧是什么

`BottomBarCoordinator.cs:99`
```csharp
public bool ShowPromoteArmorProviders => IsArmorRegistryAvailableSelection;
```

`ShellViewModel.cs:761`
```csharp
case ArmorRegistrySelectionAction.Promote:
    page.PromoteAvailableAsProviders(ids);
    break;
```

而 `PromoteAvailableAsProviders`（`:968`）**无条件**把 id 塞进 `pendingProviderIds`，它根本不读 `IsProviderDetailOpen`。

对比一下单行按钮/拖放走的 `AssignFromAvailable`（`:1042`），它是**看状态的**：
```csharp
if (selectedProviderId is null) { /* 变成视觉来源 */ }
else { /* 建成映射 */ }
```

所以：**同一个动作，单行做的是"注册到该视觉对象"，多选做的是"转为视觉对象"**。用户选得越多，得到的语义越不对。这就是他描述的那个现象，一字不差。

### D2 — 而且底栏根本没有"注册为映射目标"这个按钮

`SelectionActionBarContent.xaml:15-17` 只有三颗：`转为视觉对象` / `解除视觉对象` / `解除映射目标`。

注意这里的不对称：**移除**是状态感知的（`ShowReleaseArmorTargets` 按 scope 切换），**添加**不是（`ShowPromoteArmorProviders` 恒为"转为视觉对象"）。用户说的"（移除也同样）"需要复现确认，但代码里"添加"这一侧的不对称是确凿的。

### D3 — 配对失败是静默的

`AssignFromAvailable`（`:1095`）：
```csharp
var sourceId = RuntimeArmorMappingPairing.FindUniqueSourceForTarget(sourceMembers, targetChoice.KitKind);
if (sourceId is null || sourceId == id || ...) continue;   // ← 什么都不发生
```

`FindUniqueSourceForTarget`（`RuntimeArmorRegistry.cs:80`）在"来源里同 kind 的候选不是恰好 1 个"时返回 null（`matches.Length == 1 ? matches[0] : null`）。自定义素体 + 普通护甲混在同一个来源里、或者来源是"组合"且 kind 不明时，就会走到这里。

结果是：用户点了"注册到该视觉对象"，界面**完全没反应**，也没有任何解释。

---

## 5. 图标：用户怀疑的那个方向是对的，但机制和他想的不太一样

### E1 — 护甲页解码的是**原始图标**，不是缩略图缓存

模组库页、配置页走 `ThumbnailService`（72/128/256 三档磁盘缓存）：
`LibraryPageViewModel.cs:899` / `PageViewModels.cs:1330` 都是
```csharp
_imagePath = ThumbnailService.GetExistingThumbnailPath(_thumbnailSourcePath, 72);
```

护甲页没有，直接把 `Mod.Image`（原始 icon.png 路径）交给转换器。同样这么干的还有四个工具页：
`BatchDecorationPlanPageViewModel.cs:292`、`DecorationPlanPageViewModel.cs:402`、
`HelmetToArmorMergePlanPageViewModel.cs:346`、`PatchResourceRepackagingPageViewModel.cs:442`。

`ModListPanel.xaml:121`
```xml
<Image Source="{Binding ImagePath, Converter={StaticResource ImagePlaceholder}, ConverterParameter=72}"/>
```

`ImagePlaceholderConverter.LoadImageWithoutLock` 直接从 `ImagePath` 开文件流解码 —— 那是**模组作者放的原始 icon.png**，可能是 1024×1024。`DecodePixelWidth=72` 只减少输出尺寸，不减少读盘与解码前的工作。

实测每次 15~44ms，全部在 UI 线程。

### E2 — 每次 Convert 都要两次文件系统 stat

`ImagePlaceholderConverter.cs:54`
```csharp
private static string BuildCacheKey(string path, int decodePixelWidth)
{
    long writeTicks = 0;
    try { if (File.Exists(path)) writeTicks = File.GetLastWriteTimeUtc(path).Ticks; } catch { }
    return $"{path}\u001f{decodePixelWidth}\u001f{writeTicks}";
}
```

缓存键里带了 mtime，所以**即使命中缓存也要先 stat 两次**。每次 `Reset` 重建可见行都重跑一遍。

### E3 — 缓存无上限、无淘汰

`ImagePlaceholderConverter.cs:14`
```csharp
private static readonly ConcurrentDictionary<string, BitmapSource> ImageCache = new(...);
```
只有 `GetOrAdd`，没有 Remove，也没有容量上限。键里带 mtime，等于"改一次图标就多一份永久驻留"。

### E4 — 父链解析里有一处 O(N²)

`ArmorRegistryPageViewModel.cs:671`
```csharp
.Where(id => library.Snapshot.Nodes.Keys.Any(key => key.Value == id))
```

每个节点的每个声明宿主都线性扫一遍全部节点。测试库 14 个节点无所谓；用户库里几百个 Mod 时会变成几万次比较，跑在覆盖事实的投影里。

---

## 6. 顺手发现的两处小浪费

### F1 — `ReplaceWith` 无条件 `Reset`，即使内容一模一样

`BulkObservableCollection.ReplaceWith` 最后一行无条件发 `Reset`（`:54`）。`RebuildProjections` 在"打开详情""关闭详情""选中一个目标"这些**内容根本没变**的场景里也会调用它，于是三个列表每次都全部重建。

### F2 — 摘要每次都把整个视图枚举一遍

`ProvidersSummary` / `AvailableSummary`（`:335-341`）在搜索框有内容时用 `ProvidersView.Cast<object>().Count()`。这个属性在 `RebuildProjections` 里被 `OnPropertyChanged` 触发，绑定会重算 —— 每次都是 O(n) 枚举 + 装箱。

---

## 7. 无证据的推测（明确标出，不作为结论）

1. "选不中条目"也可能有第三个来源：`ModListPanel` 自己维护 `IsSelected`，同时 ListBox 也有原生选中态，两者在某些路径下会不同步。**没有复现，没有证据。**
2. 用户说的"（移除也同样）"具体指什么，从代码里读不出唯一解释。需要一次具体操作序列。
3. `DeployedGraph` 那 183ms 里有 183ms 是读盘还是反序列化，没有单独测量。

---

## 8. 本轮已修（2026-10-27 晚）

| # | 症状 | 改了什么 | 落在哪 |
|---|---|---|---|
| 1 | 啥也不显示 / 选不中条目 | 过渡控制器在**不可见**时直接落定，不排过渡；`PlayPending` 早退不再留脏状态；加了 600ms 布局看门狗兜底 | `ModListTransitionController.cs`（`CanAnimate` / `SettleImmediately` / `SettleWithoutAnimation` / `OnLayoutWatchdogTick`） |
| 2 | 多选只变视觉来源 | 新增作用域 `ArmorRegistry:AvailableAsTarget`：左侧停在目标列表时，右侧多选的动作变成「注册为映射目标」，底栏显示对应的第二颗按钮 | `SelectionCoordinator.cs`（`ArmorRegistryScopes`）、`ArmorRegistryPageViewModel.ApplyAvailableSelection`、`BottomBarCoordinator`、`SelectionActionBarPresentation`、`SelectionActionBarContent.xaml`、`ShellViewModel` |
| 2b | 点了没反应 | 配对失败不再静默，给一条明确的警告（来源里同类型候选不是恰好一个） | `ArmorRegistryPageViewModel.AssignFromAvailable` |
| 3 | 空状态撒谎 | 新增 `IsLoadingCatalog`；加载中两个列表都显示「正在读取护甲目录…」，加载完才显示原来那句；初始状态文案改成「正在读取」+ 说明在等什么 | `ArmorRegistryPageViewModel`（`IsLoadingCatalog` / `*EmptyMessage`）、`ArmorRegistryPageView.xaml` |
| 4 | 首开冻 UI | 首开路径也 `Task.Run`，与预热一致 | `ArmorRegistryPageViewModel.RefreshAsync` |
| 5 | 每次打开重跑 215ms 聚合 | 归档摘要按「索引库路径 + 指纹」缓存，重建时显式作废 | `AssetArchiveIndexService.cs`（`SummaryCache`） |
| 6 | UI 线程解码原图 + 每次 stat | 图标先查 `ThumbnailService` 的 72px 磁盘缓存（没有才退回原图）；mtime 探针加 2 秒 TTL | `ArmorRegistryPageViewModel.FindModIconInParentChain`、`ImagePlaceholderConverter.cs` |

**没有动 `ModInformationCenter` 的缓存语义。** §8 原表里那条"给 GameData 事实加一层结果缓存"被我否决了：那层账本故意不缓存结果，新鲜度靠生产者自己的来源指纹保证；在中心层加结果缓存会把"部署变了但页面还显示旧覆盖"变成可能。真正贵的那一段（35 万行聚合）在 `AssetArchiveIndexService` 里，缓存它既安全又足够。

## 9. 验证

**全量测试**：1307 通过 / 1 跳过（基线 1306 + 本轮新增 1 条），0 警告 0 错误。

新增两条用例（`HD2ModCore.Tests/AssetArchiveIndexServiceTests.cs`）：

| 用例 | 钉住什么 | 反向验证 |
|---|---|---|
| `Rebuild_WithUnchangedGameData_InvalidatesTheArchiveSummaryCache` | 只换命名数据、游戏文件不动时，指纹相同，重建后必须读到**新名字** | 注释掉 `SummaryCache.TryRemove` → **失败**（已实测） |
| `GetArchiveSummariesAsync_ServesRepeatCallsFromTheFingerprintKeyedCache` | 第二次读摘要返回**同一个实例**，没有重查 35 万行 | — |

**离屏探针**（`tmp/TransitionProbe`，gitignored）：用真实的 `App.xaml` 资源 + 真实 `ModListPanel`，在折叠状态下触发一次 20 行的结构性变化，然后检查面板是否还活着。

| 版本 | PresentedItems | IsHitTestVisible | 活动层 | 离场层 | 结论 |
|---|---|---|---|---|---|
| 修复前 | 20 | **false** | 1.00 | **可见** | 点不动 + 旧内容盖住新内容 |
| 修复后（连跑 3 次） | 20 | **true** | 1.00 | 隐藏 | PASS |

这就是 §2 B2 那条"从代码推导"的确凿证据：**症状是真的，而且和用户描述的完全对得上**。

**真机冒烟**：启动管理器跑完一轮启动维护 + 护甲页预热，日志里**没有** `mod-list-layout-watchdog` 告警（说明修复没有误伤正常路径），`ModList.TransitionLayoutWait` 从 114~133ms 降到 72~79ms。

## 10. 还没做的

| 项 | 为什么先不做 |
|---|---|
| A1 闸门（启动维护 14.5s）本身 | 那是启动性能问题，不是护甲页的问题。护甲页现在至少有诚实的加载态了 |
| A5/F1 内容没变时不 `Reset` | 要引入"真的没变"的判断，涉及三个列表的一致性；等 1~6 的效果被用户确认后再做 |
| E3 `ImageCache` 无上限 | 72px 位图每张约 20KB，175 件护甲不构成压力。真正要治的是 E1/E2，已修 |
| E4 O(N²) 父链解析 | 测试库 14 个节点，用户库几百个节点也才几万次比较，收益低于风险 |
| 另外四个工具页的图标（`DecorationPlan` 等） | 同一套 `ModListPanel`，同一个转换器；E1 只改了护甲页。改法一样，但需要各自验证 |
