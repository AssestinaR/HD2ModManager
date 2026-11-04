# section 主干化设计（2026-11-04 v2）

> **v2 说明**：v1 把"装饰"当成要**保留**的机制，结论是"section 只能减、加几何留在生成期、
> 两者不该合并"。真需求相反 —— **装饰要变形态**：它应该是"把后置 patch 的独立 unit 吸收成
> 命名 section 组"的一次性升级动作，不是一个长期存在的运行时机制。
> v1 里经核实仍然成立的调查事实、硬约束、以及 astra 审出的工程要求，全部保留在本文。
>
> 三份线别调查报告在 `HD2RuntimeArmorOverride/notes/section-survey-20261104/`。

---

## 0 一句话

**一个 Mod = 7 个标准 unit + 一张 section 表 + 一张组表。**
所有"变体"都是组，所有"切换"都是组开关。装饰、option 里的独立 unit、护甲级掩码、
运行时 Lua —— 这些末梢要么被吸收成主干上的一次性动作，要么剪掉。

---

## 1 主干模型

```
Mod
 ├─ 7 个标准 unit        Helmet / Torso / Hip / LeftArm / RightArm / LeftLeg / RightLeg
 │    └─ section        每块几何一个，带名字（源材质名 / 后置包名 / 用户命名）
 ├─ 组表                 section 组 = 有名字的一组 (unit, section)
 │    ├─ 猫耳    → Torso#3
 │    ├─ 披风    → Torso#7, Torso#8
 │    └─ 外套    → Torso#11, Hip#2
 └─ 隐藏状态             哪些 section 不画（唯一的权威数据）
```

三条不变量：

1. **一个 Mod 只占 7 个 unit 槽**，其余槽位（护肩、护胸板、护腿板、隐藏壳）一律不写或写空。
2. **所有可切换的东西都表达成组**，不存在"unit 级替换"这种第二套机制。
3. **组、开关、树是同一份数据的三个视图** —— 不是三份状态互相同步。

用户能做的：新增组（勾几个 section → 命名）、删除组（几何仍在，section 恢复默认可见）、
重命名组、开关组。

---

## 2 为什么这条路技术上成立（已核实，不是估计）

### 2.1 「7 个 unit」已经是现有产物的形状

实测 10 个 VPK 模型 Mod 的 `sections.json`：

| Mod | units | 全 Mod 节数 | 单 unit 最多 |
|---|---:|---:|---:|
| lunalice_bill | 7 | 98 | 25 |
| Lilium_Sancta_Bill | 7 | 67 | 18 |
| MourningVeil_Bill | 7 | 64 | 15 |
| serenade_bill | 7 | 60 | 17 |
| l4n 信浓 | 7 | 52 | 10 |
| Cow_QiPao_Milltina_Bill | 7 | 48 | 12 |
| Manuka_LaFiore_Bill | 7 | 47 | 9 |
| Manuka_JABIS_Bill | 7 | 45 | 8 |
| 旗袍chiffon | 7 | 24 | 6 |
| Elise_Bill | 7 | 18 | 6 |

**全部 7 个 unit，全部 `Undergarment/Any`，正好覆盖 7 个标准槽。**
游戏本体躯干实测 **52 节**（2026-10-17 探针，`HANDOFF-section-wardrobe-20261017.md`）。

⇒ "一个 unit 装几十个 section、靠开关选"不是我们要发明的东西，**它就是原生状态**。

### 2.2 追加时 section 边界能不能保住（这是全案的承重结论，要说准）

`CanonicalDecorationUnitAppender` 末尾有一句无条件的 `MergeSectionsByMaterialSlot`
（`CanonicalDecorationUnitAppender.cs:189-197`），按 `(MaterialIndex, MaterialSlotId)` 分组把
三角形并起来。看起来像"两个部件会合成一个 draw call、再也分不开"。实际机制是这样的：

- 装饰路径的 provenance 带 `UsesTargetUnitMaterialSlotLookup: true` + `PreferMeshLocalSlotReuse: true`
  （`CanonicalDecorationUnitAppender.cs:178-180`），所以走的是
  `ResolveSdkTargetSlot(localTargetSlotsByMaterial, materialId, occurrence, …)`
  （`CanonicalUnitMaterialLayoutCompiler.cs:92-95`）。
