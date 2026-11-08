# HD2 PhysBone 嵌入式 Patch 容器协议

状态：`HD2PBX01` 提案 / 实验性实现

本文用于 HD2 PhysBone 插件作者和 Mod 作者。目标是把：

- `lua_units.txt`
- `hd2_ib_needle.bin`
- `hd2_spring_rig.bin`

作为一个 Mod 的附加数据，嵌入对应 Patch 组的
`<archive>.patch_<n>.gpu_resources` 文件末尾，而不是额外安装到
`bin\hd2physbone`。

这不是把动态骨骼数据伪装成 GPU Asset。TOC 不会增加任何条目，游戏只会看到
原有的 TOC 引用范围；尾部容器由 HD2 PhysBone 原生插件自行读取。

## 已完成的兼容性实验

我们使用 Karin 动态骨骼样本进行了真实游戏测试：

1. 保持 `9ba626afa44a3aa3.patch_2` 完全不变；
2. 保持 `.stream` 完全不变；
3. 只在 `.gpu_resources` EOF 追加 64 字节对齐填充和测试容器；
4. 测试容器中放入三个无效的模拟资源。

游戏启动、进入游戏和模型显示均正常，没有出现过时资源报错或崩溃。

这证明在当前测试版本和 loose Patch 文件模式下，游戏不会把 TOC 未引用的
`.gpu_resources` 尾部当成普通 Asset 解析。它仍然不是官方公开的 Patch 扩展规范，
未来版本、DSAR 压缩文件和其他加载器仍需单独验证。

样本事实：Karin 原始 GPU sidecar 的长度为 `93,907,464` 字节，TOC 引用的最大
GPU 结束位置正好也是该值，因此测试数据是追加在文件末尾，而不是利用已有空洞。

## 容器使用边界

第一版只支持以下载体：

```text
<archive>.patch_<n>.gpu_resources
```

并且载体必须是 loose、未压缩的原始 sidecar。

暂不支持：

- DSAR 压缩 sidecar；
- bundled archive / `bundles.*.nxa`；
- 把数据追加到 `.patch_<n>` 的 TOC 文件本身；
- 把数据追加到某个 Asset 已声明的 GPU 范围内部。

最重要的规则：**绝不能修改任何 TOC Entry 的 `GpuResourceSize`。**

## 容器布局

所有整数均为 little-endian。容器长度包含 header、索引、数据和 footer。

### Header：48 字节

| 偏移 | 类型 | 含义 |
|---:|---|---|
| `0x00` | 8 bytes | `HD2PBX01` |
| `0x08` | `u32` | 协议版本，目前为 `1` |
| `0x0C` | `u32` | flags，bit 0 表示 footer 字段保存原始前缀长度 |
| `0x10` | `u64` | 容器总长度 |
| `0x18` | `u64` | 索引相对容器起点的偏移，当前为 `48` |
| `0x20` | `u64` | 索引长度 |
| `0x28` | `u32` | 记录数量 |
| `0x2C` | `u32` | 保留，写零 |

### Record

每条记录由固定部分和 UTF-8 名称组成：

| 偏移 | 类型 | 含义 |
|---:|---|---|
| `0x00` | `u16` | 名称字节长度 |
| `0x02` | `u16` | record flags，目前为 `0` |
| `0x04` | `u64` | 数据相对容器起点的偏移 |
| `0x0C` | `u64` | 数据长度 |
| `0x14` | 32 bytes | 数据 SHA-256 |
| `0x34` | `u32` | 重复保存的名称字节长度，必须与前者一致 |
| `0x38` | bytes | UTF-8 名称 |

当前建议只使用以下三个固定名称：

```text
lua_units.txt
hd2_ib_needle.bin
hd2_spring_rig.bin
```

未知名称应被插件安全忽略，而不是当作可执行内容处理。

### Footer：56 字节

Footer 位于载体文件 EOF：

