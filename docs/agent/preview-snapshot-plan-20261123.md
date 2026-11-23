# 模型快照（preview snapshot）规划 v1

日期：2026-11-23
状态：**规划，待审**
定位：v1 主要服务于 **CLI / agent 迭代回路**；GUI 的「预览图片」卡是顺手的产物，不是主要目标。

---

## 1. 为什么做

现在"模型改对了没有"只能进游戏肉眼看。项目里已经有 `tools/UiSnapshot` 这套**离屏渲染 UI 截图**的回路，但**几何**没有对应物。补上之后：

- agent 可以在 CLI 里对任意 patch 的任意 unit 集合出图，自己看轮廓/比例/朝向/穿插，不用人来截图；
- 用户在 Mod 详情页最下方能直接看到这个 Mod 拍出来的图；
- 以后"换甲/素体/合并"这类改动的验收多一条客观依据。

## 2. 范围

### 做
- patch（含 `Overwrite/` 生效层）里的 unit 网格 → 离屏光栅化成 PNG。
- 相机：轨道式（拍摄中心 + 三个旋转角 + 缩放），**文件名只记这 7 个数**。
- CLI：自由模式（自定义 unit 集合 / 自定义角度集合，一次一个会话目录）+ 标准模式（写进 Mod 的 `img/`）。
- GUI：Mod 详情页最下方「预览图片」卡，只支持 sectional 类 Mod（见 §4），后台异步生成，能标记旧图。

### 不做（v1 明确排除）
- **不做贴图**。理由：仓库唯一的贴图解压路径是外部 `texconv.exe`（`Bc7Encoder`），不能成为出图的前置依赖；而 v1 的目的是看形状。渲染用材质分色 + 光照。
- **不动导出**。`img/` 不随 `ModExporter` 走，导出仍只做 patch。
- 不做骨骼动画 / 姿势驱动（按绑定姿势渲染）。
- 不做材质/贴图正确性预览（那是另一条线）。
- 不做 GUI 的自由角度输入（GUI 只从预设视图表里选）。

## 3. 既有资产（全部复用，不重造）

| 能力 | 位置 |
|---|---|
| 列 patch 里的资产 | `PatchTocScanner.ScanEntriesAsync` |
| 取资产载荷 | `PatchEntryPayloadReader.ReadPayloadAsync` |
| unit → 网格 | `UnitMeshReader`（`RawMeshData`：顶点记录 + 三角形 + section + LOD + 材质槽） |
| 顶点分量语义 | `CanonicalStreamContractCompiler`：type 0 = position(vec3_float)、1 = normal(unk_normal, oct 打包)、4.. = uv(vec2_float)；解码用 `CanonicalMeshPreparation.DecodePackedOctNormal` |
| 生效载荷判定 | `ModDeployableFiles.Resolve` / `ModNodePayloadLayout.EnumerateEffectiveFilesByName`（**与部署同口径**） |
| 内容指纹范式 | `ThumbnailFingerprint`（路径+长度+mtime，不含路径时要小心） |
| PNG 编码范式 | `ThumbnailService.EnsureThumb`（`PngBitmapEncoder`，但那是 WPF；v1 走自写编码器） |
| 异步卡范式 | `ModDetailsPageViewModel.KitPollution.cs`（版本号令牌 + `RunOnUiThread` + 占位文案） |
| 卡片插槽 | `ModDetailsCardLayout` / `ModDetailsCardState` + `Sync` |
| CLI 写盘纪律 | `CliModImageOperations`（staging、verify-before-mutation、reparse point 守卫、路径逃逸守卫、`CliUserException` 错误码） |
| 原子写 | `KitInstanceFiles.WriteAtomicTextAsync` / `RuntimeArmorRegistryService.WriteAtomicTextAsync` |

## 4. 分区：哪些 Mod 能拍

**主体 = 该 Mod 的生效载荷 unit 集合。** 不是"自定义模型"，因为 `legacy2sectional` 与 `vpk2patch` 未必都有 `custom-model.json`。

| `ModStorageKind` | GUI 卡片 | 理由 |
|---|---|---|
| `Sectional`（含 legacy2sectional 产物） | ✅ 显示 | 恒为单主体；有组件树可切 |
| `Vpk2Patch` | ✅ 显示 | 恒为单主体 |
| `Legacy` / `Logical` / `Unknown` | ❌ 不显示卡片 | 一份载荷可能是包（多个模型），"一个主体"不成立 |

- 不显示 ≠ 显示成灰。按仓库规则，按钮/卡片要么能用要么不存在。
- CLI 不受此限：CLI 永远可以指定任意 patch 的任意 unit 集合。
- `Vpk2Patch` 的"单主体"是**当前转换管线的结论**，不是格式保证。将来若出现一份 VPK 转出多个模型的包，卡片退化为"渲染整份载荷"，`subject.units` 列出全部 unit id。
- 该显示卡片但载荷读不出来（缺 `sectional/`、patch 损坏、没有任何可视 unit）：卡片**显示原因**，不静默隐藏（仓库规则：错误必须可见）。

