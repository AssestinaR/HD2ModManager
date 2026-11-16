# 载荷载体三样本对照 & 「动态发光」的证据复核

> 2026-11-14，纯离线（只读游戏包与 mod 包，未改任何游戏文件）。
> 前置：`hd2-custom-material-pipeline-plan-20261114.md`（§8 路线修正、§9 变长注入）、
> `hd2-cutout-metal-offline-screening-20261109.md`、`hd2-material-handover-20261114.md`。
> 用户提供的全息背包样本：`C:\Users\Administrator\Downloads\像素全息补给背包.7z`
> （解出 `9ba626afa44a3aa3.patch_0` 44,068 B + `.gpu_resources` 16,688,544 B）。

## 一、这轮要回答什么

「游戏内不支持的能力」里，目前只有两项被实机确认**做出来了**：

| 能力 | 样本 | 实机证据 |
|---|---|---|
| 丝袜高亮半透明（随视角） | Milfy `0xe577db2cf97fcddc` | 黑丝层可见；改曲线即消失（多轮复现） |
| 动态发光（随时间） | 全息背包 `0xdec4b7db2e8095f3` | 2026-11-09 记录「目前唯一实机确认会动的样本」（未留下逐项判据） |

两者都是「本体 `advanced` 根材质 + 改一条 pass + 材质自带编译载荷」。这轮把三份载荷放在一起比。

## 二、三样本硬数据

| | vanilla advanced | Milfy 丝袜 | 全息背包 |
|---|---|---|---|
| 载荷路径 | `%TEMP%\milfy-inspect\rootpayloads3\ef9b3fc2fc1ba61e.bin` | `9ba626afa44a3aa3.patch_1.gpu_resources` 起点 | 解出的 `.gpu_resources` 内 `gpuOff=3,146,560` |
| 载荷大小 | 2,928,416 B | 4,037,336 B | 3,736,264 B |
| 头 `0x00` | 179 | 179 | 179 |
| 头 `0x04` | `0xef9b3fc2` | `0xe577db2c` | `0xdec4b7db` |
| 头 `0x08` | = 长度−16 | = 长度−16 | = 长度−16 |
| DXBC 记录数 | 202 | 272 | 202 |
| 记录块组合 | `RDEF+ISGN+OSGN+SHEX+STAT` | 同左 | 184 条 `RDEF+ISGN+OSGN+SHEX+**PRIV**`；18 条 `…+PCSG+STAT` |
| 材质主 pass 长度 | **60,412 B** | **62,652 B** | **63,052 B** |

## 三、决定性发现：长度字段是 **20 处**，两份 mod 都是「20 → 20」全量替换

在载荷里数 `u32 == 长度值` 的出现次数：

| 载荷 | `60412` | `62652` | `63052` |
|---|---|---|---|
| vanilla advanced | **20** | 0 | 0 |
| Milfy 丝袜 | **0** | **20** | 0 |
| 全息背包 | **0** | 0 | **20** |

结论：

1. 记录变长后，**旧长度一处不剩、新长度恰好 20 处**。两位作者（两份互不相关的 mod）都是这个形状
   ⇒ 这是一个**机械的全量替换**，不是需要解哈希的加密字段。
2. `hd2-custom-material-pipeline-plan-20261114.md` §9 的判读「必须改写 size 条目 **+ hash32**」
   方向对了一半：**20 处长度必须全改**，但**没有任何证据要求我们重算 hash32** ——
   Milfy 的载荷里那份被改过的 blob 连自己的 DXBC 校验和都是旧值，游戏照样加载。
3. 直接可做的实验（离线一次、实机一次）：拿本体 `advanced` 载荷，把某一族记录的长度
   从 `60412` **全量替换**成新值（20 处一起改），同步 `TocData +16`、载荷头 `0x08`/`0x18`、
   条目 `gpuSize`，其余一个字节不动。这正是之前「模型整片消失」时**没有全做**的那一步。

`payload_repackage.py verify` 对三份载荷全部 `VERIFY: OK (byte-identical)`，
说明"拆包—重打包"这一层已经完全可控。

## 四、工具修正：`PRIV` 块

`tools/payload_repackage.py` 的块名白名单里没有 `PRIV`，于是全息背包载荷里
**184/202 条记录被整批跳过**（表现成"这份载荷只有 18 个着色器"）。已加入白名单，
修正后 `segments=405 records=202`，`VERIFY: OK`。Milfy 载荷复验结果不变。

## 五、「动态发光」的证据复核 —— 11-09 的结论证据不足

全息背包材质 `0xdec4b7db2e8095f3` 的 TocData（2,372 B，`parent=0` 即它自己就是根材质）：
**12 槽 / 80 变量 / 值区 484 B**。动画相关的变量逐个读出：

