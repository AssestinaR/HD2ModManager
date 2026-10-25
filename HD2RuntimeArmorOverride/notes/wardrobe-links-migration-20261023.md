# 护甲映射：从 section 掩码切换到资源覆盖

日期：2026-10-23
依据：`src/` 实际代码 + 26088801 上的两次实测（`resource-override-confirmed-20261023.md`）

## 1. 现有链路（读代码得到的实际情况）

```
C# 管理器
  └─ 写 wardrobe.masks
       → %LOCALAPPDATA%\CowboyBingus\Helldivers2\ArmorRegistry\wardrobe.masks

Lua（armor_registry_runtime.lua 作宿主）
  ├─ require('ffi') + kernel32              ← 第 10 行，失败即整条链路退出
  ├─ armor_section_locate.lua               扫内存找 unit TOC 副本
  ├─ armor_wardrobe_masks.lua               解析 wardrobe.masks（schema 2，只读）
  ├─ armor_section_layout.lua               解析 section 布局与材质指纹
  ├─ armor_section_apply.lua                plan + 把 section 记录 +16 的 NumIndices 写 0
  └─ armor_section_publish.lua              编排 locate → plan → apply → rollback
```

文件格式（schema 2）：

```
schema=2
revision=N
enabled=true
off=<setId>,<unit 16位hex>,<sectionIndex>,<materialId|none>-<triangles>
target=<targetItemId>,<setId>
```

语义：每个护甲物品（target item）指向一个掩码集，掩码集里是若干
`(unit, sectionIndex, 指纹)` —— 命中的 section 把 `NumIndices` 写 0 以隐藏几何，
恢复时写回缓存的原值。**它是"遮掉不该显示的部件"，不是"换成另一个外观"。**

## 2. 为什么必须换：三个独立死因

**2.1 ffi 在这版游戏里不存在（致命）**

`armor_registry_runtime.lua:10-13`：

```lua
local ok,ffi=pcall(require,'ffi')
if not ok or ffi.os~='Windows' or not ffi.abi('64bit') then
    M.status='unsupported';return M
end
```

26088801 上 `require('ffi')` 直接 module not found、`jit=nil`。宿主在第二行就
`return M`，后面的 locate / layout / apply / publish **一行都不会执行**。也就是说
旧链路在这版游戏里不是"效果不好"，而是从来没有启动过。

**2.2 粒度不对**

section 掩码只能做减法。要"换上另一套外观"，旧方案必须靠补丁包把 donor 的几何
塞进同一份 unit —— 那正是被明确否决的"每套护甲复制一份 unit"（显存浪费、反复读
同一份 mesh）。

**2.3 检测面**

虽然写的是 unit TOC（数据段）而非 `game.dll` 代码页，但整条链路建立在
`ReadProcessMemory` / `WriteProcessMemory` 之上。`no-code-patching-final-20261017.md`
已经用两次实测证明：进程内 FFI 写内存同样会被判定。这条链路每多跑一天，就多一天
把用户账号暴露在检测面里。

## 3. 新链路（已实测可行）

```
C# 管理器
  └─ 写 wardrobe.links
       → 同一个 ArmorRegistry 目录

Lua（armor_override_runtime.lua）
  ├─ 读 wardrobe.links
  ├─ armor_wardrobe_links.lua    解析（扁平，无继承，无指纹）
  └─ armor_override_apply.lua    set_resource_override('unit', src, dst) × N
```

没有 ffi、没有地址、没有内存写入，只有引擎 API。

## 4. 概念对照

| 旧（section 掩码） | 新（资源覆盖） |
|---|---|
| `target=<itemId>,<setId>` | `target=<itemId>,<appearanceArmorId>`（给 UI 回读） |
| `off=<setId>,<unit>,<section>,<fp>` | `link=<sourceUnit>,<donorUnit>` |
| 写 `NumIndices=0` | `Application.set_resource_override('unit', src, dst)` |
| 写回缓存原值 | `Application.clear_resource_override('unit', src)` |
| section 粒度 | piece 粒度（需要更细时用 `Unit.set_mesh_visibility`） |
| 立即作用于活动实例 | 只作用于之后创建的实例，**需开军械库刷新** |
| 需要 ffi + 定位 TOC | 不需要 |

## 5. 改动清单

### 新增

**`src/armor_wardrobe_links.lua`** —— schema 3 解析器。
照抄 masks 的严格风格（逐行 `key = value`、重复键报错、1 MiB 上限、`unit_id`
校验 16 位 hex），但只有五个键：

