# Lua 部署架构：删除合并，只保留三条路（2026-10-28）

## 1. 结论

**管理器不再合并任何第三方 Lua。** 合并功能整体删除（27 个文件，-1223 / +308 行）。

理由：合并唯一买到的能力是"没有加载器时，让替换 `boot` 的第三方 mod 和我们的运行时
共存"，而代价是一整套第三方 boot 解析器、merge ledger、opaque 回退、一个
"允许不安全 Lua 合并"的用户开关，且每出现一种新 mod 形态就撞一次墙。
**让多个第三方 Lua mod 共存是加载器的职责，不是管理器的职责。**

## 2. 三条路，各自的职责

| 模式 | 管理器投放什么 | 依赖 | 谁派发 |
|---|---|---|---|
| **独占入口**（默认） | 只投放自己的运行时到 `boot`（**一个**文档） | 无 | 管理器自己 |
| **外置 lua 入口兼容** | 只投放 `-- HD2-Addon: mods/codex/runtime_armor_registry` 补丁 | 社区加载器 | 加载器 |
| **真映射** | 不投放任何 Lua | 无 | —— |

第三方 Lua patch（含加载器包）**一律原样部署**，管理器不解析、不合并、不改一个字节。

### 运行时的产物长什么样

独占模式生成的启动脚本就是社区加载器用的同一个套路：

```lua
local BC = "<游戏自己的 boot 字节码，逐字节重放>"
local loader = rawget(_G, "loadstring") or rawget(_G, "load")
local chunk = loader and loader(BC)
if chunk then pcall(chunk) end
-- HD2 Runtime Armor Registry
local __hd2_registry_loader = rawget(_G, "loadstring") or rawget(_G, "load")
local __hd2_registry_chunk = __hd2_registry_loader and __hd2_registry_loader("<运行时源码>", "@mods/codex/runtime_armor_registry")
if __hd2_registry_chunk then pcall(__hd2_registry_chunk) end
return
```

外层再用 `Lua51WrapperChunk.Build` 编成 Lua 5.1 字节码（version 1），和游戏自己那份
`boot` 资源同一种形态。

## 3. 入口争用：只报告，不擅自处理

引擎只执行 `data/settings.ini` 里 `boot_script` 指定的那一个 Lua 资源
（实测见 `lua-entry-capacity-investigation-20261028.md`），所以不存在"合并一下让它们
都跑"的解法。新加两条诊断：

| 码 | 级别 | 场景 | 文案要点 |
|---|---|---|---|
| `LuaBootEntryConflict` | **Error** | 独占模式下，已启用的 mod 也提供 `boot` | 两者无法同时生效；请改"外置 lua 入口兼容"或禁用其一 |
| `LuaBootEntryOverlap` | Warning | 外置/真映射下，≥2 个 mod 都提供 `boot` | 引擎只执行序号最大的那份；如需共存请装加载器 |

第一条是 Error 而不是 Warning：独占模式下我们的运行时不生效意味着**护甲映射静默失效**，
那是管理器自己的功能失败，必须挡住并给出可执行的出路。

## 4. 社区加载器兼容：按契约，不按某个加载器

外置模式产出的补丁天然满足三家加载器（BSL / MDL / boot-mod-loader）共同的发现规则：

| 契约项 | 我们的产物 |
|---|---|
| 资源在 `9ba626afa44a3aa3.patch_N` 里 | ✅ |
| 资源名 = `mods/codex/runtime_armor_registry`（`0x0A2E0E3386517D4C`） | ✅ |
| 首行是 `-- HD2-Addon: <同名>` | ✅ |
| 载荷是 version 2 明文源码 | ✅ |
| 同一 patch 里另有隐藏 unit 条目 | 加载器按 TypeId 过滤，会跳过 ✅ |

**不为任何一个加载器写适配**：加载器会随游戏更新失效（2026-10-20 一次更新干掉了
BSL 和 MDL 2.1.1），契约不会。要守的是上面这张表，不是某个加载器的版本号。

## 5. 删掉了什么

| 删除项 | 说明 |
|---|---|
| `LuaPatchInspector` | 第三方 boot 脚本解析器（415 行）——现在只需要 TOC 扫一眼有没有 `boot` 键，不读载荷 |
| `LuaPatchComposer.Compose` / `ComposeOpaque` | 多文档合并 + 不安全回放 |
| `LuaMergeLedger` / `LuaMergeLedgerBlock` / `LuaMergeLedgerSerializer` | 合并账本与序列化 |
| `LuaPatchWrapperKind` / `LuaPatchDocument.IsOpaque` / `SourceText` | 只为"读回自己写的合并产物"而存在 |
| `LuaDeploymentPermission` + `--allow-unsafe-lua` | 从接口、协调器、CLI、UI 全部摘除 |
| 设置项"允许不安全 Lua 合并部署" | 失去意义 |
| `HasActiveBingusLoader` | 一直是死代码（行为差异改由用户显式选模式） |

保留：`Lua51Bytecode` / `Lua51WrapperChunk`（单文档封装必需）、`LuaPatchRuntimeValidator`
（用游戏自己的 `bin/lua51.dll` 编译 + 冒烟校验我们的运行时源码）。

`LuaPatchComposer.cs`：632 → 93 行。`LuaPatchContracts.cs`：132 → 52 行。

## 6. 用户会察觉到的变化

**唯一失去的场景**：没装加载器 + 有第三方 `boot` 替换型 mod + 想用热切换护甲映射。
以前能合并共存，现在会明确报 `LuaBootEntryConflict` 并告诉你怎么选。

这是有意的取舍：合并对这类 mod 的真实表现本来就是 opaque 回放（"看起来能用"），
而且你的 `settings.json` 里 `AllowUnsafeLuaMerge` 早就被打开过——说明早就撞过墙。
拿掉它换来的是部署链最脆弱的一段消失。

## 7. 验收

| 检查 | 结果 |
|---|---|
| `HD2ModAdaptation.Tests` | 180 通过 |
| `HD2ModCore.Tests` | 822 通过 |
| `HD2ModCli.Tests` | 175 通过 |
| 编译（4 个项目） | 0 错误 0 警告 |
| 真实 Chef Shared Loader + 真映射 `deploy plan` | `isReady: true`，`diagnostics: []`，`lua.requiresMaterialization: false`，3 个 patch 原样部署 |

用例改动：
- 删除 `LuaPatchComposerTests`（9 个，测的是已删除的合并）。
- `DeployPlanApply_TwoLuaPatches_*` 改为断言"两个补丁各自原样部署、不生成合并补丁"。
- 新增 `LuaDeploymentLoaderPackageTests`（2 个）：加载器包原样部署；两个包只报
  `LuaBootEntryOverlap` 警告、都照常部署。
- 新增 `LuaDeployment_ExclusiveRuntimeWithThirdPartyBootMod_ReportsConflict`：
  独占运行时 + 第三方 boot → `LuaBootEntryConflict` Error，且不产出任何补丁。
- 删除 `NotifyActiveProfileChanged_UsesCurrentLuaPermission`（权限已不存在）。
