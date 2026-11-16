# HellForge 作为「修改 + 调试」工具的可行性

> 2026-11-15。基于官方仓库文档（`SkyeShade/HellForge`，GPL-3.0；SDK 部分 MIT-NA）与 nightly 0.5.0 的 changelog。
> **本文区分「文档确认」与「待核实」**，不把推断写成结论。

---

## 0. 结论先说

| 我们要的能力 | HellForge 是否能给 | 依据 |
|---|---|---|
| **读/写游戏内存、扫字节模式、挂钩任意函数** | ✅ 明确提供 | `hooks.md`：`module_base` / `find_pattern` / `read` / `write` / `write_u32_if` / `hook` / `hook_ex` / `code_matches` / `read_code` |
| **按名字拿游戏函数与全局对象** | ✅ SDK 16+ | `hf_fn_<Name>` / `hf_global_<Name>`，映射表 `mappings/functions.json`、`globals.json`（526 函数 / 108 全局） |
| **改游戏着色器字节并自动重签名** | ✅ dev/0.5.0：data spec 的 **`edit`** | `data.md`(dev)：`stages[].set` 改 stage 内字节，**HellForge 重新签名并保留 stage id**；`stages[].digest` 就是容器 4..19 的校验和 |
| **完整内存转储（回答"载荷有没有被加载"）** | ✅ 存在设置项 | `hooks.md`：崩溃会留 dump，"a small one, or a **full memory dump with the launcher's Crash dump setting**" |
| **不用重启就换着色器再观察** | ⚠️ 部分（内存 `write` 可以；data spec 是会话级） | `live.md`「mods that change while the game runs」是实验特性；内存写入始终可用 |

**总判断：可行，而且是这条线上最合适的工具。** 它把"改文件 → 重新部署 → 重启 → 肉眼看"的循环，
换成"挂钩/读内存 → 当场看日志"。

---

## 1. 前提与版本

- **HellForge 只跑一个游戏 build：Steam build 25480438**。所以按地址找函数**一次有效、长期稳定**（`hooks.md` 原话）。
  代价：**结论必须在零售版（当前更新版本）上复核**才能交付给玩家。
- 版本策略：**用 `0.5.0-nightly.20261114`**（dev 自动构建）。理由：`edit`（改 shader 并重签）在这个 nightly 的
  changelog 里，稳定版 0.4.0 的 `data.md` 里**没有**这一节。
  - 官方对 nightly 的警告：未在游戏内测试、可能改坏配置、下一次 nightly 会覆盖它；**装到独立目录，别覆盖正在玩的安装**。
  - nightly 在 Windows 上**没有 Steam bridge/broker，因此没有 sandbox**（游戏非沙箱运行）。
- 启动参数 **`--no-hit`**：⚠️ **待核实**。我在 README / releases / `hooks.md` / `data.md` / 目录页里都没找到它
  （文档里出现过的启动参数样例是 `--toaster_mode`）。先按"硬性要求"记录，用之前应在
  `HellForge.exe --help` 或 `logs/` 里确认它是否被识别；**不要假定它生效**。
- **本机状态（2026-11-15 10:30 实测）**：C/D/E 三个盘与注册表里**都没有 HellForge 安装**
  （无 `HellForge.exe`、无 `hellforge_modloader.dll`、无卸载项、无快捷方式）。唯一的产物是我被取消的半截下载
  `E:\tmp\hellforge-install\HellForge-Setup.exe`（148 MB，未校验）。所以下面的验证**都还没做**。

---

## 2. 三条能力线（按对我们的价值排序）

### 2.1 `edit`：改游戏着色器 + 自动重签名（**最重要**）

dev 分支的 `data.md` 给出 `edit` 资源类型：

```json
{ "type": "shader_library", "edit": "0xC4DE51720B9CD9DB", "gpu": { "size": 52848 },
  "stages": [{ "at": "0x106C", "digest": "<32 hex>", "set": { "0x1A4": "0B000032..." } }],
  "bytes":  [{ "at": "0x614", "expect": "32329E2A", "set": "<8 hex digits>" }] }
```

