# 交接：编译层与头身合并（2026-11-08 16:20）

> **⚠️ 已封存（2026-11-08 晚）。** 编译层已按 `docs/agent/sectional-compiler-plan-20261108.md` 重写并落地：
> 本节里"必须搬进 Core""不得走子进程"的架构结论不再适用，附录 A 还列出了本文的 6 处事实错误。
> **需要口径时读新计划文档，不要读这一篇。** 保留原文只为记录当时的排查过程。

> 接手的人先读这一篇，再读 `docs/agent/sectional-compile-base-20261108.md`（那篇是架构口径）。
> 下面所有行号都以本文件的提交 `ed754da` 为准，改动后请自行核对。

---

## 0. 一句话

**编译层（sectional → Overwrite）必须自己完成"替换护甲"，不得通过 `vpk rekey` 这个 CLI 命令（子进程）。**
当前 GUI 里"目标护甲 = 某件游戏护甲 + 头身合并 = 开"这条路会失败，就是因为它走了子进程。

---

## 1. 绝对边界（用户反复强调过，违反过一次就要重来）

1. `sectional/` 是 **patch 级权威事实**：只由转换链路（`vpk2patch` / `legacy2sectional`）产出，形状固定
   （自定义素体 + 标准 7 槽位 + 材质 patch 组）。**任何其它链路都不许改它**——换甲、section 开关、
   头身合并、材质开关全都不行。要改它只能是一次**明确授权的迁移**。
2. `Overwrite/` 是**唯一生效层**：只由编译产出。
3. 链路只有四段：**导入 → 转换为 sectional → 编译（条件）→ 部署**。
   额外的包装链路**只能调用**这些基座，不许各自新增实现；要加功能就改本体或在本体上开接口。
4. 编译层**不做优化**：每次从 `sectional/` 全量重算，不做增量、不做缓存、不做"看起来没变就跳过"。
   宁可慢，也不要能跟盘上状态不同步的分支。
5. 头身合并是编译层的条件**之一**（与目标护甲、section 开关、材质开关并列）。
   `vpk2patch --head-merge` 属于"把合并烘进 sectional"，是**协议外**的危险路径，只留给 agent 且有同意门。

---

## 2. 现在的 bug（用户 2026-11-08 报的）

**现象**：详情页点「将头盔合并到身体」，**有时候**弹出一条 CLI 的告警文字：

```
--head-merge on 是实验 / 不安全功能（它改变头画在哪一件装备上，还会让那件头盔 item 变成空模型）…
```

并伴随一条「开启头身合并 · 失败·处理失败」。

**触发条件（"有时候"的真相）**：目标护甲是**游戏护甲**时才发生；目标是**自己的自定义素体**时一切正常。

**机制（逐行）**

| 位置 | 事实 |
|---|---|
| `HD2ModCore/Infrastructure/ModCompileService.cs:83` | `isOwnModel = targets is null ‖ 全部以 "custom-model:" 开头` |
| 同上 `:89-103` | **非**自身素体 → 调 `VpkRekeyService.RunAsync(...)` |
| `HD2ModCore/Infrastructure/VpkRekeyService.cs:117` | 拼 `--head-merge on` |
| 同上 `:119` | `Hd2ModProcess.RunAsync(executable, …)` → **起子进程** |
| `HD2ModCli/CliApplication.cs:3086-3101` | `EnsureHeadMergeConsent`：`--head-merge on` 缺 `--head-merge-consent` → 抛 `HeadMergeConsentRequired` |

→ 子进程被拒 → `VpkRekeyService` 把 CLI 的 stderr 文本当成错误返回 → GUI 把它显示成通知 + 失败。

**这是设计错误的后果，不是"概率"**：分叉取决于目标护甲的**种类**。

---

## 3. 用户已经明确给出的正确实现（照抄即可，不要再设计）

头身合并只有两种情况：

### ① 目标 = 自定义素体（**已实现**，不要动）

- unit id **不变**；
- 直接改 `custom-model.json`：头 unit 的注册件从 `slot 0 / layer 0` 挪到 `slot 2 / layer 1`，
  记 `movedFrom: "slot0/layer0"`；
