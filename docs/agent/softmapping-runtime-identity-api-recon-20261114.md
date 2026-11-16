# 软映射能不能做到「无消耗 + 不污染」：引擎 API 侦察（2026-11-14）

> 只做情报与方案判定，本轮**没有改任何产品代码、没有进游戏**。
> 结论分级：**已核实**（有代码/实测/文档） / **待验**（需要一次进游戏）。

---

## 0. 一句话结论

1. **没有「按件 / 按实例换 unit 资源」的引擎 API。** `set_resource_override` 只按**资源 id**、进程级、只影响**之后**实例化的件。共享 unit 的冲突它解决不了。
2. **真正能做到「不污染」的现成架构是「克隆显示」**（spawn 我们的 unit → link 到角色骨架 → 原版件 `set_unit_visibility(false)`）。这是按实例的，Transmog 1.1.2 已经在用；代价是**每帧多一份实例的渲染/蒙皮成本**（不是零消耗），且没有布料模拟。
3. **还有一条可能「零成本 + 按甲」的路：运行期改游戏的 kit 数据表**（`Application.get_data/set_data`、`Unit.set_data`、以及全局 `data.kits`）。函数确实存在（Stingray 文档里有），但**「HD2 的护甲注册表是否以可写 Lua 表的形式暴露」还没验过**——这是本轮最高价值的待验点。
4. **识别「玩家穿着什么护甲」没有 API，但可以反推**：用「世界里有哪些 unit 实例 + 它们跟着谁动 + 在哪个 world」三件事做签名匹配。仓库里已经有两块现成零件（物理运行时的运动判据、HD2IAS 的 `units_by_resource` + 位置配对），误差来源也是已知的。

---

## 1. 引擎 API 的硬事实

数据源：本机 `tmp/stingray-api/sigs.json`（Stingray 官方 API 签名导出，178 个命名空间）＋仓库里的实机笔记。
下表只列与本问题有关的。

| 能力 | API | 语义 | 证据 |
|---|---|---|---|
| **资源级覆盖** | `Application.set_resource_override(type, name, override)` / `clear_resource_override` | 按资源 id；**进程级、全局**；只影响**之后**实例化；零额外显存；可撤销；不进磁盘 | `notes/resource-override-confirmed-20261023.md:19-116`（实测：打开军械库触发重建后才可见；`can_get` 不是有效性闸门） |
| **数据表读** | `Application.get_data(indices)`、`Level.get_data`、`World.get_data`、`Unit.get_data`、`UnitResource.get_data` | 「unit 的 script data object / 世界/关卡/应用的数据树」 | `sigs.json` 的 Application/Unit/World/Level/UnitResource 条目 |
| **数据表写** | `Application.set_data(...)`、`Level.set_data`、`World.set_data`、`Unit.set_data` | 同上的写端 | 同上 |
| **按实例换材质** | `Unit.set_material(slot_name, material_resource)` | **按实例**把某个材质槽换成另一个材质资源；返回 IdString64 | `obj_Unit.html`「Assigns a material resource to a slot」 |
| **按实例隐藏** | `Unit.set_unit_visibility(v)`、`Unit.set_mesh_visibility(mesh, v, ctx)`、`Unit.set_visibility(group, v)`、`Unit.has_visibility_group(group)` | 整件 / 单 mesh / 可见性组，全部**按实例** | `sigs.json`；`docs/agent/hd2-physics-roadmap-and-handover.md:650` |
| **实例枚举** | `World.units(w)`、`World.num_units`、`World.units_by_resource(w, id)`、`World.unit_by_name`、`World.unit_by_id` | 拿活体列表 / 按资源或名字反查 | `sigs.json`；物理运行时每帧已在用 `World.units`（`physbone_runtime.lua:1606-1620`） |
| **实例身份** | `Unit.resource_name`、`Unit.debug_name`、`Unit.name_hash`、`Unit.num_scene_graph_items`、`Unit.world_position(unit, node)` | 资源身份 / 节点数 / 世界坐标 | `hd2-bone-lua-capability.md:754-786` |
| **克隆与挂骨** | `World.spawn_unit(w, resource, pos, rot)`、`World.link_unit(w, child, childNode, parent, parentNode)`、`unlink_unit`、`destroy_unit` | 克隆显示链路；实测可用 | `hd2-bone-lua-capability.md:590-616`、`tmp/probe/link.lua` |
| **Mesh 只读** | `Unit.mesh(u, mesh)`、`Mesh.material(slot)`、`num_materials`、`material_slot_name`、`Mesh.node/local_pose/...` | 读网格与材质；**没有 `Mesh.set_material`，也没有「按实例换 unit 资源」** | `sigs.json` Mesh 条目 |