## 5. 架构分层

```
HD2ModAdaptation（net8.0，新增）
  Preview/PreviewSceneBuilder   patch/unit → 场景（顶点、三角形、法线、材质槽、包围盒）
  Preview/PreviewRenderer       z-buffer 光栅化 + 着色 → RGBA 缓冲
  Preview/PngWriter             最小 PNG 编码（ZLibStream + CRC32，零依赖）
  Preview/PreviewCamera         相机规格 + 量化 + 规范化
  Preview/PreviewNaming         文件名投影
  Preview/PreviewManifest       预览 JSON 的读写（原子）

HD2ModCore（net8.0，新增薄层）
  Preview/PreviewSnapshotService   编排：找生效载荷 → 建场景 → 循环相机 → 落盘
  Preview/PreviewStaleness         旧图判定（三个指纹）

HD2ModCli（net10.0）
  CliRenderCommand                 自由模式 / 标准模式

HD2ModManager（net10.0-windows）
  ViewModels/ModDetailsPageViewModel.Previews.cs   卡片（异步 + 版本号令牌）
  Views 里新增一张卡的 XAML
```

**渲染器必须放在 net8.0 层**，不许用 WPF：`HD2ModCore`/`HD2ModAdaptation` 是 net8.0，`HD2ModCli` 是 net10.0（无 WPF），只有 `HD2ModManager` 是 `net10.0-windows + UseWPF`。放 WPF 等于把渲染锁进 UI 项目，CLI 就拿不到 —— 而 CLI 才是 v1 的主要消费者。

## 6. 相机规格（唯一真相）

```
subject   = 生效载荷的 unit 集合（GUI 恒为一个；CLI 可任意）
frame     = 由 subject 的 LOD0 可见网格算出的包围球（中心 C、半径 R），确定性
ups       = **+Z**（P0 实测，见 §16 R1）
front     = **+Y**（P1 实测：yaw=0 拍到脸与领结，yaw=180 拍到腰后蝴蝶结）
pivot     = 归一化拍摄中心，相对 C、以 R 为单位；[-1, 1] 之外允许，但 UI 只用 0
yaw       = 绕 +Z，0 = 正视图（相机在 +Y 一侧朝 −Y 看）；无符号，规范到 [0, 360)
pitch     = 仰角，+ = 相机抬高俯视；规范到 [-89.5, 89.5]（保留 up 向量稳定）
roll      = 滚转；规范到 [-180, 180)
zoom      = 相对自动取景的倍数，> 0
projection= 恒定透视，竖直 FOV 35°，近平面由 R 派生
```

- 相机位置由 `pivot + yaw + pitch + zoom` 唯一确定；**不单独记录相机世界坐标**（多一个会漂的量）。
- `pitch` 上限定 89.5 而不是 90：正交之外的俯视图在 90° 时 up 向量退化，会出 NaN。
- **模型右手边是 +X**（正面 +Y、上方 +Z 的右手系）：所以 `yaw=90` 是右视图、`yaw=270` 是左视图（§7 的预设表已按此订正）。
- 主光是**相机相对**的（左上前方），不是世界固定：世界固定的光会让转台一半视角背光。

## 7. 命名（文件名 = 可读投影，规格在 JSON）

```
p+0.000+0.000+0.350_a037.50_e+20.00_r+00.00_z1.00.png
```

| 字段 | 含义 | 格式 |
|---|---|---|
| `p±d.ddd` ×3 | pivot，归一化（§6） | 3 位小数，带符号，固定宽度 |
| `aDDD.DD` | yaw | 3 位整数 + 2 位小数，**无符号**（0..360），所以文件名能自然排序 |
| `e±DD.DD` | pitch | 2 位小数，带符号 |
| `r±DDD.DD` | roll | 2 位小数，带符号；**恒出现**（不省略，保持字段数固定、解析无分支） |
| `zD.DD` | zoom | 2 位小数 |

长度 ≈ 55 字符；硬上限 96。超了不截断（截断即撞名），报错并让调用方拆主体。

### 让"名字即规格"成立的 5 条不变量（写死，不许破）

1. **先量化后渲染**：pivot/yaw/pitch/roll/zoom 在进渲染器**之前**就 round 到上表精度，渲染器只接受量化后的值。
2. **规范化**：`-0.00 → 0.00`；yaw 绕回 [0, 360)；pitch 夹到 [-89.5, 89.5]；roll 绕回 [-180, 180)；zoom ≤ 0 直接拒绝。
3. **投影恒定**：`img/` 里永远是透视、固定 FOV、固定近远平面推导规则。正交只在 CLI 精确模式，且**不进受管集**。
4. **分辨率 / 背景 / 超采样不进名字** → 一个机位一个槽位，重新生成即覆盖。这是**替换语义**，是故意的。
5. **主体不在名字里** → 靠目录（§8）。

