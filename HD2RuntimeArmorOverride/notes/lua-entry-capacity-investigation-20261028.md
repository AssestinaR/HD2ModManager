# Lua 入口容量：能不能扩容到多个入口（2026-10-28，含实机实测）

## 0. 结论

| 问题 | 答案 | 依据 |
|---|---|---|
| 为什么只有一个 Lua 入口？ | 引擎启动只读 `data/settings.ini` 的 `boot_script`，**一个字符串** | §2 |
| 有没有"注册文件"可以扩容？ | **没有**。入口不是表里的一行，是配置里的一个标量 | §3 |
| `settings.ini` 能改吗？ | **能**。游戏启动不会重写它 | §4 实验 1 |
| `boot_script` 真的被读取吗？ | **是**。改名字后引擎真的加载了那个资源 | §4 实验 2 |
| 入口名可以随便取吗？ | **可以**。不限于 `boot` | §4 实验 2 |
| `boot_script` 能是列表吗？ | **不能**。数组形式直接崩，引擎拿到 nil | §4 实验 3 |
| 原版有几个 Lua 资源？ | **11 条条目 / 9 个唯一资源**，只有 1 个被引擎执行 | §5 |
| 第二个入口（wwise）还活着吗？ | **已死**。投放该资源毫无反应 | §4 实验 0 |
| 那还能不能做到"一个入口带 N 个 mod"？ | **能，而且是原生的**：入口里可以 `require` 任意 Lua 资源 | §6 实验 4 |

**一句话**：入口数量固定为 1，且无法从任何数据文件扩容；但入口**名字自由**、
入口里**可以按名字 `require` 任意多个 Lua 资源**，所以"1 个入口 + N 个资源"
是完全原生的能力——不需要任何第三方加载器。

---

## 1. 引擎的 Lua 启动契约（实测，纠正了旧结论）

从探针日志直接读到的事实：

```
entry ENTERED  version=Lua 5.1
globals: io=table os=table debug=table string=table table=table math=table coroutine=table
         bit=nil jit=nil ffi=nil stingray=table require=function loadstring=function
         load=nil package=table
globals after boot: init=function update=function render=function shutdown=function
Application callbacks: init=nil update=nil render=nil shutdown=nil
INIT CALLED
```

| 事实 | 说明 |
|---|---|
| **契约是全局 `init` / `update` / `render` / `shutdown`** | 不是 `stingray.Application.*`（那四个全是 `nil`）。旧笔记里"boot.lua 设 `Application.init`"的读法会把人带沟里 |
| 引擎在脚本加载完成后调用 `_G.init` | 日志里 `INIT CALLED` 在 `entry READY` 之后 |
| **`load` 是 `nil`，`loadstring` 是函数** | 第一版探针用 `load()` 就死在这，日志只到 `LOADED` 就没了 |
| `bit` / `jit` / `ffi` 全 `nil` | PUC-Rio Lua 5.1.4，LuaJIT 已被移除 |
| `io` / `os` / `debug` / `stingray` / `package` 都在 | 文件读写、`os.getenv`、`os.time` 都能用 |
| `package.path` 是**开发者机器上的残留** | `C:\Users\Administrator\AppData\Local\Programs\LuaJIT\bin\lua\?.lua` —— 游戏直接把 Stingray 独立播放器的默认值带出来了 |

**崩溃教训**：如果顶替入口的脚本没有定义 `_G.init` 等四个全局，引擎会调用 `nil`，
游戏在启动约 10–20 秒后崩溃（黑屏 + CRS dump，无任何 Lua 报错可看）。
这解释了最初两次"探针跑完就崩"的现象——探针本身没错，是契约错了。

---

## 2. 入口 = `settings.ini` 里的一个字符串

`D:\SteamLibrary\steamapps\common\Helldivers 2\data\settings.ini`：

```
pre_boot_package = "packages/pre_boot"
boot_package = "packages/boot"
boot_script = "boot"
project_name = "Helldivers 2"
stingray_version = "1.8.0.0"
build = 19411
```

