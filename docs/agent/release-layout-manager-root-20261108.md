# 发布版布局下 `--manager-root` 被推成 `bin\` —— 1.5.5 全部 VPK 导入失败

**日期**：2026-11-08
**版本**：1.5.5 实机回归，1.5.6 修复
**症状**：装发布版后**任意** VPK 导入都失败。规划阶段就退出（`hd2mod` 退出码 2，`PrerequisitesMissing`），
报错里出现的数据库路径是 `<安装根>\bin\data\indexes\asset-index.sqlite`。

## 1. 根因

发布包的布局是两份程序、一个根：

```
<安装根>\HD2ModManager.exe      ← 管理器
<安装根>\settings.json          ← 设置（SettingsService 读写这里）
<安装根>\data\...               ← 索引、资源表
<安装根>\bin\hd2mod.exe         ← 命令行程序（+ 完整版私有运行时）
```

`CliWorkspace.Open` 在**没给** `--manager-root` 时直接用 `AppContext.BaseDirectory`，也就是
**命令行程序自己所在的目录**。开发构建里这个默认值恰好等于管理器根（`hd2mod.exe` 被复制到管理器旁边），
发布包里它等于 `bin\` —— 于是 CLI 去找 `bin\settings.json` 与 `bin\data\indexes\asset-index.sqlite`，
两个都不存在，表现为"没配游戏 data、没有骨名表、没有资产索引"。

三条入口同时踩中：

| 入口 | 为什么没传 `--manager-root` |
|---|---|
| 管理器导入（`VpkImportService.PlanAsync`） | 规划那次 `vpk2patch --dry-run` 调用只传了 `--vpk-list`/`--output` |
| 发布包的 `hd2mod.cmd` | 只是 `"%~dp0bin\hd2mod.exe" %*`，什么也没补 |
| 直接用 `bin\hd2mod.exe` | 没有可用默认值 |

**开发环境永远复现不出来**：那里 `hd2mod.exe` 就在管理器输出目录里，`AppContext.BaseDirectory` 是对的。

## 2. 修复（两层，缺一不可）

### ① CLI 自己按布局反推管理器根（根因）

`Hd2ModCliLocator.ResolveManagerRoot(baseDirectory)`：

1. 该目录自己像管理器根（有 `settings.json` 或 `data\`）→ 就是它（开发布局、便携布局都不变）；
2. 否则**只在目录名正好是 `bin` 且上一级确实像管理器根**时往上走一级（发布布局）；
3. 都不成立 → 原样返回（行为和以前一致，不乱跳）。

判据只用两样"只有管理器自己会产出"的东西，且**不会一路往上找**，所以不会把开发环境里的
设置与别的安装混在一起（有测试钉住 `bin\Debug\net10.0` 这种目录不许上跳）。

`CliWorkspace.Open` 的默认值与 `ResolveBoneHashPath` 的"程序目录"候选都改成走它。

### ② 管理器自己把三条路径传过去

`VpkImportService.PlanAsync` 现在显式传 `--manager-root` / `--library` / `--game-data`
（这三个值管理器本来就有，此前 `_managerRoot`/`_libraryRoot` 字段甚至没被读过）。
即使将来 CLI 的反推再坏一次，管理器这条路也不依赖推导。

### ③ 报错里写出"到底看了哪一份设置"

`PrerequisitesMissing` 的游戏 data 那条现在附带：
`（未找到管理器设置 <路径>）` 或 `（已读到 <路径>，但里面的 GameDataFolder 没配置或指向的目录不存在）`。
另外把 `--manager-root` 手工指到 `bin\` 时，issues 里会直说
「管理器根目录是它的上一级：`<安装根>`」。

## 3. 验证

| 验证 | 结果 |
|---|---|
| 造一份发布布局（`<根>\settings.json` + `<根>\data\resources\*` + `<根>\bin\hd2mod.exe`），在 `bin\` 里跑 `status --json` | 修前 `manager.root = …\bin`、`settingsExists:false`；修后 `…\repro-rel`、`settingsExists:true`、库与游戏 data 全部解析出来 |
| 同一布局跑 `vpk2patch <真实 VPK> --dry-run --json` | 修前 `PrerequisitesMissing`（exit 2，与用户截图逐字一致）；修后 `ok:true`、模型/材质都进了 `convertible` |
| 管理器会传的显式三条路径再跑一次 | `ok:true` |
| `--manager-root <根>\bin`（旧习惯） | issues 里点名「上一级：`<根>`」 |
| 新增测试 | `Hd2ModCliLocatorTests` +6（13 通过）、`CliAgentFriendlinessTests` +2（8 通过） |
| 全量 `dotnet build -c Debug` / `dotnet test -c Debug` | 0 警告 0 错误 / 2067 通过 1 跳过 0 失败 |

## 4. 教训

- **"程序在哪儿"不等于"数据在哪儿"。** 凡是拿 `AppContext.BaseDirectory` 当"应用根"的地方，
  都要问一句：发布包里这个 exe 是不是被塞进了子目录？本仓库答案是"是，`bin\`"。
- **只在开发环境验证的路径等于没验证。** 这条 bug 在开发机上 100% 不会出现；要复现必须
  手工造一份发布布局（本文第 3 节第一步就是脚本化的做法，5 行就能重来一次）。
- **同一份"路径"不要有两个来源。** 管理器知道三条路径就该传，让 CLI 猜是第二次推导，
  两份推导迟早会漂。这次是"两处都做"，但方向明确：调用方负责传，默认值只为裸跑兜底。