| 字段 | 含义 | 对我们的意义 |
|---|---|---|
| `edit` | 被编辑的**游戏资源 id**（这里是 `shader_library`） | 我们要先定位目标 shader 属于哪个 library |
| `gpu.size` / `gpu.sha256` | 该资源 GPU 部分的长度/SHA-256（可选，用来钉住构建） | 等价于我们的"块尺寸必须一致"那道门 |
| `stages[].at` | 该 stage 的 **DXBC 容器在 GPU 部分里的起点** | 我们过去靠"记录尺寸"定位，这里直接给偏移 |
| `stages[].digest` | 容器 **bytes 4..19**（16 字节校验和）的 hex | **就是我一直算的微软定制 MD5** |
| `stages[].set` | 容器内、chunk 表之后的偏移 → 要写入的字节 | **等于我们的"SHEX 定长替换"** |
| `bytes[].at` / `expect` / `set` | 容器之外的字节（如绑定表条目） | 可改"某个 stage 读哪张贴图" |

关键点（原文）：

- **"HellForge signs each edited container again and keeps its stage id, so the game uses it instead of its own."**
  → 我们不必再自己复签；也不必再构造整条 DXBC 记录、对齐五个块尺寸、改 `u32@20`。
- `digest` 与 `expect` 把编辑钉死在**一个构建**上：换构建或偏移错 → 拒绝（这正好防我们手滑）。
- 容器内字节被拒的地方：`at` 落在别的容器里、或改到 stage **id**（容器后 4 字节及其它出现处）。
- 每个资源是**整份替换**，谁在上面谁生效（同资源多 mod 冲突）。

**这条直接改写了我们的生产线**：以前"改文件里的 payload 记录 → 重新部署 → 重启"，现在是
"写一份 data spec → HellForge 构建时重签 → 进游戏"。而且我们已有的
`tools/build_signed_ps.py`（编译 HLSL → 定长 NOP 填充 → 复签）产出的字节**正好就是 `set` 需要的东西**。

### 2.2 挂钩与内存（`hooks.md`）

| 调用 | 用途（对我们的具体用例） |
|---|---|
| `read(addr, buf, n)` | **安全读任意地址**（失败返回 1 不崩）→ 现场扫载荷头 / TocData |
| `write(addr, data, n)` | 改代码或数据（自动改保护 + 刷指令缓存）→ 运行时替换 SHEX 字节 |
| `find_pattern(mod, "48 8B ?? 24")` | IDA 风格字节模式 → 定位我们不知道名字的函数 |
| `hook / hook_ex(mod, target, detour, &orig[, prio])` | 挂钩，**多 mod 共享一条链**（不互相覆盖） |
| `code_matches(addr, pattern)` | 按"HellForge 挂钩之前"的字节比对 → 防版本漂移 |
| `hf_fn_<Name>` / `hf_global_<Name>` | 按名字拿函数/全局（类型由编译器检查） |
| `hf_on_game_thread(mod, cb, user)` | 必须在游戏线程做的事 |

还有两条实践约束（会影响我们的写法）：
- **挂钩要在 `HF_EVENT_GAME_LOADED` 之后**（之前 game.dll 代码还没就绪）；detour 可能在**任意工作线程**跑。
- 代码补丁要**整条指令替换**，且在启动期做；两个 mod 补同一段字节会互相破坏且无人报告。

### 2.3 F8 编辑器 + 完整转储

- F8 是内置编辑器（改数据行/字段），**不是**给我们看材质/着色器的工具；但它的"live edit"机制说明运行时可改数据。
- **启动器有 Crash dump 设置，可选完整内存转储** —— 这条直接解决了我们之前"15 MB 转储抓不到载荷页"的问题：
  只要开启完整转储，`tools/dmp_scan.py` 就能在**全地址空间**里扫载荷头 / 材质 TocData / DXBC 容器。
  更进一步：有了 2.2 的 `read`，其实**不必等崩溃**，跑着就能扫。