| 变量 | 哈希 | 值 |
|---|---|---|
| `EmissiveIntensity` | `0x02f6dc5b` | **12** |
| `EmissiveColor` | `0xc985395a` | (0.2, 0.8, 1) |
| `EmissiveUvdirection` | `0x3412359b` | (1, 0) |
| `EmissiveWaveSize` | `0xd0a0e0fc` | 1 |
| `EmissiveWaveSpeed` | `0x14f7ceac` | 1 |
| `EmissiveWaveGradient` | `0x3de979ac` | 0.5 |
| **`EmissiveAnimation`** | `0x7415ab88` | **0** |
| `GlitchAmount` / `Speed` / `Gridsize` / `Color` / `ColorPower` / `CenterOffset` | — | **全 0** |

**`EmissiveAnimation = 0`。这份"唯一会动"的样本，自发光动画是关着的。**

而 2026-11-09 我们正是**照抄这一组值**写进 `advanced` 的（`1511d37` 提交正文逐项列出
`emissive_animation=0`）。所以：

> 「抄同样的变量到 advanced 没有效果」**只能证明"这一组（动画关闭的）参数不产生运动"**，
> 不能证明「`advanced` 的着色器不实现 `emissive_wave_*`」。

这条结论必须降级为**未验证**。真正该测的自变量是
`EmissiveAnimation=1` + `EmissiveWaveSpeed≠0`（或 `GlitchAmount≠0`），而不是继续抄静态值。

另一条线索：这个材质的 12 个槽里有 **`LensCutoutTexture`(`0x89bbcec2`)** 与
`SclarFieldOpacity`(`0x479fb1ef`)，载荷尾部还有 18 条带 `global_viewport` / `context_camera`
的 pass（`c_ui_3d`、`ui_3d_shadow_camera0..3`）。「全息背包在动」更可能来自
**镜头/全息遮罩那一路**，而不是自发光。这条也待证。

## 六、材质主 pass 的接口兼容性（对"移植"是关键）

本体 `advanced` 的 60,412 B 主 pass vs 全息背包的 63,052 B 主 pass：

| 块 | 结果 |
|---|---|
| `ISGN` | **完全相同**（sha256 前缀 `7a196c0476dd50fe`，248 B） |
| `OSGN` | **完全相同**（`8a37e6f36d6091c2`，44 B） |
| `c0` / `c_ui_3d` / `global_viewport` | 成员名与偏移**全部一致** |
| `c_per_object` | 81 vs 81，**关键成员偏移全同**（`emissive_wave_speed@472`、`emissive_UVDirection@240`、`emissive_animation@464`、`glitch_speed@300`…）；仅 6 个槽**换了名字、偏移不变** |
| `SHEX` | 49,980 B vs 39,672 B（**两份不同的实现**） |

⇒ **接口层完全兼容**：换掉材质主 pass 不需要动 ISGN/OSGN，也不需要动任何 cbuffer 偏移。
这是"移植载荷"路线成立的技术前提，现在有数据了。

## 七、下一步（按性价比）

1. **（离线，可立刻做）20 处长度全量替换实验**：拿本体 `advanced` 载荷，把一条记录改长
   2,240 B（内容随意，例如把 Milfy 那条 PS 搬进去），20 处长度一起改成新值，同步 4 个外层字段，
   部署后看模型是否还在。**这一步决定"变长注入"通不通，通了我们就能自由写着色器。**
2. **（实机 1 次）动态发光的正面自变量**：`EmissiveAnimation=1` + `EmissiveWaveSpeed=4`
   + `EmissiveWaveGradient=0`（或 `GlitchAmount=1`/`GlitchSpeed=2`），在 `advanced` 上测。
   注意 `advanced` 的变量表里这些 id 全都有（与全息背包偏移一致），写进去不会失败。
3. **（实机 1 次）载荷整份搬运**：把全息背包那份 3,736,264 B 载荷原样装进我们自己的材质条目
   （长度不变，零耦合字段改动），parent 指向 `0xdec4b7db2e8095f3` 或自造根。
   这是「移植」链路的最小验证，不需要任何着色器知识。
4. 若 1 成立 → 回到 Milfy 那条路：我们能自己改 SHEX 并改变长度，`o0.w` 那条 alpha 曲线就能
   从"复刻别人的"变成"我们自己的材质参数"。

## 七之二、本轮产物：等长替换的「全息背包主 pass」载荷 + 可部署 patch

**思路**：全息背包的材质主 pass（63,052 B）比本体 `advanced` 的（60,412 B）长 2,640 B，
多出来的全在 `PRIV` 块（10,824 B）。而 `PRIV` 是引擎容忍的非标准块（那 184 条记录都带它）。
把它裁到 8,184 B，整条记录就精确落在 **60,412 B** —— **长度一字不改，载荷内所有偏移与
20 处长度字段一个都不用动**。这样既绕开了「变长注入」的全部耦合风险，又能把一份
**会读 `emissive_wave_*` 的着色器**换进本体 advanced 的同位置。