- **写进 `Overwrite/custom-model.json`**，`sectional/custom-model.json` 一个字节都不动（权威留档）；
- 生效读取：`CustomModelCatalog` 优先 `Overwrite/`；`VpkCustomModelFile.TryReadKey/TryReadName`
  仍以来源为准；按 key 找持有者时跳过 `Overwrite/` 派生副本。

实现位置：`HD2ModCore/Infrastructure/ModCompileService.cs`
（`CompileOwnModelAsync` + `WriteMergedCustomModel`，提交 `5a6d7a2`）。

### ② 目标 = 原版护甲（**这是要做的**）

用户原话：

> 这里根本不需要 vpk rekey。只需要记录一个小改变：让它在替换护甲的流程中，
> 把来源头要转变的目标头的 id 更改为**目标胸的层级 1** 的 id。

也就是：换甲本来就要把"来源 unit → 目标 unit"一一对应；**只把头那一件的目标 unit
改成目标胸部 slot 的 layer 1 那个 unit**。头盔套件不写 → 现有
`AutoHideUnmappedTargetUnits` 会把它顶成隐藏壳，于是游戏里那件头盔 item 没有模型。

**不要**再写一套"选最外层/次外层"的合并逻辑（`ResolveHeadBodyMerge` 那套是这个思路，用户否掉了）。

---

## 4. 代码地图（改哪里、搬什么）

### 已经在 Core、可直接用

| 文件 | 作用 |
|---|---|
| `HD2ModCore/Infrastructure/CanonicalCrossArmorOrchestrator.cs` | **换甲引擎**（`ExecuteAsync`），同进程 |
| `HD2ModCore/Infrastructure/OverwriteMaterializer.cs` | 组装 `Overwrite/` 的 patch 部分 |
| `HD2ModCore/Infrastructure/WardrobeSectionBaker.cs` + `WardrobeSectionMapFile.cs` | section 烘焙与段表 |
| `HD2ModCore/Infrastructure/ManagedOverwriteDirectory.cs` / `OverwritePatchInstaller.cs` | 写 `Overwrite/` 的安全原语 |
| `HD2ModAdaptation/…/EquipmentUnitCatalog.cs` | `CrossArmorTransferPlan` 等计划类型 |
| `HD2ModCore/Infrastructure/ArmorAuthorityIndexReader.cs` | 装备权威表（索引库）读取 |

### 还留在 CLI、需要搬（或在 Core 里重写薄壳）

`HD2ModCli/CliVpkRekey.cs`（**959 行**）：

| 行 | 内容 | 处理 |
|---|---|---|
| `97` | `RunAsync(CliWorkspace, modSelector, targets, outputRoot, disabled, mergeHead, ct)` | 改成 `RunAsync(modId, modDirectory, StoragePaths, gameDataRoot, targets, disabled, mergeHead, ct)` |
| `112-116` | `workspace.LoadLibraryAsync` / `CliProjections.ResolveMod` / `new StoragePaths(...)` | **删除**（调用方给目录与 paths） |
| `138-140` | `modDirectory = Path.Combine(paths.ModsDirectory, node.RelativePath)` | 改成参数 |
| `144-148` | 从 `sectional/` 找 unit patch（`FindUnitPatch`） | 保留，原样搬 |
| `152` | 读 `sections.json` | 保留 |
| `158` | `BuildSourceEntry(node, sectionMap)` | 改成 `BuildSourceEntry(displayName, sectionMap)` |
| `186-190` | `AdoptIfUnitLayer` + `EnsureOverwriteIsWritable` | 保留 |
| `196` | 自身素体分支 → `RestoreIdentityOverwriteAsync` | 保留（编译层已有等价实现，二选一，别两份都留） |
| `252` | `new CanonicalCrossArmorOrchestrator().ExecuteAsync(...)` | 保留 |
| `301/313` | 写 `sections.json` / `components.json` 到 Overwrite | 保留 |
| `512-513` | `CliEquipmentFacts.LoadAsync/Project` | 搬到 Core（该文件 129 行，只是 `ArmorAuthorityIndexReader` 的薄壳） |
| `646` / `839` | `ResolveHeadBodyMerge`（选 HeadLayer/BodyLayer） | **删掉**，按 §3② 的"映射改一个目标 unit"重做 |
| `717-727` | 现有合并映射（头→最外层、身体→次外层、跳过头盔 kit） | 同上：改成"头 → 胸部 layer 1" |