| 偏移 | 类型 | 含义 |
|---:|---|---|
| `0x00` | 8 bytes | `HD2PBFT1` |
| `0x08` | `u64` | flags bit 0 开启时为容器前的原始前缀长度 |
| `0x10` | `u64` | 容器总长度 |
| `0x18` | 32 bytes | 容器 SHA-256 |

容器摘要的计算方式是：先将最后 32 字节置零，再对整个容器计算 SHA-256。
插件应先读取 EOF 的 56 字节 footer，使用 `file_size - container_length` 计算
容器起点；不要依赖写入时的绝对文件偏移，因为 Patch 可能被复制或重排。

写入位置应为：

```text
原有 GPU 文件末尾
→ 64 字节对齐零填充
→ HD2PBX01 容器
```

不要把数据放在 TOC 引用范围之间的空洞中。空洞的归属不稳定，任何重排都可能覆盖它。

## 配套 Python 工具

管理器仓库提供了只依赖 Python 标准库的工具：

[`tools/hd2physbone_patch_container.py`](../../tools/hd2physbone_patch_container.py)

它不解析三个资源的内部格式，只负责安全地原样封装、提取、校验和移除。

Python 3.8 或更高版本即可运行。

### 合并 / 封装

默认输出一个新的 Patch 副本，不修改来源文件：

```powershell
python tools\hd2physbone_patch_container.py pack `
  "C:\path\to\9ba626afa44a3aa3.patch_2" `
  "C:\path\to\Karin_DR03" `
  --output-dir "C:\path\to\out"
```

资源目录中可以直接放三个文件，也可以包含一个唯一的下级目录。工具会拒绝同名
资源不唯一的情况。

也可以逐个指定资源：

```powershell
python tools\hd2physbone_patch_container.py pack `
  "C:\path\to\9ba626afa44a3aa3.patch_2" `
  --lua-units "C:\path\to\lua_units.txt" `
  --needle "C:\path\to\hd2_ib_needle.bin" `
  --spring-rig "C:\path\to\hd2_spring_rig.bin" `
  --output-dir "C:\path\to\out"
```

明确需要原地写回时才使用：

```powershell
python tools\hd2physbone_patch_container.py pack `
  "C:\path\to\9ba626afa44a3aa3.patch_2" `
  "C:\path\to\resources" `
  --in-place
```

### 查看和校验

```powershell
python tools\hd2physbone_patch_container.py inspect `
  "C:\path\to\9ba626afa44a3aa3.patch_2"
```

`inspect` 会显示：

- GPU 文件长度；
- TOC 引用的最大 GPU 结束位置；
- 未引用尾部长度；
- 容器版本、索引、记录和 SHA-256。

### 拆分 / 提取

```powershell
python tools\hd2physbone_patch_container.py unpack `
  "C:\path\to\9ba626afa44a3aa3.patch_2" `
  --output-dir "C:\path\to\extracted"
```

`split` 是 `unpack` 的别名。

### 移除容器

```powershell
python tools\hd2physbone_patch_container.py strip `
  "C:\path\to\9ba626afa44a3aa3.patch_2" `
  --output-dir "C:\path\to\raw-out"
```

对本工具生成的容器，`strip` 会去掉容器和它生成的对齐填充，恢复原始 GPU 前缀。

## ReShade / addon64 侧读取方案

### 能不能读取

可以，但读取职责应放在原生 `.addon64` 中，而不是 ReShade FX shader 中。

官方 ReShade 文档将 add-on 定义为运行在目标进程内的 DLL，可以在初始化和事件回调
中执行自定义工作；完整 Add-on Support 版本才会加载这类 add-on。ReShade API 本身
主要抽象渲染资源和事件，并不提供一个专门的 HD2 Patch 文件读取器，但原生 DLL
可以正常使用 Windows 文件 API。

当前 `HD2PhysBoneProbe.addon64` 的 PE 导入表已经包含：

```text
CreateFileW
ReadFile
FindFirstFileExW
FindNextFileW
GetFileSizeEx
GetModuleFileNameW
SetFilePointerEx
```

所以当前插件已经具备读取和扫描文件的底层能力；新增工作主要是识别 EOF footer、
验证容器并把记录交给现有 PhysBone 数据加载流程。