- `localTargetSlotsByMaterial` = **目标 mesh 自己**的"材质 id → 槽数组"。
- `occurrence` = 本 mesh 内同一个 `materialId` 第几次出现（`:84-89`）。
- 若 `occurrence < 槽数组长度` → **复用目标的槽**；否则 `AllocateSlot(identity)`
  （`:146-148`），而 `identity` 里含 `SectionIndex`（`:90,203-204`）⇒ **每个 section 各得一个唯一槽**。

于是三种情况：

| 情况 | 结果 |
|---|---|
| 两块几何材质不同 | 槽不同 ⇒ 段不合并 ⇒ **可独立开关** ✅ |
| 两块几何材质相同、且目标自己没有这个材质 | 第 0 块走 `AllocateSlot`，第 1 块 `occurrence=1` 超出槽数组长度也走 `AllocateSlot` ⇒ 槽不同 ⇒ 段不合并 ✅ |
| **两块几何材质相同、且目标自己也有这个材质** | 第 0 块 `occurrence=0` **复用目标那个槽** ⇒ 与目标那一段合并 ❌ |

**⇒ 结论：跨来源追加基本不会丢 section 边界；唯一的破口是"donor 与目标共用同一个材质资源"。**

这不是设计问题，是**必须实测确认的边界**，而且要准备规避手段：
强制每段独占一个槽（多个槽可以指向同一个 MaterialId —— `bindingsBySlot[slot] = materialId`
本身就是这个形状），代价是材质槽数变多。这条要放进 §3 的验证清单。

### 2.3 三种"后置包"其实是同一件事的三个特例

| 后置包提供什么 | 现在怎么处理 | 主干化之后 |
|---|---|---|
| `$no_draw` 材质 | 组（关掉现有几何）—— **已支持**，9 个 Mod 共 6–30 个开关 | 不变 |
| 贴图 / 材质 | option（资源 id 覆盖）—— **已支持**，121 个 | 不变 |
| **独立 unit**（整套替换主体某个 unit） | **无路可走**（只有老的 unit 级映射，已死） | **组（预装几何 + 按组开关）** ← 新增 |

第三种就是用户要的。它不需要新机制 —— 它只是把前两种已经在用的"组"概念，
扩展到"组里的几何来自后置 patch 而不是主体"。

---

## 3 必须先验证的四件事（事实问题，不是设计问题）

这几件不确认就动手，风险不可控。都是离线可做的，不需要改产品代码。

### 3.0 donor 与目标共用同一材质时会不会并成一段

见 §2.2 的第三种情况。**做法**：造两份几何，一份用目标已有的材质，一份用新材质，
并进同一个 unit，然后看 `sections.json` 里是不是两段、`materialSlotId` 是不是两个。
若并成一段 ⇒ 需要"强制每段独占槽"的开关。

### 3.1 合并后位置与遮挡对不对

几何位置由**骨骼**决定，不由 unit 槽决定 —— 所以把护肩的几何并进 Torso unit，理论上位置不变。
但有两处可能依赖槽位语义，必须实机 A/B：

- 护甲件的 `mesh0` 是 `lod=-1` 的**深度代理/剔除壳**（`CliVpkRekey.cs:556-560,626-630`）；
  把它的几何搬走后遮挡关系会不会变。
- `layer`（层）关系决定同槽内谁盖谁；跨槽合并后层序如何重排。

**做法**：取一件多槽护甲，把 LeftShoulder 的几何并进 Torso，进游戏对比同机位截图。

### 3.2 单 unit 的 section 数上限

现有产物最多 25 节/unit，游戏本体 52 节。把 4–5 个 donor 并进一个 unit 可能到 **100–150 节**。
没有证据说不行，但也没有证据说行。

**做法**：用 `tools/unitprobe` 造一个"把 5 份几何并进一个 unit"的极限样本，量三件事：
引擎是否加载、帧率、显存。这一步同时给出"预装模式到底能预装多少"的预算。

### 3.3 组数与界面

一个 Mod 上 50 个组、150 个 section，树 + 组列表还撑不撑得住。这不是引擎问题，
是产品问题，但会影响组的默认粒度（"一个后置包 = 一个组"还是"一个后置包里的每个材质 = 一个组"）。

---

## 4 两个落点：导入即成立 + 存量升级器