因为有 1–5，**不需要哈希**：名字已经是完整规格。

### 视图标签（启发式，存在 JSON 里，不进文件名）

| 标签 | 规则（含容差） |
|---|---|
| `front` | `a ∈ [357.5, 2.5]`（绕回）且 `|e| ≤ 2.5` |
| `back` | `a ∈ [177.5, 182.5]` 且 `|e| ≤ 2.5` |
| `right` | `a ∈ [87.5, 92.5]` 且 `\|e\| ≤ 2.5`（相机在 +X，即模型右手边） |
| `left` | `a ∈ [267.5, 272.5]` 且 `\|e\| ≤ 2.5` |
| `top` / `bottom` | `|e| ≥ 85` |
| `3q-front-*` / `3q-back-*` | `a` 落在对应 10° 区间且 `|e| ≤ 15` |
| `iso-45` | `a ∈ 40..50` 且 `e ∈ 30..40` |
| 其它 | `unlabeled` |

标签是**启发式**：算不出来进 `unlabeled`，不丢图。改名只需改规则，**文件名不用动**（这正是"文件名只记事实"换来的好处）。

### 预设表（唯一真相，CLI 与 GUI 共用）

| # | 预设 | 名称 | yaw | pitch | 说明 |
|---|---|---|---|---|---|
| 1 | `front` | 正视图 | 0 | 0 | **三视图之一** |
| 2 | `left` | 左视图 | 270 | 0 | **三视图之一**（相机在模型 −X 一侧；R1 已实测确认） |
| 3 | `back` | 后视图 | 180 | 0 | **三视图之一** |
| 4 | `top` | 俯视图 | 0 | 85 | |
| 5 | `right` | 右视图 | 90 | 0 | 相机在模型 +X 一侧 |
| 6 | `bottom` | 底视图 | 0 | −85 | |
| 7 | `isometric` | 正等测图 | 45 | 35.264 | 等轴测：`atan(1/√2) ≈ 35.264°`，**不是 30°** |

- **三视图 = 正视图 + 左视图 + 后视图**（2026-11-23 定稿）。此前按"反向三视图"推的 front/left/right 作废。
- **顺序即上表顺序**：GUI 下拉与卡片里的图片都按它排，删了重拍不会跳位置。
- `yaw` 的左右归属已由 P1 实机渲染确认（模型右手边是 +X）；**标签改了也不动文件名**。
- 自定义预设追加在 7 之后，按创建顺序。
- 这些是"角度定义"，不是"图片标签"：同一张图不会因为属于某个预设而改名。
- 没有"一键拍全套"：一次拍一张（见 §13 的理据与内存复用要求）。

## 8. 落点

### 受管集（GUI / CLI 标准模式）

```
<Mod>/img/
  p+0.000+0.000+0.350_a037.50_e+20.00_r+00.00_z1.00.png
  ...
  previews.json                     ← 受管集唯一 manifest
```

### 自由集（CLI 自由模式）

```
<Mod>/img/20261123-134512-a1b2/     ← 会话目录（本地时间 + 4 位随机）
  session.json                      ← 本次会话的唯一结构文件
  p+0.000+0.000+0.350_a000.00_e+00.00_r+00.00_z1.00.png
  ...
```

规则：

| 情形 | 行为 |
|---|---|
| 默认根 | `<Mod>/img/` |
| 会话目录名 | `<yyyyMMdd-HHmmss>-<4 hex>`，本地时间；撞名就重抽后缀 |
| `--out <dir>` | 直接把该目录当会话目录（不再套时间戳）；不存在则创建 |
| `--out` 目录已含 `session.json` | 拒绝，除非 `--force` |
| 一个会话多个主体 | 允许，`session.json` 里按主体分组 |
| 标准模式 | 写 `img/` 根 + 合并 `previews.json`；检测到管理器进程在跑时**拒绝**（不往 Mod 目录里放锁文件；并发下原子写失败也转成同一个错误码） |

> **不变量**：图只能出现在 `img/` 的**子目录或 `img/` 根**，永不散到 Mod 根目录。已核实的四个扫描方都因此看不见它：`AdvancedUnitAnalysisProducer`、`ExternalInstallFileFactsService`、`ModFolderTreeExporter`（都只 `EnumerateFiles(TopDirectoryOnly)`，目录不算文件）、`ModLibraryResidualCleanupService`（只删库未引用的整目录）。**`ModFolderTreeExporter` 因此也不会把 `img/` 打进文件夹树导出** —— 与"导出保持精简"一致，无需改导出代码。

## 9. JSON 契约

### `previews.json`（受管集）