### 推荐读取流程

1. 在 `AddonInit` 或插件自己的初始化线程中确定游戏根目录。
2. 首选 `GetModuleFileNameW(nullptr, ...)`，取游戏进程可执行文件所在目录；不要依赖
   当前工作目录。
3. 枚举：

   ```text
   <game>\data\*.patch_*
   ```

   严格排除 `.stream` 和 `.gpu_resources` 本身作为基础 Patch 文件。
4. 对每个基础 Patch 查找同名 `.gpu_resources`。
5. 不要读取整个 GPU 文件。先 `GetFileSizeEx`，再从 EOF 回读 56 字节 footer。
6. 检查 footer magic、容器长度和边界，使用 `file_size - container_length` 找到容器起点。
7. 读取容器 header 和索引，检查：

   - 版本是否支持；
   - 记录数量和名称长度是否在上限内；
   - 所有偏移是否位于容器内部；
   - 每条记录的 SHA-256 是否匹配；
   - 容器总 SHA-256 是否匹配。

8. 将 `lua_units.txt`、needle 和 rig 数据载入插件自己的内存结构。
9. 继续使用 `lua_units.txt` 中的 Unit ID 作为运行时关联依据；不要根据文件名猜测
   Unit 所属对象。
10. 相同内容的重复记录可以按 hash 去重；相同 Unit ID 但数据不同则记录明确冲突，
    不要静默随机覆盖。

### 建议的插件内部结构

不要让 solver 直接依赖“数据一定在 `bin\\HD2PhysBone`”。建议先抽出一个纯数据层：

```cpp
struct PhysBoneResourceSet
{
    std::vector<uint8_t> lua_units;
    std::vector<uint8_t> inverse_bind_needles;
    std::vector<uint8_t> spring_rig;
    std::wstring source_label;
    std::array<uint8_t, 32> source_hash;
};

bool LoadResourceSetFromDirectory(const std::filesystem::path &directory,
                                  PhysBoneResourceSet &out);
bool LoadResourceSetFromGpuTrailer(const std::filesystem::path &gpu_sidecar,
                                   PhysBoneResourceSet &out);
```

两个 loader 都只负责产出同一种 `PhysBoneResourceSet`，现有 needle/rig/lua 解析器和
solver 不需要知道数据来自目录还是 Patch 尾部。这样可以保留旧目录安装方式作为兼容
回退，也能避免把“读取 Patch 文件”的逻辑散落进每帧物理代码。

插件侧的读取骨架大致如下：

```cpp
auto game_root = ParentDirectoryOf(GetModuleFileNameW(nullptr));
auto data_root = game_root / L"data";

for (const auto &patch_toc : EnumerateStrictPatchTocs(data_root))
{
    auto gpu = patch_toc;
    gpu += L".gpu_resources";

    // 仅 seek 到 EOF 读取 56-byte footer；没有 HD2PBFT1 就跳过。
    if (auto embedded = TryReadHd2PbxAtEof(gpu))
        PublishResourceSet(ParseAndVerify(*embedded));
}
```

`TryReadHd2PbxAtEof` 应使用 `CreateFileW`、`GetFileSizeEx`、`SetFilePointerEx` 和
`ReadFile` 做范围读取；它不需要、也不应把整个 `.gpu_resources` 映射或复制到内存。

### 性能和线程注意事项

- 文件扫描和读取应在初始化线程或后台线程完成，不要在每帧回调中打开文件。
- 解析完成后发布不可变快照，帧循环只访问内存数据。
- `CreateFileW` 使用只读和适当的共享标志，避免与游戏文件句柄互相阻塞。
- 读取失败时插件应记录诊断并跳过该容器，不能因为一个损坏的 Mod 让游戏崩溃。
- 不要把 90 MB 级别的整个 GPU sidecar 复制到内存；只读取 EOF 容器的长度。

### ReShade API 与普通文件 API 的分工

