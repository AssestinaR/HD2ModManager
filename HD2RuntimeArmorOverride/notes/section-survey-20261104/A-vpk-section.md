# VPK 线 section 系统现状调查（A 线）

范围：`.vpk` → `vpk2patch/` → `Overwrite/` → 游戏里「猫耳 开/关」。只读调查，未改任何代码。
前缀：`MC=`HD2ModCore，`MG=`HD2ModManager，`CL=`HD2ModCli，`SM=`HD2SourceMigration，`LU=`HD2RuntimeArmorOverride/src。

**一句话**：跑通的是「`components.json` → `wardrobe.json` → 点「应用」→ `hd2mod vpk rekey --disabled`
→ `WardrobeSectionBaker` 把 `Overwrite/<patch>` 的 `numIndices` 写 0」。没跑通的是「衣柜工作区 →
`wardrobe.masks` → 运行时内存写 dword」：C# 侧只有 CLI 能产出 `wardrobe.masks`，管理器没有写入点（§4）。

---

## 1. 完整数据链路

```
[1] L4D2 包（模型 VPK + 材质 VPK + `XXX关.vpk`）
      │  SM/VpkConversion/VpkImportPlanner.cs:420  FindComponentSwitches（只认 $no_draw）
      ▼
[2] hd2mod vpk2patch ── CL/CliApplication.cs:3566-3618  逐 patch 产 sections.json（权威）
      │                CL/CliApplication.cs:2407-2450  逐 patch 产 components.json（权威）
      ▼  <mod>/vpk2patch/{<fileId>.patch_0, sections.json, components.json, custom-model.json}
[3] 管理器导入（置只读；换载荷时保留 custom-model.json + components.json）
      │  MC/Infrastructure/VpkPayloadSwap.cs:31
      ▼
[4] 详情页「衣物装饰组件」卡：树 + 开关列表（只读视图，写的是内存）
      │  MG/ViewModels/ModDetailsPageViewModel.Vpk.cs:248 LoadVpkAsync
      ▼
[5] 勾选 → <mod>/wardrobe.json（source 键空间；**点「应用」才落盘**）
      │  MG/Services/VpkModWardrobeService.cs:180
      ▼
[6] 「应用」/「切换」/「头身合并」/「重构建」→ MG/Services/VpkRekeyService.cs:66
      │  子进程：hd2mod vpk rekey --disabled "unit:1,2;…" --head-merge on|off
      ▼
[7] CL/CliVpkRekey.cs:96
      ├─ 恒等目标 L182-220：原样复制 vpk2patch → Overwrite，再烘
      └─ 跨甲 L237-250（DirectSourceUnitReuse）
               L274-277 按新 patch 重算 Overwrite/sections.json
               L293-301 components.json 按 unitMap 翻译后写 Overwrite
               L305-307 TranslateDisabled + L369-387 BakeAsync
      ▼
[8] MC/Infrastructure/WardrobeSectionBaker.cs:57 → 写 dword（节记录 +16）
      ▼
[9] <mod>/Overwrite/{<patch>, sections.json, components.json} —— 部署时顶掉同名 patch

并行的另一条（运行时路径，本仓库无人接线）：
<库>/.hd2modmanager/wardrobe.workspace.json（权威，编辑器写；**当前无编辑器**）
   │ MC/Infrastructure/WardrobeMaskProjection.cs:177（唯一调用 CL/CliWardrobe.cs:229）
   ▼ <游戏data>/../ArmorRegistry/wardrobe.masks
   │ LU/armor_registry_runtime.lua:164,1102-1120（每次协议 apply 前读一次）
   ▼ LU/armor_section_publish.lua:35 → 在**内存**里把 numIndices 写 0
```

### 1.1 各文件：谁生成、何时、在哪