引擎拿 `boot_script` 的字符串 → `IdString64`（MurmurHash64A）→ 在已挂载的资源里找 →
执行。`boot` = `0xF476DF93691895FA`，位于 archive `9BA626AFA44A3AA3`（索引里叫 `audio/Init`）。

patch 归档按 `(TypeId, FileId)` 覆盖，所以两个 mod 都改 `boot` 只能活一个。

---

## 3. 为什么"在注册文件里扩容"不成立

| 候选 | 实际是什么 | 能放 N 个入口吗 |
|---|---|---|
| `settings.ini` → `boot_script` | Stingray 设置，**标量字符串**（实测数组不被接受，§4 实验 3） | ❌ |
| `bundle_database.data` / `bundles.nxa` | bundle 索引（3657 个包的哈希 + 分片位置） | ❌ 只描述"包在哪" |
| `data/game/generated_*.dl_bin` | 游戏数据表（真映射改的就是这类），全部加密 | ❌ 里面没有 Lua 清单 |

护甲能扩容是因为它是**数组**（每套甲一行、每件一行）；`boot_script` 是**标量**，无处可塞。

---

## 4. 实机实验与结果

探针工具：`tmp/lua-entry-scan/probe/`
（`build_patch.py` 造单资源 patch；`set_boot_script.py` 改 `boot_script`；
`gen_boot_replay.py` 生成内嵌原版字节码的回放探针）
日志：`%APPDATA%\Arrowhead\Helldivers2\lua-entry-probe.log`

### 实验 0：wwise 槽还活着吗 → **已死**

patch 只含一个资源 `core/wwise/lua/wwise_flow_callbacks`（`0x7251FDD9BB62480A`），
脚本只写一行日志。游戏正常启动、跑到大厅、**日志文件根本没被创建**。

附带确认：MDL 2.1.1 的那个载荷是 **LuaJIT 字节码**（`1b 4c 4a 02`），
在 PUC Lua 5.1 上根本加载不了 —— **MDL 2.1.1 是 2026-10-20 更新前的版本**，
这解释了"它为什么能占 wwise 槽"以及"为什么它现在不能用了"。

### 实验 1：游戏会不会重写 `settings.ini` → **不会**

把 `metrics_batch_timer = 30` 改成 `31`，启动、跑满 2 分钟、有窗口、进大厅。
文件 `LastWriteTime` 没变、SHA256 没变、标记还在。

→ `settings.ini` 是稳定的，可以改。

### 实验 2：`boot_script` 被读取吗？入口名能随便取吗 → **都成立**

`boot_script = "hd2probe_boot"`，patch 里提供名为 `hd2probe_boot` 的资源
（`0x9F2762F0E14D489E`）。日志：

```
hd2probe_boot ENTERED t=... version=Lua 5.1
globals before: init=nil update=nil render=nil shutdown=nil
hd2probe_boot READY
hd2probe_boot INIT CALLED
```

游戏正常运行 2 分钟以上。**引擎加载的是 `hd2probe_boot`，不是 `boot`**
（同一个 patch 里也放了 `boot` 回放探针，它一行日志都没有）。

→ 入口名完全自由。`boot` 只是默认值。

### 实验 3：`boot_script` 能是列表吗 → **不能**

```ini
boot_script = [
	"hd2probe_boot"
	"hd2probe_boot2"
]
```

游戏启动 → 窗口出现 → **立刻崩溃，日志文件都没创建**（两个脚本一个都没跑）。
引擎把数组当成了非字符串，拿不到入口名。

→ 标量，只能一个。这条路彻底关闭。

### 实验 4：入口里能 `require` 别的 Lua 资源吗 → **能**

在入口里执行：

```lua
Application.can_get('lua', IdString64.from_hex('c5da539c5b1fbc12'))  -- true
require("hd2probe_boot2")   -- 执行了另一个资源
require("boot")             -- 执行了 boot 资源（当时由 patch_0 提供）
```

日志证明两个 `require` 都真的执行了目标脚本（各自打了自己的 `ENTERED` 行）。
游戏全程正常运行。

