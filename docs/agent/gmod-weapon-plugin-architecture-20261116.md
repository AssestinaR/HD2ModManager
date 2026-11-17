# GMod 武器转换器：插件化集成方案（2026-11-16）

> 范围：只做设计，不改代码。取代 `gmod-weapon-to-hd2-research-20261116.md` §5.1–5.2 里「Git submodule + ProjectReference」的结论。
> 证据标记：**[F]** 已核实（附出处）· **[I]** 推测 · **[E]** 待实验。

## 0. 结论

- **同意 GPT 的方向**：转换器做成独立编译、独立发布的 .NET DLL 插件，用 `AssemblyLoadContext` 加载；管理器只做一次性接入（契约 + 加载 + 更新 + 入口），之后转换器更新**不需要重新编译管理器**。
- **补一处修正：插件不要加载进 WPF 主进程，而是加载进一个由管理器启动的工作进程（插件宿主）。** 理由有三条，见 §2。这个进程是内部实现，用户看不到，也**不是**给用户用的 CLI。
- **更新机制几乎不用新写**：管理器已有一套成熟的 GitHub Releases 更新器（镜像、SHA-256 三方核对、断点续传、失败回滚）[F]，参数化 `Repo` 后可直接拿来更新插件。
- **SHA-256 只能证明完整，不能证明来源。** 自动下载并执行第二个仓库的代码，建议加发布签名（§5.3），默认不自动安装。

## 1. 管理器现状（已核实）

