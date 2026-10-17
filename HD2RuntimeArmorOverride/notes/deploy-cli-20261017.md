# 部署路径：`hd2mod deploy`（2026-10-17 实测）

## 为什么记这个

要把改过的 Lua 插件（含 `bank_patch`）装进游戏，必须走管理器的部署流程。
之前以为只能开 GUI，实测 **CLI 有 `deploy` 子命令**（虽然 `hd2mod help` 里没列）。

## 可用命令

```powershell
# 生成部署计划（只读，不改游戏）
hd2mod deploy plan active --library E:\mod `
  --game-data "D:\SteamLibrary\steamapps\common\Helldivers 2\data" `
  --output deploy.json --json

# 应用（真正写 patch 文件）
hd2mod deploy apply --plan deploy.json --confirm --json
```

`--library` 是必须的；只给 `--manager-root` 会因为找不到 `settings.json` 而报
`LibraryNotConfigured`。

## 当前实测状态（2026-10-17）

`deploy plan active --library E:\mod` 输出：

* `ready: true`，`ownership.status: managed`，`actualPatchCount: 29`
* profile `a0b91c94-...`（名字：配置文件（放在这边的mod才会启用）），
  `logicalEntryIds` 1 条，`effectiveEntryIds` 13 条
* `patches` 29 条，把 `E:\mod\Lunalice-RE2310*\9ba626afa44a3aa3.patch_0` 依次
  映射到 `data\9ba626afa44a3aa3.patch_0..12`（含 `.gpu_resources`）
* **`lua.requiresMaterialization: false`，`lua.inputCount: 0`**

也就是说：**当前 profile 里一条 Lua 补丁都没有**，护甲注册表那个 Lua 插件
并没有被部署。这解释了为什么 `data\9ba626afa44a3aa3.patch_*` 是 0 字节、
`E:\mod\.runtime\armor-registry-addon\` 是空的。

## 部署 Lua 插件还差两步

1. **把它变成库里的一个 mod**：`E:\mod\` 下目前没有任何
   `HD2-Runtime-Armor-Registry` 目录，只有 `HD2-Armor-Composition-ReadOnly-Probe*`
   和 `Registry-Slot-Snapshot-ReadOnly*` 这些历史条目。需要按同样的目录结构
   （`manifest.json` + 载荷）放进去。
2. **加进 active profile**：`logicalEntryIds` 只有 1 条，profile 存在
   `E:\mod\.hd2modmanager\` 下。**profile 的存储格式还没查**，这是下一步要读的。

## 顺带确认：Lua 插件的部署形态

`deploy plan` 的 `lua` 段有 `requiresMaterialization` 和
`generatedTargetPaths`，说明管理器会把 Lua 插件**物化成一个生成的 patch 文件**
再写进 `data\`。所以插件不是散落的 `.lua`，而是打进 patch 归档——
这和"游戏目录里没有任何 `.lua` 文件"的观察一致。

## 库内 Mod 的真实结构（2026-10-17 查清）

一个 Lua Mod 在 `E:\mod` 里是**两个目录**：

```
前置Bingus-Shared-Loader-v15\          <- 根目录：manifest.json + 图标
   manifest.json  { Version, Guid, Name, Description,
                    Options[{Name, Include:["data"], Image, ...}] }
前置Bingus-Shared-Loader-v15-data\     <- 载荷目录：patch 归档
   9ba626afa44a3aa3.patch_0             <- 18432 字节，Lua 就打包在这里
   9ba626afa44a3aa3.patch_0.gpu_resources
   option.json  { version, kind:"Option", hostModGuids:[...],
                  sourcePackageGuid, sourcePath:"option:0", optionOrder }
```

**Lua 不是散落的 `.lua`，而是装在 patch 归档里。** 管理器在 import 时把
addon zip 转换成 patch 归档。

## 当前卡点：没有 CLI 能做 zip → patch 归档 的导入

* `hd2mod mod create <名称>` 只建一个空 Mod，**没有"往 Mod 里加文件"的命令**
* `hd2mod library sync inspect|plan|validate|apply` 只同步库清单，不负责转换
* `hd2mod mod list --library E:\mod` 共 **137 个 Mod，其中没有
  `HD2-Runtime-Armor-Registry`** —— 说明护甲注册表 addon 从来没被导入过库
* 所以当前正在跑的运行时是从别处加载的（历史遗留），不在库里

要落地，只有三条路：

1. **用管理器 GUI 导入** `HD2RuntimeArmorOverride/dist/HD2-Runtime-Armor-Registry.zip`
   —— 最直接，但要人工点一次
2. **写一个 C# 小工具**复用 `HD2ModCore` 的 patch 归档写入器，把
   `armor_registry.generated.lua` 打成 `9ba626afa44a3aa3.patch_0`，
   再手写根目录 `manifest.json` 与 `-data/option.json`
3. 逆向 `9ba626afa44a3aa3.patch_0` 的容器格式（看起来就是 HD2 的 patch 归档格式，
   `HD2ModCore` 里已有读写实现）

**建议走 2**：不动 GUI、可重复、可进 CI。
