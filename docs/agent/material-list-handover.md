# 材质列表（交接给 UI 会话）—— 逻辑侧说明

> 状态：**逻辑层已完成并有测试；UI 完全没有做。**
> 上一轮我按自己的理解做了一版详情页卡片，被判定为误解了"modlist 卡"的含义，已完整撤回
> （见 §9）。这份文档只描述逻辑与契约，不规定外观。

---

## 1. 一句话：材质选项不是 Mod

一个「材质选项」= **主 Mod 基底目录里的一个 patch 文件**。

```
<mod>/
  sectional/
    9ba626afa44a3aa3.patch_0     ← unit（模型本体）
    9ba626afa44a3aa3.patch_1     ← 材质选项 1
    9ba626afa44a3aa3.patch_2     ← 材质选项 2
    patches.json                 ← 结构文件：每个 patch 组是什么
  <来源>.vpk × N                 ← 折并时搬进来的来源包
  Overwrite/                     ← 生效层：unit + 已启用的材质
  .hd2modmanager-mod.json        ← Mod 自己的设置（启用集合在这里）
  .hd2modmanager/options/        ← 材质配图
```

由此推出两条硬约束（已经写进逻辑层，UI 不要绕过）：

- **没有"删除"**：patch 还在目录里，它就在列表里。
- **没有"移出列表"**：不是库节点，没有可移除的关系。

---

## 2. 状态放哪

| 文件 | 装什么 | 谁写 |
|---|---|---|
| `sectional/patches.json` | `order`（部署顺序）、`optionName`（显示名）、`notes`（备注）、`imageFileName`（配图文件名）| `MaterialOptionSettings` |
| `.hd2modmanager-mod.json` | `enabledMaterialPatches`（启用了哪几个）+ 已有的 `id/name/notes` | `MaterialOptionSettings.SetEnabled` |
| `.hd2modmanager/options/<文件>` | 配图实体 | `MaterialOptionSettings.SetImage` |

**为什么顺序写在结构文件里**：需求原话是"记录在结构文件，不改 patch 文件，部署时重定向顺序"。结构文件是随 Mod 走的，搬家/复制不会丢。

**重建会保留用户字段**：`SectionalPatchManifestFile.Build(...)` 多收一个 `previous` 参数，按 patch 序号把 `optionName / notes / imageFileName / order` 带过来。重转、换甲、折并都会重写 `patches.json`——如果调用方不传 `previous`，用户改过的名字就没了。**新增任何"重建结构文件"的路径，都必须传 `previous`。**

---

## 3. 逻辑 API（`HD2ModCore.Infrastructure.MaterialOptionSettings`）

```csharp
// 列表：只返回 role == Material 的条目，已按 order 排好
public static IReadOnlyList<MaterialOptionView> Read(string modDirectory);

public sealed record MaterialOptionView(
    int PatchIndex,            // 结构文件里的序号
    string PatchFileName,      // "9ba626afa44a3aa3.patch_2"（启用集合用它）
    string? Name,              // 用户改的名字；null = 用默认显示
    bool Enabled,
    int Order,                 // 越小越先部署
    string? Notes,
    string? ImageFileName);    // 配图文件名（不是完整路径）

// 写：全部返回 string? 错误文本，null = 成功
public static string? Move(string modDirectory, IReadOnlyList<int> orderedPatchIndexes);
public static string? SetName(string modDirectory, int patchIndex, string? name);
public static string? SetNotes(string modDirectory, int patchIndex, string? notes);
public static string? SetImage(string modDirectory, int patchIndex, string? sourceImagePath); // null = 清除
public static string? SetEnabled(string modDirectory, IReadOnlyList<string> enabledPatchFileNames);

public static string GetOptionsDirectory(string modDirectory); // <mod>/.hd2modmanager/options
```

行为细节，别自己重算：

- `Read` 的 `Enabled` 判定：设置文件里**没有** `enabledMaterialPatches` 时，按「`optionName == 模型内置材质`」算启用——与物化器的默认一致。
- `Move` 只认给定的那几个序号；没列出来的排在后面，保持原有相对次序。
- `SetEnabled` 只改启用集合，`.hd2modmanager-mod.json` 里的 `id/name/notes` 原样保留（这条有测试）。
- `SetImage(null)` 清除；换图会把新文件复制进 options 目录并只把文件名写进结构文件。

---

## 4. 改完之后必须重建生效层

**这是最容易漏的一步**：写进结构文件/设置文件不等于进游戏。

```csharp
var enabled = MaterialOptions.Where(o => o.Enabled).Select(o => o.PatchFileName).ToArray();
MaterialOptionSettings.SetEnabled(dir, enabled);
var result = OverwriteMaterializer.Materialize(dir, new OverwriteSelection(enabled));
// result.Succeeded / result.Error 要说给用户听
```

