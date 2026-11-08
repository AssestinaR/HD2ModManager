# VPK 重构建（2026-11-02）

> 起因：备忘里那条「vpk 重构建功能」。用户选的范围明确：
> **只重跑转换：读 Mod 根上的来源 VPK，覆盖 `vpk2patch/`，保留换甲映射、关掉的 section、
> unit id、命名空间**。不重新烘焙真映射、不动游戏数据。
> 追加要求（同日）：「构建完毕后自动按当前配置构建 Overwrite」——见 §3.5，
> 即目标取下拉当前选择、勾选取内存状态，两者都可能还没落地。

> ⛔ 已过时（2026-11-08）：本文写作时基底目录叫 `vpk2patch/`，2026-11 已改名 **`sectional/`**
> （老库仍是旧名，读侧两个名字都认）；下文所有 `vpk2patch/` 路径一律按 `sectional/` 读
> ——`vpk2patch.json` 这个**文件名**没变。
> 另外重构建现在是干净的两段：**转换**（来源 → `sectional/`）→ **编译**（`ModCompileService.CompileAsync` → `Overwrite/`），
> 它自己不再写生效层。见 `docs/agent/sectional-compile-base-20261108.md` §1.1–§2。

## 1. 它解决什么

没有它，只有两条路：

- 重新导入：会丢掉三样已经配好的东西（换到哪件护甲、哪些 section 被关掉、素体登记的 unit id），
  三样都得手工重来。
- 手工跑命令行：要知道当初用的是哪组参数、哪个命名空间、哪份 `custom-model.json`。

重构建 = 一个按钮，把"内容更新"和"配置保留"分开。

## 2. 四个必须保住的东西 + 各自的机制

| 要保住的 | 机制 | 少了会怎样 |
|---|---|---|
| 命名空间（材质/贴图 id 的一半输入） | 读 `vpk2patch.json` 的 `idNamespace`，用 `--id-namespace-token` 传回去 | 材质 id 全变，模型 patch 引用不到材质 patch 写出的那些 → 游戏里材质丢失 |
| unit id（素体 key + id 基） | `--reuse-model <旧 custom-model.json>` | id 重新随机 → wardrobe 开关掩码、真映射写进 `dl_bin` 的件表全部失效 |
| 关掉的 section | `wardrobe.json` 的 `disabled`，交给 `vpk rekey --disabled` 烘进去 | 关掉的部件全部回来 |
| 换到的目标护甲 | `wardrobe.json` 的 `targetArmorArchiveId` | Overwrite 回到"自定义素体模式"，游戏里不是那件护甲了 |

## 3. 关键实现决定

### 3.1 先转到临时目录，校验通过才替换

`vpk2patch/` 是唯一能让用户"再换一件护甲"的输入。就地转换一旦中途失败（源包损坏、
缺 texconv、模型读不出来），用户手上就只剩半份产物。所以：转到
`%TEMP%/hd2modmanager-vpk-rebuild/<guid>/convert`，验过再整批换进去；旧载荷先复制到
同级的 `backup/`（路径写进回报），失败时一个字节都不动。

### 3.1b `vpk2patch/` 里的文件是**只读**的（第一次点就栽在这）

库里的 Mod 目录会被管理器标成只读（`ModLibraryService.SetDirectoryReadOnly(readOnly: true)`，
换包时调用）。对只读文件调 `File.Delete` / 带 `overwrite` 的 `File.Copy`，
只会得到一句 `Access to the path … is denied`——**一个字都没提"只读"**，
第一眼必然怀疑权限或文件被占用（第一次实机点下去就是这么报的，原文见下）。

```
替换 vpk2patch/ 失败：Access to the path 'E:\mod\serenade_bill\vpk2patch\9ba626afa44a3aa3.patch_0' is denied.
```

修法：替换前逐个清只读、覆盖前也清目标文件（`ClearReadOnly`），
然后在**整条流程最后**再补回只读（`SetPayloadReadOnly`）——不能更早，
因为后面还有两步要往产物里写字（`physbone embed-mod`、重建 Overwrite）。

失败时的行为是对的：备份先做、删除在清属性之后，所以那次失败一个文件都没少。

### 3.2 `--reuse-model` 是这一整套的前提
转换器原本的复用逻辑是"**输出目录里**已经有 `custom-model.json` 就沿用"。临时目录当然是空的，
于是 id 会重新随机。所以给 CLI 加了显式参数 `--reuse-model <路径>`：把旧那份指过去，
产出的素体 key 与逐 Unit id 与原来**逐位相同**。

### 3.3 同批的材质包靠"命名空间认亲"

模型 VPK 通常是**零材质**（材质写在同批的材料包里）。重构建时扫 Mod 库，
把 `vpk2patch.json` 里 `idNamespace` 与本 Mod 相同的兄弟 Mod 的根 VPK 一起交给
`--material-source`。实测本库里 9 个 serenade Mod 共用 `vpk-a814cce78c2d95cf`。

