# 间接调用调查：残留进程暂停点

> 此暂停点已解除。后续已完成 V6/V7 两轮采样；最新证据和下一步见
> `refresh-master-interface-20260926.md`。下文保留历史，不代表当前阻塞状态。

2026-09-26，接续 refresh-interface-table-20260926.md。

## 已做、尚未采到

survey_format=5 已完成代码、Lua 测试、项目内 Debug CLI 构建、
Core 嵌入 Lua 源测试 1/1，并通过 CLI plan/validate/apply 部署。
新增只在 game.dll 搜索 `call/jmp [register+disp32]`（不含 SIB），
偏移限定 f8/100/108/110；最多记录 256 个候选、最多 512KiB
exception range 字节，同范围复用。候选计数与截断标志单独记录。
这些常见偏移不能证明 API 表身份，更不能当作已验证的调用入口。
离线脚本支持共享 range 与位移验证。没有执行未知函数或修改映射。

## 为什么没有新实机结果

1. PID 39912，创建时间本地 14:00:54；采样：
   `evidence/refresh/20260926-140128-4831c3d221784cbebfd7ff43829500cb`。
   在 worlds 预检期间失去可访问性，尚未进入 code_refs。
2. 没有先清理残留就发出的 Steam 重启请求没有产生有效新会话。
   `20260926-140246-9a28c7fd908e448cb51249585253b874` 仍是旧进程，
   不计第二次独立启动/崩溃，也不是新扫描失败。
3. 用户指出崩溃会残留进程。检查发现 Win32_Process、tasklist 仍枚举
   39912，Get-Process 对象报告 HasExited=True，同时出现新的
   helldivers2.exe / WerFault.exe 子进程。不能据此推断根因。
4. Stop-Process 报找不到该 PID；taskkill /PID 39912 /T /F
   能结束部分子进程，但父进程返回“没有正在运行的实例”，仍被枚举。
   未重启 Steam、未终止系统服务、未操作反作弊、未重启电脑。
   停止进一步自动启动，需用 Steam 停止或用户清理后再核实。

向导已补充 HasExited 与进程创建时间校验、拒绝已退出残留，
Wait-State 检查 PID+创建时间，避免 PID 复用/旧进程被当作新会话。
这不自动杀进程，也不能证明 Responding=True 等于游戏健康；
新 session/request 的 Lua 握手仍然必需。

## 恢复工作

先确认所有 helldivers2.exe 残留从 OS 枚举中消失，
再启动并确认新 PID/创建时间。运行：

```powershell
pwsh -NoProfile -File E:\Data\source\repos\WpfApp1\HD2RuntimeArmorOverride\scripts\Watch-ArmorRefresh.ps1 -CodeReferencePreflight -CaptureSeconds 40
```

只有 code_survey_end.complete=true 才能称扫描完成。
分析 table_call_candidate，确认指令边界、范围和寄存器的来源，
不要仅凭 f8/100/108/110 匹配声称找到刷新功能。

协议依然 revision 45 / 135 mappings，未编译/修改源定义。
生产自动刷新尚未实现；无 Git 提交。