`OverwriteMaterializer` 自己会处理两件事，UI 不用管：

- `Overwrite/` = unit 层 + 所有启用的材质；
- **unit 层取"生效层里已有的那份"**（换甲产物），没有才回落到基底——所以改材质选择不会把用户的护甲换回未换甲版本。真库 8 个换过甲的 Mod 里有 2 个字节确实不同，这条是修过的 bug。

改**顺序**之后也要重跑上面这段（顺序变了谁覆盖谁就变了）。

---

## 5. 顺序为什么真的影响部署（已实测）

部署排序键是 `(Mod 载入顺序, 归档, SourceOrder)`：

`patches.json.order` → `PatchFileIndexBuilder` 读到 `IndexedPatchFile.DeployOrder` → `ApplyPlanner` 用它当 `SourceOrder`。

实测（`Elise_Bill` 副本，把 `patch_2` 排到 `patch_1` 前面）：

| | 源序号 → 部署目标序号 |
|---|---|
| 排序前 | 0→0、1→1 |
| 排序后 | **1→0、0→1** |

即排在前面的先部署、排在后面的后部署（后部署的覆盖先部署的）。磁盘上的 patch 文件名、内容、mtime 全都没变。

用户口语上的理解应该是「列表里越靠下 = 越优先」。

---

## 6. UI 侧：应该复用什么

需求原话是「增加一个简单的 **modlist 卡**」，指的就是库页那张卡：`HD2ModManager.Views.ModListPanel`（`UserControl`）。

它已经提供：

| 能力 | 怎么用 |
|---|---|
| 行列表 + 空状态 + 可选搜索 | `ItemsSource` / `EmptyMessage` / `HeaderTitle` / `HeaderSummary` / `ShowHeader` |
| 行内动作按钮 | `RowActions="Rename,OpenFolder,MoveUp,MoveDown"`（`ModListRowAction` 是 flags）|
| **拖动排序** | `AllowInternalReorder="True"` + 行实现 `IModListInternalReorderable.CanInternalReorder` |
| 动作回调 | `RowActionInvoked`（`ModListRowActionEventArgs`）→ 调 `MaterialOptionSettings` |
| 拖动结束回调 | `InternalReorderRequested`（`ModListInternalReorderEventArgs`）→ 把新顺序写回 `MaterialOptionSettings.Move` |
| 选中态 | `ShowSelectionCheckbox` + 行实现 `IModListSelectable`（`SelectionKey` / `IsSelected`）|
| 行级动作抑制 | 行实现 `IModListRowActionFilter.HiddenActions` |

行对象只需要暴露模板认的成员：`Name`、`ImagePath`、`Description`、`RowPresentation`（`ModListRowPresentation`）。**不需要**是 `ModCardViewModel`，也不需要 `ModEntity`。

**明确不要出现的动作**：`DeleteFromLibrary`、`RemoveFromProfile`、`AddToProfile`、`ToggleDecoration`、`ExportMod`。

新文件放在哪由 UI 会话决定；建议不要改 `ModListPanel` 的公开契约（库页也在用它）。

---

## 7. UI 需要自己拍板的两件事

1. **启用/禁用的表达方式。** `ModListPanel` 现在的行模板里**没有**开关控件：
   - `ShowSelectionCheckbox` 绑的是 `IsSelected`，语义是"选中"，不是"启用"；
   - `ModListRowAction` 里也没有 Toggle 类动作（只有 `ToggleDecoration`，那是装饰包专用）。
   
   可选：① 给 `ModListRowAction` 加一个 `ToggleEnabled`（会影响库页，需确认不冲突）；
   ② 用 `RowPresentation.ActivationLabel` 显示「已启用/未启用」，点击行触发切换（库页对 Option Mod 就是这么表达的）；
   ③ 在材质卡里另加一行自带开关的模板。
   
   **这条我没做，也不敢替你定。**

2. **拖拽结束的落盘时机。** `InternalReorderRequested` 给的是新顺序；写回之后要立刻重建生效层（§4），并给一条可见反馈（"顺序已更新，未自动部署"之类）。

---

## 8. 不做 UI 也能验逻辑的 CLI

```
hd2mod mod show <GUID> --json
    → mod.materialOptions[]: patchFileName / optionName / notes / imageFileName / order / enabled

hd2mod mod material set   <GUID> <patch1,patch2|none> --confirm
hd2mod mod material order <GUID> <patch1,patch2,...>  --confirm
```

`mod show` 和 UI 共用同一个 `MaterialOptionSettings.Read`——两个界面各写一份读法的话，同一个库会显示成两种样子。

---

## 9. 我做过又撤掉的东西

提交 `a8d9c06` 里包含一版自制的详情页卡片（新 `CardKind.MaterialOptions`、独立行模板、行内 ▲▼/"配图"/"文件夹" 文字按钮）。**已全部撤回**，理由：那不是"modlist 卡"，UI 会话在这上面继续改只会越走越偏。