| 文件 | 唯一生成者 | 时机 | 位置 |
|---|---|---|---|
| `sections.json` | `WardrobeSectionMapFile.BuildAsync` `MC/Infrastructure/WardrobeSectionMapFile.cs:82` | ①转换末尾 `CL/CliApplication.cs:3605`；②每次 rekey 后按新 patch 重算 `CL/CliVpkRekey.cs:274,439`；③`wardrobe sections` `CL/CliWardrobe.cs:37` | `<mod>/vpk2patch/` 与 `<mod>/Overwrite/`（`:41`） |
| `components.json` | `VpkComponentSwitchFile.WriteAsync` `MC/Infrastructure/VpkComponentSwitchFile.cs:144` | 全部转换结束后逐 patch 写 `CL/CliApplication.cs:2407-2450`；rekey 翻译后写 Overwrite `CL/CliVpkRekey.cs:298`；恒等路径原样复制 `:435-437` | 同上两目录 |
| `wardrobe.json`（mod 级） | `VpkModWardrobeService.WriteAsync` `MG/Services/VpkModWardrobeService.cs:180` | 开/关组件卡 `MG/…Vpk.cs:125`；点「应用」`:663`；头身合并 `:532`；rekey 成功后记目标 `MG/Services/VpkRekeyService.cs:149` | **mod 目录根**（`:52`） |
| `wardrobe.workspace.json` | `WardrobeWorkspaceFile.Write` `MC/Infrastructure/WardrobeWorkspaceFile.cs:232` | 唯一调用 `WardrobeTreeViewModel.Save` `MG/ViewModels/WardrobeTreeViewModel.cs:430`，只在**已死的**护甲编辑路径上 | `<库>/.hd2modmanager/`（`:263-267`） |
| `wardrobe.masks` | `WardrobeMaskFile.Write` `MC/Infrastructure/WardrobeMaskFile.cs:177` | **只有 CLI**（`compile`/`project`）`CL/CliWardrobe.cs:89,232` | `<ArmorRegistry>/wardrobe.masks`（`LU/armor_registry_runtime.lua:164`） |

### 1.2 五个类的落位

| 类 | 哪一环 | 谁调用 |
|---|---|---|
| `WardrobeSectionBaker` `MC/…/WardrobeSectionBaker.cs:51` | 最后一环：把禁用集合写进 `numIndices` | 只有 `CL/CliVpkRekey.cs:386` |
| `WardrobeMaskCompiler` `MC/…/WardrobeMaskCompiler.cs:97` | 规格 → `wardrobe.masks`；反向校验 | 只有 `CL/CliWardrobe.cs:83,86,140` |
| `WardrobeMaskProjection` `MC/…/WardrobeMaskProjection.cs:172` | 工作区+协议+映射表 → `wardrobe.masks` | 只有 `CL/CliWardrobe.cs:229` |
| `WardrobeLinkCompiler` `MC/…/WardrobeLinkCompiler.cs:19` | **不是 section**：护甲 A→外观 B 编成 `wardrobe.links` | 生产 `MC/Infrastructure/RuntimeArmorRegistryService.cs:516` |
| `WardrobeHiddenUnitTemplate` `MC/…/WardrobeHiddenUnitTemplate.cs:18` | **不是 section**：「什么都不画」的 unit，补 `wardrobe.links` 缺槽 | `MC/Infrastructure/LuaDeploymentService.cs:630-631` |

### 1.3 烘焙时机与运行时

只在 rekey 里烘（`CL/CliVpkRekey.cs:306,187` → `WardrobeSectionBaker.BakeAsync`），**导入时不烘**。
改变 `Overwrite/` 里 `numIndices` 的只有四个用户动作：应用 / 切换 / 翻头身合并 / 重构建，全部汇流到
`VpkRekeyService.RunAsync`（`MG/Services/VpkRekeyService.cs:66`；`VpkRebuildService.cs:342`）。

