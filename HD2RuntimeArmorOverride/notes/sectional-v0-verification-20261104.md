# V0 验证配方：跨槽搬几何会不会动（离线 + 实机）

> 目的：在把"7 槽 × 身形"当真之前，用**一条**最小垂直切片回答三个"静默少几何"的入口
> 到底会不会咬人。三个入口都不报错，只在游戏里表现为"某块没了"，所以只有它俩能当裁判：
> **离线看包围盒 / 段数**，**实机看轮廓**。
>
> 设计依据：`sectional-u0-findings-20261104.md` §3、§6.1。
> 工具：`SectionalSplitter`（`HD2ModCore.Infrastructure`，U3c 交付）。

---

## 0 三个入口，各自的判据

| 入口 | 机制 | 离线能不能判 | 实机表现 |
|---|---|---|---|
| **同键并段** | donor 的段与目标共用同一 `MaterialId` ⇒ 材质槽复用 ⇒ `MergeSectionsByMaterialSlot` 把两段并成一段 | ✅ **能**：数段数 | 两块几何同生共死，关一个另一个也消失 |
| **LOD 不齐跳过** | `CanonicalCrossArmorOrchestrator.cs:497-512` 在 donor 缺对应 LOD 时直接 `continue` | ✅ **能**：逐级 LOD 数段数 | 距离远了才丢件 |
| **深度代理遮挡** | 隐藏 donor 槽位后它的 `lod=-1` depth-only 代理仍在原处 | ❌ 不能 | 某块几何整段不显示，且**不是**品红 |

外加一个现存缺陷：**`CullingBounds` 从不重算**（W1 已做成能力，默认关）。
判据是 `vertices_outside == 0`，离线可算。

---

## 1 离线部分（不需要进游戏）

### 1.1 准备样本

需要一份**多 unit**的 patch：主模型 7 个标准槽 + 至少一个"非标准槽"的 donor
（护肩最典型，本体有 162 个 `LeftShoulder` unit）。

本机目前没有现成的这种 legacy 模型 Mod（唯一的 legacy 形态是纯材质包），
所以要自己造。两条路，任选：

**路 A：从游戏本体护甲取 donor**
`GameDataUnitMeshReader.ReadAsync(archive, key)` 读一件带护肩的护甲的 slot 8，
和同一件的 slot 2 一起拼成一份"主模型 + 护肩"的样本。

**路 B（更省事）：拿任意两个已有 7-unit 产物的 patch 手工并成一份**
把两份 patch 的 unit 条目合到一个文件里，其中一份的 `slot` 字段改成和另一份相同
（改 TOC 里的语义字符串）。这样规划器就会把两套几何分进同一个 `(槽, 身形)`。

> 路 B 是"为了造样本而造样本"，但它能精确触发三个入口，且不依赖游戏数据。

### 1.2 跑预演

```powershell
Add-Type -Path '<repo>\HD2ModCore\bin\Debug\net8.0\HD2ModCore.dll'
Add-Type -Path '<repo>\HD2ModAdaptation\bin\Debug\net8.0\HD2ModAdaptation.dll'

$r = [HD2ModCore.Infrastructure.SectionalSplitter]::PlanAsync($samplePatch).GetAwaiter().GetResult()
$r.Item1.Targets   # 每个槽：host / donors
$r.Item1.Groups    # 每个组：名字 / 互斥组 / 默认开
$r.Item2           # 警告
```

**判据**：
- 有 donor 的那个槽 `DonorUnitIds.Count > 0`；
- 每个槽只出现一次（同一 `(slot, body)` 不会被拆成两个 target）；
- 没有"非标准槽"警告以外的意外。

### 1.3 跑执行 + 数段数（这是"同键并段"的裁判）

```powershell
$out = '<临时目录>'
$result = [HD2ModCore.Infrastructure.SectionalSplitter]::ExecuteAsync($samplePatch, $gameData, $out).GetAwaiter().GetResult()
$result.IsValid
$result.Warnings
$result.Sections.Units | ForEach-Object { "$($_.FileId)  $($_.Slot)  段数=$($_.SectionCount)" }
$result.Groups.Groups | ForEach-Object { "$($_.Name)  ->  $(($_.Members | ForEach-Object { "$($_.Unit):$($_.Sections -join '/')" }) -join ' | ')" }
```

