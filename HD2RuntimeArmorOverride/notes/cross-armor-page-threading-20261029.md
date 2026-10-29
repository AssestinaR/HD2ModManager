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
权威格子。实测本机索引：**2002 个原版可视 unit，零个缺权威格子**
（`select count(*) from game_data_unit_parts where is_visual=1 and is_lod=0 and authority_slot<0` = 0）。
所以"缺格子"的 part 只可能来自**非原版**来源：

* 自定义素体：`RuntimeArmorRegistryService.ReadCustomModelCatalogAsync` 造的 part
  **没有写** `Cell`（旧注释写着 "output-only targets"）；
* 或者索引陈旧（`authority_*` 两列是后加的，`AddColumnIfMissingAsync` 的默认值就是 `-1`）。

判定方法：截图里的 ID 是 `0x09c3…` `0x1f8f…` `0x8d59…` 这种**小正数**，而原版 unit 的
file id 全部 ≥ `0x801b97ed5e51e9f4`。它们是注册表分配的**虚拟 unit**（高 48 位随机前缀 +
低 16 位组件序号，例如本机 `0x5293a817200a0001`）。

### 已修：自定义素体的 part 现在带格子

`ToPieceCell(piece)` 把组件自己声明的 `(slot, pieceType)` 直接当作权威格子。这不是猜——
素体定义里那两个数就是 `ArmorCell` 的两个坐标。只认 `ArmorStructure` 真的认识的槽位和
合法层号（`piece_type <= 2`），越界时保留 `Unknown`：宁可显示"未定位"，也不要造出一个
查不到部位、却因为坐标非负被装箱规划器当成真格子用的坐标。

于是替换护甲页左边那张表现在会显示 `胸部 · 内层` 这种真实部位，而不是一片"未定位"。

## 5. 但虚拟 unit 仍然不能当来源（实测结论，别被 `Cell` 骗了）

补上 `Cell` 之后我试着让虚拟 unit 真的能被选为来源，加了"把自定义素体当虚拟候选传进
`IModEquipmentSourceFactsReader`"的管道，然后做了端到端实测（把
`…\vpk2patch\9ba626afa44a3aa3.patch_0` 复制成独立 mod，跑真实 reader）：

```
patchPaths=9ba626afa44a3aa3.patch_0
sourceUnitKeys=7   virtualKeysInPatch=7   sourceCandidates=0
  virtual unit 0x5293a817200a0001 readable=False hidden=False meshes=0 transferable=0
  …（7 个全部一样）
issues=SourceUnitReadFailed × 7
```

7 个虚拟 unit 都在 Patch 的 TOC 里，但**一个都读不出来**。原因是
`PatchUnitDependencyPolicy.RequirePatchLocalComposite`：unit 引用的 Composite 资源
（`PatchUnitMeshReader.ReadCompositePayloadAsync`）不在同一个 Patch 里就直接抛
`InvalidDataException("Unit references Composite asset 0x…, but that entry is absent from this patch.")`。

这是**有意的安全策略**：复用来源 unit 的几何要求它的 Composite 也在 Patch 内，否则产出的
Patch 依赖游戏外部资源。候选生成器写出的虚拟 unit 复用了原 unit 的 payload，Composite 仍指向
游戏里的那份，于是永远过不了这条策略。

所以那条管道**已回滚**：它买不到任何东西，只多出 7 次必然失败的 unit 解析和 7 条
`SourceUnitReadFailed` 警告。要让虚拟 unit 当来源，得先决定"候选 Patch 要不要把 Composite
一起带上"——那是生产格式的问题，不是读路径的开关。

## 6. 顺手修掉的显示问题

`BuildSourceUnitRows` 原来把"有部位但没绑上几何"一律写成 **极小化占位**，而 `sourceCandidates`
为空时干脆什么都不写（行永远停在"待计算"、作为来源列一直是 `…`）。现在：

* 构造函数里 `unreadableUnits: null` → 行保持"待计算"（还没扫，别下结论）；
* 重测完成后传入 `facts.Units.Where(unit => !unit.IsReadable)` → 读不出来的写
  "读不出几何"，其余没绑上几何的写"极小化占位"；
* 汇总条三分支：全部没归属 / 部分没归属 / 有归属但一个都不可转移。

## 7. 没做 / 待定

* **候选 Patch 要不要带 Composite**：决定虚拟 unit 能不能当来源。属于生产格式，需要单独定。
* **没有加自动化回归**。`HD2ModManager` 至今没有测试工程，而线程那条 bug 的可观测行为依赖
  真实 `CollectionView`（无 WPF 宿主时同一个错误路径不会抛）。要覆盖它得新开一个
  `net10.0-windows` + STA 的测试工程，先记在这里。
* `Cell` 与"自定义素体可装箱"这两条有单测覆盖（`RuntimeArmorRegistryServiceTests`、
  `EquipmentUnitCatalogServiceTests`）；读路径那条（虚拟 unit 读不出来）没有单测，
  因为它依赖真实 Patch 里的 Composite 缺失，只能靠上面的实测记录。

