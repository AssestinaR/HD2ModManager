# 编译基座：链路只有四段（2026-11-08）

> 这一篇定的是**物理链路**，不是代码结构。任何新功能都先问：它属于这四段的哪一段。

## 1. 四段链路

```
导入（vpk / patch 解压） → 转换（vpk2patch / legacy2sectional） → 编译（sectional + 条件） → 部署（Overwrite → 游戏）
```

| 段 | 输入 | 输出 | 谁可以写 |
|---|---|---|---|
| **导入** | 压缩包 / 目录 | 根上平铺的**来源**（`*.vpk` / `*.patch_N`） | 只写根 |
| **转换** | 根上的来源 | `sectional/`：标准 patch 组（自定义素体 + 标准 7 槽位 + 材质 patch 组） | 只写 `sectional/` 与 `vpk2patch.json` |
| **编译** | `sectional/` + 条件 | `Overwrite/`：最终生效 patch 组 | 只写 `Overwrite/` |
| **部署** | `Overwrite/` | 游戏的 `data/*.patch_*` | 只写游戏侧 |

**两条硬边界**

1. `sectional/` 是 **patch 级权威事实**：只由转换链路产出，形状固定。**任何其它链路都不许碰它**（换甲、section 开关、头身合并、材质开关、批量修复……全都不行）。要改它，只能另立一次**明确授权的迁移**。
2. `Overwrite/` 是**唯一的生效层**：只由编译产出。调用方不许自己往里面写。

## 2. 编译基座（本轮落地）

`HD2ModCore/Infrastructure/ModCompileService.cs`：

```csharp
CompileAsync(modSelector, modDirectory, context,
             targetArchiveIds?,        // 条件①：目标护甲（null = 读 wardrobe.json）
             disabledSections?,        // 条件②：section 开关（null = 读 wardrobe.json）
             onStage, ct)
```

- **条件③**（材质开关）来自 Mod 自己的设置文件（`enabledMaterialPatches`），由物化器读取。
- 顺序固定：有目标 → 换甲（同时把 section 开关烘进去）；随后把启用材质装进 `Overwrite/`。
- **只读配置**：目标护甲 / 开关是用户意图，只能由"用户做了那个动作"落盘。编译不写 `wardrobe.json`、不写设置文件。
- **不做增量、不做缓存**：每次从 `sectional/` 完整算一遍。优化留到链路稳定之后，单独做。

## 3. 现在的调用关系

| 入口 | 现在 |
|---|---|
| 详情页「切换」/「应用」 | 改条件 → **调用编译基座** |
| 详情页「重构建」 | **转换（来源 → sectional）→ 编译**，两段分明 |
| `vpk rekey`（CLI） | 底层命令（编译基座里"条件①"的实现），保留 |
| `vpk2patch`（CLI） | 底层命令（转换段），保留 |
| 批量修复 / `HostDerivedOverwriteService` | **未动**，另行处理（见 §5） |

## 4. 头身合并：退出常规链路

- GUI 的开关**已删除**（详情页不再提供这个条件）。
- `vpk2patch --head-merge` / `vpk rekey --head-merge` 保留为 **CLI 实验 / 救急**：
  - `vpk2patch --head-merge on` 会把合并**烘进 sectional 本体**（头件从 `slot0/layer0` 挪到胸部层并留 `movedFrom`），属于**协议外产物**；
  - 两个开关都会往 stderr + JSON notes 打警示，帮助文档也写明"仅 CLI、使用前必须告知用户不安全"。
- 结论：常规链路里**没有**头身合并这个条件；它要成为正式条件，得先在编译基座里实现（从 sectional 算，而不是烘进 sectional）。

## 5. 明确未做（避免顺手扩大范围）

| 项 | 为什么先不做 |
|---|---|
| `HostDerivedOverwriteService` / 批量头盔合并页 | 是第三处 head-merge 实现，牵到装饰链与批量修复；要单独定"并入编译还是删除" |
| 编译层优化（增量 / 缓存 / 只重算变化部分） | 用户明确要求先跑通链路，优化单独做 |
| `Overwrite` 的多写者收敛 | 本轮只让 GUI 与重构建走编译基座；装饰/折并/批量修复仍是直接写者 |

## 6. 下一步

- **导入链路**：解压 → 建"只有来源 VPK"的条目 → 自动跑一次标准重构建（5 线程并发）。重构建会自动用**默认编译参数**编译出 `Overwrite`，所以这条链不需要额外代码。
- 之后才是：把剩下几个 `Overwrite` 直接写者并进编译基座。