```jsonc
{
  "schema": "hd2mod.previews/1",
  "generatedBy": "HD2ModManager/preview",
  "renderer": "preview/1",
  "subject": { "kind": "sectional", "payload": "<载荷指纹>", "units": ["0d1234abcd000001", ...] },

  // ① 预设定义：删图不删它；重命名不用重拍
  "customPresets": [
    { "name": "特写·头盔", "camera": { "pivot": [0,0,0.35], "yaw": 45, "pitch": 10, "roll": 0, "zoom": 0.4 } }
  ],

  // ② 图片产物：键是机位文件名（= 唯一身份）
  "shots": {
    "p+0.000+0.000+0.350_a045.00_e+10.00_r+00.00_z0.40.png": {
      "label": "特写·头盔",
      "camera": { "pivot": [0,0,0.35], "yaw": 45, "pitch": 10, "roll": 0, "zoom": 0.4 },
      "output": { "width": 1024, "height": 1024, "background": "alpha", "supersample": 2 },
      "state": { "payload": "<载荷指纹>", "components": "<组件状态指纹>", "renderer": "preview/1" },
      "builtUtc": "2026-11-23T05:45:12Z"
    }
  }
}
```

**为什么拆两段**：预设是"定义"，图片是"产物"。删掉一张图不该把用户辛苦调出来的角度一起删掉；反过来，重命名预设也只需要改 `label`，不用重拍。

**"已拍"的判据 = `shots` 里有记录 **且** 文件真的存在**。用户在资源管理器里手删了 PNG，卡片要能发现并把它当"没拍过"（选项回到下拉），而不是显示裂图。

### `session.json`（会话）

```jsonc
{
  "schema": "hd2mod.preview-session/1",
  "generatedBy": "HD2ModManager/preview",
  "renderer": "preview/1",
  "startedUtc": "...", "startedLocal": "...",
  "source": { "modSelector": "...", "patches": ["..."], "mode": "free|standard" },
  "settings": { "width": 1024, "height": 1024, "background": "alpha", "supersample": 2, "projection": "persp", "fov": 35 },
  "subjects": [ { "units": [...], "frame": { "center": [...], "radius": 1.23 }, "shots": [ { "file": "...png", "label": "front", "camera": {...} } ] } ]
}
```

两个文件都**原子写**（tmp + `File.Move(overwrite)`），JSON 用 `WhenWritingNull` + 严格反序列化（与 `RuntimeArmorRegistry` 同风格）。

## 10. 旧图判定

每张受管图在 manifest 里记三个指纹，卡片刷新时逐张比对：

| 指纹 | 来源 | 不一致 → |
|---|---|---|
| `payload` | 生效载荷 patch 文件（名字 + 长度 + mtime），**含 `Overwrite/`** | 标"旧" |
| `components` | `wardrobe.json` 的 `State.Disabled` 排序后哈希 | 标"旧" |
| `renderer` | 渲染器版本串 | 标"旧" |

- 图**没有** manifest 记录 → **不标记**（管理器不知道它怎么来的，不替它背书）。
- 标了旧但用户不重拍 → 不提示第二次、不阻挡、不自动删。卡片顶部只汇总一句"其中 N 张已过期"。
- 组件开关是就地重烘（`ModDetailsPageViewModel.Vpk.cs:1143` 的 `TryBakeInPlaceAsync`，改 `numIndices` dword、幂等可逆），落在 `Overwrite/`，因此 `payload` 指纹会自然变化 —— **旧图判定不需要额外机制**。
- 就地烘焙落在 `Overwrite/`，而 `KitInstanceContentFingerprint` 明确排除 `Overwrite/` → 拍图/烘 section 都不会让"按实例隐藏"规则失效。

## 11. 渲染器规格

- **输入**：LOD0 的 `IsVisualMesh` 网格（跳过 `IsCullingBody` / `IsStaticMesh`）；顶点分量按 §3 的 type 映射取 position/normal/uv。
- **姿势**：绑定姿势（顶点原样，不驱动骨骼）。风险见 §16。
- **变换**：world → view → clip，透视除法；背面剔除（屏幕空间有向面积）；z-buffer（float）。
- **着色**：半球环境光 + 一盏主方向光 + 边缘光；材质颜色 = 由 material id 稳定映射的低饱和色相（同一材质永远同色，跨机器一致）。
- **抗锯齿**：只用超采样。`supersample = 2` 表示内部 2× 线性放大渲染后 2×2 box 下采样（= 4 采样/像素）。
- **背景**：`alpha`（内部按预乘合成，写盘前反预乘）或 `gray`/`black`/`white`。
- **PNG**：8 位；有透明通道用 `color type 6`，纯背景用 `2`；不写 iCCP/gAMA/tIME/text 任何辅助块；zlib 压缩级别固定。
- **确定性**：不读时间、不读随机、不读文化设置；浮点运算顺序固定；PNG 不写 tIME/text 辅助块。同输入 → **逐字节相同**。
- **性能预算**：1024² / ss=2 / 单视图 ≤ 1.5 s（单线程 CPU，16 核机器）；三视图 ≤ 5 s。超了先降 ss，再降分辨率。

## 12. CLI 契约

