# B-01 系列「完全污染」：unit 完全相同，怎么区分？（2026-11-23）

> **一句话**：这 9 个 B-01「战术」变体里，**6 个的 unit 集合完全相同**，
> 所以"用 unit 指纹当区分锚点"在这一族上**必然失效**——这是数据事实，不是工具缺陷。
> 但注册表里还有第二个维度：每个 piece 的 **LUT 贴图引用**（MaterialLut / PatternLut …），
> 而 `(unit 集合, LUT 集合)` 这一对**能唯一区分全部 9 个变体**。

---

## 1. 数据：unit 指纹分不出 6 个变体

用 `asset-index.sqlite` 对 9 个 `B-01「战术」`（同 `set_id=934502665`，`dlc_id` 各异）做 unit 指纹分组：

| 组 | 变体数 | item_id |
|---|---|---|
| G1 | **6** | 325695835, 487470129, 621795009, 1639126819, 1887719913, 3583338197 |
| G2 | 1 | 1333768893 |
| G3 | 1 | 1666296234 |
| G4 | 1 | 1831924614 |

⇒ 组内 6 个变体的 23 个 piece 的 unit id **逐个相同**。任何"看身上出现了哪些 unit"的判据
（我们探针在 AC-1/AC-2 上用的就是这条）在这里**不可能**区分它们。

## 2. 但注册表有第二个维度：每 piece 的 LUT 引用

`generated_customization_armor_sets.dl_bin` 的 piece 记录（112 B）里，除 unit id 外还有 8 个引用：

| 偏移 | 角色 |
|---|---|
| +24 | `MaterialLut` |
| +32 | `PatternLut` |
| +40 | `CapeLut` |
| +48 | `CapeGradient` |
| +56 | `CapeNac` |
| +64 | `DecalScalarFields` |
| +72 | `BaseData` |
| +80 | `DecalSheet` |

解析这 9 个 kit 的 LUT 集合后：

| item_id | unit 组 | MaterialLut 组 |
|---|---|---|
| 325695835 | G1 | A |
| 487470129 | G1 | B |
| 621795009 | G1 | C |
| 1887719913 | G1 | D |
| 3583338197 | G1 | E |
| 1639126819 | G1 | **F** |
| 1333768893 | G2 | **F** |
| 1666296234 | G3 | **F** |
| 1831924614 | G4 | **F** |

⇒ **`(unit 组, LUT 组)` 两两互不相同**，9 个变体全部可区分。
其中 G1 那一组靠 LUT 分（6 个不同 LUT 组），LUT-F 那一组靠 unit 分（4 个不同 unit 组）。

## 3. LUT 是**贴图**，不是材质

索引里查 LUT 的 `type_id`：

```
type_id = 0xCD4238C6A0C69E32  ==  MaterialDependencyResolver.TextureTypeId
```

⇒ 运行时的合法查询是 `Application.can_get('texture', <lut id>)`（不是 `'material'`）。

## 4. 提案：用「LUT 贴图是否驻留」当区分锚点

**假设**：玩家穿着某个变体时，只有该变体的 LUT 贴图被加载/驻留；其余变体的不在。

**验证方式（安全）**：对 9 个变体各自的 MaterialLut id（每变体取一个最具区分度的），
调 `can_get('texture', id)`，观察是否**恰好一个**为真。
——`can_get` 只吃 id，不遍历 unit、不读 `resource_name`，不会 AV。

**若成立**：区分锚点 = "哪一族的 LUT 驻留"，与 unit 无关，能覆盖完全污染的情形。

**若不成立**（比如 LUT 被整包常驻、或全部为真）：退路有两条——
1. 读**该实例的材质**（`Unit.mesh` → `Mesh.material`）：**风险高**，`Unit.mesh` 对无 mesh 的件会崩；
2. 接受"这 6 个变体视为同一个目标"（映射到一起），用材质包去区分颜色——但那就放弃了按变体映射。

## 5. 另一个可选的静态方向（不需要新增 API）

`dlc_id` 是 9 个变体唯一的 kit 级差异字段。如果游戏里有办法读到"当前装备的 kit 的 dlc/set id"，
那是最直接的锚点；但 178 个命名空间里没有 Equipment/Inventory/Wardrobe（已核实），
所以这条只能靠 dl_bin 的内存副本——又回到"读不到内存"的老问题。**不建议优先做。**

## 6. 建议的下一步

一个只读探针（不 clone、不改任何东西）：

```
for 每个 B-01 变体的区分用 LUT id:
    can_get('texture', lut) → true/false
逐行打日志
```

一次进游戏就能判定这条路成不成立。成本极低、零风险。