→ **一个入口可以驱动任意多个 Lua 资源，且能先探测存在性。**

---

## 5. 原版到底有几个 Lua 资源（索引实测）

只读查询管理器资产索引（`asset-index.sqlite`，`built_utc=2026-10-26`，
3567 archive / 99299 资产）得到 **9 个唯一 FileId**，遍历所有 archive TOC 得到
**11 条条目**。`boot-mod-loader` README 说 "The game ships only 11 Lua scripts" ——
他数条目，我们数资源，数字对得上。

| FileId | archive | 大小 | 是什么 | 引擎执行吗 |
|---|---|---:|---|---|
| `F476DF93691895FA` | `9ba626afa44a3aa3` | 501 B | **`boot`**：四个空回调 + `jit.off()` | ✅ **唯一启动入口** |
| `0D4816DE1D4771F1` | `9ba626afa44a3aa3` / `a964916e722753ce` | 994 B | `AnimationFlowCallbacks` | 组件脚本，按需 |
| `19BC198A3E7BB327` | `9ba626afa44a3aa3` | 644 B | `WwiseBankReference` | 组件脚本，按需 |
| `2E42BECD00D0B9D9` | `9ba626afa44a3aa3` | 2720 B | `WwiseVisualization` | 编辑器用 |
| `405896B911013778` | `9ba626afa44a3aa3` / `a964916e722753ce` | 175 B | `ANIM_REQUIRES` | 组件脚本，按需 |
| `78796AE20BB32759` | `9ba626afa44a3aa3` | 2596 B | `WwisePreview` | 编辑器用 |
| `A95A92495DDA0BA3` | `fc5b6bff0db90aab`（武器包） | 7311 B | `reflection_probe` 实体脚本 | 组件脚本，按需 |
| `3425CB597039DD69` | `7ed1f941859987b4` | 4893 B | `vector_field/global_direction` | 组件脚本，按需 |
| `79DC662443ACF6F1` | `0102bf171412f94d` | 6653 B | Stingray 独立播放器主脚本 | HD2 不用 |

`core/wwise/lua/wwise_flow_callbacks`（`0x7251FDD9BB62480A`）**已不在原版里**。

---

## 6. 真正可用的路线：我们自己当入口

实测已经把三个关键能力凑齐了：

1. 入口名可以自选 → 我们可以叫 `hd2/manager/entry`，**不占 `boot` 这个键**；
2. 入口里可以 `require` 任意资源名 → 可以拉起 N 个各自独立命名的 mod 资源；
3. `Application.can_get('lua', id)` 可探测 → 加载前先确认存在，缺失就跳过。

于是：

```
settings.ini: boot_script = "hd2/manager/entry"
                    │
                    └─ hd2/manager/entry
                         ├─ require("boot")              ← 别人写的"替换 boot"型 mod 照样能跑
                         ├─ require("hd2/armor/runtime")  ← 我们的护甲运行时
                         ├─ require("mods/x/y")           ← 任意声明式 mod
                         └─ ...
```

**这比社区加载器更强的一点**：因为 `boot` 只是一个资源名，
我们自己的入口可以把"替换 boot 的 mod"当成一个普通资源 `require` 进来，
所以**不需要那个 mod 让位、也不会有键冲突**（代价是这类 mod 之间仍然互斥，
因为它们抢的是同一个 `boot` 键）。

也就是说"不再需要加载器的支持"是可达的——**前提是加载器由我们自己出**。

### 与现有管理器模式的关系

| 模式 | 入口 | 需要第三方加载器 | 备注 |
|---|---|---|---|
| `ExclusiveLuaEntry`（现状） | `boot` | 否 | 和"替换 boot"的 mod 抢键 |
| `ExternalLuaEntry`（现状） | 别人的入口 | 是 | 依赖 MDL/BSL |
| **新增：自有入口 + 派发** | `hd2/manager/entry` | **否** | `boot` 键让给别人，还能 `require` 回来 |

---

## 7. 顺带纠正：envelope 的 version=2 完全可用

