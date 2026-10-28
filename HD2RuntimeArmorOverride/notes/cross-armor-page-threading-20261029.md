# 替换护甲页的跨线程崩溃：CollectionView 只认创建它的那个线程（2026-10-29）

## 1. 结论

用户报的

> 计划读取失败：该类型的 CollectionView 不支持从调度程序线程以外的线程对其 SourceCollection 进行的更改。

**不是数据问题，是线程问题**：`CrossArmorTransferPlanWindowViewModel` 被**在后台线程上构造**，
于是它内部那些 `await` 的续体也落在池线程上，续体里去改绑在 `DataGrid` / `ComboBox` 上的
`ObservableCollection`，WPF 直接抛 `NotSupportedException`。

一处抛在 `RefreshPlanAsync` 的 `try` 里 → 被接住写成"计划读取失败"；
一处抛在 `ScanSourceUnitsAsync` 里 → 原来那个 `catch` 的异常类型过滤接不住它 → 变成
**未观察任务异常**，页面表现为"来源部件表格永远停在待计算、一句提示都没有"。

## 2. 证据链（为什么能确定是这一条）

1. 全仓只有一处会写这个前缀：`CrossArmorTransferPlanWindowViewModel.cs`
   `PlanState = $"计划读取失败：{exception.Message}"`。
2. 那个 `try` 块里唯一的集合变更是 `RefreshTargetArchiveFilters()` →
   `TargetArchiveFilters.ReplaceWith(...)`，而 `TargetArchiveFilters` 绑在"目标 archive 筛选"
   那个 ComboBox 上。所以异常只能来自这次 `CollectionChanged`。
3. `CollectionChanged` 在非 UI 线程抛出，只可能是因为**当前线程没有 UI 同步上下文**。
4. 该 VM 的唯一构造点是 `ModDetailsPageViewModel.PlanCrossArmorTransferAsync`，而那个方法
   从第一个 `await ... .ConfigureAwait(false)` 之后就一直在池线程上跑——构造 VM 那行
   （`new CrossArmorTransferPlanWindowViewModel(...)`）也在池线程上。
5. 构造完才 `OpenCrossArmorPlanOnUiThreadAsync(...)` 把它挂到 UI 上；此时 VM 已经启动的
   后台规划 / 网格解析，续体全部落在池线程。

顺带解释"有时报有时不报"：构造时那次 `QueueRefreshPlan()` 与页面绑定是**赛跑**——
规划先跑完（ComboBox 还没建出 CollectionView）就不报，后跑完就报。用户点"刷新计划"
是 UI 线程发起的，所以手动刷一次往往就好了——这也正是这个 bug 长期被当成"偶发"的原因。

## 3. 修法（三层，缺一层就会退回去）

### 3.1 根因：构造动作本身送进 UI 线程

`OpenCrossArmorPlanOnUiThreadAsync(Func<...> create)` 现在接收**工厂**而不是实例：
`new` 发生在 `dispatcher.InvokeAsync(...)` 里面。这样 VM 里的 `await` 天然回到 UI 线程，
`CollectionView` 也是在 UI 线程上被创建出来的。

### 3.2 兜底：改集合的收尾显式回 UI 线程

`ApplyOnUiThreadAsync(Action)`（`Application.Current?.Dispatcher`，没有就就地执行）：

* `RefreshPlanAsync`：`Task.Run` 规划完之后，把"写 `plan` + 一堆 `OnPropertyChanged` +
  `RefreshTargetArchiveFilters()`"整段送回去；**代次检查在 UI 线程上重做一次**，
  防止池线程上算完时用户已经又点了一次。
* `ScanSourceUnitsAsync`：`Task.Run` 解析网格完之后，把"换 `sourceCandidates` + 重建下拉 +
  `BuildSourceUnitRows`"整段送回去。

即使将来有人又把这个 VM 挪回后台线程构造，这两条也不会再炸。

### 3.3 别再静默失败

* `ScanSourceUnitsAsync` 的 `catch` 从"只接 IO/数据异常"改成接全部：这是整条链路上唯一的重活，
  任何异常都必须进日志。
* 顺手修了一个老问题：`catch` 里写的 `SourceScanState = "计算失败：…"` 会被 `finally` 里的
  `UpdateSourceScanState()` **立刻覆盖**，所以失败提示从来没显示过。现在失败原因记在
  `sourceScanFailure` 字段里，由汇总条渲染。
* `RefreshPlanAsync` 失败时也补了一条 `LogService.Error`——原来只写界面，日志里什么都没有。

## 4. 另一件事：整张表"未定位"不等于算坏了

用户截图里 23 个 unit 全部 `部位=未定位 / 层=—`，但 `身形` 是真实的（壮硕/纤细/通用）。
这个组合只有一个来源：**catalog 里查到了这个 unit 的 part，但 `part.Cell` 是 `ArmorCell.Unknown`**。

`Cell` 来自 `game_data_unit_parts.authority_slot / authority_piece_type`，也就是注册表给的
权威格子。实测本机索引：**2002 个原版可视 unit，零个缺权威格子**（
`select count(*) from game_data_unit_parts where is_visual=1 and is_lod=0 and authority_slot<0` = 0）。
所以"缺格子"的 unit 一定是**非原版**的：

* 自定义素体 / 新增 unit（`RuntimeArmorRegistryService.ReadCustomModelCatalogAsync` 造的
  part 明确**不写** `Cell`，注释写着 "output-only targets"）；
* 或者索引陈旧（`authority_*` 两列是后加的，`AddColumnIfMissingAsync` 的默认值就是 `-1`）。

判定方法：截图里的 ID 都是 `0x09c3…` `0x1f8f…` `0x8d59…` 这种**小正数**，而原版 unit 的
file id 全部 ≥ `0x801b97ed5e51e9f4`。所以那 23 个 unit 是注册表分配的**虚拟 unit**，
不是原版 unit——它们本来就没有 GameData 归属，按格子装箱的规划器用不了它们，不能作为来源。

这条不是 bug，是设计边界；但界面原来只写"0 / 23"，等于不解释。现在：

* 汇总条补一句"这 N 个 unit 在注册表里查不到部位归属，不能作为来源"（部分缺时只报数量）；
* 每次重建表格时记一条日志：`替换护甲来源部件：共 N 个 unit，其中 M 个在注册表里查不到部位归属`。
  用户再报"全都未定位"时，日志能立刻区分"索引陈旧"和"虚拟 unit"。

## 5. 没做 / 待定

* **没有给自定义素体的 part 补 `Cell`**。`ReadCustomModelCatalogAsync` 里
  `piece.Slot` / `piece.PieceType` 本来就有，`new ArmorCell(piece.Slot, piece.PieceType)`
  一行就能让虚拟 unit 变成"已定位"，从而可以当来源。但那段注释明确写着
  "output-only targets"——虚拟 unit 只在来源 mod 已部署时存在，拿它当新候选的来源是个
  依赖关系问题，不是补个字段就完事。**这属于产品取舍，先不动。**
* 没有加自动化回归。`HD2ModManager` 至今没有测试工程，而这条 bug 的可观测行为依赖真实
  `CollectionView`（无 WPF 宿主时同一个错误路径不会抛）。要覆盖它得新开一个
  `net10.0-windows` + STA 的测试工程，先记在这里。
