# CW-36 映射变更自动刷新：待用户验收

## 已部署，不代表已通过视觉测试

2026-09-26准备。保留用户revision47：
`map_model=190632838,custom-e90b86bb2c0e4bbfab1fd115311a13cd`。
游戏、管理器均关闭；本轮不自动启动，交给用户完成视觉验收。
Lua全套测试、管理器与CLI项目内Debug构建、Core嵌入测试通过。
当前活动profile305经CLI plan/validate/apply部署，生成包源/目标SHA256一致。
证据：`../evidence/refresh/cw36-auto-20260926`。
源协议字节前后校验一致，未编译或改动用户的映射设置。

## 行为

- 独立文件`%LOCALAPPDATA%\CowboyBingus\Helldivers2\ArmorRegistry\refresh-auto-test.config`
  显式启用；缺失/disabled/过期默认关闭。
- 本轮期限：2026-09-27 16:50:19 +08:00。
- 白名单CW-36 ID190632838，每个游戏进程最多6次写尝试。
- 初次启动协议发布只建立基线，不触发刷新。
- 后续成功commit且CW-36实际索引发生变化才排队；覆盖新增、删除、组件变化。
  失败发布、无变化应用、其他目标变化不触发。
- 合并连续发布；等待1秒，触发间隔至少约5秒（前次观察结束后算）。
- 执行前确认当前穿着CW-36：requested和applied的+0xc均为目标ID。
- 复用当前版本指纹、代码字节、单玩家双向身份、稳定性复核、可写页面检查。
- 只置位1字节，不执行未知函数；确认消费和输出更换。
- 异常或结果不符关闭本进程实验，不无限重试。
- 不是完整舰船/军械库状态识别器；用户必须遵循下方实验条件。
- 本轮不声称完整游戏属性、模型资源生命周期、多人或跨场景安全性通过。

## 人工测试步骤

1. 使用刚构建的Debug管理器；从Steam启动游戏，进入单人舰船。
2. 装备CW-36，确认初始自定义外观可见。关闭军械库，站稳约5秒。
3. 切至管理器，解除CW-36映射，点击护甲映射页“应用”。
   不取消模型包部署，不关闭Lua支持；只改映射。
4. 切回游戏，不打开军械库，不切装备，等待约3–5秒。
   预期恢复原版CW-36外观。
5. 再给CW-36加回同一个自定义来源，点击应用，切回游戏观察是否自动变回。
6. 可新增另一个未装备护甲的映射并应用：当前CW-36不应因此刷新。
   若要测试其他已装备目标的自动刷新，需要下一轮扩大白名单，本轮不支持。

每步之间等待至少约8秒，避免连续操作干扰验收。
若崩溃、模型消失、属性变化或刷新无反应，停止继续切换并反馈。
失败后不会不断尝试；不要把反复点击应用当作恢复方式。

## 日志与停用

独立日志：
`%LOCALAPPDATA%\CowboyBingus\Helldivers2\ArmorRegistry\refresh-auto-test-<PID>.log`
关键事件也进入原`Logs\ArmorRegistryRuntime.log`，前缀`refresh_test:`。
关注auto_refresh_queued、pulse_written、code_survey_end及auto_refresh_disabled。
日志包含具体版本、目标、状态字节、输出前后数据。

紧急停用：将refresh-auto-test.config的enabled=true改为enabled=false，
或删除该实验文件。不会改动映射定义。正在发生的单次游戏更新不可撤销。
游戏退出后没有堆地址被持久化为下一会话写入地址。

## 实现位置与验证覆盖

- `src/armor_refresh_route_probe.lua`：auto队列与pulse目标白名单。
- `src/armor_registry_runtime.lua`：commit成功通知、独立测试配置/日志、
  已审查单字节写入的共用实现。
- tests覆盖启动抑制、未装备跳过、目标匹配、增删/无操作通知、
  失败发布不通知、队列合并、停用及每进程预算。
- 测试性路径未作为GUI正式功能公开，也未提交Git。
