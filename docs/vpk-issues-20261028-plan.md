# VPK 转换问题排查方案（2026-10-28）

> 这份文件是**压缩上下文前的交接单**：压缩后照这份继续，不必重新问用户。
> 用户样本：`C:\Users\Administrator\Downloads\测试问题.rar`（961 MB）
> 已解到：`E:\Data\source\repos\WpfApp1\tmp\issue-20261028\测试问题\`（1.23 GB，**别删**）
> 用户日志：`tmp\issue-20261028\测试问题\manager_20261028.log`（2 MB，3681 行，2026-10-28 16:23–18:07）

## 日志先说了什么（已核对，不是推测）

- **0 条 ERROR**，830 条 WARN 全是 `[UIPerf]` 性能噪声（event-burst / frame-stall / slow-operation）。
  → 用户说的"管理器报错"不是崩溃日志，而是**导入报告里的跳过原因**或界面提示框。
- 三次 VPK 导入的汇总：
  - `15 个来源包，转换 5，跳过 10`（doomflame-aaliyah：Aaliyah 版一切正常，同批手办/着色器包按设计跳过）
  - `20 个来源包，转换 2，跳过 18`（少女前线2 夏安）
  - `44 个来源包，转换 0，跳过 44`（少女前线2 秋桦 仁心仁术 shader）

## 四个问题分类（用户自己分的文件夹，正好对应四条线）

### A. 无材质独臂（`无材质独臂\`）—— 最可能的真 bug，优先查

用户样本：`【少女前线2：追放】DSR50-暮色絮语 原版.zip`、`夏安 青涩夜会 人物MOD.zip`、
`秋桦 人物MOD.zip`、`【少女前线2：追放】秋桦 仁心仁术 shader 基础组件.vpk`
现象：模型能进游戏，但**没有材质**且**缺一条手臂**。

日志里的两条硬证据：

1. 角色 VPK 全部报 `几何读不出来（InvalidDataException）：VVD magic 不是 IDSV，不是 Source 顶点文件。`
   （夏安 9 个、秋桦 若干，无一例外）
2. 基础组件那个包直接 `打不开（InvalidDataException）：VPK 目录树无法解析（16 与 18 字节条目都试过）`

**已做的实测（关键）**：把它解出来单独喂给 `VpkArchive.Open` ——
`【少女前线2：追放】秋桦 仁心仁术 shader 基础组件.vpk` 仍然 `VPK 目录树无法解析`，
而同目录的正常 VPK（`Lilium_Sancta_Bill.vpk`）解析出 10 个条目。
→ 不是路径/中文/权限问题，是**这个 packer 的 VPK 布局我们没覆盖**。

文件头看起来完全正常：
```
34 12 aa 55 | 01 00 00 00 | 90 30 00 00    magic=0x55AA1234 v1 treeSize=12432
76 76 64 00 ...                             紧随其后就是 "vvd\0"，说明 12 字节 v1 头是对的
```

但目录树里第一个条目的对齐位置**比我们读到的文件名晚 2 字节**：

```
... benevolent_heart 00 33 00 | 9a e6 37 75 | 00 00 | ff 7f | 93 f4 d2 0b | c0 85 7e 00 | ff ff
                            ^^ 这 2 字节
    若把条目起点放在 9a 处：CRC=0x7537e69a  preload=0  archive=0x7FFF(Inline!)  offset=0x0bd2f493  length=0x007e85c0(7.9MB)  terminator=0xFFFF ✓
