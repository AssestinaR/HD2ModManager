# HD2 Mod Manager

- HD2ModManager.exe：图形化管理器入口。
- hd2mod.cmd：供脚本和 Agent 使用的命令行入口；完整指南位于 hd2mod-docs。
- settings.json：首次启动管理器后在此目录自动生成；请不要手工创建空文件。
- data、third_party：管理器随包资源，请保留。

{{RUNTIME_INSTRUCTIONS}}

如需让 Agent 使用命令行，请先运行：

~~~powershell
.\hd2mod.cmd capabilities --json
.\hd2mod.cmd agent skills list --json
~~~