VPK 线**不经过运行时**：烘焙过的 patch 直接部署。运行时唯一会写 `numIndices` 的路径读
`<ArmorRegistry>/wardrobe.masks`（`LU/armor_registry_runtime.lua:164`），在每次协议 apply 前
`prepare_section_masks`（`:1102-1120`，调用点 `:1207-1212`），失败整份回滚
（`LU/armor_section_publish.lua:100-123`、`armor_registry_runtime.lua:1130-1135`）——而这份文件没有管理器代码产出。

---

## 2. 界面上真正能用的能力

`CardTemplate.VpkComponents`（`MG/Views/ModDetailsPageView.xaml:139-199`）= 开关列表 + 树。

**`VpkSwitches`（按部件命名）**
- 来源：`components.json` 的 `switches[]`，`vpk2patch/` 优先（`MG/Services/VpkModWardrobeService.cs:112-124`）。
  装载时过滤：`Disabled` 里每个 unit 必须仍在本 Mod 的 section 表里，否则算 stale 并提示
  （`MG/ViewModels/ModDetailsPageViewModel.Vpk.cs:794-828`，判定 `:804`）。名字来自 addoninfo 标题
  去「切换/关」前后缀（`SM/VpkConversion/VpkImportPlanner.cs:576-603`）。
- 勾一下：只改内存 `_vpkFile.State`（`MC/Domain/VpkWardrobeState.cs:66`）+ 让树重画（`MG/…Vpk.cs:839-847`）。
- 生效：点「应用」才 `WriteAsync(wardrobe.json)` 再跑 rekey（`MG/…Vpk.cs:646-737`，顺序说明 `:637-645`）。
  **不点「应用」离开页面就丢**（刷新重读文件 `:222-246`；`PersistVpkAsync` 只被 `:125` 调用）。

**`VpkTree`（`WardrobeTreeViewModel` + `VpkModWardrobeTree`）**
- 画 **Armour → BodyType → Slot/Unit 合一 → Section**（`MC/Domain/WardrobeTree.cs:144-228`，合并 `:183-193`）。
- 只画**基础 7 槽**：`pieceType==Undergarment` 或 `slot==Helmet`（`MC/Domain/VpkWardrobeState.cs:115-131`），
  换甲后多出的护甲层/隐藏壳故意不画（`:106-113`）。
- **同一份状态的两个视图**：树与开关都读 `_vpkFile.State`（`MG/…Vpk.cs:770-786` vs `:831-837`），互相同步
  （`:781-782`、`:846`），没有第二份持久化数据。

**两条写回路径**

| 视图 | 方法 | 写哪里 |
|---|---|---|
| 树 | `IWardrobeTreeToggleStore.ApplyToggle` `MG/…Vpk.cs:776-783` → `VpkWardrobeState.SetEnabled` `MC/Domain/VpkWardrobeState.cs:23` | 内存；「应用」统一落 `wardrobe.json` |
| 开关 | `WriteSwitch` `MG/…Vpk.cs:839-847` → `VpkWardrobeState.SetSectionsEnabled` `MC/Domain/VpkWardrobeState.cs:66` | 同上 |

`WardrobeTreeViewModel` 那条「编辑即保存」的路（`Load` `:316-388`、`Save` `:423-436`）只服务护甲掩码，
**当前无调用者**；VPK 走 `toggleStore` 分支（`:188-192`、`:402-408` 明确「只改内存状态」）。

---

## 3. 三份 section 文档的职责边界

| 文档 | 回答什么 | 生命周期 | 主语 |
|---|---|---|---|
| `WardrobeSectionMapDocument`（`sections.json`） | 这个 patch 里**有哪些** section、身份、各级 LOD 落点 | 构建产物，重转即变 | patch |
| `VpkComponentSwitchDocument`（`components.json`） | **用户能关什么**（`$no_draw` 包名 + 材质 + 落点） | 源侧意图，跨 patch 汇总 | VPK 包 |
| `WardrobeMaskDocument`（`wardrobe.masks`） | 某件**护甲**隐藏哪些 section（扁平、无继承） | 部署产物，可重生成 | 护甲 itemId |