---

## 3. 三个问题的答案与判据

### Q1「注入的载荷到底有没有被引擎加载？」

**做法**（在 HellForge 里写个探针 mod，`HF_EVENT_GAME_LOADED` 之后）：
1. `find_pattern("game.dll", <载荷头特征>)` 或直接遍历可读区，按我们的指纹匹配
   （`+0=0xb3`、`+4=材质id高32`、`+16=1`）；
2. 命中则 `read` 出 `+0..+32`，与文件里的载荷头逐字段比对（`+4`、`+8`、`+12`）；
3. 再找该材质的 TocData 指纹（`288,1,24` + `EndOffset/undat2/parent/NumTextures`），核对 `undat2` 是否等于载荷长度。

**判据**：
- 命中且 `+4` 等于我们注入的 id → **载荷被加载了**；那么"不显示"的原因在 **记录选择/着色器创建**，不在加载。
- 完全找不到 → **引擎没加载这份载荷**（材质→载荷的解析没走到我们这份）；这时改多少字节都没用。
- 找到一个 `+4` 是别的 id（例如 `ef9b3fc2`）的载荷 → 引擎用了**别的**载荷，印证"载荷被忽略"。

**这条能直接回答 MourningVeil 那个会话的 E2/E3 疑点。**

### Q2「能不能绕过文件层，直接改着色器并重签名？」

**能**，两条路：
- **静态**：data spec 的 `edit`（§2.1）—— HellForge 构建期重签，最干净，适合交付给玩家。
- **动态**：`write()` 直接把新字节写进内存里的 stage，适合**一次会话内快速试错**（不用重启、不用改 mod 文件）。

**判据**：改完立刻进游戏看效果；
- 画面变 → 字节写对了；
- 崩溃在 `dxilconv` → 我们写的是非法/越界读的着色器（我们在零售版踩过：**读 ISGN 未提供的分量**会让 dxilconv 空指针）；
- 无变化 → 目标 stage 不是可见 pass（我们在 Milfy 上踩过：62,652 那组游戏内不可见）。

### Q3「能不能在运行时替换某个 pass 的 SHEX 并观察引擎反应？」

**能**，而且这是 HellForge 最大的价值：`read` 取原字节 → `build_signed_ps.py` 产出新字节 → `write` 覆盖 →
当场看是否崩/是否生效，**一次会话里可以试十几个变体**。
配合 `hook` 在 `dxilconv`/D3D12 调用前后打日志（`hook_ex` 拿 HRESULT），就能把"为什么被拒"变成日志，而不是猜。

---

## 4. 落地清单（按顺序，前两步不需要写代码）

| # | 动作 | 验收 |
|---|---|---|
| 1 | 装 **nightly 0.5.0-nightly.20261114** 到独立目录（不要覆盖正在玩的安装） | 启动器左下角显示该版本号 |
| 2 | 打开 **Settings → Crash dump = full（完整内存转储）** | 设置里有该项；下次崩溃的 dump 远大于 15 MB |
| 3 | 确认 **`--no-hit`**：`HellForge.exe --help` 或在 `logs/` 里看是否被识别 | 有明确回显；若不存在，记录下来别当它生效 |
| 4 | 装 mod 工具（Mods 页 **Install mod tools**，pinned Zig + CMake/Ninja） | `tools/` 出现编译器 |
| 5 | 写**探针 mod**（C，约 200 行）：`find_pattern` + `read` 扫载荷头/TocData，把结果写进 `logs/sessions/<s>/mod-<id>.log` | 日志里能看到引擎当前加载了哪些载荷头 |
| 6 | 用探针回答 Q1（MourningVeil 的 `ab344c46`） | 日志给出"加载/未加载" |
| 7 | 试 `edit` 静态改 shader（先把我们已知能工作的 `famA_alpha_only` 那套字节搬进 spec） | 画面与零售版一致地出现"贴图驱动半透明" |

