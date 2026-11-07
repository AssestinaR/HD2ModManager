# 本工作区的约定

（工具链与本机事实写在 `~/.dsh/AGENTS.md`，这里只放这个仓库自己的规则。）

## 文档放哪

- **`docs/` 是给人看的**：用户自己写的说明、教程、发布说明。**agent 不要往这里加新文件。**
- **agent 写的文档一律放 `docs/agent/`**：分析、计划、审计、交接、复盘、踩坑记录都算。
- 第三方与参考项目的资料放 `docs/agent/external/`。
- `docs/` 根长期只保留 `app-update.md`（随应用分发）。
- 已经在 `docs/` 里的历史文档不搬；**新写的**一律进 `docs/agent/`。

## 交付门

- `dotnet build -c Debug` 必须 **0 警告 0 错误**（本仓库把警告当错误）。
- 提交前跑全量测试：`dotnet test -c Debug`，绿了再提交。
- 管理器开着会锁 `bin\`：构建前可 `Stop-Process -Name HD2ModManager -Force`（用户允许）。

## 别碰

- `docs/` 下**未跟踪**的文件和目录属于用户本人（`参考项目/`、`_backup/`、`temp/`、各种 `*.zip`），
  不要移动、不要删除、**不要提交**。
- `docs/` 的 `.gitignore` 是白名单式的：新增的可跟踪目录要显式加例外，别把整棵第三方树拖进来。