### 3.4 材质塌陷守门（这条是防"看起来成功"）

**失败长什么样**：材质包没进解析集合时，每个 section 引用的材质会全部塌成同一个兜底 id。
而 Unit 数、section 数、patch 大小、`sections.json` 结构**全都正常**——文件层面看不出来，
进游戏才发现一身衣服共用一张图。

**判据**：比 `sections.json` 里"被 section 实际引用的不同 `materialId` 个数"，
比上一次少就**不替换**，并说明原因与临时产物路径。

实测三组数字（serenade_bill，60 个 section）：

| 产物 | 不同材质数 |
|---|---|
| 原产物 | 36 |
| 重构建（带同批材质包） | **36** ✅ |
| 重构建（只给模型 VPK） | **6** ← 守门拦下 |

> 为什么不用转换报告的 `patches[].materials`：模型 patch 恒为 0（材质不在里面），照搬只会误导。

### 3.5 "按当前配置"包括还没点应用的那些

重构建跑完转换后**一定**会重新生成 `Overwrite/`（只要这个 Mod 有登记过素体），
而且用的是**界面上正显示着的配置**，不是文件里上一次的：

| 配置项 | 取哪一份 |
|---|---|
| 目标护甲 | 上方下拉里**当前选中**的那件（用户可能刚换过、还没点「切换」） |
| section 勾选 | **内存里**的树状态（可能刚勾掉几节、还没点「应用」） |

配套两条顺序要求：

1. **状态先落盘再重跑**（与「应用」同一理由）：重构建是从状态算出来的，状态没写就先做，
   崩在中间会出现"产物已改、状态还是旧的"，下次点「应用」会把旧状态烘回去。
2. **目标都没有时退回自己的素体**：`wardrobe.json` 没记过护甲就看 `custom-model.json`，
   于是"从没换过甲"的 Mod 也能重构建出一份完整的 Overwrite（自定义素体形态）。

> ⛔ 已过时（2026-11-08）：上面第 1 条**不再成立**——编译只读配置。重构建把界面上当前的目标/勾选
> 作为入参传给 `ModCompileService.CompileAsync`，**不写** `wardrobe.json`、不写 Mod 设置文件，
> 所以不存在"把旧状态烘回去"。见 `docs/agent/sectional-compile-base-20261108.md` §2「只读配置」那条。

### 3.6 旧产物没有记录参数时，按"今天的导入默认"重跑

`vpk2patch.json` 里的 `conversion` 块是这次新加的。旧产物没有它，也永远补不出来。
两条路里选了"今天的默认"（`--components merge --matcap metal`）：

- CLI 自己的默认是 `--matcap bake`（Python 时代）。照它重跑，等于把每个旧 VPK Mod 的
  金属件退回"又黑又没反光"，用户会觉得"重构建把东西弄坏了"。
- 重构建的常见动机本来就是"用现在的转换器重出一遍"，所以今天的默认最接近预期；
  代价（观感可能变）写进回报，不静默。

### 3.7 选项也能一起重构建（含纯材质包）

点「重构建」时先看这个 Mod 的**选项**里有没有也是 VPK 转换产物的，有就多问一句：

```
这个 Mod 有 6 个选项也是 VPK 转换产物：
serenade基础材质b1服装色白、serenade基础材质c1发色浅紫、…
要把它们也一起重构建吗？
· 是：先重建主体，再逐个重建这些选项
· 否：只重建主体（选项保持现在的产物）
```

- **判据只看 `vpk2patch.json`**（转换器写的报告）。不能用 `IsVpkMod`——那个要求有
  `sections.json`，也就是"带 Unit 的模型产物"，纯材质包一律不算，会被漏掉。
- 为什么要问而不是自动做：选项可能是别的来源包，重建同样要几分钟，用户完全可能只想换主体。
  反过来若什么都不问，用户会发现"主体换了、材质包还是老的"。
- **主体失败就不往下走**：那时选项引用的材质 id 是新的还是旧的没人知道，继续只会把问题叠起来。
- 纯材质包走同一套执行体：没有 `sections.json` 就不要求它、没有素体就不谈 `--reuse-model`、
  没有 Unit 就跳过换甲那一步；材质条目塌陷改用**报告里的材质条目数**守门。
  实测 `serenade基础材质d1瞳色紫` 重构建出的 patch 与原产物逐字节相同（`A7081F26…`，524 B）。

## 4. 验证：重构建产物与原来**逐字节相同**

用本库里真实的 `serenade_bill`（模型 Mod，来源 VPK 仍在根上）走了一遍与按钮完全相同的路径：

| 项 | 结果 |
|---|---|
| `custom-model.json` 的素体 key | `custom-85b58d966d4a483bbf16483b66d2cd14`（与原来相同） |
| 7 个 Unit id | `2fe1cb63905c0001…0007`（与原来逐个相同） |
| `9ba626afa44a3aa3.patch_0` | SHA256 `07B1A982…91D5F7`，**与原来逐字节相同**（258458 B） |
| `.gpu_resources`（含 `physbone embed-mod` 写进去的容器） | SHA256 `EDEAD472…4037DE`，**与原来逐字节相同**（30251035 B） |