---

## 5. 风险与边界（必须写进交付说明）

1. **构建不一致**：HellForge 是 **25480438**，零售版更新。所有"地址/偏移/digest"结论**不能直接用于零售**；
   `edit` 的 `digest` 机制本身也说明它按构建绑定。
2. **nightly 未测试**：官方明说可能改坏安装/配置；用独立目录、别在有进度的安装上升级。
3. **nightly 无 sandbox**（Windows 缺 Steam bridge）：安全性比稳定版低，只在自有机器上跑自己的 mod。
4. **mod sync 视 `edit` 为 raw data**：朋友要下载需信任/验证，别指望自动分发。
5. **反作弊**：零售版有 EAC。HellForge 是离线副本、网络被墙，**不得**把这条路上的做法用于零售联机。
6. **`--no-hit` 未核实**（见 §1）。

---

## 6. 与现有工具链的衔接

| 我们已有的 | 在 HellForge 里的角色 |
|---|---|
| `tools/build_signed_ps.py` | 产出 `stages[].set` 需要的字节（编译 + 定长 NOP 填充） |
| `tools/gate_build.py`（G1–G4） | G1/G4 变成"HellForge 会替我们做"；**G3（输入分量 ⊆ ISGN 掩码）仍然必须自己做** —— 它防的是 dxilconv 崩溃 |
| `tools/dmp_scan.py` | 完整转储下继续可用；有了 `read` 后可以改成"在线扫" |
| `hd2-custom-shader-runbook-20261115.md` | 零售版路线（文件层 payload）仍然有效；HellForge 是**加速验证**的另一条路 |

---

## 7. 命名 API 的实测边界（本轮新增，已用仓库源码核对）

为了让"探针 mod"少走弯路，我把官方仓库浅克隆到本地（`E:\tmp\hellforge-src`，sparse: mappings/docs/examples/native/templates），
逐项核对了两张映射表：

| 文件 | 内容 | 与材质/着色器/载荷的关系 |
|---|---|---|
| `mappings/globals.json`（10.8 KB，`build: 25480438`） | **17 个**命名全局：`Game`、`PlayerManager`、`LocalUser`、`GameMode`、`EntityManager`、`HealthManager`、`MotionManager`、`ExplosionQueue`、`ProjectileSystem`、`CombatQueues`、`ScoreManager`、`WielderManager`、`InventoryManager`、`Destructibles`、`DestructionRecords` | **一个都没有**。全是玩法对象，没有材质/着色器/包/载荷管理器 |
| `mappings/functions.json`（11.8 KB） | 只有**少量**带完整签名的函数（如 `ConsumeDamageEvent`），注释说明完整签名才允许 hook | **没有**材质/着色器/贴图/载荷加载类函数 |
| `mappings/hd2r-keys.json`（≈618 处 material/texture 字面） | HD2R Editor 的 preset 键 → `component|…|字段`、`number` 等 | 命中的是**组件字段与数字**，`Opacity` / `Emissive` / `Metallic` / `Roughness` / `Albedo` / `SclarField` **全部 0 命中** ⇒ 编辑器**不暴露材质参数** |
| `mappings/references.json` | package id → package 名（如 `packages/generated/loadout/...`） | 可用于**定位包**，不是材质 |

**⚠️ 上一版这里写错了，已更正。** 我当时用的是 `main` 分支的 `mappings/functions.json`（**11.8 KB，只有少数带完整签名的函数**），
据此得出"没有材质/着色器 API"。**`dev` 分支的映射表才是完整的：552 个函数 / 108 个全局**，其中确有整套材质、资源、包 API。

### 7.1 dev 分支的材质/资源/包 API（实测条目）