**重复表达**
1. `(unit, sectionIndex)` 出现在全部三份，外加第四份 `wardrobe.json` 的 `disabled`
   （`MG/Services/VpkModWardrobeService.cs:158-172`）。只有 `wardrobe.masks` 与 `sections.json` 带指纹；
   `components.json` 明确选择只存下标（`MC/Domain/VpkComponentSwitch.cs:13-17`），`wardrobe.json` 同理。
2. `components.json.Disabled` 是 `sections.json.sourceMaterial` 的**缓存 join**：
   `CliVpkComponentSwitches.Resolve` 的全部工作就是「材质裸名 → 出现在哪些 (unit,index)」
   （`CL/CliVpkComponentSwitches.cs:40-59`），而 `Materials` 已留在同一条记录里（`:104-108`）。
   ⇒ **这一项可以取消**（读取时现算），留着它的代价是重建后会静默变旧（§8 坑 1）。
3. `WardrobeSectionMapEntry.Fingerprint` 在 VPK 线上**只写不读**：烘焙用 `Index`+`Toggles`
   （`MC/Infrastructure/WardrobeSectionBaker.cs:110-152`），界面用 `FindUnit`（`MG/…Vpk.cs:804`）。
   读它的只有掩码那套（`WardrobeMaskCompiler.cs:286`、`WardrobeMaskProjection.cs:121,266`、
   `MC/Domain/WardrobeMasks.cs:192`）⇒ VPK 线里是纯冗余。

**能不能砍**：`sections.json` 不能（偏移不可推导，且受 LOD 家族约束）；`components.json` 不能整体砍
（`Name`/`Unmatched`/`Materials` 不可推导）但 `Disabled` 可砍；`wardrobe.masks` 不能（运行时按它查表），
它与 `wardrobe.workspace.json` 是「编译产物/权威数据」一对（`MC/Infrastructure/WardrobeMaskFile.cs:8-21`）；
`wardrobe.json.disabled` 与 `components.json.Disabled` 不是重复（值 vs 定义）。

**`WardrobeSectionFingerprint`**：格式 `<materialId|none>-<triangles>`，纯字符串拼接、绝不做数值运算
（`MC/Domain/WardrobeMasks.cs:56-83`；Lua 同规则 `LU/armor_wardrobe_masks.lua:52-64`）。生成点唯一：
`WardrobeSectionMapFile.cs:318-321`。校验者三处：`WardrobeMaskCompiler.Validate`（CLI `wardrobe validate`
`…:286-293`）、`WardrobeSectionIndex.Create`（多份表同 unit 必须逐节一致 `WardrobeMaskProjection.cs:113-125`）、
运行时按键查表（`armor_wardrobe_masks.lua:157-159`，**不匹配 = 静默不隐藏**，不报错）。
**VPK 线一次都没校验过指纹**——「重建后指纹变了」这条保护在 VPK 线上不存在。

---

## 4. 死代码 / 半成品清单

判定：`rg` 全仓引用，排除 `*.Tests`。