也就是说：**同样的来源 + 同样的参数 → 完全相同的产物**。这条是"重构建不会偷偷改变什么"的硬证据。

留一个已知偏差：`sections.json` 大小与原来不同（69738 vs 69824）。它是构建产物，
由当前代码按新 patch 现算，内容自洽；差异来自旧文件是更早版本的构建器写的。

## 5. 代码位置

| 文件 | 作用 |
|---|---|
| `HD2ModCore/Infrastructure/VpkConversionReport.cs` | 读 `vpk2patch.json`（命名空间 / 参数 / 计数），也能直接解析 CLI 的 stdout |
| `HD2ModCore/Infrastructure/VpkRebuildService.cs` | 重构建执行体：找来源 → 认亲 → 转换 → 守门 → 替换 → 嵌物理 → 重跑换甲（2026-11-08 更正：已从 `HD2ModManager/Services/` 移到 Core） |
| `HD2ModCore/Infrastructure/Hd2ModProcess.cs` | 跑 `hd2mod.exe` 的**唯一**一份样板（导入/换甲/重构建共用）（2026-11-08 更正：已从 `HD2ModManager/Services/` 移到 Core） |
| `HD2ModManager/ViewModels/ModDetailsPageViewModel.VpkRebuild.cs` | 「重构建」命令 + 确认文案 + 状态回报 |
| `HD2ModManager/Views/ModDetailsPageView.xaml` | VPK 动作卡里的按钮 |
| `HD2ModCli/CliApplication.cs` | `vpk2patch` 报告新增 `conversion` 块与 `notes`（命名空间警告）；`--reuse-model` |
| `HD2ModCore.Tests/VpkConversionReportTests.cs` | 6 条：读到/读不到/旧产物/参数翻译/stdout 解析 |

> ⛔ 已过时（2026-11-08）：上表最后那步"重跑换甲"已经改成**调用编译基座**——
> `VpkRebuildService` 现在收尾时调 `ModCompileService.CompileAsync`（目标护甲 + section 开关 → 换甲产物 → 材质物化，
> 全部只在 `Overwrite/`），自己不再往生效层写。见 `docs/agent/sectional-compile-base-20261108.md` §2。

**「重构建会不会沿用原来的命名空间？」会**——读 `vpk2patch.json` 的 `idNamespace`，
用 `--id-namespace-token` 原样接上（不是按当前包名重新派生）。上表里 patch 逐字节相同
就是这条的证明：材质/贴图 id 只要有一位对不上，那一整份字节就不可能是同一个哈希。
产物里没有 `vpk2patch.json` 或没有这个字段时才会退回"按今天的默认 + 无命名空间"，
并明确写进回报。

## 6. 待办

- [ ] 用户点一次按钮做验收（在 `serenade_bill` 上点最安全：转换产物本来就该逐字节不变）。
- [x] 重构建后**按当前配置**自动重建 Overwrite（目标取下拉当前选择、勾选取内存状态）——见 §3.5。
- [x] 选项里也是 VPK 产物的，点重构建时多问一句"要不要一起重建"——见 §3.7。
- [x] 头身合并（`vpk rekey` 默认开）：见 `docs/agent/vpk-head-body-merge-20261102.md`。
      注意它改变了 Overwrite 的形状（身体降一层、头进胸部槽位、头盔 item 变空），
      所以重构建/换甲之后 Overwrite 不再是原来那份——`vpk2patch/` 仍然逐字节不变。
- [ ] 组件开关包（`XXX关` 那类 `$no_draw` 包）不在 Mod 里，`components.json` 是**原样保留**的：
      重构建不重算它。若新版本模型改了 section 顺序，开关会指向错的 section——目前不会报警，
      需要时可以拿 `components.json` 里的 unit/section 去对新的 `sections.json`。
- [ ] 没有记参数的旧产物，`--cutout` / `--matcap-gain` 一律回到默认。
- [ ] `CLI` 侧还没有 `vpk rebuild` 子命令（目前只有管理器按钮）。要做的话执行体可以直接搬。

> ⛔ 已过时（2026-11-08）：上列有两条已不成立——
> ①「头身合并（`vpk rekey` 默认开）」：它已退出常规链路。GUI 开关删除，`--head-merge` **默认 off**，
> 只有 CLI 显式写 `on` 才做（且会把合并烘进 `sectional/` 本体，属协议外产物）；重构建/换甲都不会再做它。
> ②「`CLI` 侧还没有 rebuild 子命令」：现在有 `hd2mod mod rebuild <GUID|前缀> --confirm`
> （命令名是 `mod rebuild`，不是 `vpk rebuild`），执行体就是同一个 `VpkRebuildService`。
> 见 `docs/agent/sectional-compile-base-20261108.md` §2 / §4。