| 函数 | rva | 签名要点 | 含义（原文摘要） |
|---|---|---|---|
| **`PackageHasLoaded`** | `0x321330` | `uint32_t (uint64_t id)`，可 hook | **"1 when the engine knows the package and every part of it is loaded (part state 4), else 0"** |
| `RequestPackages` | `0x12FE350` | 只可调用 | 走 `hf_global_PackageSystem`（RefcountedPackageSystem） |
| `PackageSystem`(全局) | `0x347CEB0` | `map` + `capacity` | 引用表，**按 package id（u64）为键**的 16 字节条目 |
| **`MaterialReinitializeShader`** | `0x30F420` | `void (void* material)`，可 hook | **"Queues a render command that rebuilds a material's shader"** |
| `MaterialSetShaderPassFlag` | `0x30F150` | `void (void* material)` | `material +0x40` = 它的 pass flag 位 |
| `MaterialSetTexture` | `0x30F1B0` | `void (void* material, …)` | 运行时改材质贴图槽 |
| `MaterialSetScalar` / `Vector2/3/4` / `Matrix4x4` | `0x4F33D0` / … | `void (void* material, …)` | 运行时改材质参数（标量/向量/矩阵） |
| `MaterialSetResource` | — | `void (void* material, …)` | 运行时改材质资源 |
| `ResourceManagerGet` / `ResourceManagerCanGet` | `0x5F2290` / … | `void*` / `uint8_t` | 按资源类型哈希/资源取对象；`EngineCore +0x3F8` |
| `CanGetResource` | `0x321930` | `uint8_t (…)` | 资源是否可得 |
| `EngineRenderStep` / `UnitMesh` / `UnitNumMeshes` / `UnitResourceName` | — | — | 渲染步进 / 单位网格与资源名 |
| `FindPackage` / `ReleasePackages` / `MasteryPackageOf` / `StratagemPackages` | — | — | 包查找与释放 |

**由此修正 §3-Q1 的做法（比堆扫描更硬）**：

1. **先用 `PackageHasLoaded(package_id)`**：我们注入的材质与载荷都在某个 `data/<id>` 归档里
   （`patch_N` 的 bundle id）。它返回 1 ⇒ 那个包整体已加载；返回 0 ⇒ 引擎根本没把这份数据装进来。
2. 再用 `ResourceManagerGet` / `CanGetResource` 按**资源 id** 问：引擎是否认得 `0xab344c4630d26a94` 这个材质。
3. 都通过却仍不显示 ⇒ 问题在**记录/pass 选择或着色器创建**，不在加载。这时才用堆扫描（§9）看载荷头。
4. 想直接盯"材质重建着色器"的瞬间：hook **`MaterialReinitializeShader`**，在 detour 里读 `material +0x40`（pass flag 位）
   并打日志 —— 这比事后猜"为什么这个 pass 没画"信息量大得多。

**仍然成立的部分**：`hf_global_` 的 108 个全局里，与材质/着色器直接相关的只有 **`PackageSystem`**（`HellpodPayloadManager` 是空投舱的，不是 shader payload）；

**另一条推论（新）**：既然有 `MaterialSetScalar` / `MaterialSetVector*` / `MaterialSetTexture`，
**"调材质参数看效果"也不必重新打包了** —— 可以在运行时把 `Opacity` / `EmissiveColor` / `Metallic` 之类直接写进去，
一次会话里扫参数。这比我们在零售版上"改 TocData → 部署 → 重启"快一个数量级。
（注意：`hd2r-keys.json` 说明 **F8 编辑器界面**不暴露这些参数，但 **SDK 层可以**，是两件事。）

可行路线只有两条：

1. **`find_pattern` + `read`/`write` + `hook`（按地址）** —— 用我们已有的指纹自己找：
   - 载荷头：`+0=0xb3`、`+4=id 高32`、`+16=1`
   - 材质 TocData：`+0=288, +4=1, +8=24`（随后 `EndOffset/undat2/parent/NumTex`）
   - DXBC 容器：`"DXBC"` + version==1 + 合法块表
   （这套指纹已在 `tools/dmp_scan.py` 里实现，可直接搬进 C 探针。）