```
三个字段同时合理（尤其 archive=0x7FFF=InlineArchiveIndex、term=0xFFFF），
说明**18 字节条目布局是对的，只是文件名后面多了 2 字节**。
`VpkArchive.TryParseTree` 又要求"正好吃完整棵树"，于是整体判定失败。

**下一步（按顺序）**：
1. 写个一次性的 hex 检查工具（复用 `tmp\vpklist` 探针那套：`dotnet run` + `VpkArchive`）：
   把这个文件的树按 18 字节步长、从 2 字节偏移处开始解析，打印**前 40 个条目**，
   看能否一路吃到 `treeSize` 末尾。能 → 布局确定，加一个"先试正确对齐再退回"的分支。
2. 若第 1 步能吃完树：确认那 2 字节是什么（filename 的 NUL 后紧跟的 2 字节，
   疑似 packer 的 per-file 标记或 hash）。**不要猜**——用第二个包（夏安/DSR50 里的 VPK）交叉验证。
3. VVD magic 失败很可能是同一个根因（数据偏移算错 → 读到垃圾）。
   一旦树能正确解析，先重跑 `hd2mod vpk2patch --dry-run` 看 VVD 是否恢复。
   若 VVD 仍失败，再单独查数据区（是否有 LZMA/压缩块、offset 是否相对数据段起点）。
4. 修好后把这几个包都跑通，再验"缺一条手臂"是否随之消失（独臂多半是几何读不出→
   只拿到部分 mesh 的结果，不是独立 bug）。

### B. 仅有服装，材质闪烁，管理器报错（`仅有服装，材质闪烁，管理器报错\`）

样本：`【KKS】刑部姫 Osakabehime 白色泳衣 原版\`（**已解开的 VPK + materials/models 目录**，
含 `body.vmt`、`face_*.vmt`、`shader\vmt_base.vmt`、`Outline_*.vmt`）
+ `【IS】Houki Shinonono 连体衣 白 人物MOD.7z` + 一张 `31e2f5…png`

现象：只有服装（没人）；材质闪烁；管理器报错。

**假说（待验）**：
1. **材质闪烁**经典成因：多个材质共用同一个 material id 但行为不同（模板/贴图不同）→
   部署时互相顶替，逐帧闪。这正是 `SourceMaterialConverter.BuildMaterialIdentity` 那段注释讲过的坑
   （`|alphaclip`、`|emission`、`|matcap:` 三个后缀就是为了拆开撞车的材质）。
   查法：转换后 dump `materialDetails`（`--json`）看**有没有两个不同名字的材质拿到同一个 `material` id**。
2. 这个包名带 "shader"（`shader\vmt_base.vmt`、`eye_base.vmt`）→ 很可能是**纯着色器覆盖**，
   与上一轮 `ExplainNothingProduced` 那条（HD2 模板没有 phong/rimlight 槽）同类。
3. "管理器报错"要从用户那里拿**具体弹窗文字**，或者复跑一次看导入报告。

### C. 老鼠干（`老鼠干\`）

样本：`【VRM】橘雪莉 人物MOD.zip` —— 里面是 **21 个 .vpk + 2 个嵌套 .zip**（不是 .vrm！）。
现象："老鼠干" = 模型塌成一团（典型**重定向失败**：骨名没对上 → 全部顶点收缩）。

**下一步**：先按 A 把 VPK 解析/几何搞定（如果这些包也 VVD 失败，那 C 和 A 同源）；
再跑 `hd2mod legacy inspect` / 现有 `SourceRetargetPipeline` 看 `Diagnostics` 里有没有
`MissingBones` / `NonInvertibleBindMatrix`。注意 `Retargeter.FillHd2OnlyHeads` 里的
`_newHead["spine1"]` 会在源骨架缺 spine1/spine2 时抛（上一轮已把异常降级成"该包失败"，
但**根因没修**）——"老鼠干"很可能就是这个：骨对不上但没抛，于是姿态全塌。

### D. 解压时间过长（`解压时间过长（十几分钟仍未完成\`）

样本：`求生之路-Urara公式服8人包.zip`（46 MB，10 个 .vpk + 2 个嵌套 .zip）。
**没有报错，只是慢**。

**日志实测（已核对）**：这次会话里 `Import.ProcessQueue` 的耗时分别是
100.5 s（16:26，Aaliyah 15 包）、16.9 s（16:39，夏安 20 包）、2.9 s、6.9 s、
57.0 s（16:47）、16.0 s（16:53）——**最长 100 秒，日志里找不到"十几分钟"那次**。
所以要么用户说的那一次不在这次会话里，要么他看到的是**转换阶段任务条长时间不动**
（阶段文字只写"正在转换 i/N"，10 个包 × 每个 1–3 s 只是十几秒，但如果卡在解压/探测就难说）。
**下一步**：
1. 让他指出那次的**时间点**，或者直接复跑并把 `Urara` 那个 zip 交给我们测（已在样本里）。
2. 拆时间：解压 zip（7z）→ 探测是否 VPK 源（`7z l`）→ 解压到 staging → 逐个 VPK 转换。
   若落在转换，就是**每包起一次 hd2mod 进程**的固有开销（`--material-source` 把整批交出去，
   等于每个包都要重扫全部 10 个 VPK 的目录）。
3. 已知可优化点：`ConvertAsync` 串行逐包起进程；每个子进程都重新打开整批 VPK。
   先量再改，别猜。

## 复现与验证的固定动作

```
# 单包诊断（不落盘）
HD2ModCli\bin\Debug\net10.0\hd2mod.exe vpk2patch <某个.vpk> --output <临时目录> \
  --material-source <同目录> --manager-root <管理器根> \
  --game-data "D:\SteamLibrary\steamapps\common\Helldivers 2\data" --json

# 只看规划（快）
… vpk2patch <vpk...> --output <临时目录> --dry-run --json
```
现成探针：`tmp\vpklist`（`VpkArchive` 列表/实体转储 + `MaterialAppearance.IsContentFreeBase` 统计），
`HD2MOD_DEBUG=1` 会让 CLI 打完整堆栈。

## 本轮已确认、不要重复踩的结论

- 发布包已内置 10 份 `.material` 模板（`Hd2SourceMigration.csproj` 的 EmbeddedResource），
  **不再依赖 HD2SDK 目录**；`texconv.exe` 仍在 `third_party\hd2sdk\deps\`，由
  `Assert-ReleaseLayout` 守着。
- `patch` 型 VMT（include/insert/replace）已支持；`$matcaptint ≥0.9` 不再单独决定
  "matcap 就是表面"，还要"底色无内容 ∧ alpha 不承担形状"（花瓣那个 bug 的修法）。
- 一个坏包不会拖垮整批（逐 VPK 的 catch 已收 KeyNotFound/Argument/IndexOutOfRange）。