本仓库 `LuaPatchContracts.cs` 的注释写着"version 2 + 源码文本会让游戏启动崩溃"，
据此管理器改为只发 version 1 + 字节码。**这个结论是错的。**

实测：本次所有探针都是 `version=2` + 明文源码（`build_patch.py` 用的就是
社区 `write_module` 的 `LUA_SOURCE_FLAG = 2`），全部被正常加载执行。

真正的失效原因是**虚拟机从 LuaJIT 换成了 PUC Lua 5.1.4**：
`bit` / `jit` / `ffi` / `goto` 全没了，旧 mod 的 LuaJIT 字节码（`1b 4c 4a 02`）
在 PUC Lua 上加载不了。MDL 2.1.1 的载荷就是 LuaJIT 字节码，所以它失效——
和 envelope 的版本号无关。

> 对管理器的意义：`Lua51Bytecode` 编解码器不再是必需品（已逐字节往返验证过，
> 留着无害）；而**发源码**对可读性和排错更友好。

---

## 8. 本次新增工具（`tmp/lua-entry-scan/`，全部只读，除探针 patch 外不碰游戏）

| 文件 | 作用 |
|---|---|
| `scan_strings.py` / `probe_plaintext.py` | 扫 `game.dll` 字符串 → 结论：dll 加壳，静态字符串分析不可行 |
| `toc_census.py` | 读 patch/bundle TOC，列出 TypeId=lua 的行 |
| `namehash.py` | MurmurHash64A 名字哈希，把 FileId 反推成资源名 |
| `query_index.py` / `query_lua_assets.py` / `describe_lua_archives.py` | 只读查资产索引，枚举全部 Lua 资产 |
| `extract_lua_assets.py` | 从 `bundles.nxa` 抽出每个 Lua 资源的载荷并读常量表 |
| `probe/build_patch.py` | 造单资源 Lua patch（含 `.stream` / `.gpu_resources` 空旁文件） |
| `probe/set_boot_script.py` | 改 `settings.ini` 的 `boot_script`（标量 / 数组两种形式都认，可回滚） |
| `probe/gen_boot_replay.py` | 生成内嵌原版 `boot` 字节码的回放探针（即 `BootWrapperV1` 的做法） |

---

## 9. 复现步骤

```powershell
$py = 'C:\Users\Administrator\.workbuddy-ai\binaries\python\envs\default\Scripts\python.exe'
$g  = 'D:\SteamLibrary\steamapps\common\Helldivers 2'
$b  = 'E:\Data\source\repos\WpfApp1\tmp\lua-entry-scan\probe'

Copy-Item "$g\data\settings.ini" 'E:\tmp\hd2-mem\settings.ini.orig' -Force

# 1) 造一个自定义入口
& $py "$b\build_patch.py" "$g\data" '9ba626afa44a3aa3.patch_0' 'hd2probe_boot' "$b\hd2probe_require.lua"
& $py "$b\set_boot_script.py" 'hd2probe_boot'

# 2) 启动、看日志
Start-Process 'steam://rungameid/553850'
Get-Content "$env:APPDATA\Arrowhead\Helldivers2\lua-entry-probe.log"

# 3) 还原（必须做）
Stop-Process -Name helldivers2 -Force
Get-ChildItem "$g\data" -Force | ? { $_.Name -like '*.patch_*' } | Remove-Item -Force
Copy-Item 'E:\tmp\hd2-mem\settings.ini.orig' "$g\data\settings.ini" -Force
```

---

## 10. 还没做的（可选）

| 项目 | 价值 |
|---|---|
| 用 `stingray.Application.set_resource_override('lua', ...)` 做入口重定向 | 低——`boot_script` 已经够用 |
| 组件的 Lua 脚本（`AnimationFlowCallbacks` 等）能否当"局内钩子" | 中——但那是局内时机，不是启动入口 |
| `pre_boot_package` 里塞 Lua 会不会被执行 | 低——原版该包里没有 Lua 资源 |
| 把 `hd2/manager/entry` 派发器工程化进管理器 | **高**——见 §6，这是唯一真正新增的能力 |