2. **data spec 的 `edit`**（改 shader stage 字节 + HellForge 重签），见 §2.1。

### 另一处缺口：`edit` 没有示例 mod

`dev` 分支的 `examples/mods` 里**没有**任何演示 `edit` / `shader_library` 的样例（只有 golden-liberator、cube-gun、model-carbine 等模型/贴图类）。
仓库里也没有 `shader_library`、`"stages"`、`"digest"` 的其它出现。
⇒ **我们会是这个特性的第一批使用者**，出问题时要按 `debugging.md` 收集 `logs/` 再反馈；不要假定它已广泛验证。

## 8. 本轮状态与剩余工作

- 安装：**仍不存在**（C/D/E 深度 4 全盘复查，无 `HellForge.exe` / `hellforge_modloader.dll`）。
- 已完成：能力线梳理（§2）、三问判据（§3）、命名 API 边界（§7）、给另一会话的答复草稿。
- 下一步（**依赖安装**）：
  1. 装 nightly 0.5.0-nightly.20261114 到独立目录 → 读 `app/sdk/docs/modding` 与 `app/sdk` 头文件；
  2. 确认 `--no-hit` 是否存在；
  3. 开启完整 Crash dump；
  4. 写探针 mod（`find_pattern` + `read`，用上面的指纹）。

---

## 9. 探针 mod 的最小骨架（签名取自 `app/sdk/hellforge_mod.h`，非虚构）

### 9.1 一个必须先纠正的细节

`hooks.md` 说 `find_pattern(module, pattern)` 是"在模块镜像里找"——**头文件原文确认**：

> `/* The first match of an IDA-style byte pattern ("48 8B ?? 24 ?? E8") in the module's image, or 0. */`

⇒ **载荷头 / 材质 TocData / DXBC 容器都在堆上，不在模块镜像里**，所以**不能**用 `find_pattern` 找它们。
必须自己走一遍地址空间（`VirtualQuery`）+ `read` 逐块扫。这也说明 §2.2 里"用 find_pattern 扫载荷头"那句要按此修正。

### 9.2 manifest（照示例）

```json
{
  "format": 1,
  "id": "yourname.hf-probe",
  "name": "HellForge Probe",
  "version": "0.1.0",
  "description": "Scans the game's heap for material payload headers, TocData and DXBC containers.",
  "gameplay": false,
  "code": { "dll": "hf_probe.dll", "sdk": 16 }
}
```

> `"sdk": 16` 是因为要用 `hf_fn_`/`hf_global_` 系列（本探针其实只用到 SDK 1 的 `read`/`log`/`on`，写 16 也无害）。

### 9.3 C 骨架（关键部分）