主干形状不该只对存量做一次性转换 —— **新导入的包必须一出生就是这个形状**，
否则永远在追。

### 4.1 导入即成立（主管线）

```
一个文件夹里的 VPK 集合
   ├─ 主体 VPK（含 .mdl）           → 7 个标准 unit
   ├─ 后置 VPK（含独立 unit）        → 吸收成组，几何并进对应的标准 unit
   ├─ 后置 VPK（只有 $no_draw）      → 吸收成组（关掉现有几何）
   └─ 后置 VPK（只有贴图/材质）      → 独立的贴图覆盖包（option）
                              ▼
        产物：vpk2patch/{patch, sections.json, components.json}
             —— 7 unit、section 表带来源标记、组表带默认状态与互斥关系
```

现有的 `--components merge` 与 `componentSwitchSources` 已经把"哪些后置包属于这一套"
算出来了（`vpk2patch.json` 里有这两个字段）。要扩的是"后置包里有独立 unit"这一支。

### 4.2 存量升级器（一次性）

`hd2mod mod normalize <Mod> [--apply]`：
输入一个已经导入的老 Mod（多 unit、unit 级替换），输出同一目录里的主干化产物。
- 默认**预演**：列出"会并哪些 unit 进哪个槽、会生成哪些组、会隐藏哪些 unit、预计节数"。
- `--apply` 才动手，且先备份（沿用 `VpkPayloadSwap` 的"先备份再删、保留文件不删"口径）。
- 可回滚：升级前的产物整体留一份。

**归属槽的规则**（需要拍板，见 §9）：按 donor unit 自己的 `slot` 语义映射到 7 槽之一；
映射不到（Shoulder / Cape 这类）→ 并进 Torso。

---

## 5 组的数据形态

不新增文件 —— **把 `components.json` 从"开关表"升级成"组表"**。它现在就是
"名字 + 一组 (unit, sections)"，只是成员来源被限定成"源侧 `$no_draw` 推出来的"。

```
{
  "schema": "hd2mod.section-groups/1",
  "groups": [
    {
      "id": "g-cat-ears",              // 稳定 id，用户改名不变
      "name": "猫耳",
      "source": "noDraw" | "donorUnit" | "manual",
      "exclusiveGroup": "头部配件",     // 同组内只能开一个；null = 不互斥
      "defaultEnabled": false,          // 预装组默认关（否则所有变体同时出现）
      "members": [ { "unit": "af43…", "section": 3 } ]
    }
  ]
}
```

- **唯一的权威数据仍然是"哪些 section 不画"**（`wardrobe.json` 的 `disabled`）。
  组只是它的**视图定义** —— 开一个组 = 把它的 members 从 `disabled` 里移除。
- `exclusiveGroup` 一次解决两件事：预装变体（同一槽位多个变体只能选一个）、
  以及贴图覆盖型选项的冲突（v1 §7.4 悬而未决的那个问题）。
- `source` 是给界面用的：用户需要知道"这个组是自动识别出来的，还是我自己建的"。

---

## 6 身份稳定（组要在重建后活下来）

组存 `(unit, sectionIndex)` **不够**。重建一次下标就可能变，用户辛苦建的组会指向别的几何。
这正是 v1 §3.2 那段（astra 审核后重写的）要解决的问题，现在它是核心而不是附属：

- **`sections.json` 的每个 section 记来源**：`sourceUnit` + `sourceSlotId` + `materialId` +
  `triangles`，以及它在来源侧的 `sectionIndex`。
- **组的成员存来源身份**，不存产物下标；读取时按来源身份解析成当前下标。
  解析不到 → 标 stale 并**阻断**（不是只给提示），界面明说"这块在当前产物里找不到了"。
- **生成代**：`sections.json` 带 `generation`（来源版本 + 目标护甲 + 合并选项 + 生成器版本 +
  布局指纹）。组的解析只在同代内有效。
- **不校验可变 patch 的整文件 SHA**（这条硬约束不用破）——校验的是不含可变 dword 的结构指纹。

---

## 7 修剪清单（末梢）