```
hd2mod render shot    --mod <选择器> [--units <@payload|id[,id...]>] [--preset <组>] [--angles <表>] [--out <目录>] [--force]
                      [--size 1024x1024] [--background alpha|gray|black|white] [--supersample 1|2|3]
                      [--patch <路径>] [--ortho <尺度>] [--json]

hd2mod render standard --mod <选择器> [--angles ...] [--replace]
```

- `shot` = 自由模式（一个会话一个目录）；`standard` = 写进 `img/` + 合并 `previews.json`。
- 错误码沿用现有风格：`RenderSubjectEmpty`、`RenderUnitsAmbiguous`、`RenderOutputContainsSession`、`RenderManagerLocked`、`RenderNameTooLong`、`RenderPixelBudgetExceeded`。
- 路径纪律沿用 `CliModImageOperations`：逃逸守卫、reparse point 守卫、staging + 原子落盘。
- 输出：一句话结论 + 落点 + 张数；`--json` 时输出结构化摘要（给 agent 解析）。

## 13. GUI 契约（卡片状态机与派生规则）

### 13.1 卡片构成

标题「预览视图」。**初始为空但仍然显示**（否则用户找不到入口）。

```
┌ 预览视图 ───────────────────────────────────────────┐
│  视图方向 [ 正视图        ▾ ]   [ 拍摄 ]             │
│  （选中「自定义角度」时，这里展开编辑器，见 13.5）    │
│                                                      │
│  ┌───────────┐ ┌───────────┐                        │
│  │  正视图    │ │  左视图    │  …                     │
│  └───────────┘ └───────────┘                        │
└──────────────────────────────────────────────────────┘
```

### 13.2 状态（唯一真相）

| 状态 | 来源 |
|---|---|
| `builtinPresets` | 代码常量（§7 的 7 条） |
| `customPresets` | `previews.json` 的 `customPresets` |
| `shots` | `previews.json` 的 `shots` + 文件存在性 |

**两个列表都是派生的，不另存一份**：

- `AvailableOptions` = `(builtinPresets + customPresets)` 中「`shots` 无记录 **或** 文件不存在」的项，按 §7 顺序，末尾恒加一项「自定义角度」
- `ShotList` = `shots` 中文件存在的项，按 §7 顺序

于是"拍完移出下拉"和"删除后回到下拉"是**同一状态的两种读法**，不是两处维护 —— 这就是"让控件自己解决"的正确做法。

### 13.3 交互

| 动作 | 行为 |
|---|---|
| 选预设 + 点按钮 | 按 §7 顺序插入一张「加载中」占位 → 后台渲染 → 成功后换成图，**并从下拉移除该项** |
| 悬停图片 | 叠层：左上「拍摄规格」（预设名；已过期则加「· 已过期」徽标）；左下「左键用系统默认应用打开」；右下三枚幽灵按钮：**删除** / **从文件夹打开** / **重拍** |
| 删除 | 删 PNG + 删 `shots` 条目（`customPresets` **不动**）→ 该项自动回到下拉 |
| 从文件夹打开 | `explorer /select,<文件>` |
| 重拍 | 同机位重渲 → 同名覆盖 + 更新 `shots` 条目的三个指纹与 `builtUtc` |
| **失败** | **不移除**下拉选项；占位保留并显示原因 + 「重拍」 |
| 选「自定义角度」 | 下拉与图片之间展开编辑器；按钮文案变「新增预设」 |
| 编辑器 + 点「新增预设」 | **不拍照**：写进 `customPresets`、下拉新增并选中它、编辑器自动收起；用户再点一次（此时按钮是「拍摄」）才拍 |
| 下拉没有可拍项 | 不会发生：「自定义角度」恒在 |

### 13.4 两条硬规则

1. **按钮文案随模式变**：`拍摄` / `新增预设`。同一个按钮两种语义却不改文案，用户必然点错。
2. **新增预设先查重机位**：算出的 7 个数与任何已有预设（内置或自定义）完全一致 → **拒绝**，提示"这个机位已经有『正视图』了，直接拍它就行"。否则同一机位两个名字、一个文件，删除逻辑立刻打架。

预设名约束：非空、≤ 32 字符、不含 `\ / : * ? " < > |`、不与已有预设重名（含内置名）。

### 13.5 自定义角度编辑器

| 字段 | 默认 | 备注 |
|---|---|---|
| 角度 | 取**当前选中预设**的 yaw/pitch（默认正视图 0/0），roll 恒 0 | 三个数字框；从"正视图"微调，比从 0 猜好 |
| 中心位置 | 自动（包围球中心，pivot 0,0,0） | 勾「自动」即禁用三个框；数值单位是**主体半径** |
| 缩放距离 | 自动（刚好包裹全部网格，zoom 1.0） | 勾「自动」即禁用；数值是 zoom 倍数 |
| 预设名称 | 空 | 按 13.4 的约束校验 |

---

## 14. 场景复用与内存寿命

用户明确要求：连续拍多张要**复用已解码的场景**（不反复卸载重建），同时**不能一直占着内存**。

