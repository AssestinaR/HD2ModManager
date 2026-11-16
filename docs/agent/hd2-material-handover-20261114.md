# HD2 自定义材质 —— 交接文档（2026-11-14）

> ⛔ **已过时（2026-11-16 标记）。** 本文 §0 说的「唯一卡点：换进我们编译的 SHEX 后那条 pass 不再产生可见贡献」
> 已于 **2026-11-15** 解决（根因是真 NOP 的指令 token 写错 + 记录头 `u32@20` 必须为 1，见 `build_signed_ps.py`），
> 自定义 DXBC 已实机执行。现行文档：`hd2-custom-shader-milestone-20261115.md`（成果）、
> `hd2-custom-shader-runbook-20261115.md`（实施手册）、`hd2-handover-hf-lab-and-next-steps-20261115.md`（交接）。
> 本文保留作当日快照，**不要照它的「下一步」执行**；其中 §1 的事实矩阵仍可引用。

> 面向"压缩上下文后继续推进"。本文只写**已验证的事实**、**实测矩阵**、**当前唯一卡点**与**下一步**，
> 不重复推导过程。原理与逐轮记录见 `hd2-custom-material-pipeline-plan-20261114.md`，
> 操作手册见 `hd2-material-payload-howto.md`。

---

## 0. 一句话现状

容器格式、patch 规则、载荷结构、作者改了什么——**全部已解出并有工具**；
唯一卡住的是：**把我们编译的 SHEX 替换进记录后，引擎不报错、但那条 pass 不再产生可见贡献**。

---

## 1. 已确认的事实（都有实测证据）

### 1.1 patch 容器

- 文件：`<档案id>.patch_<N>` + 同名 `.gpu_resources`（+ 可选 `.stream`）；
- TOC 文件结构：`magic 0xF0000011` + 72 B 头 + 32 B/条类型表 + 80 B/条条目 + TocData 区；
- **条目里的 `gpuOffset` 是"侧车文件内部偏移"，不是 TOC 文件内偏移**（踩过大坑）；
- 条目 80 B 布局：`fileId(u64) typeId(u64) tocOff(u64) streamOff(u64) gpuOff(u64) unk1(u64) unk2(u64)
  tocSize(u32) streamSize(u32) gpuSize(u32) unk3(u32=16) unk4(u32=64) entryIndex(u32, 从 1 起)`。

### 1.2 两条硬规则（必须遵守）

1. **patch 索引从 `_0` 开始必须连续**：引擎按 `patch_0`、`patch_1`… 顺序读，**缺号之后的都不会被读**。
   新增 patch 必须是"当前最大索引 + 1"（当前 9ba 档案已用到 `patch_72`，下一份是 `patch_73`）。
2. **`9ba626afa44a3aa3` 是公共档案**：注册到它的条目会覆盖"对应资源"，**与资源原本属于哪个档案无关**。
   覆盖本体 `advanced`（其实属于 `18235e0c9ec0e636`）也要挂在 9ba 下。

### 1.3 材质 TocData

- 头部 136 B；`+16`(u64) = **载荷字节数**（改载荷长度必须同步，否则材质加载失败、用它的模型整片消失）；
- `+24`(u64) = parent 材质 id（0 = 根材质，自带载荷）；
- 之后是 `N×u32` 贴图键、`N×u64` 贴图 id、`M×20 B` 变量表。

### 1.4 载荷结构（以本体 advanced 为例：2,928,416 B）

- 头部：`0x00` 记录数(179)、`0x04` 材质 id 低 32 位、`0x08` = **长度−16**、`0x18` = **最大偏移**、
  `0x30` = 32 位哈希（**引擎不校验**，我们改过大量内容都生效）；
- 记录区：`0x40` 起 88 B/条 的头部记录（含 size/hash 等），之后是 **202 条 DXBC 记录**，
  记录之间夹 92 / 3780 B 空隙；
- 每条记录 = `RDEF + ISGN + OSGN + SHEX + STAT`，块表是**偏移数组**（`+28` 块数，`+32` 起每项 u32）。

### 1.5 DXBC 与 checksum

- `fxc` **校验** 128 位 checksum：只翻 checksum 1 个比特就拒绝反汇编；
- **游戏引擎不校验**：我们改过几十次内容（含整段 SHEX 替换）都没因此被拒；
- 因此：被改过的 blob 只能用自研反汇编器（`.dsh/tmp/sm5disasm.py` / `decode_tail_full.py`）。

### 1.6 作者那份"自定义材质"到底是什么

