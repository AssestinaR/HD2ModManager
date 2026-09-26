# V9 实机与关联链写入端：2026-09-26

## 环境恢复

用户关闭便携向导后，journal 已是 restored / completed=true。
通过 Set-ArmoryControlStage.ps1 -Stage restore 恢复原部署和 revision 45 /
135 mappings；原协议 hash 为
`37BEE774415F1104312F039BB4290870221E0D1077D0E6FFFE7BBC994389A99C`，
本轮结束再次核验一致。没有编译源 JSON 或改变护甲映射。

CLI 使用项目内 Debug 构建，经 plan/validate/apply 部署。
首次测试 PID 53772，随后主动结束该进程用于部署采样修复；
紧接着一次部署因进程尚在退出被保护性拒绝。重新确认进程消失、
协议未变后部署成功，第二次游戏 PID 1784，未进行人工换装。
便携崩溃诊断包没有替换，不把主动结束当作崩溃证据。

## 实机证据

所有路径相对于 evidence/refresh：

- `20260926-132211-34c0968a87034060b9b90fe96df09622`
  捕获 World.link_unit 的直接 callee，RVA 0x3361c0。
  `link-callee.asm.txt` 是从实时捕获字节离线反汇编，不是磁盘 EXE。
- `20260926-132307-83f3251cad4241069a2505b62fcafbd6`
  26 次同 avatar 解析成功，18 个子节点，静置无变化。
  phase_end 标记 truncated=true：无关元数据遍历在到时仍未结束，
  不能将整份日志说成完整。随即禁用 owner_trace 的元数据遍历。
- `20260926-132522-a60bff9ded4b4512baeefffe917b0e19`
  读取经审查的 tail jump，得到实现 RVA 0x1af240；
  `link-implementation.asm.txt` 保存反汇编，未调用。
- `20260926-132604-27bd6d5a4ecb4e13ae25e7e25258369e`
  修复后的连续采样：10 秒内 26 次 resolved_candidate，同一个对象，
  18 个子节点，成员无变化。complete_log=true，错误 0。
  6,630 是 Unit 检查次数，不是唯一实例数量。
  已生成 native-transition-summary.json；探针最终 stopped。

## 确认了什么

包装函数 0x3eb860 -> callee 0x3361c0 -> 实现 0x1af240：

1. 中间层通过既知 resolver 解析两个 Unit handle，最后 tail jump。
2. 实现操作 World+0x2304b0 数组，调用 Unit 虚表 +0x220、+0xe8，
   写节点变换和标记位；这些虚表调用未进一步验证，不可主动调用。
3. 实现 +0x1c9 起读取 child+0x1d0，与新 parent 比较；
   若 parent 不同，从旧 parent+0x1d8 链中移除 child，
   然后写 child+0x1d0=newParent、
   child+0x1e0=oldHead、newParent+0x1d8=child。

这从写入端交叉验证了既有 +0x1d8 / +0x1e0 子链表的含义，
并给出 +0x1d0 的 parent 字段线索。它是通用 Unit 挂接/变换逻辑，
不是“重新消费护甲注册表”的证据，更不是装备控制器入口。
附近 0x1af780 完整函数可见反向移除及节点处理，但没有执行。
2KB 窗口包含邻接函数，不能把整个窗口算作同一个函数。

## 下一步：短换装采样，而非继续沿通用挂接函数盲走

双击 scripts/Start-ArmorRefreshTransitions.cmd。
先在舰船静置完成预检，然后按提示穿 A、打开军械库：
静置 -> 确认 B -> 确认 A。默认每阶段 10 秒，3 秒切回。
不要更改映射/部署/身形/头盔。本向导不要求新增关军械库对照。

目的：用已确认的 parent/children 装配，观察换装时是否父对象不变而
子链更新、是否仅列表重排、或整体候选改变。无需重复广域走动采样。
静置证据无法回答换装问题，也不能凭同一地址排除 ABA 或确立本地玩家权限。
下一轮须将变化时间与 action_cue 对齐，不能把列表变化直接称为调用栈。

正式自动刷新仍未实现：缺少上层外观装配控制器、更新请求的正确入口及
本地玩家权限确认。没有调用 link/unlink、销毁/生成 Unit、绕过反作弊，
没有给正式运行时加入猜测的刷新调用。

## 验证与交接状态

Lua 全套测试通过；新增 tail opcode/目的地拒绝测试；
owner_trace 不再运行元数据遍历有测试覆盖。
Debug CLI 构建成功（既有 CA1416 警告）。
本轮最终保留游戏 PID 1784 供用户短换装采样，探针已停止、不会自动继续采样。
此 PID 为记录时状态，后续须重新查询。无 Git 提交。