| 事实 | 出处 |
|---|---|
| 管理器 `net10.0-windows` WPF；库项目多为 `net8.0` | `HD2ModManager/HD2ModManager.csproj`、各库 csproj |
| 发布：框架依赖（`--self-contained false`）、`PublishSingleFile=true`、**未开裁剪**、`ReadyToRun=false`；另有「自带运行时」变体（`bin\dotnet`） | `tools/PublishRelease.ps1:380-400` |
| **现有转换故意放在进程外**：VPK 转换由管理器逐包启动 `hd2mod.exe`，理由是「不要两份会漂移的实现」+「必须能看进度、能取消」 | `HD2ModManager/Services/VpkImportService.cs:116-129` |
| 管理器对 CLI 只做构建期引用（`ReferenceOutputAssembly="false"`），CLI 单独发布到 `bin\` | `HD2ModManager.csproj`、`PublishRelease.ps1` |
| 现有更新器：GitHub API → `releases.atom`/`expanded_assets` 兜底；镜像只转发 github.com；资产 `sha256:` 摘要 + `SHA256SUMS` + 实际哈希三者一致；`update-package.json` 版本/变体核对；替换失败回滚；结果写 `result.json` | `HD2ModCore/Updates/*`、`docs/app-update.md` |
| 更新器按 `Repo` 参数化（当前 `AssestinaR/HD2ModManager`） | `HD2ModManager/Services/AppUpdateService.cs:26,52` |
| 仓库内**没有**任何插件/动态加载机制 | 全仓 grep `AssemblyLoadContext` 无结果 |

## 2. 为什么用「插件 DLL + 进程外宿主」，而不是加载进管理器进程

| 问题 | 进程内 ALC（GPT 方案） | 进程外宿主（建议） |
|---|---|---|
| 原生崩溃 / 栈溢出 / 死循环（解析来自创意工坊的**不可信**文件；BC7 等编码器） | 拖垮整个管理器；GPT 自己也指出没解决 | 只死宿主进程，管理器报错继续 |
| 取消 | 只能协作式取消 | 协作式取消 + 超时强杀 |
| 更新生效 | 需要重启管理器（已加载的 DLL 卸载困难） | **下一次转换立即生效**，不用重启 |
| DLL 被占用 | 运行中替换不了 | 宿主退出即释放 |
| 依赖冲突 | 要靠 ALC 精细隔离 | 进程级隔离，天然无冲突 |
| 与现有架构一致性 | 新模式 | 与 `hd2mod.exe` 进程外转换是**同一模式** |
| 代价 | 无 IPC | 需要一个通用宿主 + 简单 IPC（JSON 行） |

宿主内部仍用 `AssemblyLoadContext` + `AssemblyDependencyResolver` 加载插件，契约程序集从默认上下文共享（这是微软插件教程的标准做法 [I]，接入时以官方文档为准）。

若以后确实需要进程内调用（例如轻量的 `Inspect` 预览），同一份契约也可以进程内加载 —— 契约不绑定传输方式。

## 3. 组件与职责

```
管理器仓库（一次性接入，之后基本不动）
├─ HD2ModManager.PluginContracts   契约（接口 + DTO + 版本号），目标 net8.0
├─ HD2ModManager.PluginHost        通用宿主进程：加载插件、转发调用、JSON 行通信
├─ PluginRegistry / PluginUpdater  发现、版本检查、安装/回滚（复用 HD2ModCore/Updates）
└─ UI：插件页（版本、来源、更新、兼容信息）+ 转换入口（复用导入流程）

用户的仓库（独立 GPL-3.0）
└─ GModWeaponConverter.dll (+依赖)   实现契约；GitHub Actions 构建、测试、发布
```

**插件只产出一个暂存目录**：标准 Mod 文件夹 + 产物清单 + 兼容报告。它**不碰**游戏目录、不碰管理器的库目录；导入、部署、卸载全部交给管理器现有的导入管线（与 VPK 产物同路）。

## 4. 契约草案

```csharp
// HD2ModManager.PluginContracts（net8.0，零依赖）
public static class PluginContract { public const int Major = 1; public const int Minor = 0; }

public interface IConverterPlugin
{
    PluginDescriptor Describe();                                   // id、名称、版本、能接受的输入、可选参数定义
    Task<InspectResult> InspectAsync(InspectRequest request, CancellationToken ct);
    Task<ConversionResult> ConvertAsync(ConversionRequest request,
                                        IProgress<ConversionProgress> progress,
                                        CancellationToken ct);
}

// DTO 全部 record，带 SchemaVersion，可序列化为 JSON
public sealed record ConversionRequest(int SchemaVersion, string InputPath, string StagingDirectory,
                                       string GameDataDirectory, IReadOnlyDictionary<string, string> Options);
public sealed record ConversionResult(int SchemaVersion, bool Succeeded, string? OutputDirectory,
                                      CompatibilityReport Report, IReadOnlyList<Diagnostic> Diagnostics);
```

- **参数定义**由插件声明（布尔 / 枚举 / 数值 / 文本 + 默认值 + 说明），管理器据此生成通用设置界面 —— 插件加参数不用改管理器。
- **兼容报告**：每个功能（模型、材质、资源粒子、Lua 粒子、Lua 动画……）各自给出「需要什么、在当前环境会以哪一档呈现、为什么」。
- **游戏数据只读**：插件需要读 HD2 原生资源（找目标武器 Unit）时，拿 `GameDataDirectory` 自己只读打开，不经管理器内部 API —— 避免契约依赖管理器内部类型。

**版本规则**：
- 插件包内 `plugin.json`：`id`、`version`、`contract`（如 `1.0`）、`entryAssembly`、`entryType`、`minHostRuntime`。
- 宿主只加载 `contract.Major == 宿主 Major` 且 `contract.Minor <= 宿主 Minor` 的插件；不匹配 → 插件页显示「需要更新管理器 / 插件」，不加载。
- 契约只做**加法**演进（新增可选字段 = Minor+1）；任何破坏性变化 = Major+1，需要双方协调发版。
- 插件目标框架 ≤ 宿主运行时（管理器自带 .NET 10 运行时变体时，插件用 `net8.0` 最稳）。

## 5. 分发与更新

### 5.1 发布（用户仓库）

推 tag → GitHub Actions：构建 + 全测 → 打 `GModWeaponConverter-<ver>.zip`（DLL + 依赖 + `plugin.json`）→ 生成 `SHA256SUMS`（+ 签名，见 5.3）→ 草稿 Release，人工确认后发布。与管理器自己的发版流程同构 [F]。

### 5.2 安装与回滚（管理器侧）

- 复用 `HD2ModCore/Updates`：GitHub API / atom 兜底 / 镜像 / 三方 SHA-256 核对 / 断点续传，换 `Repo` 即可 [F]。
- 安装布局：`plugins/<id>/<version>/`，旁边一个 `current` 指针文件。新版本解压到新目录 → 校验 `plugin.json` 版本与 Release 一致 → 切指针。**回滚 = 指针指回上一版**，保留最近 2 版。
- 进程外宿主每次转换都按指针重新加载，所以切换后**下一次转换即生效**，不需要重启管理器。
- 默认：检查更新开，**自动安装关**（与管理器自身更新的默认值一致 [F]）。

### 5.3 来源可信（建议补充）

SHA-256 证明「下载到的就是 GitHub 上那个文件」，但证明不了「那个文件是插件作者发的」（账号被盗、Actions 被投毒都能产出带正确摘要的恶意包）。对一个会自动下载并执行代码的机制，建议：

1. 插件来源**白名单**：管理器只认登记过的 `owner/repo`，默认列表为空或只含经管理器作者同意的条目。
2. **发布签名**：插件作者持有一把 ECDSA P-256 私钥（.NET 内置 `ECDsa`，无需第三方库），对 `SHA256SUMS` 签名；公钥随白名单条目登记在管理器里。镜像、GitHub 资产都无法伪造签名。
3. 首次安装和**公钥变更**都必须用户手动确认。

这是给管理器作者审核时最可能被问到的问题，提前给出答案能降低接入阻力。

## 6. 一次性接入的工作量与边界（估计 [I]）

| 项 | 规模 | 说明 |
|---|---|---|
| 契约项目 | 小 | 接口 + 十来个 record |
| 插件宿主 | 小–中 | ALC 加载 + stdin/stdout JSON 行；可参考 `hd2mod.exe` 的进度上报方式 |
| 插件注册/更新 | 中 | 主要是把 `AppUpdater` 的安装器换成「解压到版本目录 + 切指针」 |
| UI | 中 | 插件页 + 转换入口复用导入流程 |
| 测试 | 中 | 版本匹配、签名、回滚、宿主崩溃、取消 |

**需要管理器作者接受一次**：上面五项。之后转换器发版与管理器无关；只有契约 Major 变化才需要协调。

在上游接受之前，用户自己的 fork 可以先带着这套接入运行，不影响转换器独立开发。

## 7. 转换器内部结构（独立仓库）

代码复用按 GPT 审核意见调整：**先验证、后提取**。

1. 验证期（当前）：在用户的 fork 里，用现有项目直接做技术验证（见验证计划），**不复制代码、不建仓库**。
2. 验证通过后：按实际用到的依赖，只提取必要的文件到新仓库（Source 读取、贴图/材质、HD2 写出的子集），保留原版权头，附 NOTICE（来源提交号）。护甲专用的重定向/分段代码不带。
3. 新仓库自足，不反向引用管理器，只引用 `PluginContracts`（以 NuGet 包或固定版本 DLL 形式，**不是**管理器源码）。

建议的内部分层：`Gma`（解包）→ `SourceAssets`（MDL/VVD/VTX/VMT/VTF/PCF/动画）→ `Hd2Formats`（HD2 写出）→ `Pipeline`（模型/材质/粒子/动画各自独立的转换步骤，单步失败只降级自己）→ `LuaRuntime`（随产物分发的游戏内 Lua 与能力层）→ `Plugin`（契约实现，薄壳）。