- 与本体 advanced 的同名材质 pass 相比：**RDEF / ISGN / OSGN 逐字节相同**，SHEX 多 2,240 B；
- 差异只有两处：`dcl_temps` 21→30，以及尾部追加 560 条指令（把 `mov o0.w, l(1.0)` 换成算出来的值）；
- **不是从别的游戏/lilToon 搬来的**：cbuffer/贴图槽/IO 全是 HD2 自己的；
- 尾部代码做的事：UV 导数→mip LOD→`sample_l t[7]`（细节法线）+ 导数构造 TBN + `N·V` →
  `t = saturate((N·V - 0.21) · 1.26582)` → `alpha = (1 - t⁴(3-2t))²` → 再按 UV 采一次 `t[7] × 0.266` 调制；
- 作者是**改字节/拼 SHEX**（checksum 是旧值），不是重新编译整个着色器。

### 1.7 引擎日志（本轮新打通）

- `data/settings.ini`：`local_console_log = "%APPDATA%/Arrowhead/Helldivers2/logs/console_*.txt"`；
- **默认不落盘**，必须加启动项 **`--log-to-file`**（exe 里有 `--log-to-file / --log-to-memory / --no-console`）；
- 已验证可产出（约 36 KB / 402 行），但**只记录 info 级**：
  会记录 `PackageLoader`（含 `9ba626afa44a3aa3`、`ffcad1f7ff9888d7` 等包）、`ItemCustomizationManager`，
  **不记录材质/着色器级别的错误**；`d3d_debug = false` 可考虑打开（需配合调试输出捕获）。

---

## 2. 实测矩阵（改动 → 结果）

| # | 改动对象 | 改动内容 | 结果 |
|---|---|---|---|
| 1 | 全 34 个"视角曲线"块 | `T 0.21 → 0.6` | **丝袜整个消失，只剩皮肤** |
| 2 | 10 条材质 pass（62,652 B 家族） | `T=0.5` | **无变化** |
| 3 | 10 条材质 pass | 强制 `alpha ≡ 1`（bias=-2） | 无变化 |
| 4 | 10 条材质 pass | 曲线结局换成常数 0 / 0.5 / 1 | 三种**都无变化** |
| 5 | 4 条 46 KB pass | `T=0.5` | 无变化 |
| 6 | 20 条 60 KB pass | `T=0.5 / 0.6` | **黑层消失（变光腿）** |
| 7 | 60 KB 家族 A（8 条副本） | `T=0 / 0.12 / 0.6` | 0.6 → 光腿；0 与 0.12 → 光腿 |
| 8 | 60 KB 家族 A | 抖动幅度 `0.266 → 0` | 光腿 |
| 9 | 整张 albedo 贴图 | 换成纯绿（BC7 1024² 11 mip，texconv 生成） | **无变化**（贴图链路不驱动可见颜色） |
| 10 | 材质 pass 的 SHEX | 整段换成我们编译的**洋红探针**（等长） | **无变化** |
| 11 | 60 KB 家族 A 的 SHEX | 洋红探针（只写 o0，1 个输出） | **黑层消失，但没洋红** |
| 12 | 60 KB 家族 A 的 SHEX | 洋红探针（**3 个输出，契约对齐**） | 同上：黑层消失、无洋红 |
| 13 | 60 KB 家族 A 的 SHEX | 洋红探针（3 输出 + **RDEF 资源全量声明**） | 同上：黑层消失、无洋红 |

> 结论：**家族 A 的 SHEX 替换确实被引擎接受了**（因为它改变了画面），
> 但我们的着色器**没有产生可见贡献**——pass 被丢弃或输出被后续处理吃掉。

---

## 3. 当前唯一卡点

**替换 SHEX 后，那条 pass 不再产生可见贡献。**

已排除：
- 不是长度问题（记录 60,412 / 载荷 / TocData 全未变）；
- 不是容器问题（TOC 直接用作者原文件验证过）；
- 不是 checksum（引擎不校验）；
- 不是命名/索引（9ba + 连续索引，且改动确实生效了）。

仍可能的（按可能性排序）：
1. **引擎按记录里的 RDEF 做资源绑定校验**：我们的 SHEX 声明的 `dcl_constantbuffer` 范围
   （fxc 只声明实际用到的）与 RDEF 的 `CB0[46] … CB7[13]` 不完全一致 → 创建失败 → pass 被跳过；