产物（`%TEMP%\milfy-inspect\swap\`）：

| 文件 | 大小 | 说明 |
|---|---|---|
| `new_pass_eqlen.dxbc` | 60,412 B | 新记录：`RDEF+ISGN+OSGN+SHEX+PRIV` |
| `advanced_payload_holopass_eqlen.bin` | 2,928,416 B | 本体 advanced 载荷，10 份主 pass 全部换成新记录 |
| `9ba626afa44a3aa3.patch_73` | 2,472 B | 覆盖本体 `advanced` 根材质（`0xef9b3fc2fc1ba61e`）的 patch |
| `9ba626afa44a3aa3.patch_73.gpu_resources` | 2,928,416 B | 侧车（`gpuOff=0`） |

工具：`.dsh/tmp/swap_eqlen_pass.py`（造记录 + 替换）、`tools/payload_repackage.py`（结构校验）。

**验收（全部离线通过）**：

| 检查 | 结果 |
|---|---|
| 新记录块组成 | `RDEF(12172)+ISGN(248)+OSGN(44)+SHEX(39672)+PRIV(8184)` = 60,412 ✓ |
| `RDEF`/`ISGN`/`OSGN`/`SHEX` 与 donor 逐字节 | **四块 sha256 全同** |
| 载荷内 60,412 记录份数 | 替换前 10 份，替换后 **10 份全部**等于新记录 |
| 载荷总长 / `60412` 出现次数 | 2,928,416 B 不变 / 20 处不变 |
| 记录数 | 202 → 202 |
| patch 回读 | `fileId=0xef9b3fc2fc1ba61e tocOff=184 gpuOff=0 tocSize=2288 gpuSize=2928416`；TocData 解析出 12 槽 / 77 变量，槽名 `BloodTiler · SclarFieldOpacity · Nar · IlluminateData · DetailNormalLeather · AlbedoEmissive · WoundNormal …` 与本体完全一致 |
| 部署位 | `data\` 里 9ba 索引 0–72 连续，下一个可用 **patch_73** |

**部署（一次重启即可判定）**：

```powershell
$d = 'D:\SteamLibrary\steamapps\common\Helldivers 2\data'
$s = "$env:TEMP\milfy-inspect\swap"
Copy-Item "$s\9ba626afa44a3aa3.patch_73"            $d
Copy-Item "$s\9ba626afa44a3aa3.patch_73.gpu_resources" $d
# 回退：删掉这两个文件即可
```

**看什么**：这条 patch 覆盖的是**本体 advanced 根材质**，所以凡是父材质为 `advanced` 的件都会
套用全息背包那份主 pass（爆炸半径大，但一局就能看出通不通）：

1. **模型还在、画面没崩** ⇒ 引擎接受「外来 SHEX + 裁短的 PRIV」这条路，变长注入的替代方案成立；
2. **再给某个 advanced 材质把 `EmissiveAnimation` 写 1、`EmissiveWaveSpeed` 写非 0、`EmissiveIntensity` 写 12**
   ⇒ 若能看见自发光在流动，**动态发光就打通了**（用户口径里的"随时间变化"）；
3. 若模型消失 ⇒ 引擎校验的另有其物（`PRIV` 长度被引用了 / RDEF 与 SHEX 不配套），
   下一步应保留 `PRIV` 原长但改从别的块省出 2,640 B。

⚠️ 已知的不确定性：这份主 pass 是 advanced 家族的（RDEF 只绑 `__tex_nar`、`__tex_albedo_emissive`、
`__tex_detail_normal_porcelain`、`__tex_zone_damage_ids`、`__tex_wounds_256` 和引擎全局贴图，
**没有** `LensCutoutTexture`），所以"全息背包在动"若来自镜头那一路，这份 patch 不会复现它 ——
它验证的是**自发光动画这一路**。

## 七之三、部署链路真相 + 打错目标的教训（2026-11-14 晚）

### 1. `data\` 的 patch 是符号链接，**库目录名 ≠ 部署索引**

```
data\9ba626afa44a3aa3.patch_69            → ...\9ba626afa44a3aa3.patch_73\9ba626afa44a3aa3.patch_0
data\9ba626afa44a3aa3.patch_69.gpu_resources → ...
```

库里的目录叫 `9ba626afa44a3aa3.patch_73\`，管理器却把它分配到 **index 69**（按 `profiles.json`
的 `loadOrder` 重新编号，overlay 固定占最后两位）。所以：

- **不能按目录名或"下一个空位"推断索引**，只能反查符号链接的目标路径；
- 往 `data\` 手拷文件无效 —— 管理器重部署时会覆盖。曾经据此误判"载荷没被加载"，实际是加载了。

工具：`tools/verify_deployed.py`（按符号链接目标反查 + sha256 比对 + 索引链连续性），
`tools/who_defines.py`（查某个 fileId 由哪些索引定义，看有没有被更高索引盖掉）。

### 2. 加载顺序：`9ba` 比本体归档早 6 秒

```
17:08:22.817  Starting load #ID[9ba626afa44a3aa3]        ← 所有 mod 的 patch 在这里
17:08:23.606  Loaded   #ID[9ba626afa44a3aa3]
17:08:28.705  Starting load #ID[18235e0c9ec0e636]        ← 本体 advanced 所在归档
```

「用 9ba 覆盖本体根材质 `0xef9b3fc2fc1ba61e`」实测**完全无变化**，而
`who_defines.py` 确认该 id 只由 index 69（我们）定义、没有任何更高索引抢占。
⇒ 与「后加载的归档赢」一致，即 **9ba 压不住本体归档里的资源**（待更多样本确认）。

**这条改变了路线**：不要再去覆盖本体根材质。**自造材质 id + 自带载荷 + 改 unit 的材质引用**
才是正确形态 —— Milfy 的作者和全息背包的背包作者都是这么做的。

### 3. 打错目标：作者载荷里有两个「长得都像主 pass」的组

| 尺寸 | 份数 | 是否含 `-0.21` | 实测作用 |
|---|---|---|---|
| 46,300 / 46,328 / 46,360 / 46,388 | 各 1 | 含 | 改它**无可见效果** |
| **60,416×2 / 60,444×2 / 60,476×8 / 60,504×8** | **20** | 含 | **改这组能让黑丝消失/出现（家族 A 在此）** |
| 62,652×10 | 10 | 含 | 改它**无可见效果** |

第一次实验换的是 62,652 那 10 条（错），随后改回从原始备份重做，**只换 60k 组那 20 条**，
每条保持自己的长度、差额由 `PRIV` 吸收：

```
size=60416 x2 → 60,416 B (PRIV 8,188)
size=60444 x2 → 60,444 B (PRIV 8,216)
size=60476 x8 → 60,476 B (PRIV 8,248)
size=60504 x8 → 60,504 B (PRIV 8,276)
载荷 30,601,520 B 不变；payload_repackage verify: VERIFY OK (byte-identical)
```

教训：**"体积最大 / 份数最多"不能用来识别主 pass**，必须用"改它有没有可见效果"来认。

## 七之四、⚠️ **已证伪（2026-11-15 02:12）：「整条记录替换」不生效** —— 原文保留作反面教材

> **本节下面的"突破"结论是错的，2026-11-15 已用对照实验推翻。改动之前请先读这一段。**

**对照实验**：`TOC 用原始版本`（不多改一个字节）+ 本节所述 holobag 记录替换侧车 ⇒
实机**仍是黑丝基线，毫无变化**。变体 `.dsh/variants/probe_recswap_control/`。

**误判的成因**：当初部署那份替换侧车时，磁盘上的 TOC 还是**上一轮换过的 advanced TocData（12 槽 + 品红自发光）**。
那次"黑丝→光腿"是 **TocData** 造成的，我却归因给了记录替换。

**由此确立的边界（取代本节结论）**：

| 手段 | 实测 |
|---|---|
| 在被替换的记录内部**改立即数 / 改指令**（长度、结构全不变） | ✅ 多次生效（`T` 值、抖动幅度） |
| **整条记录替换**（哪怕 length、签名、校验和、RDEF 全部对齐） | ❌ **从不生效** |
| 换成自己用 fxc 编的 SHEX（含"原版 RDEF/STAT + 我们的 SHEX"混合体） | ❌ 不生效 |

⇒ **自制着色器的真实形态只能是"在原 SHEX 内部改指令"**，不是"换一整段"。
推测引擎按记录的元数据（`STAT` 里的内容哈希/配对信息，或载荷头部那张 `(size, hash32)` 表）做校验或缓存命中，
内容与元数据不一致就退回原版。

以下是原始记录（**结论作废，过程与数据仍有效**）：

---

## 七之四-原（作废）、**"突破"：等长整条记录替换，引擎接受外来 SHEX**

### 结论

把作者载荷 mat 组那 10 条 62,652 B 记录，整条换成**全息背包**的材质主 pass
（`RDEF 12,172 + ISGN 248 + OSGN 44 + SHEX 39,672 + PRIV 10,424`，总长仍是 62,652），
实机结果：**黑丝变成光腿**。

判据（这是关键，不是"看起来变了"）：

| 检查 | 结果 |
|---|---|
| 作者载荷里与 mat 组同签名（ISGN `7a196c0476dd` / OSGN `8a37e6f36d60`）的记录 | **只有那 10 条** |
| 我们替换的份数 | **10 / 10** |
| ⇒ 引擎有没有可回退的同角色记录 | **没有** |
| 实机 | 黑丝 → 光腿（原记录里算 `o0.w` 的那 2,240 B 尾巴没了） |

**所以引擎确实执行了我们塞进去的外来 SHEX。** 不需要解 checksum、不需要碰任何长度字段。

### 角色签名是硬约束（这是上一版失败的真正原因）

| 样本 | ISGN | OSGN | 结论 |
|---|---|---|---|
| 本体 `advanced` 主 pass (60,412) | `7a196c0476dd` 248 B | `8a37e6f36d60` **44 B** | 材质主 pass |
| 全息背包 (63,052) | `7a196c0476dd` 248 B | `8a37e6f36d60` **44 B** | **同角色，可换** |
| 作者 mat 组 (62,652) | `7a196c0476dd` 248 B | `8a37e6f36d60` **44 B** | **同角色，可换** |
| 作者「家族 A」(60,504) | `e7f01c14d472` 252 B | `91ce3dc382b3` **92 B** | 光照合成 pass，**换不了** |
| 作者 60,476 / 60,444 / 60,416 | 各不相同 | `91ce3dc382b3` 92 B | 同上 |

把材质 pass 塞进光照 pass 的槽位 ⇒ 引擎绑不上 ⇒ 回退 ⇒「和原始一模一样」。
**换记录之前必须先比对 ISGN/OSGN，签名不同的一律不能换。**

数字旁证：作者 mat 组 SHEX = 52,220 B，本体 = 49,980 B，差 **2,240** —— 正是他加的尾巴。

### 由此获得的能力

「自造材质 → 自带载荷 → 等长换掉某个角色的 pass」这条链现在通了：

1. 只要目标 pass 与 donor 的 ISGN/OSGN 相同，就能换（同角色）；
2. 载荷总长、`TocData +16`、`gpuSize`、20 处长度字段**全部不用动**；
3. 材质变量由我们自己的 TocData 决定（可以整体换成另一份根材质的 TocData，见七之五）。

## 七之五、用一份「别人的载荷 + advanced 的变量集」合成新材质

`tools/` 之外的两个临时工具：

- `.dsh/tmp/rebuild_patch_toc.py` —— 把一个 patch 里某个条目的 TocData 整体换掉，
  其余条目与 `.gpu_resources` 原样保留，只重算 `tocOff`/`tocSize`。
- `.dsh/tmp/swap_pass_into_author.py` / `swap_60k_family.py` / `build_control_pass.py`
  —— 等长换记录（donor 块 + `PRIV` 补差）。

组合出来的一版实验（已部署）：作者材质 `0xe577db2cf97fcddc` 的 TocData 换成**本体 advanced 的**
（12 槽 / 77 变量），载荷仍是改过的（mat 组 = 全息背包的 pass），并写入探针：

```
EmissiveColor=(1,0,1)  EmissiveIntensity=12  EmissiveAnimation=1
EmissiveWaveSpeed=4    EmissiveWaveSize=1    EmissiveWaveGradient=0  EmissiveUvdirection=(1,0)
```

意义：这是「自己决定材质变量 + 自己决定着色器」的第一次合并验证。若能看到**流动的自发光**，
`动态发光` 这一项即达成，且它是"我们的材质文件、可换贴图、可改参数"的。

## 七之六、Donor 目录：全机只有 2 份载荷具备材质主 pass 签名

工具 `tools/pass_catalog.py`（识别口径：**SHEX 最大的那一组**才是主 pass —— 248/44 这个签名
在小工具着色器上也存在，只靠签名会认错）。扫了 42 份载荷（`rootpayloads` / `rootpayloads2` /
`rootpayloads3` / `candidates` / 全息背包 / ec55 / 作者那份），结果：

| 载荷 | 材质主 pass | 签名 | SHEX | 可换进作者 mat 槽？ |
|---|---|---|---|---|
| `ef9b3fc2fc1ba61e`（本体 advanced） | ×10 @ 60,412 | 248 / 44 | 49,980 | ★ 可 |
| `dec4b7db2e8095f3`（全息背包） | ×10 @ 63,052 | 248 / 44 | 39,672 | ★ 可 |
| 其余 40 份 | 各自不同 | 72/132 … 464/44 | — | 否 |

⇒ **「换别人的 pass」只能在同家族内做**。能力不来自换 pass，而来自**选哪个根材质**。

## 七之七、主目标直攻：把作者材质整个换成「镂空+金属」根材质

`0xec555165044bb60a`（154 用例）的 TocData 与载荷已从游戏取出（`PayloadDump --toc`）：

```
TocData 368 B  undat2=103,216  parent=0（自己就是根）
4 槽: NormalXyAoRoughMap / OpacityClipMap / BaseColorMetalMap / ParallaxMap
7 变量: UvOffset UseParallax RoughnessOpacity MetallicOpacity=1 UvScale BaseColorOpacity NormalCcnormOpacity
```

做法（`.dsh/tmp/rebuild_patch_toc.py` 支持换 TocData + 改 gpuSize）：

1. 材质条目的 TocData 换成 ec55 的（`undat2` 已是 103,216），`gpuSize` 改成 103,216；
2. 侧车**前 103,216 B** 换成 ec55 的载荷，贴图区（≥4,037,376）一个字节不动；
3. 后面 5 个贴图条目的 `tocOff` 顺移（TOC 4,748 → 2,684 B）。

这样可见面上跑的就是**一个同时具备 `OpacityClipMap` 与 `BaseColorMetalMap` 的着色器**，
且 `MetallicOpacity=1`（金属权重开）。这是主目标「镂空+金属」的第一次正面验证。

备选（都已取好，在同一目录）：`0x71ed0820a138df90`（464 B / 109,072 B，镂空+金属+自发光+FlickerSpeed）、
`0xa991793474328c30`（912 B / 2,425,760 B，7 槽，多 ClothDetailNormal/Clearcoat/SpecIri）。

## 七之八、贴图语义实测：这组 p38 贴图基本是占位图（**别再猜槽位映射**）

`.dsh/tmp/tex_stats.py` 对作者 patch 里 5 张贴图的全分辨率统计（texconv 解码后逐通道算）：

| 贴图 id | 角色 | 尺寸 | RGB | A |
|---|---|---|---|---|
| `a0b3c160…` | `Nar` | 1024² | **常量**(128,128,254) | 常量 162 |
| `b2e5bc25…` | `IlluminateData` | 1024² | 常量 (255,0,0) | 0 |
| `fd31e564…` | `AlbedoEmissive` | 1024² | **≈21（几乎全黑）** | **0** |
| `2c2e2a11…` | `DetailNormalPorcelain` | 4×4 | 常量 | 255 |
| `8354a7fa…` | `RoughnessMap / DetailNormalLeather` | 4096² | **有结构**（均值 49/92/0） | **255** |

**两个直接后果**：

1. 「把 `OpacityClipMap` 指向 `AlbedoEmissive`」= 拿一张 alpha=0、RGB 全黑的图当掩码 ⇒
   必然被整片裁掉 ⇒ 画面就是"那层没了，只剩光腿"。这与实机结果完全一致，**不是槽位映射错**。
2. 「打开 emissive 却看不到光」也是同一原因：自发光掩码就是 `AlbedoEmissive.a`，而它是 **0**。

**结论：要有镂空/自发光，必须先提供一张 alpha/亮度有真实分布的贴图**，
在这个 patch 里只有 `8354a7fa`（RoughnessMap/Leather，alpha=255、RGB 有结构）能用。

另一个被忽略的样本：早期抽取的 `p30_*` 那组是**真贴图** —— `p30_tex3`（4096² BC7）
RGB 均值 ≈220、**alpha 均值 148（55% 像素为 255）**，有完整结构，正是理想的
"自发光掩码 / 镂空掩码"。它不在当前 patch 里，要用得把它作为贴图条目搬进来。

## 七之九、子材质的 `undat2` 必须为 0（**很可能是历史「整族不渲染」的真凶**）

材质 TocData 的 `+16`（`undat2`）在**根材质**上等于它自带载荷的字节数，
在**子材质**上必须是 **0**：

| 样本 | parent | gpuSize | `+16 undat2` |
|---|---|---|---|
| `0x0714f22d9efaca5a`（能正常显示） | `advanced` | 0 | **0** |
| `0xe577db2c…`（index 70 的小版本，能显示） | `0x8f669f36…` | 0 | **0** |
| 我们第一次做的子材质 | `ec555165…` | 0 | **103,216** ← 自相矛盾 |

`gpuSize=0` 却声明"我有 103,216 字节载荷"⇒ 材质加载大概率失败 ⇒ 那一层不画。
2026-11-08 那次「从游戏根材质整块拷贝再改父 id」会**原样带过来**这个字段，
正好解释了「`0xec555165044bb60a` 整族不渲染，原因未查明」。

**规则：子材质 = `parent=根id` + `gpuSize=0` + `undat2=0`；根材质 = `parent=0` + `undat2=载荷字节数`。**

工具已固化：`tools/compose_material.py --child`。

## 七之十、子材质路线的能力候选（三份已备好）

做法统一：`parent` 指向候选根材质、不携带任何载荷、槽位用该根材质自己的默认集。

| 变体 | parent（根材质） | 槽 / 变量 | 目标能力 |
|---|---|---|---|
| `child_71ed0820a138df90` | `0x71ed0820a138df90` | 5 / 10 | **镂空 + 金属 + 自发光（含 FlickerSpeed）** |
| `child_7b9f6c5638fcb231` | `0x7b9f6c5638fcb231` | 2 / 4 | **带贴图半透明**（槽位就是 `albedoopacity_01`+`tex_normals`） |
| `child_25ea2c4516ba68fd` | `0x25ea2c4516ba68fd` | 16 / 37 | **半透明 + 金属**（uber 材质） |

产物在 `%TEMP%\milfy-inspect\children\`，每个都是 TOC + 侧车，可直接覆盖部署。

## 七之十一、里程碑：**镂空 + 金属 达成**（配方定稿，2026-11-14 17:56）

实机确认：**身体出现圆形孔洞 + 金属感**。存档 `.abtest-backup\20261114-1756-CUTOUT-METAL-OK\`。

### 配方（可复制到任何材质）

1. **目标材质改成子材质**：`parent = <有需要的槽的根材质>`、`gpuSize = 0`、**`undat2 = 0`**（这条是关键，见七之九）。
2. **槽位全部指向我们自己的贴图**（贴图条目留在本 patch 的侧车里）：
   - `NormalXyAoRoughMap` ← 角色的 Nar
   - `BaseColorMetalMap` ← 角色的底色（**它的 alpha 就是金属度**；本项目里均值 148 ⇒ 金属很明显）
   - `OpacityClipMap` ← 我们生成的掩码图
3. **根材质选 `0xd84d04634a1e2f60`（本体 `alphaclip`）**，载荷 1,983,968 B，3 槽，
   `OpacityThreshold = 0.5`（白留、黑挖）。

### 掩码贴图怎么做

`tools/make_honeycomb_mask.py`：PIL 画六边形排布圆孔 → `texconv -f BC7_UNORM -m 1` →
**原地替换**侧车里某个同尺寸贴图条目的像素区间（BC7 1024² 无 mip = 1,048,576 B，不动任何长度字段）。
四通道同值，所以引擎从 RGB 还是 A 取掩码都能出图案。
参数：`--radius 30 --pitch 112` ⇒ 打孔约 26.7%。那个 **DX10 扩展头**是 20 字节，忘了会差 20 字节。

### 走过的死路（别再试）

| 根材质 | 载荷 | 结果 |
|---|---|---|
| `0xec555165044bb60a` | 103 KB / 18 条 | **整片消失**（换成亮底色掩码、白底蜂窝掩码都一样 ⇒ 不是裁剪，是这一族不渲染） |
| `0xa991793474328c30` | 2.4 MB / 63 条 | **整片消失**（纯白掩码也一样 ⇒ 裁剪已被证伪） |
| `0x9fc2c6b713891a3b` | 3.3 MB | ✅ 正常渲染，**金属出来过**（但无镂空槽） |
| `0xd84d04634a1e2f60`（alphaclip） | 2.0 MB | ✅✅ **镂空 + 金属都出来了** |

**为什么 alphaclip 行而 ec55/a991 不行，目前未知**。可用的判据只有"实测"。

### 已确认的其它事实

- 作者的材质 `0xe577db2cf97fcddc` 是**皮肤**（覆盖全身），不是丝袜；黑丝是它用 N·V 压光照"画"出来的。
- 衣服/身体材质是 `0x0714f22d9efaca5a`（本 mod 的 `patch_0`，index 70）。
- 那份作者载荷的**材质变量是编译期折叠的**（plan §4.2）⇒ 改它的 TocData 变量无效，只能改着色器字节。

## 七之十二、第二轮实机：自发光通了，也修正了"哪个材质画哪块"

同一次上机同时验证了两件事：

| 改动 | 结果 |
|---|---|
| 身体材质 `0x0714f22d9efaca5a` → alphaclip 子材质 + 蜂窝掩码 | ✅ **身体/头盔/衣服都出现圆形镂空**；**身体是金属**（衣服没有金属） |
| 皮肤材质 `0xe577db2cf97fcddc` → advanced 子材质 + `EmissiveColor=(1,0,1)` / `Intensity=12` / `Animation=1` / `WaveSpeed=4` | ✅ **全身皮肤均匀发出品红光** |

**两条重要修正**：

1. **`0x0714f22d9efaca5a` 覆盖的不只是躯干**，而是「身体 + 头盔 + 衣服」的大部分；
   衣服没有金属，说明**衣服另有材质 id**，要单独做一遍。
2. **自发光遮罩确实来自 `AlbedoEmissive` 的 alpha** —— 换成 `p30_tex3`（alpha 均值 148）后
   立刻亮起来。之前"一点光都没有"就是因为作者那张 alpha 恒为 0。

**尚未确认**：观察结果是"均匀品红"，看不出**随时间流动**。`EmissiveAnimation=1`、
`EmissiveWaveSpeed=4`、`EmissiveWaveGradient=0` 都已写入，但动画是否真的在跑需要专门观察
（或换一组更极端的参数，例如 `WaveSpeed=0.5` + `WaveGradient=1` 做明显的大范围扫光）。

**另注**：`advanced` 的 TocData 里**没有** `EmissiveMultiplier`（作者那份有），
`EmissiveUVDirection` 用名字查不到（用哈希 `0x3412359b` 可直接写），默认就是 1/0，不影响。

移植步骤已单独整理成可执行手册：`hd2-cutout-metal-runbook-20261114.md`。

## 七之十三、**真混合存在**：隐身族材质的全局不透明度通了（2026-11-15）

### 结论（推翻了我此前的悲观判断）

HD2 的角色渲染路径**确实支持 alpha 混合**。做法是把目标材质改成

```
parent = 0x818cf042a3fa9aae     （游戏的隐身 / 光学迷彩材质，载荷 4.62 MB）
gpuSize = 0   undat2 = 0
Opacity       1 → 0.4           （全局不透明度）
OpacityStealth 1 → 0            （关掉隐身覆盖，否则可能被顶回 1）
AlbedoIridescence ← 0x7a1c0de5f00d0001（亮条纹，用于观察）
Normal            ← 皮肤法线
```

**实机：皮肤整件均匀半透明（能看到身后）+ 明显的胶质/塑胶光泽。** 变体：`.dsh/variants/skin_cloak_opacity/`。

为什么是这个根材质：它带 `CloakingNoise` 槽 + `OpacityStealth` 变量 —— **隐身效果本身就是让角色半透明**，
所以这个族在角色身上必须走混合，否则隐身做不出来。

### 附带发现：**胶质/塑胶光泽**（很有用，单独记下来）

同一个材质还会带出一层**均匀的胶质高光**，和透明度同时出现。它来自该族的 clearcoat/specular 通路
（`ClearcoatRoughnessAoSpec` 槽 + `Opacity` 压暗后的镜面反射）。

这类"果冻 / 塑胶 / 湿滑"质感在 HD2 里很难单独做出来，**它本身就是一项可交付的材质效果**：

- 复现：上面那套（`0x818cf042a3fa9aae` 子材质 + 压低 `Opacity`）
- 想单独拿光泽不要透明：把 `Opacity` 保持 1，只继承该族的 clearcoat 通路
- 想更强/更弱：调该族的 `Clearcoat*` 变量与 `ClearcoatRoughnessAoSpec` 贴图

### 逐像素不透明度：**仍未证实**（2026-11-15 修正，撤回上一版的结论）

⚠️ **这里我犯了一次控制变量的错误，必须记下来。**

第一版（`all_cloak_nativealpha`）我**把同一张 `01ba0f` 同时绑给底色槽和三个不透明度槽**，
实机看到"黑处反射更强、白处更透"就判成了"逐像素成立"。
第二版（`translucent_split`）只把**底色换成彩色条纹、掩码不变** —— 透明度又变回**均匀**，条纹也没出现。

⇒ 那个"浓淡"来自**底色与高光的明暗**，不是 alpha。**逐像素不透明度没有成立。**

**教训（写进流程）**：要证明"透明度随掩码变化"，必须让**底色恒定**（用 4×4 纯色贴图之类），
否则任何亮度变化都无法与 alpha 区分。

### 这一族的真实能力边界

| 现象 | 结论 |
|---|---|
| `Opacity` 压低 → 整件均匀半透明 | ✅ 真混合（全局） |
| 胶质/塑胶光泽 | ✅ 明显，可单独取用 |
| `AlbedoIridescence` 绑彩色贴图 → **不显示** | ❌ 该族在隐身未激活时**把底色贡献乘 0**，只剩镜面 |
| 三个不透明度槽帮 alpha 贴图 → 无浓淡 | ❌ 至少不是"贴图 → 逐像素 alpha"这条 |

**结构性问题**：隐身族能给你「透明 + 光泽」，但**给不了贴图颜色**。
所以"带贴图的半透明"不能靠这一族单独实现 —— 要么另找同时具备 `BaseColor` 与不透明度通道的根材质，
要么接受两段式（透明件用隐身族、花纹件用 `alphaclip` 抖动）。

（旁证：同一张彩色条纹贴图绑在 `AlbedoIridescence` 上，在 `0x8f669f36` 那族**是能显示的**
（变体 `skin_opacity_8f66`，实测"条纹 + 光泽"），所以贴图本身没问题，是隐身族压掉了底色。）

## 八、复现脚本（`.dsh/tmp/`）

| 脚本 | 用途 |
|---|---|
| `probe_holobag.py` | 载荷结构普查（前段内容 / 记录表 / RDEF 名） |
| `probe_holobag2.py` / `probe_holobag3.py` | 定位「202 个 DXBC 只有 18 个可解析」的原因 = `PRIV` |
| `diff_payloads.py` | 两份载荷的记录级 diff（注意：跨游戏版本的 SHEX 会整体不同，不能用来找"作者加的"） |
| `cmp_material_pass.py` | 主 pass 的 ISGN/OSGN/cbuffer 逐项对照 |
| `cmp_header_tables.py` | 头部 `(size, hash32)` 表普查 |
| `dump_material_vars.py` | **按权威布局**读材质 TocData（`NumTextures@+64`、`NumVariables@+104`、20 B/变量，id 在第三个字段） |

## 九、踩过的坑（别再踩）

1. **材质 TocData 的 `+16` 是 `undat2`，不是"载荷大小"** —— 它在三份样本里恰好等于载荷字节数，
   容易误当成通用字段。真正的权威布局见 `Hd2MaterialAsset.cs` 的注释，变量记录是
   `{klass, elements, id, offset, stride}`，**id 在第三个字段**；从第一个字段读会得到全乱码，
   而且"自洽的回环检测"查不出来。
2. **`payload_repackage.py` 的块名白名单会静默丢记录**。凡是记录数对不上预期，先查 fourcc。
3. **跨版本载荷 diff 无意义**：两份 mod 载荷的 SHEX 与当代本体大面积不同（编译快照不同），
   想找"作者加了什么"必须比 `ISGN/OSGN/cbuffer` 与**长度**，不能比字节。
