# VPK 换甲丢手套/丝袜 —— 根因与修法（2026-11-01）

## 一句话

**换甲把我们的几何挂在了「内层」，而内层会被外层护甲件的「只写深度的剔除代理网格」挡掉。**
改成**外层优先**（我们的几何占最外层、内层全部隐藏）后，实机手套、丝袜、袖套全部正常。

## 根因

HD2 每个护甲件都带一个 `mesh0`：

```
lodIndex = -1     isVisual = false     isCulling = true
形状 = 整条前臂 / 小腿 / 胯（439 顶点、2280 三角形，与部位一一对应）
```

它不画颜色、**只写深度**，用途是遮挡 / 阴影代理。

而换甲做"隐藏"时，`SameKeyCanonicalUnitRebuilder` 有一个分支把它**原样保留**：

```csharp
if (targetRaw.LodIndex == -1 && !request.MinifyUnmappedCullingMeshes)
{
    // A culling mesh with no explicit source counterpart must retain the
    // target cutout; it is not a visual mesh eligible for minification.
    finalMeshes.Add(targetRaw);
}
```

（`MinifyUnmappedCullingMeshes` 这个开关存在，但**所有调用点都传 false**。）

于是「看不见的护甲件」其实还留着一个**形状正确的深度代理**。换甲原来按"内层优先"把我们的几何放到
`Undergarment`（内层），那个代理就在我们**前面**，把那几圈的深度剔除掉 —— 表现为：

- 手套 / 袖套 / 丝袜**整段不显示**，而**不是**画成品红（几何在，只是被深度剔除）；
- 手、皮肤、蕾丝、头发正常（在代理形状之外）；
- **不装材质时反而能看到品红**：错误材质不走深度-only 那条路径；
- patch 里怎么做都没用（去壳 / 去挖洞 / 材质阈值 / 改名 / 转换时直接产目标 id）—— 因为变量不在这里。

## 修法

`HD2ModCli/CliVpkRekey.cs` 的 `BuildPlan`：承载层从**内层优先**改成**外层优先**。

```
旧：Undergarment → Armor → Accessory        （取最低层）
新：Accessory → Armor → Undergarment        （取最高层）
```

这样我们的几何在最外层，内层（含那个深度代理）被隐藏，代理在**我们后面**，不再挡任何东西。

附带覆盖了原来的边界情况：AF-52「禁锢」右臂只有护甲层，那时护甲层就是最外层，本来就该由它承载。

## 实机验证（用户确认）

| 配置 | 结果 |
|---|---|
| 内层优先（旧） | 手套 / 丝袜不显示；军械库正常 |
| **外层优先（新）** | **手套、丝袜、袖套全部正常** |

## 对照实验记录（全部已排除，勿重走）

| 实验 | 结果 |
|---|---|
| `Overwrite` 与 `vpk2patch` 逐字节对比（TOC + GPU） | **完全相同**，只换了 asset key |
| 删掉 12 个隐藏空壳（护甲件回落原版） | 无变化（此时原版护甲真的物理挡住） |
| 左臂/左腿空壳去掉 `mesh0` section 计数 | 无变化（清零 section 计数对代理不生效） |
| 两个 alphaclip 材质 `OpacityThreshold` 0.5 → 0 | 无变化 |
| 转换链路直接产出目标护甲 id（`--unit-ids`） | 无变化 |
| 删掉同 id 不同内容的材质冲突 | 只有 c1/c2 发色贴图一处，与手臂无关 |
| 运行时 `wardrobe.links` / `wardrobe.masks` | 真映射模式下根本不部署 Lua（日志停在 10-25） |
| 注册表件表改成"只有我们的件"（真映射手段） | **有效**，但用户否决：VPK 替换必须只用常规 patch 手段 |

## 同批修掉的另一件事：真映射 + 自定义素体的件表铺法

`ArmorRegistryDataMapper.CloneKit` 原来是"整份来源件表原样铺进目标套件"，导致：

- 素体的**头盔件漏进护甲套件**（装身体也冒头盔）、**身体件漏进头盔套件**（装头盔也冒身体）；
- 匹配不到的 body 行被写成**空行** → 军械库崩溃；
- 编辑器合并成 Any 后残留的 `IsReserved` 副本也被写出去。

现在：按目标套件的**槽位族**过滤、`Any` 展开进每条身体行、同 `(slot,层)` 只留一条、
**披风 slot 1 原样保留**、空行整行去掉并写回 `bodyCount`、一个件都给不出时原样不动。

## 关键文件

| 文件 | 改动 |
|---|---|
| `HD2ModCli/CliVpkRekey.cs` | `BuildPlan` 外层优先（本次根因修复） |
| `HD2ModCore/Infrastructure/ArmorRegistryDataMapper.cs` | `CloneKit` 按领域规则重建件表 |
| `HD2ModCore/Infrastructure/RuntimeArmorRegistryService.cs` | 组装素体件表时跳过 `IsReserved` |
| `HD2SourceMigration/VpkConversion/VpkPatchAssembler.cs` | 新增 `VpkUnitIdentityOverride`（`--unit-ids` 诊断开关） |
| `HD2ModCli/CliArguments.cs`、`CliApplication.cs` | `--unit-ids` 解析与接线 |
| `HD2ModCli.Tests/VpkRekeyPlanTests.cs` | 层规则用例改成外层优先 + 新增三层用例 |
| `HD2ModCore.Tests/ArmorRegistryDataMapperTests.cs` | fixture slot1→3 + 两条新契约用例 |

## 环境坑（复审时别踩）

- `deploy apply` 要求**游戏退出**；5 个 `DeploymentCliTests` 在游戏或管理器开着时必失败，是环境不是代码。
- 重建 Debug 版要求管理器完全关闭（DLL 被锁，MSB3027/MSB3021）。
- `generated_customization_armor_sets.dl_bin` 是**sealed** 的，直接搜 unit id 搜不到；用 `tmp/dumplib --raw` 先解密。
- 文件里的 `piece_type` 编号：**0 = 护甲层、1 = 内衣层**（`game_data_authority_pieces` 里 `raw_body_type` 0/1/3 = Stocky/Slim/Any）。
  索引库的 `body_variant` 是它 +1 归一后的值，两者别混。