| 对象 | 判定 | 证据 |
|---|---|---|
| `WardrobeSectionBaker` | **活**（唯一入口是 CLI rekey） | `CL/CliVpkRekey.cs:386`；管理器靠子进程，不直接调 |
| `WardrobeMaskCompiler` | **半死**：只有 `wardrobe compile/validate`，无 UI、无服务调用 | `CL/CliWardrobe.cs:83,86,140` |
| `WardrobeMaskProjection` | **死**：产物 `wardrobe.masks` 无人写进 `<ArmorRegistry>/` | 唯一调用 `CL/CliWardrobe.cs:229` |
| `WardrobeLinkCompiler` | **活**（护甲注册 apply 时写 `wardrobe.links`） | `MC/Infrastructure/RuntimeArmorRegistryService.cs:516-524` |
| `WardrobeHiddenUnitTemplate` | **活**（部署 Lua 包时带过去） | `MC/Infrastructure/LuaDeploymentService.cs:630-631` |
| `WardrobeSectionSource` | **半死**：唯一消费者 `RequestUnitSectionStructureAsync`，而该请求全仓无人发起 | 调用点 `MC/Infrastructure/ModInformationCenter.cs:472`；定义 `:404`，无生产调用者 |
| `WardrobeTreeViewModel.Load(uint,…)`/`Save`/`FollowSource`/`MakeOwn`/`MakeInherit` | **死**：护甲掩码编辑器入口整块被删 | `MG/ViewModels/WardrobeTreeViewModel.cs:316-388`，`rg` 无调用；旁证 `notes/backlog-20261104.md:100-105` |
| `WardrobeWorkspaceFile` 写入 | **死**（只剩 CLI `wardrobe project` 读） | 写 `WardrobeTreeViewModel.cs:430`（不可达）；读 `CL/CliWardrobe.cs:209` |

**CLI 命令/选项归属**

| 命令/选项 | 判定 |
|---|---|
| `wardrobe sections` `CL/CliWardrobe.cs:31` | 给用户（重算 sections.json），管理器不需要 |
| `wardrobe compile` `:74` | **半成品**：规格要手写（`WardrobeMaskSpec` `MC/…/WardrobeMaskCompiler.cs:43-55`），产物只有运行时读 |
| `wardrobe validate` `:125` | **只读诊断**，半成品，无 UI 入口 |
| `wardrobe project` `:193` | **半成品**：整条「按护甲编辑 section」链的唯一出口，入口（编辑器）已删 |
| `vpk rekey --disabled` `CL/CliApplication.cs:2609`，解析 `:2999` | **活**：管理器「应用/切换」就靠它 `MG/Services/VpkRekeyService.cs:100-106` |
| `vpk rekey --archive-id` / `--head-merge` | **活**（`:97-98` / `:109`） |
| `vpk2patch --unit-ids` `CL/CliArguments.cs:302`，解析 `CL/CliApplication.cs:3091` | **死**：帮助里**没有这一项**（`:8191-8216` 无 `--unit-ids`），管理器从不传；只有 `source convert` `:2698` 与 `vpk2patch` `:3489` 内部读。设计意图（`:3072-3090`）已被 `vpk rekey` 取代 |
| `--sections` `CL/CliArguments.cs:282` | 只被 `wardrobe compile/validate/project` 接受（`CL/CliWardrobe.cs:77,128,196`）；`armor-registry apply` 明确拒绝（`CL/CliApplication.cs:4179`） |

---

## 5. 开关包识别现状（`SM/VpkConversion/VpkImportPlanner.cs`）

**两个魔数**
- `MaximumSwitchEntries = 12`（`:399`）：`archive.Entries.Count > 12` 直接否（`:463`）。数的是**条目**（含
  `addoninfo.txt`/`addonimage.jpg`），理由「实测 2–4 条，留三倍余量」（`:398`）。
- `MaximumSwitchMaterialBytes = 64*1024`（`:409`）：只累加 `.vmt` 的 `TotalLength`（`:500-513`），
  理由是实测有包塞 1.5 MB `addonimage.jpg`（`:401-408`）。

**判定序列**（`TryDescribeComponentSwitch` `:456-478`），全满足才算：①材质数 ≠ 0 且条目 ≤ 12（`:463`）；
②**无任何 `.vtf`**（`:465`）；③`.vmt` 字节 ≤ 64 KiB（`:467`）；④每份 `.vmt` 的 `$no_draw` 为真
（`:470-474` → `IsNoDraw` `:997-1012`，用 `IsEnabled`，故 `$no_draw "0"` 不算）。

**`VpkImportKind` 五个取值**