### 1.1 明确不存在的东西（已核实）

- **没有装备 / 背包 / 衣柜 / 玩家命名空间**：178 个命名空间里没有 `Equipment`、`Inventory`、`Wardrobe`、`Player`、`Loadout`（`sigs.json` 全量列表）。
- **没有物理 / 射线 / 导航**：`PhysicsWorld`/`Actor`/`Joint`/`Mover`/`Cloth`/`HumanIK`/`Vehicle`/`Raycast`/`NavigationMesh` 在 HD2 里被裁掉（`hd2-bone-lua-capability.md:509-526`）。
- **没有「结构关系」可用**：`Unit.is_child(护甲, avatar)` / `is_child(avatar, 护甲)` / `scene_graph_parent` 七个探针**全部返回 nil**（`docs/agent/armory-warbond-crash-20261112.md:196-207`）。⇒ 判断「这件是不是玩家身上的」只能看行为。
- **没有 ffi**：本版引擎 `require('ffi')` 失败（`HD2ModCore/Infrastructure/RuntimeArmorRegistryLuaSource.cs:13-24`；`armor_registry_runtime.lua:12-14` 的 ffi 闸门）。⇒ 老的「内存改注册表」链路在我们的投放方式下不可用。

---

## 2. 为什么「检测穿着 → 动态重定向」救不了共享 unit

这是最容易误判的一条，先把逻辑钉死：

1. `set_resource_override` 的键是**资源 id**，不是实例、不是 kit。共享 unit 被 A、B 两套甲同时引用，覆盖它就会同时改 A 和 B。
2. 覆盖**只对之后实例化的件生效**。玩家在军械库里换甲的那一刻，游戏才销毁旧件、实例化新件；我们的 Lua 若在「换完之后」才检测到穿的是 B，覆盖已经来不及作用到已实例化的件上（要等下一次重建：再开一次军械库 / 进任务）。
3. 覆盖是**本机进程级**的：我们自己看到的队友、军械库预览里的人台，也会命中同一个资源 id。
   （好消息：它只影响本机视觉，不会同步给别人。）

⇒ **只有当「引用同一个 unit 的所有甲都映射到同一个 donor」时，资源覆盖才是安全的。** 当前 299 条映射全部指向同一个素体，所以今天是安全的；一旦用户给不同甲配不同素体，共享 unit 立刻变成冲突。

---

## 3. 三条可行路线（按「不污染」强度排序）

### 路线 A：克隆显示（按实例，真正不污染）

```
检测到玩家穿着 kit K
  → 按 K 的意图找到 donor unit（我们的模型件）
  → World.spawn_unit(main_world, donor, 位置, 朝向)
  → World.link_unit(child, childNode, playerAvatar, 目标节点)
  → 原版件 Unit.set_unit_visibility(false)（属性/被动仍由它提供）
```

- 现成先例：**Transmog 1.1.2** 的 dresser 就是这套（`docs/agent/transmog-compat-analysis-20261108.md:15-20`；上游源码注释：*a spawned copy has no cloth simulation*）。
- 真正做到「按实例」：别的甲、别的玩家、军械库人台都不受影响。
- 实测成本（对方真机，`transmog-compat-analysis` §6 / 技术文档 §228-230）：运行基线 10 unit / 373 链 / ≈1080 引擎调用每帧 / dt 0.017；挂 7 个克隆件后 17 unit / 625 链 / ≈1800 调用 / dt 0.023。
- 已知坑：① 没有布料模拟；② 第一人称/ADS 下 `set_unit_visibility` **不是权威**（HD2IAS 实测：ADS 时原生渲染路径会把 unit 又显示回来，`runtime.lua:1178-1193`）；③ 被隐藏的原生件是否仍有蒙皮成本**未定**（`transmog 02-技术文档.md:294` 把它列为剩余成本嫌疑）；④ 世界拆解时对克隆件的生命周期管理很脆（HD2IAS 为此外置了一整套「不缓存句柄、每帧重发现」的规则，`runtime.lua:885-893`）。

