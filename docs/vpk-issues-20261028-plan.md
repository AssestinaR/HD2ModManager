# VPK 转换问题排查方案（2026-10-28）

> 这份文件是**压缩上下文前的交接单**：压缩后照这份继续，不必重新问用户。
> 用户样本：`C:\Users\Administrator\Downloads\测试问题.rar`（961 MB）
> 已解到：`E:\Data\source\repos\WpfApp1\tmp\issue-20261028\测试问题\`（1.23 GB，**别删**）
> 用户日志：`tmp\issue-20261028\测试问题\manager_20261028.log`（2 MB，3681 行，2026-10-28 16:23–18:07）
> 额外解压（**别删**）：`tmp\issue-20261028\x\`（夏安 zip + DSR50 zip 解出来的 VPK）

## 已经修好的（本轮，三笔提交）

| 提交 | 症状 | 根因 | 验证 |
|---|---|---|---|
| `42da2c6` | 整具模型**没有材质** | `patch` 材质里 `insert` 被当成"只补缺"，于是 include 的 `$basetexture "basetexture"` 占位值永远压着子材质写的真贴图 | 夏安 基础组件：材质 0→18、贴图 0→22 |
| `42da2c6` | 某包直接"打不开"（44 个包全跳过） | 目录树里的名字是 **GBK**，按 UTF-8 解出 U+FFFD，「大腿」「倒地」撞成同一路径 → 整树判失败 | 秋桦 shader 基础组件 201 条目正常打开并可转 |
| `42da2c6` | 转换进程**爆栈崩掉** | `RetargetedMeshBuilder` 循环体里的 `stackalloc` 每轮再占一份栈（19 万三角形 × 12 B ≈ 2.3 MB） | 同包可转：129558 顶点 / 190530 三角形 / 47 材质 / 40 贴图 |
| `795db79` | 报告里全是看不懂的 `VVD magic 不是 IDSV` | 那些包的 `.vvd/.vtx` 是 **28 字节占位文件**（只有 IDSV 头 + 与 mdl 一致的校验和，零顶点），真几何在配套「基础组件」包里 | 夏安 9 条跳过理由已改成可照做的说明 |

**怎么确认 `insert` 是覆盖语义**（这条别再翻案）：GF2 与 KKS 两个不同作者的包，
基础材质都是 `shader/vmt_base.vmt`，里面明写 `"$basetexture" "basetexture"` 并注释
「由 patch VMT 替换」；33 份子材质**全部**用 `insert` 写真底色，`replace` 出现 0 次。
若 insert 只补缺，这些包在 L4N 里根本找不到底色贴图，作者不可能没发现。

## 用户日志里读出来的东西（已核对，不是推测）

- **0 条 ERROR**，830 条 WARN 全是 `[UIPerf]` 性能噪声（event-burst / frame-stall / slow-operation）。
  → 用户说的"管理器报错"不是崩溃日志，而是**导入报告里的跳过原因**或界面提示框。
- 三次 VPK 导入的汇总：
  - `15 个来源包，转换 5，跳过 10`（doomflame-aaliyah：Aaliyah 版一切正常，同批手办/着色器包按设计跳过）
  - `20 个来源包，转换 2，跳过 18`（少女前线2 夏安）
  - `44 个来源包，转换 0，跳过 44`（少女前线2 秋桦 仁心仁术 shader）

## 四个问题分类（用户自己分的文件夹，正好对应四条线）

### A. 无材质独臂（`无材质独臂\`）—— ✅ "无材质"已定位并修好，"缺一条手臂"仍待查

用户样本：`【少女前线2：追放】DSR50-暮色絮语 原版.zip`、`夏安 青涩夜会 人物MOD.zip`、
`秋桦 人物MOD.zip`、`【少女前线2：追放】秋桦 仁心仁术 shader 基础组件.vpk`
现象：模型能进游戏，但**没有材质**且**缺一条手臂**。

**"没材质" = `insert` 覆盖语义 + 占位几何识别，两处都修好了**（见上面那三笔提交）。
当时的日志证据：
1. 角色 VPK 全部报 `几何读不出来：VVD magic 不是 IDSV` —— 这些包的 `.vvd` 只有 28 字节，
   是占位文件（零顶点），按设计就该跳过；现在理由已经说清楚了。
2. 基础组件那个包 `VPK 目录树无法解析` —— 是 GBK 名字撞车，已修，包能正常转。

**我最初"文件名后多 2 字节"的判断是错的**，别再沿这条路走：那 2 字节就是文件名 `"3"` 和它的
终止符，18 字节条目本身完全正确（201 个条目首尾相接、偏移+长度之和正好等于数据段长度）。

**"缺一条手臂"还没查清。** 现有线索（都是实测数字，不是推测）：

- 夏安 基础组件：7 个 Unit，左右手 5449 / 5263 顶点，基本对称 —— 不像几何被吃掉。
- 秋桦 shader 基础组件：7 个 Unit，但 **LeftArm 只有 447 顶点 / 657 三角形，RightArm 2326 / 3968**，
  而且 **Chest 吃到 71281 顶点**（占全模型 55%）。24 个 mesh group → 7 个 unit。
  这种"一大块几何挤进 Chest、一条手臂几乎空"的分布，最像是**分部位（BodyRegion）判定把几何划错**。
- 下一步：把 `VpkUnitPlan` / mesh group 的分配过程打出来（每个 group 的 region 判定依据），
  对秋桦那个包逐组比对；同时拿 `秋桦 人物MOD.zip` 里 `原版版本\… 基础组件.vpk`（374 MB）
  这份"同一角色的正常版"做对照 —— 若那份左右手对称，问题就锁在 shader 版的分部位逻辑上。
- 另一个待验方向：in-game 少一只手也可能来自 rekey（替换 11 个 Unit / 隐藏 48 个网格）那一步
  把某个手臂 Unit 顶掉。用 `hd2mod` 的 rekey 报告查目标护甲里手臂 Unit 的映射结果。

### B. 仅有服装，材质闪烁，管理器报错（`仅有服装，材质闪烁，管理器报错\`）—— ✅ 同一根因，已修

样本：`【KKS】刑部姫 Osakabehime 白色泳衣 原版\`
+ `【IS】Houki Shinonono 连体衣 白 人物MOD.7z` + 一张 `31e2f5…png`

**日志里的决定性一行**（L2180）：
```
✔ 【kks】刑部姫 osakabehime 白色泳衣 基础组件.vpk → （7 个 Unit / 1 个材质 / 3 张贴图）
```
整个包只出 **1 个材质**。原因：这个包里 16 份材质用的是 `insert`，只有
`Outline_Base` / `Outline_Cloth` 两份用 `replace` —— 旧实现让 `insert` 只补缺，
于是只有那两份 replace 材质活下来（它们恰好共用同一个 id，所以是"1 个材质"）。
=> 游戏里只剩轮廓/描边 → 用户看到的"只有服装（没人）"+"材质闪烁"（描边材质与目标护甲抢同一个槽）。
"管理器报错"就是导入报告那一列跳过原因，不是崩溃。

**修完实测**（`--material-source` 指向同目录）：7 个 Unit / **16 个材质 / 23 张贴图**，
只剩 4 条真·不支持（`eye_base` 用 `$iris`、`vmt_base` 是模板本身），
重复 id 那些经逐份比对**基础贴图完全相同**，属于设计内的合并，不是闪烁源。

👉 用户需要做的：用新构建重导一遍这两个包再看游戏。若描边仍闪，再查
`SourceMaterialConverter.BuildMaterialIdentity` 的 `|alphaclip` / `|emission` / `|matcap:` 拆解。

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

**本轮实测可用的两个"管理器根"**（CLI 要它找 `data/resources/bonehash.txt`、`data/indexes/asset-index.sqlite`）：
`HD2ModManager\bin\Debug\net10.0-windows`（索引 113 MB，2026-10-27，**能跑通**）。
`--manager-root` 换成 `HD2ModManager`（源码目录）**不行**，那里没有 `data\`。

现成探针：`tmp\vpklist`（`dotnet run -c Release -- [tree|list|magic|dump|model] …`）：
- `tree <vpk>`：按 18/16 字节两种步长手走目录树，报"是否正好吃完整棵树"、重复路径、非 ASCII 名字的原始字节
- `list <vpk>`：条目偏移/长度，验证偏移和长度是否首尾相接
- `magic <vpk…>`：把每个 vvd/mdl/vtx 条目的前 16 字节与长度打出来（认占位文件就靠这个）
- `dump <vpk> <子串> <输出目录>`：把匹配的条目落盘（看 VMT 原文用）
- `model <vpk> <后缀>`：解析 mdl 并打骨名（判断骨架是否是标准 ValveBiped）

`HD2MOD_DEBUG=1` 会让 CLI 打完整堆栈。

## 本轮已确认、不要重复踩的结论

- **`patch` VMT 的 `insert` 与 `replace` 都是覆盖语义**（见上面证据）。别再改回"只补缺"。
- **VPK 目录树里的名字要先 UTF-8 严格解、失败再按 CP936 兜底**；名字解码差异会制造假重复路径，
  所以"重复路径"不能当作"步长选错"的判据（真正的判据只有"正好吃完整棵树"）。
- **循环体里不能写 `stackalloc`**（C# 里每轮再占一份栈，方法返回才回收）。大模型会 Stack overflow，
  而且是进程级崩溃、没有任何托管异常可 catch。
- 28 字节的 `.vvd/.vtx` = 占位文件（`IDSV` + 版本 + 与 mdl 一致的校验和 + `lodCount=1`，零顶点）。
  这类包（GF2 每个角色的 8~9 个 survivor 包、KKS 的同类包）本来就只带材质和菜单图，
  **真正的模型只在「基础组件」包里**，跳过它们是对的。
- 发布包已内置 10 份 `.material` 模板（`Hd2SourceMigration.csproj` 的 EmbeddedResource），
  **不再依赖 HD2SDK 目录**；`texconv.exe` 仍在 `third_party\hd2sdk\deps\`，由
  `Assert-ReleaseLayout` 守着。
- `$matcaptint ≥0.9` 不再单独决定"matcap 就是表面"，还要"底色无内容 ∧ alpha 不承担形状"
  （花瓣那个 bug 的修法）。
- 一个坏包不会拖垮整批（逐 VPK 的 catch 已收 KeyNotFound/Argument/IndexOutOfRange）。

## 还没做的（按优先级）

1. **A 的"缺一条手臂"**：秋桦 shader 包的 Unit 顶点分布明显异常（Chest 71281 / LeftArm 447 /
   RightArm 2326），先查分部位判定，用 `秋桦 人物MOD.zip` 里 `原版版本\… 基础组件.vpk` 做对照。
2. **C 老鼠干**：`Retargeter.FillHd2OnlyHeads` 里 `_newHead["spine1"]` 在源骨架缺 spine1/spine2
   时会抛，目前只是降级成"该包失败"，根因没修。先确认 `【VRM】橘雪莉` 那 21 个 .vpk 能否转换（A 修完
   之后可能已经不同），再看重定向 Diagnostics。
3. **D 解压慢**：日志里最长 `Import.ProcessQueue` 只有 100.5 s，没有"十几分钟"那次；
   要么向用户要时间点，要么直接量 `求生之路-Urara公式服8人包.zip`（样本里有）。
4. 用户回归：用新构建重导 A/B 两批包，确认"没材质"消失；同时观察是否还缺手臂。