### 不该再出现的调用

- `HD2ModCore/Infrastructure/VpkRekeyService.cs`（起子进程那一层）：编译层与 GUI **都不该用**。
  建议：GUI/重构建改调新的 Core 步骤；`vpk rekey` 保留给 agent（继续带同意门）。

---

## 5. 我踩过的坑（别重复）

1. **误删 UI**：用脚本按 `Grid Margin` 切区间删"头身合并"那一行，把上面的「section 组件开关」一起删了。
   改 XAML 请逐块确认。
2. **旧二进制冒充新代码**：只 `dotnet build <单个项目>`，管理器 `bin` 里的 `HD2ModCore.dll` 是旧的，
   验证结论全错。验证前必须 `dotnet build -c Debug`（整解决方案）并核对 dll 时间戳。
3. **顺手"优化"**：删掉两次 `LoadVpkAsync` 想止抖 → 目标护甲不再回读，下拉弹回旧值。
   正确做法是"进页面按盘上记录重选"（已修，`9bce6cd`）。
4. **加"目标没变就跳过"**：状态一不一致就出现"没有变化"而产物是旧的（已删，`256bb57`）。
5. **把编译层实现成子进程**：就是现在这个 bug。
6. **消息口径**：`outcome.Rekey?.HeadMerge` 在自身素体路径是 `null`，曾导致"永远说已关闭"（已修）。

---

## 6. 验证（沙箱，可复现）

- 沙箱库：`E:\tmp\rb2-mods`（一个 Mod：`Lunalice_Bill`，GUID `b000a8cb-a4bd-4b4c-8a6a-dc1ff39bb04b`）
- 资产索引：`E:\Data\source\repos\WpfApp1\HD2ModManager\bin\Debug\net10.0-windows\data\indexes\asset-index.sqlite`
  （`--manager-root` 指到那个目录）
- 游戏 data：`D:\SteamLibrary\steamapps\common\Helldivers 2\data`
- 命令：
  ```
  hd2mod mod rebuild b000a8cb-a4bd-4b4c-8a6a-dc1ff39bb04b \
    --manager-root <上面那个 bin 目录> --library E:\tmp\rb2-mods \
    --game-data "…\Helldivers 2\data" --confirm --json
  ```
- **自身素体 + 合并开**的期望（已验证）：
  - `rekeyed=false`；
  - `Overwrite/custom-model.json` 里头件 `slot 2 / pieceType 1 / movedFrom "slot0/layer0"`；
  - `sectional/custom-model.json` 的 SHA256 前后一致。
- **原版护甲 + 合并开**：目前会失败（本 bug）。改完后期望：
  - 头那件映射到的目标 unit = 目标胸部 slot 的 layer 1；
  - 头盔 kit 全部走隐藏（`AutoHideUnmappedTargetUnits`）；
  - `Overwrite/patch_0` 里头的几何落在目标胸部 unit 上。

---

## 7. 发布状态

- **1.5.2 已发布**（`https://github.com/AssestinaR/HD2ModManager/releases/tag/v1.5.2`），
  包含本 bug（影响面：**目标=游戏护甲 + 开了头身合并** 的用户）。
- 修好后建议直接出 **1.5.3**（只含这一处；发布流程见 `.github/workflows/release.yml`，
  注意：给已发布 Release 改说明时 `PATCH` 必须带 `tag_name`，否则会被打回 `untagged-…`）。

## 8. 未决

- 编译层里"自身素体的编译"目前有两份等价实现（`ModCompileService.CompileOwnModelAsync` 与
  `CliVpkRekey.RestoreIdentityOverwriteAsync`）。搬完换甲那步后应只留一份。
- `Overwrite` 还有别的直接写者（`HostDerivedOverwriteService` / 折并 / 批量修复 / physbone 嵌入），
  只针对 legacy 类，与 sectional 无关——用户认为**合理**，不用动。
