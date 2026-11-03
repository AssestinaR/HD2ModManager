# 设置页 lua 两个下拉的默认值：映射＝真映射，物理保持关闭（2026-11-03）

用户原话：「需要把管理器设置页面的 lua 卡片内的两个下拉选项设置默认改为真映射模式和独占 boot 模式」
→ 随即：「物理模式默认改回关闭吧」。

改的是**默认值**，不是可选集合——三个档位都还在，用户随时能切回去。

## 1. 最终状态

| 下拉 | 旧默认 | 新默认 |
|---|---|---|
| 映射模式 `ArmorMappingModes.Default` | `ExclusiveLuaEntry`(0) | **`TrueMapping`(2)** |
| 物理模式 `PhysicsModes.Default` | `Off`(0) | `Off`(0) —— **未变** |

映射只改了一个常量，全链路（GUI 设置页、启动同步、CLI 计划）都从它推导，
不存在"改了一处漏了一处"。

## 2. 映射为什么默认真映射

不投放任何 Lua 运行时，显存占用最低，也不占 boot 入口——是三条路里副作用最小的一条。
代价只有一个：改映射要重启游戏。需要热切换的人自己切到两个兼容档即可。

## 3. 物理为什么当天就改回来了

试过 `PhysicsMode.ExclusiveLuaEntry`，改回来是因为它不是"偏好"，而是**会把默认路径
变成失败路径**：准备物理运行时必须读到游戏自己的 boot 归档（`9ba626afa44a3aa3`），
而那个归档的路径是从 `settings.json` 里解析的。默认开 = 所有没配好游戏目录的人
第一次 `deploy plan` 就撞 `PhysicsRuntimePreparationFailed`。

「开了总开关却看不到头发动」这个理由不足以抵消它——那是个"选一下就好"的体验问题，
而默认开是"直接报错"的正确性问题。

## 4. 顺手补上的一个真实缺陷：CLI 从来不读物理模式

`CliWorkspace.Open` 以前只从 `settings.json` 读 `ArmorMappingMode`，
**`PhysicsMode` 和 `BuiltInLuaEnabled` 一个字都没读**，于是 CLI 侧永远停在静态默认值上。

旧默认值是「关闭」，所以这个洞表现为"CLI 静默少投放物理"——看不出来。
但只要默认值哪天翻成「独占 boot」（今天就这么试过），它立刻变成
"CLI 无条件投放物理，不管用户在 GUI 里选了关闭"。这个洞不该留着等下一次踩，
所以补上：

```csharp
LuaDeploymentService.BuiltInLuaEnabled = settings?.BuiltInLuaEnabled ?? true;
LuaDeploymentService.MappingMode  = ArmorMappingModes.Normalize(…, builtInLuaEnabled);
LuaDeploymentService.PhysicsMode  = PhysicsModes.Normalize(…, builtInLuaEnabled);
```

口径与 GUI 的 `SettingsService.ApplyToLuaDeployment` 完全一致（三项都读、都过总开关收敛）。

## 5. 对已有用户的影响（需要说清）

| 情况 | 结果 |
|---|---|
| 从没碰过「映射模式」 | 变真映射：不再热切换，改映射要重启游戏 |
| 从没碰过「物理模式」 | 不变（仍是关闭） |
| 显式选过某一档 | 不变，存的值优先 |

只有映射这一项会静默改变老用户的行为。如果要"老用户保持旧行为"，就得在迁移时按
`settings.json` 是否已存在来分叉默认值，那等于让默认值变成"看情况"——比直接改更难解释。

## 6. 测试侧的连带修正

三个测试类原来靠"产品默认值恰好等于被测模式"来工作，默认值一翻就红。
改成**显式钉住**被测模式，不再跟随默认值：

- `RuntimeArmorRegistryServiceTests`：`ExclusiveLuaEntry` + `PhysicsMode.Off`
- `LuaDeploymentLoaderPackageTests`：`TrueMapping` + `PhysicsMode.Off`
  （物理若开着会往 boot 里塞运行时，把"第三方包原样部署/入口争用"的断言搅乱）
- `DeploymentCliTests` 的 settings.json：显式写 `armorMappingMode` / `physicsMode`

`BuiltInLuaGatingTests.Physics_DefaultIsOff` 换成 `Defaults_AreTrueMappingAndPhysicsOff`，
另加 `Defaults_AreSelectableWithoutTouchingBuiltInLuaSwitch` 钉住"默认值必须出现在选项集合里"，
防止设置页一打开就显示一个下拉里不存在的值。

## 7. 验证

| 测试 | 结果 |
|---|---|
| Core | 885 |
| SourceMigration | 227（1 跳过） |
| CLI | 201 |
| Adaptation | 182 |
| SourceModel | 68 |
| Decryptor | 5 |
| **合计** | **1568 通过，0 失败** |
