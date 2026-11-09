# VTF 7.1 老贴图包导入失败 —— 审计与修复方案

**日期**：2026-11-09
**来源**：另一位贡献者转来的用户反馈（`牛牛rebone替换路易斯.vpk`）+ 贡献者自己的排查记录（`_conv_niuniu.txt`）
**状态**：**已修复（同一天）** —— 落地记录见文末 §8；正文保留当时的诊断过程。

---

## 0. 结论（三句话）

1. **真因**：`VtfFile.Read` 无条件从 `+63` 读 `depth`，而 **VTF 7.0 / 7.1 没有这个字段** —— 那两个字节其实是低分辨率缩略图的前两个字节。读出来的"depth"是 5889…65281，链长算到 1.5–790 **GB**，于是 `checked` 溢出或"图像块越过文件末尾"，**这个包 30 张贴图里的 21 张 v7.1 全部读不出**。
2. **放大器**：`SourceMaterialConverter` 里 7 处 `VtfFile.Read` 的 `catch` 过滤器都写成
   `InvalidDataException or IOException or InvalidOperationException`，**不含 `OverflowException`**，
   所以一张贴图读不出不是"该材质降级"，而是**整个 VPK 转换失败**（`patches: []`）。
3. **误导项**：管理器导入日志里那句「（0 个 Unit / 0 个 材质 / 0 个 贴图）」**不是失败证据** ——
   `VpkImportService` 自「来源入库」重构后，`Converted` 条目的三个计数一律硬编码 `0`，
   于是**每一次成功导入都会显示 0/0/0**。真正的错误发生在导入后的自动重构建。

---

## 1. 复现（可复制）

```powershell
$cli = 'HD2ModCli\bin\Debug\net10.0\hd2mod.exe'
& $cli vpk2patch 'C:\Users\Administrator\Downloads\牛牛rebone替换路易斯.vpk' `
    --output E:\tmp\niuniu-conv `
    --manager-root 'HD2ModManager\bin\Debug\net10.0-windows' `
    --library E:\tmp\niuniu-lib `
    --game-data 'D:\SteamLibrary\steamapps\common\Helldivers 2\data' --json
```

实测结果（4 秒，退出码 **2**）：

```json
{ "ok": false, "patches": [],
  "failures": [ { "vpk": "…\\牛牛rebone替换路易斯.vpk",
                  "error": "7 处失败 —— OverflowException：Arithmetic operation resulted in an overflow. @ ThrowHelpers.ThrowOverflowException" } ] }
```

> 注意 `DescribeFailure` 的注释里**已经记着这个包**（"实测 2026-11-04 的 `牛牛rebone替换路易斯.vpk`，
> 整批只报'转换 0 个'"）。当时只补了**报错措辞**，没补根因 —— 所以它换了张脸又回来了。

## 2. 证据链（对真实文件量出来的，不是推测）

包内共 52 条目：`vtf=30, vmt=15, mdl=2, vtx=2, vvd=2, txt=1`。30 张贴图的版本分布：

| 版本 | 文件头 | 张数 | 现状 |
|---|---|---|---|
| **v7.1** | 64 | **21** | **全部读不出** |
| v7.2 | 80 | 5 | 正常 |
| v7.4 | 88 | 4 | 正常 |

对 21 张 v7.1 逐个做两种算法（`t=\tmp\vpkprobe`，临时探针，未进仓库）：

| 贴图 | `+63` 读到的"depth" | 按它算的链尾 | 按 depth=1 算的链尾 | 文件长度 |
|---|---|---|---|---|
| milltina_face.vtf (4096²) | 31745 | 532 592 722 112 | **16 777 408** | 16 777 408 ✅ |
| milltina_body.vtf (4096²) | 23809 | 399 448 735 936 | **16 777 408** | 16 777 408 ✅ |
| cloth.vtf (2048²) | 58113 | 243 743 588 544 | **4 194 496** | 4 194 496 ✅ |
| 5.vtf (512²) | 5889 | 1 543 766 208 | **262 336** | 262 336 ✅ |
| …（其余 17 张同样） | 6 289…65 281 | 1.5 G…790 G | 精确等于文件长度 | — |

**21/21 在 `depth = 1` 下精确等于文件长度**；v7.2 / v7.4 的 9 张本来就带真 depth（=1），两种算法一致。
这就是"版本 7.1 没有 depth 字段"的直接证据。

### 2.1 与 Valve 官方头文件对齐

