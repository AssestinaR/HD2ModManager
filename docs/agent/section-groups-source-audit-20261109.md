# section 树 / section 组的数据来源：为什么 VPK 来的组会丢（审计）

**日期**：2026-11-09
**现象**（用户）：legacy mod 集合那条线暂不清楚；**VPK 来源的 section 组异常丢失**，希望支持 L4N 的开关与"外部 VPK 类开关"。
**结论**：不是"数据被删了"，而是**重构建这条链上根本没人再生成它**——外加一份**没人更新的旧文件**在冒充真相。
本轮只做诊断，未改代码。

---

## 0. 一句话

VPK 的 section 组（`components.json`）只在**转换那一次**生成，而开关包（`$no_draw` 覆盖包）在导入时就被丢掉了；
自从 2026-11-08 `4325d86`（导入 = 建条目 + 标准重构建）之后，转换只有重构建一个入口，
它只看 Mod 根目录的那几个 VPK —— **开关包不在里面，于是组再也不会被生成**，
而旧的 `components.json` 既没人清理、也没人按新的 `sections.json` 重算，索引就慢慢漂到错的 section 上。

## 1. 这两样东西分别是什么

| 文件 | 谁写 | 内容 | 失效条件 |
|---|---|---|---|
| `sectional/sections.json`（section 树） | 转换器（每次转换/重建都重写） | 每个 unit 有哪些 section、每个 section 用哪个**源材质裸名**（`sourceMaterial`） | 每次重建都变（索引、材质 id、三角形数） |
| `sectional/components.json`（section 组） | 转换器**只在识别到开关包时**写 | 「这个 Mod 有哪些有名字的可开关部件」→ 成员是 `(unit, section 下标)` | **必须与同一份 `sections.json` 配对**（`SectionGroup.cs` 顶部注释明写："重建后几何全变，组必须重转"） |

组的成员存的是**下标**，不是身份 —— 这条取舍在文件注释里写得很清楚，也正因为如此，
"sections.json 更新了、components.json 没更新"就一定会错位。

## 2. 数据是怎么走到那一步的（时间线）

1. **导入**（`VpkImportService`）：规划阶段识别开关包（`VpkImportPlanner` 的 `VpkImportKind.ComponentSwitch`，
   判据 = 无贴图 + 条目少 + 总字节小 + 每份 VMT 只有 `$no_draw`），然后在导入日志里报「已识别为组件开关」。
   **但只把"模型 + 材质"拷进 Mod 根**（`ConvertAsync` 里 `sources = [group.ModelVpkPath, ...group.Materials]`），
   开关包留在临时目录里，随解压目录一起被删掉。
2. **重构建**（`VpkRebuildService`）：转换的输入是 `FindSourceVpks(modDirectory)`（Mod 根目录的 `*.vpk`）
   + 同命名空间的兄弟 Mod。开关包不在其中，CLI 的 `componentSwitches` 就是空的，
   `CliSectionGroupResolver` 无话可说 → **不写 `components.json`**（Closer：`Groups.Count == 0 && Unmatched.Count == 0` 时直接 `continue`）。
3. **旧文件留着**：`VpkPayloadCollector.Collect` 明写"会被创建，但不会被清空"，所以上一次那份 `components.json` 原地不动。

### 改造之前不是这样

`4325d86^`（2026-11-08 11:13 之前）的 `VpkImportService` 里有一段：

```csharp
var switchListPath = WritePathList(..., plan.Items.Where(item => item.Kind == "componentSwitch").Select(item => item.VpkPath));
...
arguments.AddRange(["--component-source-list", componentSourceListPath]);
```

导入**当场转换**，并且把整批开关包一起交给 `vpk2patch` —— 组就是在那一刻生成的。
`4325d86` 把导入侧的整套转换删掉之后，这段随之消失，重构建那边**没有接回来**。

## 3. 证据（本机真实库）

### 3.1 组文件的时间戳与 sections 的时间戳对不上

| Mod | sectional 目录 | sections.json | components.json |
|---|---|---|---|
| Elise_Bill | 11-09 11:54 | 11-09 11:54 | **11-08 08:40** |
| Lilium_Sancta_Bill | 11-09 12:29 | 11-09 12:27 | **11-02 16:50** |
| Manuka_JABIS_Bill | 11-09 12:26 | 11-09 12:26 | **11-02 13:08** |
| MourningVeil_Bill | 11-09 13:13 | 11-09 13:11 | **11-08 08:56** |

（`4325d86` 落地时间：11-08 11:13。）所有 `components.json` 都早于这次改造，之后再没被重写过。

### 3.2 十一个 Mod 里，3 个完全没有组文件、1 个有 7/12 组错位

