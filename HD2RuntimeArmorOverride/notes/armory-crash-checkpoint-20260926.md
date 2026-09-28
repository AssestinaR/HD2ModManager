# 军械库退出崩溃：暂停点与恢复入口

## 用户决定：暂列已知问题，暂停调查

2026-09-26：用户补充单映射也有复现，开/关阶段的口述可能不准确；
决定将其作为已知但尚难修复的问题，不再继续本轮对照。
这不代表已证明是游戏本体缺陷，或已排除映射实现的问题。
保留下文证据和候选解释供将来恢复，不将猜测性修复混入后续GUI工作。

## 2026-09-26 后续：已收到报告并恢复调查

稳定版本已提交`a18c1bc`。新复核见
`armory-report-analysis-20260926.md`：
映射开/关对照有效，报告实际填O（打开时崩溃），不是C；
来源胸部type2/type3命中已捕获原生装配路径的同一输出句柄位置。
这是具体待验证线索，不是已经确诊的清理崩溃。
尚未修改运行时或部署新对照；以下是历史暂停记录。

更新：2026-09-26。状态：用户已把便携诊断包发送给可稳定复现者，
等待报告；此调查暂时暂停，独立推进“注册更新后刷新已装备模型”。

## 已知事实与未定论

- 本机对照 `evidence/armory-control-20260926/run-20260926-105225`：
  baseline 和 low_present 各完成三次开关军械库，未崩溃。
- expanded_present 在用户操作军械库前就剧烈卡顿并崩溃。
  同一 PID 25020、同一进程创建时间的 c000000d 与五条 c0000026
  属于一次故障过程，不能算六次独立复现。
- 用户随后手动重启成功，不等于完成该组开关军械库对照。
  low_absent、expanded_absent 尚未测试。
- 槽位数量、层级同时变化，不能宣称已证明“仅扩容导致崩溃”；
  缺资源与关闭预览清理路径的关系仍未确定。
- 发布过的 clone 缓冲区有保活；尚无证据证明是普通 Lua GC 回收。
  Lua 不 yield 并不代表游戏引擎其他线程停止读取。
- 所检查的 WER 档案只有 Report.wer，没有转储；没有做全盘 dump 搜索。

## 已发诊断包：不要悄悄替换此版本

`HD2ModCLI/bin/Debug/net10.0/diagnostics-package-20260926-111649-96691b/Armor-Diagnostics-Debug.zip`

SHA256：`AFC00C54D28878442367D2DF3C8D9EF583689BC508EE6F9C4E04091F6B259B5F`

完整解压 Armor-Diagnostics 目录到管理器目录内，运行 Start.cmd。
使用已有管理器、配置及运行时，不附带第三方加载器，不重新打包或部署资产。
以原映射与全关闭映射两组新进程对比：启动后静置 30 秒，再手动开关三次。
有 PID/创建时间/协议 revision/映射数检查，有日志、部署 Unit 摘要、
有限的事件记录和逐步采样。用户只发回 `*-report-*.zip`；
不要发送本地恢复目录或原始配置备份。

待收报告后先：
1. 确认包版本、各组是否通过预检、是否确实换了进程。
2. 区分启动/静置/关闭军械库/强制结束，按 PID 与创建时间合并事件。
3. 对比映射、克隆大小、层级、已部署资源和失败阶段，而非只比机器配置。
4. 确认恢复完成；若未完成，让用户使用 recover，禁止覆盖外部新改的协议。
5. 根据结果决定下一轮最小变量实验。不要把刷新实验混进这两组。

实现与验证见 `notes/portable-armor-diagnostics-20260926.md`。
关键源码：CLI 的 ArmorDiagnosticSession/Evidence/Wizard.cs，
启动器 Armor-Diagnostics.cmd；管理器启动时检查未完成诊断 journal。
七个隔离测试、定向 15 个测试已通过；历史全 CLI 测试有五个部署测试
因当时游戏仍运行而被保护性拒绝，不能把历史结果记为全绿。
真人端到端流程仍待用户反馈。

## 本地配置接续（开始刷新调查前核对）

此记录创建时，本机仍是 expanded_present，协议 revision 48，
仅 RE-1861（3340614564），19 Pieces，1960 字节克隆。
协议 SHA256：
`48633E03F607CA122324BA66BD10EA0F495BB1156EF24AC59F53D39CA921284D`

原协议在 `evidence/armory-control-20260926/original.protocol`，
revision 45 / 135 mappings，SHA256：
`37BEE774415F1104312F039BB4290870221E0D1077D0E6FFFE7BBC994389A99C`。
恢复入口（游戏与管理器均须关闭；脚本有 hash 与配置漂移检查）：

```powershell
pwsh -File HD2RuntimeArmorOverride/scripts/Set-ArmoryControlStage.ps1 `
  -ExperimentRoot E:\Data\source\repos\WpfApp1\HD2RuntimeArmorOverride\evidence\armory-control-20260926 `
  -Stage restore
```

以上是创建时状态，不代表之后仍未恢复。
以 current-stage.json 和实际协议 hash 为准；刷新调查的后续笔记应记录恢复结果。
原配置、源 JSON、私人恢复备份不可打包给用户。

### 本轮恢复尝试：被占用保护阻止，尚未恢复

恢复脚本在 deploy apply 被 `LibraryWriteBusy` 拒绝。
检查发现本机便携诊断向导 PID 38368 仍在运行，
`data/diagnostics/armor/active.json` 状态 original / completed=false，
会话 `20260926-035516-c33973dc756f4a3b932325c44bfdc444`。
未终止它、未删除锁、未绕过恢复流程。复核协议 hash 仍为上述 revision 48。
该诊断会话的“原配置”是 revision 48，不是 revision 45；
因此应先结束/恢复便携诊断，再恢复旧的 armory-control 对照配置。
这些 PID 与状态为本轮检查时观察，后续必须重新核实。

### 后续已恢复

用户确认关闭向导后，重新核实 journal 为 restored / completed=true，
无 game/manager/diagnostic 进程。已通过原恢复脚本恢复 revision 45 /
135 mappings 并核验原 hash 一致。current-stage.json 已为 restore。
刷新实机后续记录见 `refresh-link-implementation-20260926.md`。

## 刷新任务独立入口

`notes/refresh-native-association-findings-20260926.md`：
已确认 avatar 候选与 12 个自定义护甲 Unit 的指针关联，
未找到可安全调用的外观重建入口，未证明多人场景本地玩家身份。
下一步是审查已捕获 World.link_unit 包装函数的直接 callee，
只读取代码，不把 link/unlink/destroy 冒充外观刷新。