官方 SDK 的 `vtf.h` 里三处都能对上（[source-sdk-2013 `public/vtf/vtf.h`](https://raw.githubusercontent.com/ValveSoftware/source-sdk-2013/master/src/public/vtf/vtf.h)）：

- `int VTFFileHeaderSize(major, minor)`：`major == 7` 时 **`minor` 0 与 1 都返回 `sizeof(VTFFileHeaderV7_1_t)`**，2 才开始是 `V7_2_t`；
- `VTFFileHeaderV7_1_t` 继承 `VTFFileBaseHeader_t`（`fileTypeString / version[2] / headerSize / width / height`），后续是
  `flags@+20、numFrames@+24、startFrame@+26、padding@+28、reflectivity@+32、padding@+45、bumpScale@+48、
  imageFormat@+52、numMipLevels@+56、lowResImageFormat@+57、lowResImageWidth@+61、lowResImageHeight@+62`，
  **到 +62 结束**；
- `depth`（`unsigned short`）出现在 **`VTFFileHeaderV7_2_t`** 里，位置正是 `+63`（第三方注释也写着
  "Can be 0 or 1 for a 2D texture (**v7.2 only**)"，见 [png2vtf/vtf_format.h](https://github.com/maxme/png2vtf/blob/master/vtf_format.h)）。

也就是说：我们读的其余字段**偏移全对**，唯独 `depth` 少了一个"版本 >= 7.2 才有"的前提。

（附带说明：`VtfFile` 头部注释写的是"v7.2 headerSize=80 / v7.4 headerSize=96"，实测这个包的 v7.4 是 **88**；
`headerSize` 从文件里读，所以不影响解析，但注释该顺手改正。）

## 3. 为什么本机一直测不出来

| 证据 | 结论 |
|---|---|
| 扫了用户库 61 个 VPK、**528 张贴图**：`v7.2×178、v7.3×26、v7.4×324`，**v7.0/v7.1 = 0** | 本地样本里根本没有 7.1 |
| `VtfFileTests` 的合成头固定写 `major=7 / minor=2 / +63 depth` | 单测也**只覆盖 7.2** |
| 7.1 是 2013 年前后 VTFEdit / 旧 SDK 的产物 | 只有老包命中：贡献者点名的 `北黑kk (GeTiRe)`、`牛牛 rebone (GeTiRe)`、4 月/6 月那批 |

所以这不是"偶发"，而是**一类老包**：只要作者用 2013 年的工具导出过贴图，就会 100% 失败。

## 4. 次级缺陷（同一现场暴露出来的另外四处）

| # | 位置 | 问题 | 后果 |
|---|---|---|---|
| ① | `SourceMaterialConverter.cs:2243`（以及 1345/2462/3063/3258/3390/3710/3840 的 catch） | 过滤器不含 `OverflowException` | 一张贴图坏 → **整个 VPK** 失败，而不是该材质降级 |
| ② | `SourceMaterialConverter.cs:2245` | 降级时的文案 `"{stem}：贴图 {textureName} 读不出来——{message}"` 本来是好的，但①让它永不生效 | 用户拿不到"是哪张贴图" |
| ③ | `VpkImportService.cs:315/341` | `Converted` 条目的 `Units/Materials/Textures` 硬编码 `0` | 每次成功导入都显示「0 个 Unit / 0 个 材质 / 0 个 贴图」（就是用户截图那一行） |
| ④ | `VpkRebuildService.ExplainConversionFailure`（L515） | 只看顶层 `error` 对象；每 VPK 失败是 `failures[]` + 退出码 2，于是落回 `Hd2ModProcess.Explain`，而 stderr 只有进度行 → **把整份 JSON 当错误文案贴进 UI** | 用户看到 1 000 字的 JSON（已实测复现该文案） |

> 贡献者那份 `_conv_niuniu.txt` 里 `ok:false` 的原因**不是贴图**，而是
> `registryError: "Access to the path 'e:\steam\steamapps\common\HD2ModManager\mods\.hd2modmanager\armor-registry.workspace.json.…tmp' is denied."`
> —— 他把管理器装在 `e:\steam\...` 下，素体工作区写不进去。那条是**环境问题**，
> 与本次贴图 bug 无关；而且说明他那一轮（`niuniu-v72.vpk`）**其实转出了 7 Unit / 15 材质 / 18 贴图**。

## 5. 修复方案

### P0 —— 认版本，别把缩略图当 depth（一行判据）

`HD2SourceMigration/VpkConversion/VtfFile.cs` 的 `Read`：

```csharp
// v7.0 / v7.1 的头到 +62 就结束，没有 depth；+63 那两个字节是缩略图首字节。
// 无条件读它 = 把压缩像素当层数，链长直接算到 GB 级（实测 21/21 张老贴图全中）。
var depth = SupportsDepthField(major, minor) ? BinaryPrimitives.ReadUInt16LittleEndian(data[63..]) : 1;
```

- 判据：`major > 7 || (major == 7 && minor >= 2)`。
- 顺带两条防御：
  - 未知**主版本**（`major != 7`）直接 `InvalidDataException("不支持的 VTF 版本 7.x/…")`，别硬猜；
  - 越界报错里带上 `v{major}.{minor} depth={depth} mips={n}`，让下一次能在报错里就定位。
- 头部注释把 `v7.4 headerSize=96` 改成"实测 88（以文件里的 headerSize 为准）"。

### P1 —— 一张坏贴图不该毁掉整个 VPK

- 7 处 `VtfFile.Read` 的 catch 过滤器统一加 `OverflowException`（建议加一个 `static bool IsTextureReadFailure(Exception)` 谓词，**一处定义、七处引用**，别再各写各的）。
- 验收行为：坏贴图 → 该材质 `LoadOutcome.Failed`（文案带贴图名）→ 该材质退化成占位/纯色，**其余材质照常出包**，报告里出现 `degradedMaterials` / `missingMaterials`。

### P1 —— 管理器报错要说人话

- `ExplainConversionFailure`：退出码非 0 时优先读 `failures[].error`（几条以内），其次 `error.message`，最后才退回 `Hd2ModProcess.Explain`；**永远不要整段贴 JSON**（限制在 ~300 字并给出"临时产物保留在 …"）。
- 导入日志：`Converted` 条目要么从 `vpk2patch --dry-run` 的 `signature` 拿材质/贴图数，要么把这三个数字从文案里去掉（写"来源已入库，产物在重构建时生成"）。

### P2 —— 补上 7.1 的回归面

- `VtfFileTests` 增一个 7.1 合成头（同样 4×4 DXT1，但 `headerSize=64`、**不写 depth**，且把 `+63` 故意填成非零垃圾），断言 `depth == 1` 且 `Mips[0]` 正确 —— 这条用例在修前必须红。
- 再补一条：把同一个包按 7.2 写（带 depth=1）时结果一致（防止"改一处、另一处漂"）。
- 真数据回归：把 `牛牛rebone替换路易斯.vpk` 收进 `docs/agent/external/` 或测试夹具（138 MB，建议只留一张 7.1 的切片或做个小包），断言转换 `ok:true`、`units=7 / materials=15`。

## 6. 验收清单

1. `hd2mod vpk2patch 牛牛rebone替换路易斯.vpk --json` → `ok: true`，`patches[0]` 里 `units=7 / materials=15 / textures≥18`，`failures: []`。
2. 把某张贴图故意截断 → 只有该材质进 `degradedMaterials`，其余照常出包（P1 落地后）。
3. 管理器导入日志不再出现「0 个 Unit」；重构建失败时文案里能读到"哪张贴图 / 哪个 VPK"。
4. 全量 `dotnet test -c Debug` 绿；库里 61 个老包各转一次不回归（它们全 7.2/7.3/7.4）。

## 7. 待确认

1. 这个用户是不是**只有这一个包**失败？如果他有整批老包，最好一次要到 2–3 个（7.1 的那批：北黑kk、4 月/6 月的老包），确认不是"只有这一种损坏"。
2. 贡献者那边 `e:\steam\steamapps\common\HD2ModManager` 的**素体工作区写权限**要不要一起排（那是他机器上的安装位置问题，不属于我们的 bug，但会持续误导排查）。
3. 修复优先级：P0 单独就能让这类包转出来；P0+P1 才让它"转不出来时也说清楚"。建议一个版本内一起发（1.5.7），避免"能转了但报错还是天书"再被报一次。

---

## 8. 落地记录（2026-11-09）

用户随后又给了一份 7.1 的包（`L4D2-3335630365-原版.zip`，内含 `3335630365.vpk`）：**22 张贴图里 10 张 v7.1**，
按 `depth = 1` 算 22/22 全部精确吃满 —— 与第一个包同一形态，没有第二种坏法（`depth@63` 有 2 张恰好是 1，
也就是"偶然能读"）。修前它同样 `exit=2 / patches=[] / 5 处失败 —— OverflowException`。

### 改了什么

| 级别 | 文件 | 改动 |
|---|---|---|
| P0 | `HD2SourceMigration/VpkConversion/VtfFile.cs` | 先读版本：`major != 7` 明确拒绝；`depth` 只在 `minor >= 7.2` 时从 `+63` 读，7.0/7.1 恒为 1（`HasDepthField`、`FirstMinorVersionWithDepth`、`MaxDepth` 三个常量）；`depth` 不在 1…2048 时按"头部损坏"报 `InvalidDataException`；把 `checked` 的 `OverflowException` 翻成带版本/尺寸的 `InvalidDataException`；越界报错带上版本、header、mips、frames、depth、缩略图尺寸；头部注释改正（`depth` 仅 7.2+、v7.4 实测 88/96 都见过） |
| P1 | `HD2SourceMigration/VpkConversion/SourceMaterialConverter.cs` | 十处 `catch (… is InvalidDataException or IOException or InvalidOperationException)` 收敛成 `IsTextureReadFailure(exception)`（新增 `OverflowException` / `NotSupportedException` / `ArgumentException`）——**改一处即改十处**，避免"漏一处等于没修" |
| P1 | `HD2ModCore/Infrastructure/Hd2ModProcess.cs` | 新增 `DescribeFailure(result, what)`：先从 JSON 取 `error.message`，其次 `failures[].error`（最多三条 + "共 N 个"），最后才退回原文并截断到 400 字（`Clip`） |
| P1 | `HD2ModCore/Infrastructure/VpkRebuildService.cs` | `ExplainConversionFailure` 改用 `DescribeFailure`（不再把整份 JSON 拼进消息） |
| P1 | `HD2ModManager/Services/VpkImportService.cs` | 规划失败同样走 `DescribeFailure`；导入明细里那三个**硬编码 0** 只在真有数字时才印，否则写"来源已入库，产物在随后的重构建里生成" |

### 测试（新增 17 条）

- `VtfFileTests` +6：7.1 合成头（`+63` 故意填 0xD801 = 55 297，与真文件同值）、7.0 等价、未知主版本拒绝、荒谬 depth 报可读错、**真数据夹具** 7.1 多点几层链尾对齐（`Fixtures/vtf71-panel-256x128.vtf`，43 KB，取自报障包）、真数据 7.2 仍带 depth。
- `TextureReadFailureTests` +11：`OverflowException` 必须算可降级；空引用/越界/内存不足/取消**不许**被吞。
- `Hd2ModProcessTests` +5：`failures[]` 不再粘整份 JSON（用真实抓到的那份 1 046 字报告做样本）、`error.message` 优先、最多三条、超长截断、两段输出都空时仍给退出码。
- 反向验证：把 `depth` 那行临时改回无条件读，`VtfFileTests` **3 条立刻红**；改回后 12/12 绿。

### 端到端

| 包 | 修前 | 修后 |
|---|---|---|
| `牛牛rebone替换路易斯.vpk`（30 贴图 / 21 张 7.1） | `ok:false`，`patches:[]`，`exit 2`，`7 处失败 —— OverflowException` | `ok:true`，7 Unit / 15 材质 / 18 贴图，`exit 0`（16 s） |
| `3335630365.vpk`（22 贴图 / 10 张 7.1） | `ok:false`，`patches:[]`，`exit 2`，`5 处失败 —— OverflowException` | `ok:true`，7 Unit / 8 材质 / 15 贴图，`exit 0`（8 s） |
| 人工破坏一个**在用**贴图的头部（把 `tf.vtf` 改成 v7.2 + depth=9999，长度不变） | （旧代码：整包失败） | `ok:true`，`materialGap: required 8 / resolved 7 / fallback ["tf"]`，报告写着「源材质 tf 合成失败：…VTF depth=9999 不可信（v7.2，正常是 1；超过 2048 一律当成头部损坏）」——**一个材质降级，其余照常出包** |

全量：`dotnet build -c Debug` 0 警告 0 错误；`dotnet test -c Debug` **2089 通过 / 1 跳过 / 0 失败**。

### 还没做的

- 管理器界面上那条"导入完成后自动重构建"的**成功**文案仍由重构建给（有 Units/Materials），本次只把导入明细里的假 0 去掉；GUI 侧没有自动化测试（WPF 工程不参与测试项目），只能靠人工点一次。
- 老包的其它形态（VTF 7.0 真实样本、带动画帧的 7.1）仍无真数据；这次的 7.0 覆盖是合成的。