| 取值 | 分支 | 产出 |
|---|---|---|
| `CharacterModel` `:9` | 有 `.mdl` → `ProbeModel`（`:624`），骨架须含 `ValveBiped.Bip01_` 五骨（`:165-168,990-994`）；几何指纹重复只留一份（`:194-217`） | **unit patch + `sections.json`**，唯一能长出 section 开关的产物 |
| `Material` `:12` | 无 `.mdl`、有 `.vmt/.vtf` 且未被前两条命中（`:663`）；须被角色模型引用（`:225-238`） | **材质 patch**（无 unit ⇒ 无 `sections.json` ⇒ 无开关） |
| `ComponentSwitch` `:22` | `TryDescribeComponentSwitch` 命中（`:634-642`） | **不产 patch**；从材质解析集合剔除（`CL/CliApplication.cs:2316-2322`），名字+材质交给 `components.json`（`:2424`） |
| `MaterialVariant` `:33` | 无 `.vtf` + 材质 ≤ 12 + `.vmt` ≤ 64 KiB + **至少一份声明 `$basetexture`**（`:654-661`，`:481-497`） | **不产 patch、不产开关**，只给理由（`:658-660`）；**没有**被剔出解析集合 |
| `Unsupported` `:36` | 无内容（`:627`）、整包 `$no_draw` 但太大/太多（`:645-650`）、打不开（`:665`）、非角色模型（`:910` 等） | 跳过 + 理由 |

**三类开关包的真实表现（代码确认）**
1. **`$no_draw` 型**：只有完全命中四条才成为 `ComponentSwitch`。略超 12 条或 64 KiB 就掉进 `:645`
   「整包 `$no_draw`」→ `Unsupported`，开关**彻底丢失**（不是降级）。
2. **`$basetexture` 替换型**（`MaterialVariant`）：有分类、不产出。且有第二后果——没被剔出解析集合，
   而 `SourceMaterialSource` 同名冲突是「先出现的赢」（`SM/VpkConversion/SourceMaterialSource.cs:21`），
   一份「文身关」的 VMT 排在真材质前面就会让模型绑到被替换的底色上（§5.1 的开关包没这问题，被剔除了）。
3. **纯参数覆盖型**（只改 `$phong`/`$color`/描边）：若仍写 `$basetexture`（常见）→ `MaterialVariant`，跳过，
   理由文案「换贴图或换渲染参数」（`:658-660`）；**若不写** `$basetexture` → 落 `Material`，且因同名于模型材质
   而「被引用」（`:230`）→ **真的产出一个材质 patch**，而 HD2 模板没有这些参数 ⇒ 装上什么都不变
   （「0 Unit / 0 材质」产物在 `notes/backlog-20261104.md:144-146` 有实测计数）。⇒ 注释里「参数覆盖整包跳过」
   只在第一种情况成立。

**`VpkSectionSource` 与 `sourceMaterial` 的可信度**：名字来自
`VpkSectionSource(MaterialName, MaterialId)`（`SM/VpkConversion/VpkPatchAssembler.cs:83`），由
`BuildSectionSources` 按 `mesh.Sections[i].SourceMaterialIndex` 逐节填（`:1063-1086`），裸名 =
`Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(name))`（`:1142-1143`）；传入点在
`CL/CliApplication.cs:3591-3603`。**自校验**：`WardrobeSectionMapFile.LookupSectionName`（`:283-309`）要求
「该节实际 materialId == 那个源材质算出的 id」才采信名字，否则退回按 id 的通用名（`:208-209`）
⇒ 顺序错位**不会关错**，只会退化成数字名。采信之后 `components.json` 的 join 完全按名字
（`CL/CliVpkComponentSwitches.cs:44-46`），所以 `sourceMaterial` 是整条开关链唯一名字来源；它一旦为空
（旧产物，或 Overwrite 里那份——`CL/CliVpkRekey.cs:274` 调 `BuildAsync` **没传** `sectionNames`），开关再也匹配不上。