### 路线 B：运行期改 kit 数据表（按甲，理论零成本）——**待验**

如果 HD2 的护甲注册表以 Lua 数据表形式暴露，并且游戏是在装备/预览时**现读**这张表，那么：

```
data.kits[kit].bodies = { ...我们的件... }   -- 只改这一套甲
```

- 这是**按 kit** 的，天然不污染；不复制资源，也没有克隆件。
- 函数存在：`Application.get_data/set_data`、`Level.get_data/set_data`、`World.get_data/set_data`、`Unit.get_data/set_data`、`UnitResource.get_data`（`sigs.json`）。
- **但**：Transmog 那个 `data.kits` 是**它自己的 catalog**（`require('hd2tm.data')`），不是游戏的原生注册表（`E:\tmp\transmog\release\source\hd2mm_registry.lua:257`；`notes-integration.md:194` 的 `package.preload['hd2tm.data']`）。所以「改 data.kits 就能改游戏」这件事**在 Transmog 身上不成立**，不能直接照搬。
- 要判定这条路，只需一次进游戏探测：
  ```lua
  local d = rawget(_G, 'data')                     -- 有没有全局 data
  -- 有的话：type(d.kits), 数量, 取一个 kit 看 structure
  -- 没有的话：pcall(Application.get_data, {'kits'}) / has_data({'kits'})
  -- 以及 Application.get_data({'generated_customization_armor_sets'})
  ```
  判据：能拿到 422 个 kit、且 `bodies[body]` 里的 piece 与本机注册表一致 ⇒ 表是真的；再改一个 kit 的一个 piece，看军械库预览是否变化。
- 风险：`get_data` 的 indices 语义是「数据树路径」，猜错键可能返回 nil（安全）也可能触发原生错误（**pcall 拦不住 AV**，仓库有前科）。所以要先 `has_data` 再 `get_data`，并在菜单里做、不要在加载期做。

### 路线 C：混合（推荐作为工程落点）

1. **独占 unit**（只被一套甲引用的）→ 继续用资源覆盖：零显存、零额外实例、零冲突。
2. **共享 unit** → 不覆盖；运行时按「玩家穿的是哪套」用**克隆显示**补那一件，或者干脆保持原版。
3. 需要「玩家穿着哪套」的检测只在第 2 步用得上。

这样克隆件的数量被压到「共享件」这一小撮，帧成本可控。

---

## 4. 识别「玩家现在穿着什么护甲」

### 4.1 没有 API，但有可靠的间接证据

| 证据 | 怎么拿 | 说明 |
|---|---|---|
| 世界里存在哪些 unit 实例 | `World.units(world)` / `World.units_by_resource(world, id)` | 物理运行时每帧已在用前者做判活（`physbone_runtime.lua:1606-1620`） |
| 实例是哪个资源 | `Unit.debug_name`（字符串里带 hash，HD2IAS 就靠这个匹配）或 `Unit.resource_name` | `runtime.lua:950-954` |
| 实例是不是「玩家身上的」 | **运动判据**：玩家动，件也动；人台不动（`physbone_runtime.lua:620-721`，阈值：主人累计位移 > 5 cm 时，件至少达到主人一半） | 已实机验证，能剔除军械库人台/预览 |
| 在哪个 world | `Application.worlds()` 全遍历；**军械库预览不在 `main_world()`**（`resource-override-confirmed-20261023.md:74-79`） | 只看 main_world 会漏，必须按需扫 |

### 4.2 具体算法（低成本版）