**判据（三条，缺一不可）**

1. **段数对账**：`产物该槽的段数 == 宿主段数 + 所有 donor 段数`。
   少了就说明"同键并段"发生了 —— `uniqueSlotPerSection` 没起作用。
2. **组员不交叠**：同一个 `(unit, section)` 不该同时出现在两个组里。
   交叠说明"两块几何同生共死"，界面必须把它标成**耦合**而不是两个独立复选框。
3. **逐级 LOD**：把产物按 LOD 再数一遍段数。某一级比 LOD0 少，
   说明"LOD 不齐跳过"咬了 —— 那级在游戏里会丢件。

### 1.4 包围盒（可选，但很便宜）

```powershell
$env:HD2_CANONICAL_CULLING_DIAGNOSTICS = '1'
# 再跑一次 ExecuteAsync，然后读 logs\canonical-culling-diagnostics.log
```

**判据**：每个网格 `outsideAfter == 0`。不是 0 说明保守并集还不够，
或者坐标空间对不上（那是更严重的问题，先查坐标空间）。

---

## 2 实机部分（必须做，只有它能判深度代理）

### 2.1 装上去

1. 把产物目录当成一个 Mod 导入管理器（或直接覆盖某个 Mod 的 `vpk2patch/`）。
2. 部署到游戏，选一件**和样本同一个槽位**的护甲。
3. **重启游戏**（改磁盘 patch 不等于运行中热切换）。

### 2.2 同机位三张

进游戏后**不要动鼠标视角**，军械库预览：

| 张 | 机位 | 看什么 |
|---|---|---|
| A0 | 正面，默认距离 | 基线：两臂完整、接缝贴合 |
| A1 | 侧面 90°（搬过几何那一侧朝镜头） | 肩部接缝、袖根有没有裂缝 |
| A2 | 跑动中（那侧手臂抬起） | 蒙皮有没有被拉断 |

### 2.3 判据

| 现象 | 结论 |
|---|---|
| A1 与基线轮廓 **IoU ≥ 0.98**、质心偏移 **≤ 2 px**（1080p） | 位置没变 ✅ |
| 质心偏移 **> 10 px** | 位置变了 ❌ —— 方案必须强制走追加链路 |
| **某块整段消失、游戏不崩** | 深度代理遮挡，或 `CullingBounds` 没重算 |
| **军械库整页空白 + 崩** | 隐藏 unit 的 id 不存在（空壳写错了） |
| 袖根出现 **> 3 px 的裂缝 / 穿插** | 跨槽合并的层序或遮挡有问题 |

### 2.4 第四步（只在 2.3 命中"整段消失"时才做）

对比两种隐藏壳：
- **B1**：现行 `patch hide-unit`（`lod=-1` 代理**保留**）
- **B2**：可见 LOD + `lod=-1` 代理**一起**最小化（`MinifyUnmappedCullingMeshes = true`）

各拍一张 A1。B1 被挡、B2 正常 ⇒ 代理遮挡成立，方案必须默认 B2。
两者都正常 ⇒ 跨槽场景下代理不构成遮挡，那条风险降级为可选。

---

## 3 结果怎么用

- **三条离线判据全过 + 实机位置没变** ⇒ "7 槽 × 身形"这个形态可以往下做（组编辑器接拆件）。
- **任一条离线判据不过** ⇒ 先修 `uniqueSlotPerSection` / LOD 对齐 / 包围盒，**不要**进游戏浪费时间。
- **离线过、实机某块消失** ⇒ 深度代理，按 §2.4 处理。

---

## 4 已知缺口

- 本机没有多 unit 的 legacy 模型样本，§1.1 的两条路都要先造数据。
- `SectionalSplitter` 目前只吃**一个 patch 文件**；游戏本体护甲（`.nxa` 里）要先导成 patch 才能当输入。
- donor 合并那一段（`TryAppendLodFamily` 在拆件语境下）还没有实机证据 ——
  它依赖的追加链路本身是发版行为，但"拆件这个用法"没被单独验证过。