```c
#include <windows.h>
#include "hellforge_mod.h"

static const HfApi* hf;

/* 指纹：载荷头 +0=0xb3(179), +16=1；材质 TocData +0=288,+4=1,+8=24 */
static int looks_like_payload_header(const uint8_t* p) {
    uint32_t u0, u4, u16;
    memcpy(&u0, p, 4); memcpy(&u4, p + 4, 4); memcpy(&u16, p + 16, 4);
    return u0 == 0xb3 && u16 == 1 && u4 != 0;
}

static void scan_heap(HfMod* mod) {
    MEMORY_BASIC_INFORMATION mbi;
    uintptr_t addr = 0;
    uint8_t buf[64 * 1024];
    int found = 0;

    while (VirtualQuery((LPCVOID)addr, &mbi, sizeof mbi)) {
        uintptr_t base = (uintptr_t)mbi.BaseAddress;
        SIZE_T size = mbi.RegionSize;
        int readable = (mbi.State == MEM_COMMIT) &&
                       !(mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) &&
                       (mbi.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_EXECUTE_READ |
                                       PAGE_EXECUTE_READWRITE | PAGE_WRITECOPY));
        if (readable && size >= 0x400) {
            for (SIZE_T off = 0; off + 32 < size; off += sizeof buf) {
                SIZE_T chunk = min(sizeof buf, size - off);
                if (hf->read(base + off, buf, chunk) != 0) continue;   /* 失败不崩，跳过 */
                for (SIZE_T i = 0; i + 32 <= chunk; i += 4) {
                    if (looks_like_payload_header(buf + i)) {
                        uint32_t idhi; memcpy(&idhi, buf + i + 4, 4);
                        hf->log(mod, "payload header at 0x%llx id_hi=0x%08x",
                                (unsigned long long)(base + off + i), idhi);
                        if (++found >= 64) return;   /* 别刷爆日志 */
                    }
                }
            }
        }
        addr = base + size;
        if (addr < base) break;
    }
    hf->log(mod, "scan done: %d payload header(s)", found);
}

static void OnGameLoaded(HfMod* mod, HfEvent ev, void* user) { scan_heap(mod); }

HF_MOD_EXPORT int hellforge_mod_init(const HfApi* api, HfMod* mod) {
    hf = api;
    if (api->version < 1) return 1;
    api->on(mod, HF_EVENT_GAME_LOADED, OnGameLoaded, NULL);
    return 0;
}
```

用法：进游戏 → 看 `logs/sessions/<session>/mod-yourname.hf-probe.log`。
判据见 §3-Q1：**日志里出现 `id_hi=0xab344c46` 就等于"引擎加载了那份载荷"**；一条都没有就是没加载。

### 9.4 未做

- 没有编译过（本机无 HellForge 的 pinned 工具链）。
- 没有跑过（本机无 HellForge）。
- `read` 的失败语义、`VirtualQuery` 在 sandbox 下的行为，都需在实机确认。

---

## 10. ★ 官方自定义着色器宿主（安装后新发现，最重要的一节）

`D:\Program Files\HellForge\app\sdk\` 里有 `shaders/`（4 个 `.hlsli`）与 `docs/modding/shaders.md`（131 行）。
**HellForge 直接支持"mod 自带编译好的像素着色器"**：它**复制一份游戏材质**、把你的 shader 放进副本、再给你一个绘制它的 unit。

### 10.1 怎么用（data spec 的一个资源）

```json
{
  "format": 1,
  "patches": [{
    "archives": ["f4dc2361985c3026"],
    "resources": [
      { "shader": "decal",       "name": "yourname/mod/rings", "dxbc": "shaders/rings.dxbc" },
      { "shader": "transparent", "name": "yourname/mod/glow",  "dxbc": "shaders/glow.dxbc",
        "mesh": { "obj": "models/column.obj" } }
    ]
  }]
}
```

| 字段 | 含义 |
|---|---|
| `shader` | 宿主：`decal` / `transparent` / `refract` |
| `name` | 你的资源路径，同时是**要 spawn 的 unit 名**；`<name>/material` 是它的材质，`transparent` 还会建 `<name>/root` |
| `dxbc` | 编译好的 shader（mod 目录里的 `.dxbc`） |
| `mesh` | OBJ；`transparent` / `refract` **必需**，`decal` 不需要 |

### 10.2 三个宿主（原文）

| 宿主 | 画什么 | 读什么 | 阶段空间 |
|---|---|---|---|
| `decal` | 画在 1 m 盒子里已存在的东西上（地面、敌人），特效跟着地形走 | 深度缓冲 + 法线 | 11,688 B |
| `transparent` | 网格**向场景加光**，双面 | 深度缓冲 | 63,564 B |
| `refract` | 网格**替换它后面的画面**，仅前面 | 完成的场景 + 深度缓冲 | 61,276 B |

### 10.3 写与编译

```hlsl
#include "hellforge_shader_decal.hlsli"

