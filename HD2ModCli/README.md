# HD2 Mod Manager CLI

供脚本和 AI Agent 使用的本地入口；先运行 `hd2mod capabilities --json`，再只导出所需 Skill。

```powershell
hd2mod agent skills list --json
hd2mod agent skills export --output .\.agents\skills
hd2mod agent skills export safe-library-deletion --output .\.agents\skills
```

当前 Skill：`library-inspection`、`safe-import`、`mod-metadata`、`option-management`、`safe-library-deletion`、`profile-management`。写操作需要桌面端关闭、跨进程写租约与显式 `--confirm`；不会隐式部署或修改 Patch 字节。

目录导出带 SHA-256 清单，不会删除同级用户 Skill；刷新已管理文件请加 `--overwrite`。旧 `agent skill export mod-management` 与 `agent skill` 仍可用作兼容入口。