```text
ReShade addon API
    ├─ AddonInit / AddonUninit
    ├─ 插件菜单和状态显示
    └─ 游戏/渲染事件回调

Windows 文件 API
    ├─ 定位游戏根目录
    ├─ 扫描 data 中的 Patch 组
    └─ 从 GPU sidecar EOF 读取 HD2PBX 容器

PhysBone runtime
    └─ 使用解析后的 lua_units / needle / spring_rig 数据
```

ReShade FX 只适合 shader 和渲染参数，不应承担二进制容器解析、文件扫描或动态骨骼
数据管理。

## Lua 合并时的关键问题

Lua 入口只能存在一份。管理器合并多个 Lua Patch 后，会撤下原始 Lua Patch，部署
一个新的合并 Patch。

因此，如果动态骨骼资源和 Lua 入口位于同一个 Patch 组，不能只合并 Lua 文本而丢掉
容器，必须把源 Patch 组中的 HD2PBX 容器一并转移到生成的合并 Patch，或者保留一个
仍然启用的非 Lua 资源载体。

推荐优先采用前者：

```text
Lua A + PhysBone A ─┐
Lua B + PhysBone B ─┼─> 一个合并 Lua Patch + 一个合并 HD2PBX 容器
Lua C + PhysBone C ─┘
```

多个容器合并时：

- 相同文件内容可以去重；
- 不同 Unit ID 的记录直接合并；
- 同一 Unit ID 或同一资源名的不同内容必须报冲突；
- 合并顺序应记录在日志中，不能依赖不稳定的文件枚举顺序。

## 管理器侧后续要求

当前协议可以先由作者脚本和插件独立使用，但管理器正式支持时应把容器作为：

```text
PatchCompanionResource / OpaquePatchExtension
```

而不是普通 AssetKey 或 GPU payload。

需要覆盖这些流程：

- 导入时识别并保存容器事实；
- 普通部署时随 Patch 组复制；
- Patch 重编号时随组移动；
- Lua 合并时迁移或合并容器；
- 修复 Patch、重打包资源、生成装饰和替换护甲时保留或明确拒绝丢弃；
- 导出 Mod 时自动包含容器；
- 删除 Patch 组时随同删除，不留下 `bin\hd2physbone` 孤儿文件。

任何会重新生成 `.gpu_resources` 的 writer 都必须显式接收并写回 opaque extension，
因为只按照 TOC Entry 重建 sidecar 会自然丢掉 EOF 容器。

## 兼容性和安全门

- 没有容器的普通 Mod 必须保持原有行为。
- 旧版插件看不懂容器时，应报告“需要支持 HD2PBX01 的插件版本”，不能假装动态骨骼
  已启用。
- 容器不是加密格式；它只负责封装和校验，不能当作安全边界。
- 拒绝绝对路径、`..`、符号链接语义和超大长度。
- 不执行容器中的任何内容。
- 不信任记录数量、长度和偏移，所有字段先做整数溢出和范围检查。
- 对 DSAR 或 bundled 文件不要直接追加；应另行定义对应的封装方式。

## 建议的验证顺序

### A. 插件读取验证

1. 使用 `tools/hd2physbone_patch_container.py pack` 封装真实 Karin 三个文件。
2. 插件启动时只读取 EOF 容器，不读取整个 GPU sidecar。
3. 日志输出识别到的记录名称、长度、SHA-256 和 Unit 数量。
4. 与原来 `bin\hd2physbone` 文件的运行结果进行 A/B 对照。

### B. Lua 合并验证

1. 只启用一个带 Lua + HD2PBX 的 Mod。
2. 启用两个带 Lua + HD2PBX 的 Mod，触发管理器 Lua 合并。
3. 确认原始 Lua Patch 不再部署，但合并 Patch 的容器包含两份数据。
4. 分别停用其中一个 Mod，确认对应资源也从合并容器中移除。

### C. 重写链路验证

1. 导入含容器的 Mod。
2. 执行一次 Patch 重建或资源重打包。
3. 使用 `inspect` 检查容器仍在 EOF，且记录 hash 不变。
4. 使用 `strip` 后确认原始 Patch payload 可以恢复。