Out main(In i)
{
    HfBox b = hf_box(i);
    float ring = smoothstep(0.05, 0.0, abs(b.radius - (1.0 - frac(hf_time() * 0.5))));
    return hf_decal(float3(0,0,0), 0.5 * ring, float3(10,3,30) * ring);
}
```

```text
fxc /T ps_5_0 /E main /I <shaders folder> /Fo shaders\rings.dxbc shaders\rings.hlsl
```

**必须保留反射**（不要 `/Qstrip_reflect`）—— 我们的 `build_signed_ps.py` 用 fxc 默认参数，本来就是保留的。

### 10.4 可用的东西

| 头文件 | 提供 |
|---|---|
| `hellforge_shader.hlsli`（每个宿主都有） | `hf_time`、`hf_delta_time`、`hf_camera`、`hf_camera_forward`、`hf_target_size`、`hf_view_depth`、`hf_weight` |
| `_decal.hlsli` | `hf_box`、`hf_box_size`、`hf_progress`、`hf_emissive`、`hf_decal` |
| `_transparent.hlsli` | `hf_uv`、`hf_around`、`hf_facing`、`hf_scene_depth`、`hf_soft`、**`hf_opacity`**、`hf_transparent` |
| `_refract.hlsli` | `hf_uv`、`hf_normal`、`hf_screen`、`hf_scene`、`hf_bend`、`hf_strength`、`hf_refract` |

**关键限制（必须记牢）**：
> "A shader has only the bindings of the game's stage, at the stage's registers. **There are no textures or constants of your own**, and anything else is refused."

也就是说：**不能采样自己的贴图**。透明靠**程序化**（噪声、`hf_facing` 菲涅尔、`hf_time` 动画、`hf_scene_depth` 软过渡）。
`transparent` 宿主里 `hf_opacity()` 读的是 `c_per_instance[23].x`（材质的 opacity_mult），而 mod 可以用 `MaterialSetScalar` 之类去改它。

### 10.5 被拒的情形（HellForge 会拿你的 dxbc 和游戏的 stage 逐项比）

- 不是 `ps_5_0`，或用了 doubles 等可选 GPU 特性；
- **输入不是该 stage 的、或不在该寄存器、或比 stage 的大** ← 和我们零售版踩的 G3 同一个坑；
- **输出没写全**（每个输出都必须写，头文件的输出函数会写全）；
- 绑定了 stage 没有的寄存器/类型，或 cbuffer 比 stage 的大，或 cbuffer 外有全局变量；
- **文件里没有反射**。

### 10.6 怎么让它出现在画面里

> "The SDK has no call for spawning a unit yet. For now a code mod does it on the game thread through game memory (hooks.md): the world's spawn call, with the resource's name hashed and the unit's package loaded."

⇒ **还差最后一步**：写个 code mod，在游戏线程用世界的 spawn 调用生成这个 unit（资源名哈希 + 包已加载）。
这正是 `hooks.md` 那套（`hf_on_game_thread` + hook/调用）。

### 10.7 对我们意味着什么

| 我们原本的目标 | 在 HellForge 里 |
|---|---|
| 自带编译的像素着色器 | ✅ 一等公民（data spec `shader`） |
| 真半透明 | ✅ `transparent` 宿主（加光式 alpha） |
| 折射/玻璃感 | ✅ `refract` 宿主 |
| 贴花/镂空环 | ✅ `decal` 宿主（1 m 盒，随地形） |
| **贴图驱动的逐像素 alpha** | ❌ **做不到**（不允许自带贴图/常量）—— 只能用程序化图案 + 材质自带的 `opacity_mult` |
| 时间动画（呼吸/流动） | ✅ `hf_time` / `hf_delta_time` |

**结论**：HellForge 是我们**做效果原型**最快的地方（写 HLSL → fxc → 放进 spec → 进游戏），
但**"贴图驱动的逐像素半透明"这一条只能在零售版的文件层 payload 路线里做**（我们已经在 `famA_alpha_only` 上做成了）。
两者是互补的，不是替代关系。