| 对象 | 处理 | 理由 |
|---|---|---|
| `decoration.json` / 装饰包 / `decoration-activations.json` / `DecorationActivationStore` / `DecorationHostRebuildService` / `HostDerivedOverwriteService` / 两个装饰页面 | **被"吸收成组"取代** | 现网 0 个装饰；它的能力（把 donor 几何搬进宿主）保留，机制（独立包 + 启用状态 + 重建宿主）不需要了 |
| option 里"提供独立 unit"的那一类 | **升级时吸收成组** | 只保留"提供贴图/材质"的 option |
| 护甲级 section 掩码（`wardrobe.workspace.json` / `wardrobe.masks` / `WardrobeMaskCompiler` / `WardrobeMaskProjection` / `armor_section_*.lua` / `armor_wardrobe_masks.lua` / CLI `wardrobe compile\|project\|validate`） | **按层退场**（停分发 → CLI 弃用一个周期 → Core 归档） | 运行时 Lua 因缺 `ffi` 加载即退出；管理器零写入点；权威数据无编辑器。**但"按护甲保存隐藏策略"这个能力不判死**，它将来可以改成"护甲级策略 → 生成该护甲的烘焙产物" |
| `pieceOverrides` / `targetModes` / `CompositionMode` | **字段留、编辑入口不恢复、校验从致命降为警告** | 现网 0 条、无运行时消费；但坏数据现在会让整份工作区读不出来，一个没人消费的字段不该有这种权力 |
| 组件编辑器（已删） | 由**组编辑器**取代 | 只剩孤儿 XAML 要清 |
| 头身合并（VPK / 常规两份） | **留**，但抽共用的"候选识别 + 物理可行性判定" | 两者对"层"的排序方向**相反**（`CliVpkRekey.cs:844-852` vs `HelmetToArmorMergeService.cs:706-712`）；且光统一排序不够，因为两者对骨骼绑定、unit 槽位、容量限制的要求本来就不同 —— 得出不同结果可以合法，但要说得出理由 |
| `WardrobeSectionSource` / `RequestUnitSectionStructureAsync` / `vpk2patch --unit-ids` / `DecorationPatchSnapshotService` / `.bin` 旧路线 / `ArmorPieceEditorTemplate` | **直接删** | 逐条已确认无在役调用者 |

---

## 8 分批

| 批 | 内容 | 判据 |
|---|---|---|
| **U0 事实核查** | §3 的四件事。造极限样本，进游戏 A/B | 给出"单 unit 能装多少节""跨槽合并后遮挡对不对""共用材质会不会并段"的**实测结论**；这一步决定方案形状 |
| **U1 组模型** | §5 组表格式 + §6 来源身份 + 生成代；`components.json` 升级 | 老 `components.json` 能原样读进来（`source: noDraw`），不需要迁移 |
| **U2 组编辑器** | 详情页：树 + 组列表 + 新建/删除/重命名/开关；组/开关/树三视图统一 | 建组 → 开关 → 重建 → 组还在；改名不影响开关状态 |
| **U3 吸收** | §4.1 导入即成立（后置 unit → 组）+ §4.2 存量升级器 | 一个多 unit 的老 Mod 升级后：7 unit、组表齐、其余槽全隐藏；预演与真做的答案一致 |
| **U4 生效成本** | 就地烘焙（不再重跑整条换甲）+ 勾选即保存 | 勾一下 3 秒内 patch 变了；隐藏→恢复后字节复原；切页不丢 |
| **U5 修剪** | §7 的退场与删除 | 测试全绿；库能正常打开；无孤儿 XAML / 死命令 |

U0 是唯一的前置。U1 与 U0 可并行（格式设计不依赖实测结果）。

---

## 9 待确认

1. **归属槽规则**：donor unit 的 `slot` 映射到 7 槽之一，映射不到（Shoulder / Cape）并进 Torso ——
   还是"所有非 7 槽的几何一律并进 Torso"？我倾向后者：规则简单、可预测，而且几何位置由骨骼决定，
   槽位只是分类。但如果某些护甲件的可见性依赖槽位，就得按前者。
2. **组的默认粒度**：一个后置包 = 一个组，还是一个后置包里的每个材质 = 一个组？
   （后者更细，但 30 个开关的包会变成 30 个组。）
3. **预装组默认关还是开**：我倾向默认关（否则所有变体同时出现），但那样"装上去什么都没变"，
   用户可能以为坏了 —— 需要在界面上把"有 N 个组待选"说清楚。
4. **`U0` 的极限样本用哪件护甲**：需要一件 7 槽之外还有几何（护肩/护胸板）的护甲。