留在仓库里的逻辑侧东西（`a8d9c06` + `d9f46d0` 里与 Core/CLI 有关的部分）都还在，可以放心用：

- `HD2ModCore/Infrastructure/MaterialOptionSettings.cs`
- `HD2ModCore/Domain/SectionalPatchManifest.cs`（`Order` / `Notes` / `ImageFileName` / `BuiltInMaterialOptionName`）
- `HD2ModCore/Infrastructure/SectionalPatchManifestFile.cs`（`Build(..., previous:)` 保留用户字段）
- `HD2ModCore/Domain/IndexedPatchFile.cs`（`DeployOrder`）
- `HD2ModCore/Infrastructure/PatchFileIndexBuilder.cs`、`ApplyPlanner.cs`（顺序进部署）
- `HD2ModCore.Tests/MaterialOptionSettingsTests.cs`（7 个用例）

---

## 10. 真库现状（折并之后）

10 个 Mod，其中 9 个 sectional 类，材质选项共 43 个：

| Mod | 材质选项数 |
|---|---|
| Lilium_Sancta_Bill | 12 |
| Lunalice_Bill | 12 |
| MourningVeil_Bill | 10 |
| Elise_Bill | 3 |
| `[huahua]Chiyu公式服-替换bill` | 2 |
| Manuka_JABIS_Bill / Manuka_LaFiore_Bill / `l4n…信浓_bill` / `恋星milfy_bill` | 各 1 |

`恋星milfy_bill` 的 1 个是「模型内置材质」（`patch_1`，模型自带）；其余都是折并进来的材质包。

---

## 11. 一句话总结给 UI 会话

> 逻辑已经全在 `MaterialOptionSettings` 里，读写都是"传目录、传序号/文件名、拿回错误文本"。
> 你要做的只是：用 `ModListPanel` 把这些条目画成一张 modlist 卡，行内动作限制成
> 改名/打开文件夹/上移/下移（**没有删除**），`AllowInternalReorder` 打开拖动，
> 再加一个启用开关；每次改完调用一次 `OverwriteMaterializer.Materialize`。

---

## 12. UI 侧的决定（已落地，提交 20439a3）

§7 那两条需要拍板的事，UI 会话的选择如下：

**① 放哪。** 详情页里直接摆一张 `ModListPanel`，作为 `CardStackHost` 的**兄弟节点**
（和「可为该 Mod 启用的附加选项」`HostAttachmentList` 完全同一种做法）。
不新增 `CardKind`：那张面板自带 header 卡 + 列表卡，塞进卡片栈就是卡片套卡片。

**② 启用/禁用。** **照抄同页那张「可为该 Mod 启用的附加选项」卡的做法**：
一个电源图标的行内动作（`ModListRowAction.ToggleEnabled`），状态用行内文字
`已启用 / 未启用` 表达。**不加开关**——行模板是全应用共用的一份，不该为某一张卡
多画一种状态控件；那张卡本来也没有开关。

> 教训（第一版就是在这里走偏的）：第一版给行尾加了一个 `AppToggleSwitch`，
> 还为此给 `ModListPanel` 加了 `ShowEnabledSwitch` + `IModListToggleable`。
> 那不是"照抄"，那是从 **VPK 组件卡**（用的是开关列表）借了一个视觉相似的实现。
> 同一页里语义更近的是 `HostAttachmentList` 的 `ToggleDecoration`：
> 提示原文就是"启用或禁用选项"。
> 文档里写"不要出现 ToggleDecoration"是指**不要用那个动作**（装饰包专用），
> 不是"不要用那种做法"。

### 落盘与物化

所有写操作收口在 `ModDetailsPageViewModel.Materials.cs`：

| 操作 | 逻辑 | 之后 |
|---|---|---|
| 点电源胶囊 | `SetEnabled`（按当前列表拼启用集合） | 物化 |
| 上移/下移 | 重排后 `Move(orderedPatchIndexes)` | 物化 |
| 拖动排序 | 同上，插入点按"先把被拖的行拿出去"的坐标系修正 | 物化 |
| 改名 | `SetName`，走底栏通用输入条 | 物化 |

失败时把状态文字推回原值，错误写在卡片下方的状态行（不弹窗打断）。
同一时刻只允许一个物化在跑（`_materialOperationInFlight`），后到的写操作直接返回并提示。

### 仍然没做的

- **备注（`SetNotes`）与配图（`SetImage`）没有入口**：§11 把行内动作限定成
  改名/打开文件夹/上移/下移，所以这两个 API 目前只有 CLI 在用。
  要给它们 UI，最省的位置是同一个「改名」输入条旁边再加一列动作。
- 行的缩略图沿用 modlist 行模板的 36×36 占位块；配图进 UI 之后才会真的显示出来。