---

## 6. rekey 之后 section 数据怎么活下来

- **unit id 会变**：来源 unit payload 挂到目标护甲键上（`DirectSourceUnitReuse` `CL/CliVpkRekey.cs:245`），
  映射在 `:280-288`（source fileId → target fileId）。
- **section 下标不变**（对搬运过去的 unit）：payload 原样复用（`:76-78` 注释、`:245`）。但 patch 的 **unit 集合
  会变**（多出目标护甲其它层与隐藏壳），`Overwrite/sections.json` 按新 patch 全量重算（`:274`），
  所以文件整体换了一份（含 sha256、偏移）。
- **仍有效**：`wardrobe.json` 的 `disabled`（source 键空间，`MG/…Vpk.cs:424,701`）；下次换甲仍从
  `vpk2patch/` 出发，故它永远是正确输入。
- **被翻译**：`components.json`（`VpkComponentSwitchFile.Translate` `:105-141`；两个来源并到一个目标时合并
  sections `:122-131`）；勾选（`TranslateDisabled` `:341-361`，映射不到目标的条目**直接丢弃**）。
- **失效时用户看到什么**：开关指向的 unit 没落点 → stale 计数 +「N 个开关指向的 Unit 不在这份转换产物里」
  （`MG/…Vpk.cs:804,816-818`）；烘焙时 unit 不在 patch / 下标超范围 → **只 warning 不报错**
  （`WardrobeSectionBaker.cs:157-171`）；`sections.json` 与 patch 不是一套 → 抛 `InvalidDataException`
  「请重新转换」（`:114-121`，落点越界 `:251-263`）→ 界面「换甲失败/应用失败：…」；某来源 unit 整份没写出去
  → 换甲 warnings 点名（`CL/CliVpkRekey.cs:733-741`），界面取前两条（`MG/…Vpk.cs:446-449`）。

---

## 7. 硬约束清单（不能碰）