| 项 | 规格 |
|---|---|
| 缓存条目 | **单条**（只留当前主体），不按 Mod 累积 |
| 键 | `载荷指纹 + renderer`。组件开关的就地烘焙会改 `Overwrite/` 里 patch 的字节 → 键自动变化 → 旧场景自动失效，不可能拿旧几何拍新状态 |
| 寿命 | 详情页打开期间保留；页面关闭（`_disposed`）、切换 Mod、或空闲 **5 分钟**后释放 |
| 渲染缓冲 | 颜色 + 深度按 `(宽, 高, ss)` 分配一次、跨视角复用；参数变了才重分配（1024²/ss2 ≈ 32 MB） |
| 并发 | **串行**：一个后台渲染队列，一次一张。既满足 §11 的逐字节确定，又不需要场景多副本 |
| CLI | 进程即寿命：一个 `render shot` 会话内 decode 一次、拍完退出 |

预算（以 20 万三角形的主体估）：

- 首次解码 ≤ 2 s（一次性）
- 首张（含缓冲分配）≤ 2.5 s；同会话后续每张 ≤ 1.5 s
- 常驻 ≤ 64 MB；超了先降 `ss`，再降分辨率，并把降级原因写进 `session.json`

测试锁三条：连续 3 张只 decode 一次；切换 Mod 后释放；载荷指纹变化后缓存失效（用一次就地烘焙改 patch 来造）。

---

## 15. 测试与验收

| 层 | 内容 |
|---|---|
| 单测（确定性） | 合成 unit（球/立方体/双臂状）→ 三视图逐字节比对 golden PNG；相机量化/规范化（含 −0.00、绕回、夹紧）；命名投影；manifest 原子写 |
| 单测（契约） | 旧图判定三种指纹各一条；无记录不标记；`--out` 已含 session 的拒绝；路径逃逸；reparse point；名字超长 |
| 单测（几何） | 生效载荷选择（基底 + `Overwrite/` 同名顶替）；跳过 culling/static；LOD0 选择 |
| 单测（缓存，§14） | 连续 3 张只 decode 一次；切换 Mod 释放；载荷指纹变化后失效（造一次就地烘焙来验） |
| 单测（GUI 派生，§13.2） | `AvailableOptions` 与 `ShotList` 由同一状态派生：拍完消失、删图回归、手删 PNG 当作未拍；新增预设撞机位被拒；按钮文案随模式切换 |
| 真实数据冒烟 | 至少 2 个真实 unit（护甲 + 武器）出图，人工核对朝向/比例。样本缺失**默认跳过**（沿用本仓库约定），但设 `HD2_REQUIRE_PREVIEW_SAMPLES=1` 时改为**失败** —— CI 不会因缺游戏数据变红，要必跑的人也不会被静默跳过糊弄过去 |
| CLI 契约测试 | `shot` 目录结构、`session.json` 字段、`--force`、标准模式加锁 |
| 交付门 | `dotnet build -c Debug` 0 警告 0 错误；`dotnet test -c Debug` 全绿；**零新 NuGet 依赖** |

## 16. 风险与待核对项

| # | 事项 | 处理 |
|---|---|---|
| R1 | ~~朝向约定~~ **已解决**（2026-11-23）：上轴 **+Z**（P0 的 AABB：Z 上 2.10 m、Y 上 0.57 m）；正面 **+Y**、右手边 **+X**（P1 出图：yaw=0 拍脸、yaw=180 拍腰后蝴蝶结、yaw=90 拍右侧） | 已写回 §6/§7。这条本来只影响标签，现在连标签也不用猜了 |
| R2 | **绑定姿势是否够用**：某些 unit 的骨骼变换不是单位阵，直接渲染会偏移 | 用 2–3 个真实 unit 核对；不行则在 §11 加一步"按 `UnitTransformInfo` 应用节点矩阵" |
| R3 | 超采样内存：1024²×ss2 = 2048² 色缓冲 + 深度缓冲 ≈ 32 MB/视图 | 单视图串行、复用缓冲；ss3 时降分辨率或拒绝 |
| R4 | MAX_PATH：长 Mod 名 + 55 字符文件名 | 名字上限 96 + 落点前检查绝对路径长度，超了报 `RenderNameTooLong` |
| R5 | 元素可见性：`sections.json` 与载荷可能不完全对应（换甲/合并后） | 渲染只信载荷本身，不猜 section 语义（v1 不做组件级过滤，因为就地烘焙已让载荷等于当前状态） |
| R6 | 大量图占盘：自由模式每次一个目录 | v1 只提供 `hd2mod render gc --stale`（可选，后置），不自动删 |

## 17. 分阶段

