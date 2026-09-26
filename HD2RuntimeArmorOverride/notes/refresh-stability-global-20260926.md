# 全护甲本地刷新与连续切换修复（待新一轮压力验收）

## 失效根因：证据，而非六次上限猜测

用户PID52544的第4次触发/revision51：
dirty已清除、输出更新、requested不变，但applied+0x40从2变0。
旧测试要求整段applied除dirty外不变，因此记录postconditions_not_met，
永久停用本进程。实际未到6次上限。
原日志已保存在`../evidence/refresh/refresh-stability-20260926/user-test.log`。

为避免盲目放宽检查，自动启动读取当前877200的1024字节：
`../evidence/refresh/20260926-170724-1fa984805f914010bde8a61b20b75e39`。
PID68804，无刷新写入（旧测试开关已关闭），采样结束主动结束进程。
第一次启动未留下游戏进程，仅重试一次。

当前代码明确：
- 8772bb按index*0x44选中实例；
- 8772cf从applied+0xc的护甲ID找Kit；
- 877328/32e/334按Piece字段筛选并读取+0x10；
- 87734c写manager+index*0x44+0x9ac，即applied+0x40；
- 877362读取该值分支选择字符串，再调用接口+0x90。
与历史已识别Player_ArmorWeight分类链一致。当前字符串未重新读取，
故音效分类语义有历史佐证，并非本轮逐字符串验证。
不能将该字段变化简单解释为护甲值变化，也不能声称刷新完全没有音效副作用。

## 修改

1. 自动路径仍检查requested不变、身份稳定，以及applied前0x40字节
   （只屏蔽dirty）。仅尾部4字节不再作为致命不变量，仍完整记录。
   增加3个当前尾部指令字节检查，版本/代码不符关闭。
2. 所有body护甲ID参与变化集合。触发前与当前已装备护甲+0xc匹配；
   未装备目标无需刷新。没有扩展头盔、披风或队友。
3. 成功commit的实际operations产生目标集合；初次启动只基线，
   无操作/失败发布不触发。合并连续revision、保留所有待处理目标。
4. 新revision到来时，旧观察取消/重解析而不永久禁用。
   runtime忙不再丢任务；等候最多30秒，不无限轮询。
5. 无六次总次数上限。仍串行，750ms合并窗口，完成后至少1秒间隔。
   dirty消费且连续3次输出稳定、至少500ms后可结束观察；
   3秒无消费或受保护字段变化仍熔断。
6. 普通换装/场景导致身份变化时取消旧地址观察并重新解析，
   不再继续使用过期堆地址。初始不稳定/已有pending限时等待。
7. 输出全空不阻止下一次刷新，支持隐藏全部组件后重新启用。
8. 独立状态文件refresh-auto.status；PID日志每2MiB轮换一次并保留previous。
   旧测试日志/开关保留为证据，但不再控制新路径。

## 仍保留的安全边界

- 单玩家、单组件和owned身份限制未解除，多人会延后/跳过。
- 仍只有单字节pending写入，不执行未知函数或修改代码保护。
- 当前build固定指纹检查，没有假装已实现跨版本自动修复。
- 未验证完整军械库/预览/任务场景状态门槛；用户仍需在单人舰船、
  军械库关闭时应用。不应打包为无条件全场景功能。
- 通过用户视觉验证的是上一版CW-36；新队列已通过模拟测试，
  尚未由用户完成连续切换、多护甲视觉压力验收。
- requested和applied保护字段不变不等于所有游戏属性完全审计。

## 配置、部署与测试

新独立开关：
`%LOCALAPPDATA%\CowboyBingus\Helldivers2\ArmorRegistry\refresh-auto.config`
schema=2, enabled=true, scope=local_equipped_body,
confirm=single_player_ship_armory_closed。
本机已开启，无临时过期/次数上限；缺文件默认关闭，改enabled=false可停用。
尚未增加GUI开关，因此不等于其他用户安装后自动启用。

旧refresh-auto-test.config保持disabled，不删除原数据。
用户revision59协议前后hash一致；profile305未改。
管理器和CLI均项目内Debug构建，当前profile经plan/validate/apply部署。
最终compositionFingerprint：
657f8bcd35e406d749ee93adfb3518ff5fc4968a97554de061ecb4613072fe97。

Lua全套、Core嵌入测试通过；新增测试覆盖：
末尾派生值变化不熔断、保护字段变化熔断、全空视觉恢复、
全目标匹配、忙状态保留、目标集合合并、处理中revision替换、
超过6次、开关停用、30秒有界重试、普通装备变动重新解析。
没有Git提交。

## 下一轮用户验收

在单人舰船穿任意目标护甲（可先CW-36，再一套其他护甲），关闭军械库；
只改映射并应用，测试来回超过6次，再测试短间隔应用最终是否一致。
增加未装备目标映射不应重建当前模型。
不要同时启停模型包/部署，不在军械库中应用，不用多人任务做首轮验证。
若失效请保留refresh-auto.status和refresh-auto-<PID>.log；
这版会区分无目标变化、目标未装备、上下文等待超时、保护熔断。