1. **离线**：为每个 kit 建签名 `S(kit) = {该 kit 引用的全部 unit id}`（按身形分开；注册表里现成）。
2. **运行时**：每帧取 `World.units(main_world)`，与上一帧做**差分**（只关心新出现的句柄）；对新句柄调一次 `debug_name` 取 hash。
   - 成本 O(新增件)，换甲时只有 1~20 个新件；稳态几乎为 0。
   - 别每帧对全部 unit 调 `debug_name`（HD2IAS 的 census 在加载期对 2291 个 unit 连调三个 API 直接把游戏打死，`hd2-physics-roadmap-and-handover.md:2066-2071`）。
3. 把 hash 与已知 unit 目录比对，累积成「当前身上的 unit 集合」；与 `S(kit)` 求交，命中率最高的 kit 就是穿着的那套。
4. **消歧**：
   - 同一套的配色变体常常**共用全部 unit** ⇒ 签名相同、**外观也相同**，映射到同一个 donor 不影响结果，不需要区分。
   - 第一人称会隐藏 slot 0/2 的 unit ⇒ 签名可能缺两块；用「已知件命中 + 未命中件允许缺失」的容差匹配，或只在军械库/第三人称时做最终判定。
   - 模组甲不在目录里 ⇒ 匹配失败时保持原版（不要猜）。

### 4.3 能不能做到「尽量与设置意图一致」

能，但要接受三条硬限制：

1. **时效**：资源覆盖要等游戏重建件（开军械库 / 换甲 / 进任务）；克隆显示可以「检测到就上」，但代价是实例成本与生命周期。
2. **多人**：映射是本机视觉，别人看不到；别人穿什么也不影响我们的判定（`World.units` 里所有角色都在，靠运动/位置区分自己）。
3. **三选二**：**零额外成本 / 零污染 / 任意时刻即时生效**，最多同时满足两个。
   - 资源覆盖 = 零成本 + 即时（但要预注册所有甲，共享 unit 会污染）
   - 克隆显示 = 不污染 + 即时（但有实例成本）
   - 数据表改写 = 零成本 + 不污染（但要等游戏重读表；且**未验证**）

---

## 5. 建议的验证顺序（都不改产品代码）

| # | 实验 | 判据 | 风险 |
|---|---|---|---|
| **E1** | 进游戏探测 `data` / `Application.has_data` / `get_data` 能不能拿到护甲注册表 | 能拿到 422 kit 且结构与注册表一致 | 中：未知签名，必须先 `has_data`，只在菜单里做 |
| **E2** | `World.units` 差分 + `debug_name` 识别当前 kit | 在军械库换 3 套甲，识别结果与手选一致；人台/队友不误判 | 低：全是只读 |
| **E3** | 克隆 1 件（1 个 donor unit）+ 隐藏原版件 | 可见、位置对、ADS/第一人称不穿帮、退出军械库不崩、帧成本可测 | 中：生命周期与 ADS 是已知坑 |

E1 决定「有没有零成本路线」；E2 决定「动态方案能不能落地」；E3 决定「克隆方案的代价」。

---

## 6. 与现有代码的接口

| 要做的事 | 落点 | 备注 |
|---|---|---|
| 识别穿着 kit | 新增 Lua 模块（随 `armor_override_runtime.lua` 一起投放） | 复用 `physbone_runtime.lua` 的 `World.units` 判活与运动判据 |
| 资源覆盖（独占件） | `WardrobeLinkCompiler` 增加「unit → 引用它的 kit 集合」过滤 | 判据现成：`Inspect-UnitSharing.lua` |
| 克隆显示（共享件） | 新增运行时模块；需要 donor unit 已部署 | 参考 Transmog 的 dresser + 物理桥接 |
| 数据表改写（若 E1 成立） | 同一运行时模块 | 需要 kit 表结构与写回时机 |

---

## 7. 一句话给决策

**「零成本 + 不污染」的圣杯只可能在路线 B；路线 B 现在只差一次进游戏探测。**
在 B 未证实之前，工程上最稳的是路线 C：**独占件用覆盖（现在就能做），共享件用克隆（代价可控），并用 E2 的识别决定什么时候上克隆。**
