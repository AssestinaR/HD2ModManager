# HD2 Mod Manager CLI

`hd2mod.exe` 会随管理器输出放在 `HD2ModManager.exe` 旁边。它面向脚本与 Agent：检查、导入、Mod/Option/Profile 管理、安全部署或停用，以及独立 Patch 资源重打包；不提供 Patch 重构、护甲替换或任意自定义 Patch 字节编辑。

```powershell
.\hd2mod.exe --help
.\hd2mod.exe capabilities --json
.\hd2mod.exe agent skills list --json
.\hd2mod.exe agent skills export safe-import --output .\.agents\skills
.\hd2mod.exe agent skills export safe-deployment --output .\.agents\skills
.\hd2mod.exe agent skills export patch-resource-repackaging --output .\.agents\skills
```

`--json` 用于机器读取；以 `capabilities --json` 和 `--help` 作为当前命令表的唯一依据。库写入需要桌面管理器关闭、跨进程库租约和显式 `--confirm`。部署还要求游戏退出、真实 `data` 目录、双租约及完整 Patch 归属证明；不会接管未知 Patch 或执行 Mod Lua。资源重打包只复制已审阅的完整 AssetKey Payload 到一个新的库外目录，不改来源、不自动导入或部署。

当前**不支持** `--dry-run`；无写入流程统一使用 `inspect/plan` → `validate`。只有人工确认后才可 `apply --confirm`。