| 阶段 | 交付 | 验收 |
|---|---|---|
| P0 | `PreviewSceneBuilder` + 合成 fixture + 统计输出 | 能从真实 patch 读出顶点数/三角形数，与 `unitprobe` 口径一致 |
| P1 | `PreviewRenderer` + `PngWriter` + 相机/取景 | 合成模型三视图出图，golden 逐字节一致 |
| P2 | 命名 + 量化 + `session.json` + CLI `shot` | 自由模式端到端；CLI 契约测试绿 |
| P3 | 标准模式 + `previews.json` + 旧图判定 | 同角度替换；改组件后标旧 |
| P4 | GUI 卡片（§13 状态机 + 悬停叠层 + 旧图徽标）+ §14 场景缓存 | 连续拍 3 张只 decode 一次；删除后选项回归；打开详情页不卡 |
| P5（可选） | 更多预设视图 / iso / 正交 / `gc` | — |

### P0 实施记录（2026-11-23，已完成）

新增：

- `HD2ModAdaptation/Preview/PreviewScene.cs` —— 场景 IR（`PreviewVertex` / `PreviewTriangle` / `PreviewMesh` / `PreviewBoundingSphere` / `PreviewSceneStats`）。
- `HD2ModAdaptation/Preview/PreviewSceneBuilder.cs` —— `BuildAsync(patchPaths, unitFilter)`（读盘）+ `BuildScene(units)`（纯函数，不碰盘）。
- `HD2ModAdaptation.Tests/PreviewSceneBuilderTests.cs`（11 条）、`PreviewSceneRealDataTests.cs`（1 条，`HD2_PREVIEW_PATCH` 驱动）。

真实数据核对（`E:\mod\Elise_Bill\sectional\9ba626afa44a3aa3.patch_0`，7 unit / 35 mesh）：

| unit | stream nv | stream ni | 取到的 LOD0 顶点 | nv/顶点 | 取到的 LOD0 三角形 | ni/三角形/3 |
|---|---|---|---|---|---|---|
| 1 | 75560 | 328365 | 15112 | **5** | 21891 | **5** |
| 2 | 59456 | 298116 | 14864 | **4** | 24843 | **4** |
| 3 | 6440 | 32688 | 1610 | **4** | 2724 | **4** |
| 4 | 20984 | 110220 | 5246 | **4** | 9185 | **4** |
| 5 | 20968 | 110112 | 5242 | **4** | 9176 | **4** |
| 6 | 29480 | 156204 | 7370 | **4** | 13017 | **4** |
| 7 | 29460 | 156096 | 7365 | **4** | 13008 | **4** |

每行的两个比值都等于该 unit 的 LOD 层数 ⇒ 选取口径与 `unitprobe` 的独立读数**逐单位精确对账**，且索引数 = 3×三角形×层数 ⇒ **零三角形丢失**。

顺带钉死的两件事：

1. **上轴是 +Z**（AABB 1.47 / 0.57 / 2.10）。规划里"Y 是上"的假设作废；相机 `up` 用 +Z，`pitch` 绕 X。
2. **`lod = -1` 的 3 顶点 / 1 三角形网格是占位体，不是几何**。实测 unit 2 同时带 `lod=0` 与这样一份 `lod=-1`。规则：每 unit 优先 LOD0、没有才回退 `-1`，且**一律要求 `UnitGeometryFactsBuilder.HasRenderableGeometry`**（顶点>3 且 三角形>1）—— 复用仓库既有判据，不另立阈值。

### P1 实施记录（2026-11-23，已完成）

新增：

- `PreviewCamera` —— 轨道相机 + **量化/规范化**（`Quantize`：负零归零、yaw 绕回、pitch 夹 ±89.5、roll 绕回）与 `Resolve`（自动取景 + 正交基）。
- `PreviewImage` / `PreviewRenderOptions` —— 预乘 alpha 的 8 位缓冲、盒式下采样、背景、内部像素预算（`MaximumInternalPixels`）。
- `PreviewRenderer` —— z-buffer 光栅器：近平面裁剪（Sutherland–Hodgman）、1/z 深度测试、透视校正法线、半球光 + 相机相对主光 + 边缘光。
- `PreviewPngWriter` —— 自写最小 PNG 编码（`ZLibStream` + 自算 CRC32，零依赖，不写任何辅助块）。
- 测试 40 条：相机 9、渲染 12、PNG 6、golden 4、真数据出图 1、P0 场景 12（含原 11）。

真数据出图（Elise_Bill 素体，512²/ss2，`artifacts/preview-p1/*.png`）：正面 10.2 万字节、侧面 7.7 万、背面 6.8 万，覆盖正常、形体清楚。

**P1 抓到并修掉的两个真问题**：

1. **材质分色全塌成一个颜色**。材质槽号实测在 1e8～4e9 量级，`slot * 0.618f % 1` 在 float 里连小数位都留不下 → 19 个不同槽变成同一色相。改成整数散列（`slot * 2654435761u`，取中间 16 位当色相）。
2. **世界固定的主光让转台一半视角背光**。第一版主光是世界方向的，正视图整个发暗。改成**相机相对**光（左上前方），每次转动都亮。

**R1 结案**：上轴 +Z、正面 +Y、右手边 +X —— 三条都是出图看出来的，不是猜的。附表 l/right 的 yaw 因此从 90/270 订正为 270/90。

