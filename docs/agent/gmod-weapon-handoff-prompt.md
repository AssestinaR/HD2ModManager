# handoff-prompt（新会话直接粘贴）

```text
接手 HD2ModManager 中「GMod 武器 → HD2」的验证工作。先读 AGENTS.md，再读 docs/agent/gmod-weapon-handoff-20261116.md（交接，含文档地图与当前状态）；需要证据时再读 docs/agent/gmod-weapon-offline-validation-20261116.md，其余历史文档按需查阅，不要一次全部加载。

当前阶段：离线实验 V0/V1/V2/V5 已完成并交用户审核。粒子路线暂停。下一步优先：
1) A-W：HD2 原生动画写回实验（改 AR-23 fire.animation 的枪栓后坐量，单独 patch）；
2) V6：GMod c_ 步枪模型替换 AR-23 机匣（部件刚性绑到 5 根变形骨 + attach_mag）。
成功标准与退出条件见交接文档 §5。

约束：不修改正式功能代码（实验在 %TEMP%\gmod-weapon-lab 进行，脚本副本放 docs/agent/gmod-weapon-lab-20261116/）；不清理实验文件；不提交 Git；不写 .dl_bin；Lua 只用白名单 API；默认禁止影响其他武器；游戏内验证需用户进游戏观察。执行前先向用户确认交接文档 §4 的待定问题（附件处理方式、机匣 Unit 被 3 个实体共用是否可接受）。
```