2. **引擎要求 SHEX 与 RDEF 的变量/采样器列表逐项对应**（不只是索引范围）；
3. 该 pass 的产物需要**特定通道语义**（例如 o1.x 是"覆盖度"），写死会得到"无贡献"；
4. 我们替换的是**8 份副本之外的"真正使用的那份"**（可能性低：8 份逐字节相同，且任一被改都影响画面）。

---

## 4. 可复用的工具与脚本

| 脚本 | 作用 |
|---|---|
| `.dsh/tmp/sm5disasm.py` | 最小 SM5 反汇编器（DXBC 规范操作数解码） |
| `.dsh/tmp/decode_tail_full.py` | 完整解码作者追加的尾部代码块 |
| `.dsh/tmp/merge_stat.py` | 按块重建 DXBC 记录（自动重算块表/total） |
| `.dsh/tmp/inject_equal_length.py` | 把编译好的 SHEX **补齐等长**后替换进记录 |
| `.dsh/tmp/probe_all_copies.py` | 对一组记录批量做等长 SHEX 替换（当前指向家族 A 的 8 条） |
| `.dsh/tmp/build_patch_correct.py` | **正确**的 patch 容器构造（TOC 只放头/类型/条目/TocData，载荷进侧车） |
| `.dsh/tmp/compare_patch_layout.py` | 对照作者 patch 与我们 patch 的字段差异 |
| `.dsh/tmp/fix_payload_header.py` | 同步载荷头 `0x08 / 0x18` 与 `TocData +16` |
| `tools/payload_repackage.py` | 载荷拆包 / 等长替换 / **字节一致重建校验** |
| `tools/payload_selftest.py` | 体检：记录图 / 分组常量 / 差异字节 |
| `tools/PayloadDump` | `--fp` 指纹、`--toc` 导出材质 TocData+载荷（含 `archive=` 归属）、`--inventory` 根材质特性清单 |

---

## 5. 环境状态与还原

- **mod 库载荷已恢复原始**：`E:\mod\【常服】Milfy 替换 CE-07-材质\9ba626afa44a3aa3.patch_1.gpu_resources`
  （30,601,520 B，与 `.abtest-backup\20261114-140811-alphaonly\` 逐字节一致）；
- `data\` 下**没有**我们放的 `patch_73` / `patch_91`（9ba 索引链 0–72 连续）；
- 所有实验产物：`%TEMP%\milfy-inspect\`（`variants\`、`inject\`、`rootpayloads*\`、`advanced-root\`）；
- 备份：`.abtest-backup\<时间戳>-<标签>\`，`20261114-140811-alphaonly` 是最早的原始载荷。

---

## 6. 下一步建议（按优先级）

1. **对齐 `dcl_constantbuffer` 范围**：手写 DXBC 或用 HLSL 强制引用全部 cbuffer 的全部范围
   （例如每帧读 `cb0[45]`、`cb7[12]`），使我们 SHEX 的声明与 RDEF **逐项一致**，再测一次；
2. **拿一个"作者已经改成功"的记录做对照**：把作者那条 62,652 B 记录**原样**塞进本体 advanced 的
   材质 pass（同长度不可能，但可塞进作者自己的载荷里已有的位置）——验证"作者改过的 SHEX 能被引擎接受"，
   从而区分"引擎拒绝所有外来 SHEX"还是"只拒绝我们的"；
3. **打开 D3D 调试层**（`settings.ini` 的 `d3d_debug = true`）+ 捕获 OutputDebugString，
   看 `CreatePixelShader` 是否失败及原因；
4. 若确认引擎拒绝外来 SHEX，则改用**等长就地指令改写**（不换整段 SHEX），
   在家族 A 的现有指令流里做文章（我们已验证改立即数/换指令都能生效）。

---

## 7. 关键教训（避免重走）

1. **不要相信"引擎会读 patch_N"，除非索引连续**；
2. **`gpuOffset` 是侧车内偏移**，把载荷塞进 TOC 文件是致命错误（会让材质整片消失）；
3. **改载荷长度必须同步 `TocData +16` 与载荷头 `0x08/0x18`**，否则模型消失；
4. **"模型消失"通常不是崩溃，而是材质加载失败的 fallback**（`settings.ini` 里 `missing_material = warning`）；
5. **日志默认不落盘**，必须 `--log-to-file`；
6. **判断"改动是否生效"必须用画面差异，不要用"文件写了"当证据**——
   我们曾多次以为改进去了，实际上 patch 根本没被读；
7. 家族 A（60 KB，8 份副本）是**唯一被证实能影响可见黑层的 pass**；10 条材质 pass 改什么都没效果。