```
schema=3
revision=N
enabled=true
target=<targetItemId>,<appearanceArmorId>    # 可选，供 UI 回读，不参与执行
link=<sourceUnit>,<donorUnit>                # 执行用的唯一一行
```

**`src/armor_override_apply.lua`** —— 纯函数层，**不 require 任何东西**，可离线测：

- `plan(document)` → `{links={{source_hex,dest_hex},...},count=N}`，`enabled=false` 时返回空
- `apply(app, ids, links)` → 逐条 `set_resource_override`，返回已应用列表；任何一条失败则逆序回滚
- `rollback(app, applied)` → 逆序 `clear_resource_override`

**`src/armor_override_runtime.lua`** —— 宿主。复用现成的目录约定，不要另起一套：

- `base_directory` = `loader.log_directory` 去掉 `/Logs`，否则 `%LOCALAPPDATA%\CowboyBingus\Helldivers2`
- `registry_directory` = `base_directory..'/ArmorRegistry'`
- `read_file` / `log` 照抄 `armor_registry_runtime.lua:168-179` 的实现
- 启动时读 `wardrobe.links` 并应用，写 `override.status` 记录 applied/skipped/error
- 日志里必须留一句"打开军械库以刷新外观"——没有这句，用户会以为功能没生效

### 解耦

`armor_registry_runtime.lua` 的 ffi 段**不能**再挡在覆盖链路前面。它现在
`return M` 之后，任何 `require` 它的模块都拿不到东西。两种做法：

1. 拆成 `armor_registry_ffi.lua`（可选能力，ffi 缺失时返回 nil）+ 一个不含 ffi 的宿主；
2. 或者让覆盖运行时完全不依赖它（推荐，改动面最小）。

### 退役

- `src/re824_to_re2310_override.lua` —— 22 对硬编码由 `wardrobe.links` 取代。
  它的 addon 外壳（loader 检测、`open_log`、`clear` 路径）正好是新运行时的骨架，
  可以整体搬过去，只把数据源换掉。
- `armor_section_apply/layout/locate/publish.lua` —— 新链路上没有职责。源码保留
  做历史记录，不参与构建。

## 6. C# 侧要产出什么

1. **件表**：`WardrobeSectionMapUnit` 已有 `fileId / slot / pieceType / bodyType`。
2. **匹配规则**：`(slot, pieceType, bodyType)` 精确优先、`bodyType=Any` 兜底 ——
   这正是生成 `notes/re824-to-re2310-mapping.csv` 的 `semantic_match` 列所用的键。
3. **扁平化**：C# 把"护甲 A → 外观 B"展开成逐件 `link=` 行，运行时不查继承。
   沿用 schema 2 已经定下的原则："把继承解析成扁平投影之后，运行时只剩两次查表"。
4. **不要用 `can_get` 做门禁**：22 个源在实测里全是 `false`，覆盖照样生效。
   加了这道门禁会静默跳过整套映射，而且看不出错。
5. **写入位置**：与 `wardrobe.masks` 同一目录，文件名 `wardrobe.links`。

## 7. 需要决策的三个语义问题

1. **piece 粒度够不够？** 如果某套护甲需要"同一件里只换一部分"，新链路只有两条路：
   拆 unit（等于回到复制）或 `Unit.set_mesh_visibility`（mesh 级、纯 API）。建议先按
   piece 级交付，mesh 级作为后续能力。
2. **身形不匹配怎么办？** CSV 里 `_any` 是通配，`_stocky`/`_slim` 必须同类匹配。
   跨身形映射很可能穿模，需要一条明确的回退规则（建议：无同类件时跳过并记入 status，
   而不是拿 `_any` 硬凑）。
3. **头盔/披风槽**：22 件里没有 `af43`（头盔）。管理器的基础 7 槽理论上含头盔，但要
   确认头盔的 unit id 也在 authority database 里，否则头盔映射会静默缺失。

## 8. 已知风险

- **只影响未来实例**：目标 unit 已经加载时覆盖不生效，必须开军械库刷新。这是 UI 文案问题，不是实现问题。
- **任务加载是否重新实例化未验证**：决定映射能否"进任务即生效"，需要一次实测。
- **误覆盖风险**：覆盖的是 unit 资源，若生成端把非护甲 unit（船员、场景物件）写进表，
  会全局替换。生成端必须只从护甲件表取，且运行时最好按 id 白名单再校验一次。
- **撤销时机**：`clear_resource_override` 只影响之后的实例化；"关掉衣柜"要生效同样需要刷新。