已知取舍：v1 不贴图，所以脸和镂空处只能看形体；`CullBackFaces` 默认关（宁可多画，也不要因为缠绕方向反了整块消失）。
已知失败：`HD2ModCore.Tests.ArmorMappingPickerPlannerTests.BuildBaseCatalog_RealAssetIndex_MatchesExpectedThreeTabCounts` 仍红，与本次改动无关（审计文档 §5.1 已定位：它默认读 2026-09-22 的 **Release** 索引）。

## 18. 明确不做（再列一次，防蔓延）

- 不做贴图、不做材质正确性、不做骨骼姿势、不做动画。
- 不动 `ModExporter`（导出仍只做 patch），`img/` 不随导出走。
- 不给 GUI 一个"任意拍摄、不落预设"的入口。GUI 的自由角度必须走 §13.5 的**预设编辑器**（有名字、可复用、可删除），不是一次性临时相机。
- 不接管 `icon.*`（那是另一套东西，`ModIconLocator` 不动）。

---

## 19. 决策记录（与用户对话逐条对应）

| 用户决定（原话要点） | 文档位置 |
|---|---|
| **三视图 = 正视图 + 左视图 + 后视图**（把之前"反向三视图"的说法澄清掉） | §7 预设表 |
| **正三角视图 → 正等测图（Isometric）**，`atan(1/√2) ≈ 35.264°` | §7 第 7 项 |
| 图片默认放 Mod 的 `img/` 文件夹，按标准视图方向与生成条件命名 | §7 §8（改成：**文件名只记机位事实**，条件进 JSON） |
| GUI 在 Mod 详情最下方加「预览视图」卡，打开后后台异步加载 | §1 §13 §17 P4 |
| CLI 可自定义输出位置、精确角度、尺寸 | §12 |
| 文件名只记 6 个数（拍摄中心 3 + 三轴角度）+ 缩放，角度 2 位小数 | §7（加了：roll 恒出现、pitch 夹 89.5、yaw 无符号） |
| 其余字段记在 JSON | §9 |
| GUI 只支持 sectional 类（含 legacy2sectional、vpk2patch）；CLI 不受限 | §4 |
| 拍照按当前组件启用状态；快照状态记在 `img` 的 JSON；同角度替换 | §9 §10（就地烘焙已保证载荷同步，§10 说明为什么旧图判定因此免费） |
| 组件状态变了但没重拍 → 卡片标记旧图；没有结构文件的图不标记 | §10 |
| CLI 自由模式每次拍照一个独立时间戳文件夹，天然不冲突 | §8 |
| **不做"一键拍全套"**：一次拍一张，"一次点击变三次点击"是可接受的 | §7 末尾、§13 |
| **连续拍多张要复用已解码的场景**，不许反复卸载重建；也不许一直占内存 | §14 |
| 下拉 = "还没拍的"：拍完移除、删除后还原；由下拉控件自己解决，不打补丁 | §13.2（改成：两个列表都从同一状态派生，不各存一份） |
| 自定义预设"先建后拍"两步走 | §13.3 §13.5 |
| 图片按预设顺序排，不按拍摄时间 | §7 §13.2 |
| V1 不做贴图（主要服务 CLI/agent） | §2 §18 |
| 导出保持精简，只做最基本 patch 导出 | §8（验证 `img/` 不进导出）、§18 |
| 默认输出规格（1024²、透明底、超采样）沿用草案 | §11 |

### 审核结论（对着聊天记录核过一遍）

- **无歧义项**：范围（§2）、分区（§4）、相机规格（§6）、命名规则与不变量（§7）、落点（§8）、两个 JSON（§9）、旧图判定（§10）、CLI 契约（§12）、GUI 状态机（§13）、缓存寿命（§14）、测试门（§15）。
- **刻意留白、已标为待核对**：`a=90` 对应左还是右（R1）、绑定姿势是否够用（R2）。两者都只影响标签或需要加一步矩阵变换，**不影响文件名契约**，所以可以边做边核。
- **我替用户定下、需要点头的三处**：
  1. §11 的"材质分色"默认取**低饱和色相**（同材质同色、跨机器一致）。不喜欢就改一个常量变黏土色。
  2. §13.4 的**按钮文案随模式变**（拍摄 / 新增预设）。原话是一个固定的"确定"。
  3. §13.4 的**新增预设撞已有机位一律拒绝**。原话没提，但不拒绝就会"一机位两名字一文件"。
- **一个我主动收窄的地方**：原话是"按标准视图方向、生成条件等命名"，最终定为"文件名只记机位，生成条件全部进 JSON"。原因是生成条件里含分辨率/背景/超采样，进名字会让长度翻倍且必然引发替换语义的分歧。
- **一处与旧描述冲突、已改掉的**：§18 原来写"不给 GUI 自由角度输入"，与本轮的"自定义角度预设编辑器"矛盾；现在改成"自由角度必须走预设编辑器，不做一次性临时相机"。