| # | 约束 | 位置 |
|---|---|---|
| 1 | 节记录固定 **24 字节** | `MC/Domain/WardrobeSectionMap.cs:136-137`；校验 `WardrobeSectionMapFile.cs:364-366`；运行时 `LU/armor_section_apply.lua:11` |
| 2 | `numIndices` 在节记录 **+16** | `WardrobeSectionMap.cs:139-140`；校验 `WardrobeSectionMapFile.cs:367-369`；写入 `WardrobeSectionBaker.cs:193`；运行时 `armor_section_apply.lua:12,64` |
| 3 | 偏移 = **相对该 unit 自己的 TocData**（`tocRelative`） | `WardrobeSectionMap.cs:142-143`；换算 `WardrobeSectionBaker.cs:243-266`（`:251` 越界即抛）；运行时 `armor_section_layout.lua:200` |
| 4 | **LOD 家族节数须与 LOD0 基准一致**才能按下标对齐；不一致的 LOD 直接丢 | `WardrobeSectionMapFile.cs:177,184-195`（`consistent` `WardrobeSectionMap.cs:98`）；`armor_section_layout.lua:190-201`；`armor_section_apply.lua:33` |
| 5 | 基准 mesh = **LOD0 里 section 最多的那个**（两侧同规则） | `WardrobeSectionMapFile.cs:163-171` vs `armor_section_layout.lua:157-165` |
| 6 | 指纹是**字符串**，禁止数值化（64 位小端，易高低字写反） | `MC/Domain/WardrobeMasks.cs:55-83`；`armor_wardrobe_masks.lua:52-64`；比较按键 `:157-159` |
| 7 | 运行时查表键 = `unit:sectionIndex:fingerprint`（冒号分隔、unit 前 16 字符） | `armor_wardrobe_masks.lua:68-70`；`armor_section_publish.lua:182-192`（`key:sub(1,16)`） |
| 8 | 掩码集 id ≤ **0xFFFF**（运行时 uint16） | `WardrobeMaskCompiler.cs:155-158`；`WardrobeMaskFile.cs:32`；`armor_wardrobe_masks.lua:16` |
| 9 | `wardrobe.masks` ≤ **1 MiB**，`schema=2`/`revision`/`enabled` 三行必需 | `WardrobeMaskFile.cs:28,111-116,144-146`；`armor_wardrobe_masks.lua:14,136-138` |
| 10 | 护甲注册协议 ≤ **64 KiB**（掩码刻意不塞进去） | `MC/Infrastructure/RuntimeArmorRegistryService.cs:486`；理由 `WardrobeMaskFile.cs:15-16` |
| 11 | 烘焙**不校验 patch sha256**，改校验「当前值 ∈ {0, 原值}」；写入必须幂等 | `WardrobeSectionBaker.cs:43-49,114-121,147-148` |
| 12 | `sections.json.patch.sha256` 只被 `wardrobe compile` 的规格声明比对，别处不校验 | 记录 `WardrobeSectionMap.cs:26-30`；读取仅要求非空 `WardrobeSectionMapFile.cs:373-374`；比对 `WardrobeMaskCompiler.cs:142,348-371` |
| 13 | unit id 一律 **16 位小写 hex、不带 0x**（不归一会「部分失效」） | `MC/Domain/WardrobeMasks.cs:84-100`；`WardrobeSectionMap.cs:88-89`；`armor_wardrobe_masks.lua:44-50` |
| 14 | 界面只画基础 7 槽（`Undergarment`/`Helmet`），否则露出隐藏壳 | `MC/Domain/VpkWardrobeState.cs:106-131` |
| 15 | 空条目必须删掉（「有 unit 但一条都不关」≡「没有 unit」） | `VpkWardrobeState.cs:47-54,93-99` |
| 16 | `wardrobe.json.headMergeIntoBody` **缺字段 = 关**（产物形状开关，不能替用户默认开） | `MG/Services/VpkModWardrobeService.cs:151-157` |
| 17 | 换载荷时 `custom-model.json`/`components.json` **不备份不删**（否则 unit id 基与开关定义被冲掉） | `MC/Infrastructure/VpkPayloadSwap.cs:31,53-55` |
| 18 | 开关包识别当前唯一判据 = 12 条目 / 64 KiB / 无 vtf / 全 `$no_draw` | `SM/VpkConversion/VpkImportPlanner.cs:399,409,463-474` |
| 19 | 运行时只在**协议 apply** 时读掩码；单改掩码不触发重载 | `LU/armor_registry_runtime.lua:1102-1120`；`CL/CliWardrobe.cs:281-283` |
| 20 | 运行时「取消勾选」只能靠 `originals` 缓存写回原值（内存里已是 0） | `LU/armor_section_publish.lua:23-25,100-123,138-144` |

---

## 8. 本次调查新发现的三个隐患

1. **`components.json` 被保留但 `sections.json` 被替换**：`VpkPayloadSwap` 只保留
   `custom-model.json`+`components.json`（`:31`），重构建后 `sections.json` 是新的；装载开关时只校验 unit 存在、
   **不校验下标**（`MG/…Vpk.cs:804`）⇒ 节数变化后开关静默关掉别的几何，烘焙那边也只给 warning
   （`WardrobeSectionBaker.cs:166-170`）。
2. **`MaterialVariant` 包留在材质解析集合里**：只有 `switchPaths` 被剔除（`CL/CliApplication.cs:2320-2322`），
   同名冲突「先出现的赢」（`SourceMaterialSource.cs:21`）。
3. **勾选只存内存**：`PersistVpkAsync` 唯一调用点是「启用组件卡」（`MG/…Vpk.cs:125`），勾了不点「应用」
   就切页 = 静默丢失（`:222-246` 重读文件覆盖内存）。
