# 本地玩家 → 小型护甲实例 → 外观组件：实机身份关联通过

## 本轮结论

刷新尚未实现，没有写 dirty 位、调用未知函数、改装备 ID 或附加调试器。
不再重复广域扫描。本轮仅固定代码验证、三段各 1024 字节的代码读取、
小型实例表、精确散列表查找和两次稳定性复核。

有效证据：
`../evidence/refresh/20260926-162920-0d18a485a8124016825165692750a4cb`
内有 `identity-analysis.json`。PID68212；revision46、零映射。
当前游戏指纹仍 timestamp1790161983 / size74727424。

两次观测均为：

|节点|结果|
|---|---|
|customization instance index|0|
|entry.key08 / 玩家记录 ID|5|
|玩家表查询索引|0|
|player+3a8 的 engine Unit|164|
|entity descriptor ID|251|
|entity resource hash|4d1c334d294dfa97|
|avatar component index|0|
|实体描述符 flags|1|
|applied+29 dirty|0|
|body flag|1|

玩家记录24字节与 customization entry 完全一致。
由玩家 engine Unit 查询的 entity、由 entity ID 反查的 entity、
avatar+110[0] 指向的 entity 三者24字节完全一致。
所有读取的根、表头、条目和状态复读一致；并非持锁的原子快照保证。

三个已审查输出缓冲区（各0x28）共16个非零32位值，
两次完全一致。**未证明这些值的句柄编码，也未与旧14子节点结果比较**：
本轮零映射原版外观，不能沿用旧135映射时的部件数量。

## 新审查的精确身份翻译

来自当前代码捕获，而非直接移植旧版：

```text
606630(entry.key08)
  player = *game+3326468
  lookup(player+d0, key08) -> player index
  engineUnit = player+3a8 + index*32
  -> fd9ba0(engineUnit)

fd9ba0(engineUnit)
  owner = *game+346bf98
  lookup(owner+f22ec8, engineUnit) -> entity index
  entity = owner+f32f18 + index*24
  return entity+8 (ID)

fd9d40(entityID)
  lookup(owner+f1aeb0, entityID) -> entity index
  return owner+f32f18 + index*24

877ab0
  lookup(avatar+f8, entityID) -> avatar index
  outputs = avatar+546b4c/+546b74/+546b9c + index*1b8
```

Lua只复现这些读操作，不调用上述函数。
散列乘法按16位拆分计算低32位，避免Lua double大整数乘法舍入。
当前地址和布局只对固定指纹诊断启用，不是已完成的跨版本产品定位器。

## 有价值但不能夸大的启动对照

同一PID68212的稍早采样：
`../evidence/refresh/20260926-162813-464733ded85b492f9ed18036f33cec95`
当时 instance key=5、dirty=1；local Unit=0x7fff，avatar count=0。
随后进入场景才得到上述完整身份链、dirty=0和非空输出。

说明 runtime.status ready 只表示注册表就绪，不保证玩家实体已生成。
启动前后结果与已解码 dirty 消费链吻合，但没有调用跟踪，
不能声称已经证明手动置位的安全性、或解决了外观刷新。

## 修复的诊断工具问题

- 旧JSON日志序列化只接受标量，嵌套 instances/components 会输出 null。
  本轮改为 identity_snapshot / identity_instance / identity_component 独立标量行，
  没有改全局序列化语义。
- 新模式几十毫秒内完成，向导100ms轮询可能错过capturing。
  仅自动identity模式接受同请求的complete；人工动作模式仍拒绝错过采样。
- code探针错误现在计入native_errors；不再把只读代码捕获标为native调用。
- 新增离线严格关联分析器 `scripts/summarize_refresh_identity.py`。

## 会话与配置保护

- 用户新配置revision46 / 零映射全程保留，未恢复旧revision45。
- 当前profile revision305，按当前活动profile plan/validate/apply部署新探针。
- 协议SHA256前后：
  EB3965D53AD7289BA7E7A5988E708D422C4A37F805905EE397B2882E4D2096F7。
- PID44336首次只读取证后主动结束；初版嵌套行丢失，仅代码证据可用。
- PID72648在身份探针请求发送前退出；Application日志记录ntdll c0000026，
  同时有LiveKernelEvent141报告。只保存相关性，不据此诊断崩溃根因；
  不应归咎于尚未执行的identity读取，也不能排除其他运行时/环境因素。
- 之后仅重试一次PID68212，完成上述采样，探针stopped后主动结束。
- 项目内Debug构建，无外部构建目录，无Git提交。

## 下一步：只剩一个具体调用边界问题

已不再缺“这是哪个玩家”的证据；当前最短路径是审查 dirty 消费的完整副作用，
而不是重做对象搜寻。877ab0不是纯视觉函数：
当前代码877f3a以后的owned分支包含数值计算、avatar状态写入及9ccae0等调用，
随后还调用8202e0、877200。因此不能直接宣传为安全刷新API。

优先检查：
1. 原dirty消费的调用时序是否无需额外拆除旧装配，以及是否每帧自然轮询。
2. 877ab0前段的三个装配消费者是否自行替换/释放旧输出；
   后段在装备ID不变时是否仅重算原值，还是有不可接受的额外动作。
3. 只在上述得到明确证据后，设计一次本地玩家、舰船内、单次触发的实验，
   固定装备ID、属性和头盔，不做每帧强制重建。成功仍需用户视觉确认。

不要再次请求同样8轮军械库采样；不要直接执行未知函数或跨会话保存堆地址。