| Mod | 组文件 | 组数 | 错位 | schema |
|---|---|---|---|---|
| 恋星milfy_bill | **缺** | — | — | — |
| l4n【人物】silent_code_shinano信浓_bill | **缺** | — | — | — |
| Serenade_Bill | **缺** | — | — | — |
| Elise_Bill | 有 | 6 | 0 | `section-groups/1` |
| MourningVeil_Bill | 有 | 14 | 0 | `section-groups/1` |
| Lilium_Sancta_Bill | 有 | 12 | **7** | `vpk-components/1`（旧） |
| Lunalice_Bill | 有 | 30 | 0 | 旧 |
| Manuka_JABIS_Bill | 有 | 14 | 0 | 旧 |
| Manuka_LaFiore_Bill | 有 | 13 | 0 | 旧 |

前三个正是 **11-08 23:10 之后**才导入的（目录时间 11-08 23:10–23:13），那会儿改造已经生效 —— 与"新导入没有组"完全吻合。

错位的长这样（Lilium，"手套Glove"）：

```
记录：  unit 49dd02a1c4990006 的 section 2
现在：  同一个材质落在 0006 的 section 10、以及 0007 的 section 10
```

也就是说点这个开关，**关掉的是别的东西**（或者什么都不关）。这类错一半以上时，用户看到的就是"组不对/组没了"。

### 3.3 开关包在本机已经一个都不剩

扫了 `E:\mod` 全部 61 个 VPK、1.65 GB：`VpkImportPlanner.FindComponentSwitches` 识别到 **0 个**开关包。
但 Elise / Lilium / Manuka / MourningVeil 的组文件里明明记着 `<名字>关.vpk`。
它们的原件现在只在仓库的临时目录里（`tmp/lilium/材质开关/*.vpk`、`tmp/issue-20261028/x/shuli/组件材质/*.vpk`）——
**导入之后没被保存过**，这也解释了为什么今天的重构建不可能把它们重新识别出来。

## 4. 另外两件容易被混进来的事（本题之外，但要分清）

1. **`--components` 默认是 `merge`**：多变体的 bodypart（L4N 的 `$BodyGroup` 实时切换）会被并进它主导的身躯槽位，
   几何层面的"这个 bodypart 用哪些材质"当场消失。`CliSectionGroupResolver` 的注释也明说"不从 bodypart 推导开关"。
   所以**即使把开关包修好**，L4N 那种"部件实时切换"仍然不会自动变成组 —— 那是另一件事（`docs/agent/vpk-cross-mod-id-collision-20261030.md` §2.4 的 B1/B2）。
2. **legacy mod 集合**（不走 VPK 那条路）的组是另一套来源，本次没动，也没发现它被这次改造影响。

## 5. 修法（待定，按用户选择落地）

### 5.1 让组能重新生成（主修）

两条路线，可以叠加：

- **A. 把开关包留在 Mod 里**：导入时把 `ComponentSwitch` 类的 VPK 也拷进 Mod 根（它们很小：实测 487 B – 1.5 MB，
  绝大多数 < 7 KB）。重构建的输入集合自动包含它们 → CLI 的 `FindComponentSwitches` 命中 → 组重新生成。
  零 CLI 改动；用户还看得见"这个 Mod 依赖哪几个开关包"。代价：多个 Mod 共用同一个开关包时会各存一份（体积可忽略）。
- **B. 只留"开关定义"**：导入时把 `{名字, 来源包名, 材质裸名[]}` 写进 Mod（几十字节），重构建时用新参数
  （如 `--switch-definitions <json>`）交给 CLI，由它与**新的** sections 重新 join。不复制包，但 CLI 要多一个输入面，
  且用户之后换/删开关包时不会跟手。

推荐 **A 为主 + 5.2 兜底**：实现小、可逆、用户可感知；B 留作将来"外部共享开关包"的入口。

### 5.2 让旧文件不再骗人（救援存量）

重构建的后处理里加一步：**用 `components.json` 自己记的 `materials`（源材质裸名）对新的 `sections.json` 重算成员**，
再整份写回（顺手把 `hd2mod.vpk-components/1` 升成 `hd2mod.section-groups/1`，保留 `name / sourceVpk / id / exclusiveGroup`）。

- 修前：Lilium 12 组里 7 组指错节；
- 修后：12 组全部按新 sections 落位；找不到材质的组标成"未匹配"而不是指向空气。

这条不需要开关包，所以**存量的 10 个 Mod 都能自愈**（3 个本来没有组文件的除外 —— 它们的开关包已经不在了，只能重新导入）。

### 5.3 外部 VPK 类开关

用户把开关包拖进 Mod 根（或从"添加开关包"里选 VPK）→ 重构建即生效。最小实现就是 5.1 的读取侧：
`FindSourceVpks` 已经会扫 Mod 根，所以**只要文件在那儿就行**，不需要新界面；要界面的话是后话。

## 6. 验收

1. 用 `tmp/lilium`（模型 + 材质 + `材质开关\`）走一遍导入 → 重构建后 `components.json` 有 12 组、且与新 sections 一致。
2. 只把 `材质开关\` 里的 VPK 拷进一个已有 Mod 根 → 重构建后组出现/被修正。
3. Lilium 存量（7/12 错位）→ 重构建后错位归零。
4. 全量 `dotnet test` 绿；legacy schema 仍能读（`SectionGroupModelTests.LegacyJson` 不许改）。