## 可直接转发给插件作者的简要说明

> 我们已经在当前游戏版本中验证：在不修改 Patch TOC、也不修改任何 GPU Entry
> 的情况下，把自定义容器追加到 `.gpu_resources` EOF 不会导致游戏报错、崩溃或模型
> 异常。建议 HD2 PhysBone 使用 `HD2PBX01` 容器承载 `lua_units.txt`、
> `hd2_ib_needle.bin` 和 `hd2_spring_rig.bin`。插件从 EOF 读取 `HD2PBFT1` footer，
> 根据容器长度回读并校验记录，然后将数据交给现有 PhysBone runtime。不要把容器
> 内容加入 TOC，也不要每帧读取文件。
>
> 我们提供了无第三方依赖的 `hd2physbone_patch_container.py`，支持封装、查看、
> 提取和移除。当前工具只支持 raw loose `.gpu_resources`，不处理 DSAR/bundled
> archive。管理器后续会把该尾部视为 Patch companion resource，在 Lua 合并和
> Patch 重建时显式保留。

## 参考

- [ReShade Add-on Documentation](https://reshade.me/docs)
- [ReShade API Reference](https://crosire.github.io/reshade-docs/index.html)
- [HD2SDK Patch 读写参考](HD2SDK-CommunityEdition-3.8.0/__init__.py)
- [HD2 Patch mesh / sidecar 说明](hd2-patch-mesh-retargeting-notes.md)

---

## 实现落地（2026-11-01）：参数随 patch 走，切换模型不再换文件

### 运行时怎么找到它（实测确认）

**Lua 用相对路径就能打开 data 目录里的 patch**（进程工作目录是游戏根目录）：

```lua
local f = io.open("data/9ba626afa44a3aa3.patch_0.gpu_resources", "rb")   -- 成功
```

所以探针按 `<data/|data\|./data/|../data/|..\data\> × patch_0..15` 扫一遍，
对每个能打开的载体读 EOF footer → 取容器 → 拿到 `hd2_spring_rig.bin`。
多个 mod 可能各带容器，按"**这份 rig 的骨名能在当前 unit 里对上几个节点**"打分选最匹配的一份；
全部失败才退回 `%LOCALAPPDATA%\hd2pbx_rig.bin`（保留为调试超控），并明确打一行告警。

实测日志（Lilium，54 骨）：

```text
[jigphys] 从 data 目录读到 N 个 patch 载体（示例 data/9ba626afa44a3aa3.patch_0.gpu_resources）
[jigphys] 用 patch 内嵌容器：data/9ba626afa44a3aa3.patch_0.gpu_resources（54/54 根骨能对上当前 unit）
```

### 注入点：必须是"成品阶段"，而且要覆盖两处

```text
来源 VPK
  │ GUI 导入（vpk2patch）
  ├─▶ vpk2patch/   ──[批次结束自动 embed-mod]──▶ 尾部带容器
  │ GUI「应用为 RE2310」（custom-model convert）
  └─▶ Overwrite/   ──[CarryForward 搬运]──────▶ 容器被原样搬过去
```

> ⛔ 已过时（2026-11-08）：**结论没变**（嵌入必须在成品阶段、`Overwrite/` 必须单独搬），
> 但本节出现的目录名与产线名过时了：转换产物基底已从 `vpk2patch/` 改名 `sectional/`（老库仍是旧名，读侧两名都认，
> 下面写"根 / vpk2patch / Overwrite"的地方今天读作"根 / `sectional` / `Overwrite`"）；
> `Overwrite/` 现在由**编译基座**（`ModCompileService.CompileAsync`：换甲 → 材质物化）产出，
> 不再是「应用为 RE2310 / `custom-model convert`」这一步的直接产物。
> 见 `docs/agent/sectional-compile-base-20261108.md` §1.1、§1.2。

- **为什么不在转换阶段嵌**：转换输出还要过受管步骤，尾部多出来的裸字节会让记账错位
  （历史症状：材质整体投递失败、面部品红）。
- **为什么 Overwrite 必须单独搬**：它是**按条目重新写**的新 patch，尾附数据不会自动跟过去。
  2026-11-01 实测就踩了这条：vpk2patch 里有容器，Overwrite 里没有，于是运行时退回外部文件、
  拿了**别的模型**的 54 骨去驱动 56 骨的模型。
- `CarryForwardAsync` 现在如果发现"来源 patch"里没有容器（可能是 `PatchSubsetWriter` 的裁剪件），
  会顺着 mod 目录（根 / vpk2patch / Overwrite）自己找一份真的带容器的再搬。

### 命令

```bash
# 单个 patch（成品阶段注入；来源可以是 VPK，也可以是一份调好的 rig 文件）
hd2mod physbone embed <patch_0> <来源VPK或rig文件> [模型名] [--output <目录>]

# 整个 mod 目录（根 / vpk2patch / Overwrite，只碰含 Unit 的 patch；材质包自动跳过）
hd2mod physbone embed-mod <mod 目录> <来源VPK或rig文件> [--json]
```

### 三条守门（都属于"不会报错、只会静默丢参数"那一类）

| 守门 | 行为 |
|---|---|
| 写入后回读校验 | 重新读 footer → 取容器 → 校验 `hd2_spring_rig.bin` 存在且长度一致；不一致就报失败，不交付没参数的 patch |
| 幂等 | 每次先 `Strip` 再 `Append`；单测钉住 `Append(Append(x)) == Append(x)`（否则 patch 会一层层变大） |
| 占用容错 | 文件被游戏/管理器打开时只跳过那一个并如实报告，不整批失败、不写坏 |

### 端到端验证（2026-11-01，mourningveil）

1. GUI 导入 → `vpk2patch/` 带容器（10,572 字节 / 56 骨 / 39 链）✔
2. `custom-model convert`（应用为 RE2310）→ 产物仍带容器（`rig=10,394 字节`）✔
3. 游戏内：`[jigphys] 用 patch 内嵌容器：…（56/56 根骨能对上当前 unit）`，物理正常 ✔
4. 手动删除 `%LOCALAPPDATA%\hd2pbx_rig.bin` 后仍能有物理（参数完全来自 patch）

### 2026-11-01 追加：容器在**任何重写**之后都必须跟随（已做成框架级保证）

第三轮实测又丢了一次，这次是**部署前**：`ArmorNormalizationService` 会用 `PatchArchiveWriter`
**按条目重写** patch（新 TOC + 新 sidecar），尾附容器自然不在里面。日志表现为
`45 个 patch 载体里都没读到物理参数 ⇒ 退回外部 rig 文件`，然后拿**别的模型**的参数去驱动。

逐点打补丁追不完（标准化、装饰合并、头盔合并、应用为 RE2310…每一处都是重写）。
所以改成**在写入器里统一保证**：

```csharp
// PatchArchiveWriter.WriteAsync 收尾处
if (gpuLength != 0)
{
    var carried = await PhysBoneCarrier.TryReadContainerFromFileAsync(source + ".gpu_resources", ct);
    if (carried is not null)
        gpuLength = await PhysBoneCarrier.AppendContainerToFileAsync(gpuPath, carried, ct);
}
```

要点：

- **只读尾部**（`TryReadContainerFromFileAsync` 用 seek 读 footer + 容器段），不会把几十 MB 的 gpu 读进内存；
- **幂等**：目标已带同一份容器就不动；带旧容器先截掉再追加（否则每次重建都长 10 KB）；
- 容器放在 TOC 引用范围**之后**，不影响任何偏移（协议本来就这么要求）；
- 覆盖面：所有走 `PatchArchiveWriter` 的重写点自动生效，未来新增的也一样。

配套：
- `hd2mod physbone repair-library --library <库>`：全库兜底（只处理 `kind=standard` 且有 `.vpk` 的 mod），幂等；
- 回归测试 `PatchArchiveWriterContainerTests`：拿真实 patch 做一次"什么都不改的重写"，
  断言容器字节完全一致、`rig` 记录长度不变、再来一次不增长。
