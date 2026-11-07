# 程序自身更新（应用内更新）

管理器会自己检查 GitHub Release，发现新版本后在「设置 → 程序更新」里给出更新说明，一键下载、校验、替换并重启；用户不需要再到各处去找安装包。流程参考 llama-web 的应用内更新，针对 HD2 的 zip 免安装发布包做了替换步骤。

## 用户侧行为

- 启动 15 秒后检查一次，之后每 24 小时一次（间隔记在 `config\update-checks.json`，连不上也算一次尝试）；可在设置里关闭自动检查。
- 发现新版本：消息中心提示一次；设置页显示版本、发布日期、更新说明，可「下载更新」「跳过此版本」「打开 Release 页面」。
- 「自动下载并在空闲时安装」默认关。打开后：自动下载；只有没有部署、导入等后台任务时才自动安装并重启，否则停在「已下载」等用户手动安装。被跳过的版本永远不会自动安装。
- 稳定版只升到稳定版；预发布版可升到更新的预发布版。
- 开发构建（没有 `update-package.json`）只提示新版本、给 Release 页面链接，不做应用内安装。

## 安全

- 更新包必须同时满足：GitHub 资产的 `sha256:` 摘要、Release 里的 `SHA256SUMS`、下载内容的实际哈希，三者一致，否则丢弃。
- 安装前再校验一遍；解压时条目不得越出暂存目录；包里的 `update-package.json` 的版本必须等于 Release 公告的版本，变体必须等于当前安装的变体（`complete` / `requires-dotnet` / `compat-*`），否则拒绝。
- 镜像只转发 `github.com` / `api.github.com`，重定向只允许回到镜像自己或 GitHub；自定义镜像只接受 https 公网域名。官方元数据优先，官方不全才采用镜像给的摘要，并在界面注明「校验信息来自镜像」。

## 下载来源

`设置 → 程序更新 → 下载来源`：

| 模式 | 行为 |
|---|---|
| 直连，失败后询问（默认） | 只直连；用户**主动**检查/下载因连不上失败时，弹窗问是否换镜像，可勾选「以后自动换」。自动检查从不弹窗。 |
| 直连失败时自动换镜像 | 先直连，失败后按测速排序依次试镜像。 |
| 优先使用镜像 | 先走所选镜像，全部失败才直连。 |

API 被限流（匿名每小时 60 次）或出错时，会改用 `releases.atom` 和 `expanded_assets` 页面取元数据。

## 替换过程

1. 下载到 `cache\app-update\`（支持断点续传），校验通过后解压到 `cache\app-update\stage-<版本>\`。
2. 写出 `apply-update.ps1` 并后台启动，管理器走正常的「保存后关闭」流程退出。
3. 脚本等旧进程（以及 `bin\` 里的命令行进程）退出后：备份将被覆盖的文件，整体替换 `bin\`，其余随包文件逐个覆盖，逐个核对大小。**用户数据不动**（`settings.json`、`mods\`、`config\`、`data\indexes` 等都不在包里）。
4. 任何一步失败都回滚（恢复备份、删掉新增文件），结果写进 `cache\app-update\result.json`，日志在 `logs\app-update.log`；然后重启管理器。下次启动时消息中心会显示「已更新到 x.y.z」或「没有完成，已恢复」。

已知取舍：包里的 `data\resources`（随包的资产元数据快照）会被覆盖；如果用户之前在线同步过更新的元数据，需要再同步一次。

## 发布一个版本

版本号只在 `HD2ModManager\HD2ModManager.csproj` 的 `<Version>`。

```powershell
# 1. 改 <Version>，提交并打 tag：v<Version>
# 2. 构建发布包（产出带版本号的 zip 和 SHA256SUMS）
.\publish.ps1
# 3.（可选）兼容版发到同一目录，SHA256SUMS 会合并四个包
.\publish-compat.ps1 -OutputDirectory artifacts\publish
# 4. 创建 Release 并上传 artifacts\publish 下的所有 zip 和 SHA256SUMS
gh release create v<Version> artifacts\publish\*.zip artifacts\publish\SHA256SUMS --title "v<Version>" --notes-file <更新说明.md>
```

更新器按固定文件名找包：`HD2ModManager-<version>-win-x64-<variant>.zip`，Release 里必须同时有 `SHA256SUMS`。Release 说明正文会原样（纯文本）显示给用户。

检查更新的仓库在 `HD2ModManager\Services\AppUpdateService.cs` 的 `Repository` 常量（目前是 `egoist74110/HD2ModManager`），合并到上游后改成上游仓库即可。
