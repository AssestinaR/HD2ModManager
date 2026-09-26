## 最新：连续刷新修复与全body护甲范围已部署（待验收）

见 [refresh-stability-global-20260926.md](refresh-stability-global-20260926.md)。
用户失效是第4次applied+40派生值变化触发保护；当前尾部代码已读取确认。
合并/保留队列、取消正常supersession熔断、去六次上限、仍限单玩家舰船测试。
新开关refresh-auto.config，原revision59协议保留；不是全场景生产安全认证。

## 最新：CW-36自动刷新实验已部署，待用户视觉验收

见 [refresh-cw36-auto-test-20260926.md](refresh-cw36-auto-test-20260926.md)。
保留revision47，白名单190632838；成功增量发布后仅已装备目标触发，初次启动不触发。
测试配置2026-09-27 16:50 +08到期；本轮未启动游戏，勿宣称已完成视觉验收。

## 最新进度：单次pending触发更新装配输出（2026-09-26 16:40）

见 [refresh-pending-pulse-20260926.md](refresh-pending-pulse-20260926.md)。
只写1字节，由游戏原流程消费；16个非零输出全部更换，装备状态记录不变。
零映射原外观实验成功，不等于自定义映射视觉验证或生产安全性通过。

## 最新进度：本地玩家身份关联通过（2026-09-26 16:29）

见 [refresh-identity-join-20260926.md](refresh-identity-join-20260926.md)。
当前版本玩家记录→护甲实例→avatar外观组件双向核验通过；刷新调用仍未执行。
保留用户revision46零映射配置；下一步审清dirty消费的拆除/重建及非视觉副作用。

# 玩家外观刷新：V9 接续

**最新：当前版本实机重新定位成功！先读refresh-current-route-20260926.md。
160528证据：874520比较/置位→874c0d调用877ab0→清applied+29；
下游81e7a0/81f470/81ed60读取Kit/Body/Piece结构。
全局33264f8同时含Kit表及小型实例状态，3326468玩家侧，3326d20组件侧。
6候选/4窗口，离线11结构断言通过；未调用/置位，生产映射不变。
下一步一次性只读交叉验证本地玩家身份与小型实例/装配输出，不再V4重复采样。
游戏PID73512已结束且无残留。以下历史。**

**最新：用户要求换路线并止损。离线重审旧完整转储，找到具体候选链：
历史+86a730比较requested/applied并置applied+29，
调用+86db90后清位；后者调用+8157e0/+8164b0/+815da0，
这些消费者读取Kit+30/+38、Body24、Piece96，与已验证协议吻合。
见refresh-offline-breakthrough-20260926.md。这是旧版静态证据，不可直接调用！
修正旧+a7c步长0x60→0x40、d3e370资源解析器误命名。
下步只重定位并验证该链，不继续泛化快照。未启动/部署/写游戏内存。**

**最新用户V4结果154021已离线分析：8有效窗/862帧/6次14换14保留4；
1682候选记录有效。换装时只有已知avatar+1d8链表头特异变化，
+40静置也递增；hub/df/eb头部在6次换装全不变。
见refresh-multisite-result-20260926-154021.md与证据analysis-summary.json。
不再请求同样采样；下一步优先已有代码的链表写入上层/有界数组访问者。
头部不变不排除指向数组或瞬态行为。未发现刷新入口，本轮未部署/启动游戏。**

**最新：多位置V4已部署、静置实机自检通过。先读
`refresh-multisite-20260926.md`。avatar/hub/df/eb同时观察，旧124仅目录。
成功153707窗口132帧，候选237条均captured，候选批次最大0.4284ms。
已修复游戏环境长十六进制地址解析差异。游戏/探针均已停止。
下一步用户1次静置+2次换装，尚未找到刷新入口。以下均为历史。**

**最新接续：用户152256 V3实测8窗/800帧，6次14换14；
0x124组件504条有效观察全静止、游标/记录字段0、地址相等0，降低优先级，
不要再重复此候选实验。资源新节点覆盖6次均13/14，后5次前后各13/14，
ID一致符合用户刻意全部映射同一素体的控制条件。
见refresh-component-result-20260926-152256.md及证据analysis-summary.json。
当前未改源码/部署，下一版默认停0x124，转向实际装配持有者/注册表消费端。
以下为历史：hotkey_version=3 已部署，新增固定0x124组件时间序列与地址相等检查，
成员变化触发资源扫描优先级调整，主窗口±0.5秒，资源额外补采0.75秒。
静置实测组件72次全零无变化；待用户实际换装验证，不宣称0x124为护甲。
见refresh-hotkey-guide.md末尾。用户明确同素体映射是有意控制变量。
以下为历史：用户深度采样151132完成，5窗/531帧/4次14换14，
跨相邻文件前4个装配状态各17/18身份，已观察ID集合相同（12个自定义Unit）。
最后新状态ID缺失，说明资源分批采样优先级需修，不需重复原样测试。
详见refresh-resource-result-20260926-151132.md及证据目录analysis-summary.json。
以下为历史：Lua hotkey_version=2 深度资源观察已部署。
静置自检143帧、17/18子对象关联资源ID（包括5236bd49853b系列），
见refresh-hotkey-guide.md尾部；待用户1次静置＋2次换装。
资源关联是独立时间戳证据，非完整快照；不宣称刷新入口已找到。
以下为历史：用户V2快捷键实测8窗有效、901帧，
前两窗无变化，后六窗每次移出14/加入14/保留同一4个；
观察夹逼约9–11ms，变化均在标记前。
见 `refresh-hotkey-result-20260926-150035.md`，结果目录已有analysis-summary.json。
无需重复同样样本；下阶段应增加资源身份/装配状态维度。
以下为历史：已制作并部署F8快捷键前后0.5秒滚动观察工具，
实机静置合成标记116样本有效，等待用户真实同步换装。
见 `refresh-hotkey-guide.md`。脚本入口 `scripts/Start-ArmorRefreshHotkey.cmd`。
本轮停止游戏PID19012且无残留；原映射不变；Lua全套与Core嵌入测试1/1通过。
资源ID关联尚未完成，本版仅高频装配列表。以下为历史：
V11一次合并采样表明该表属于按编号通用分发。
停止逐项追整张表，参见 `refresh-route-decision-20260926.md`，
下一步回到换装相关avatar装配证据。用户要求效率并接受有依据的单次风险实验。
以下为历史：V9/V10 两轮完成，确认0x124实时对象以及静态代码指针段，
新线索为 game+bc2d16 → game+214c2e0（字节候选，尚未解码验证）。
先看 `refresh-dispatch-table-20260926.md`。游戏及探针均已停止。
以下是历史：
V8 已采样完成，找到 game+12acf50 所属范围及两个上层调用点，
两者均按组件类型 0x124 分发，尚未确定是否属于护甲。
先看 `refresh-component-chain-20260926.md`。
下述 V6/V7 是上一阶段：
残留已清理，survey_format 6/7 两次采样完成。
已确认 master+e0 / master+8 的接口别名，并在 game+12ad1ce 找到
到 enqueue_node_link 的静态调用链，尚不是护甲刷新入口。
请先看 `refresh-master-interface-20260926.md`；
下文保留早期历史暂停点。**

## 目标及当前结论

目标是在注册表发布成功后，只刷新本地玩家受影响的视觉装配，
不改装备身份、属性、其他玩家、军械库预览或游戏状态。
目前仍没有已验证的重建入口，不能宣称自动刷新已实现。
avatar 到护甲子 Unit 的关联已在旧证据中确认，但不等于找到装备控制器。

## 本轮实现（尚未部署/实机验证）

- `armor_registry_runtime.lua`：追加 World.link_unit 的已审查直接调用点代码采样，
  包装 RVA 0x3eb860、E8 +0xb8，预期 callee 0x3361c0；
  仍锁定已观察到的 EXE 指纹，只读 2048 字节窗口，绝不调用该函数。
- V9 探针增加 `owner_trace`。旧 owner_native 仍保持一次性取样，
  新模式只追踪 avatar 资源，使用重新枚举、alive 检查、generation 校验。
  最快每 250ms 一个原生对象、每阶段最多 160 次。只保存标量和列表文本，
  不持有跨帧原生对象或 Unit 引用，不反复输出 2KB 机器码。
- 各条观察带前次样本及变化标记，并保留完整子节点列表用于离线复核。
  稳定地址或 generation 仍不能排除 ABA；读取不是原子快照。
- 向导增加 `-NativeOwnerTransitions`：
  先检验可重复取得完整链表，再采“军械库内静置、确认装备 B、确认装备 A”，
  每阶段默认 10 秒/3 秒切回倒计时。无需新增一次关闭军械库实验。
- `scripts/summarize_refresh_transitions.py`：只在同阶段/同对象观察键内比较，
  区分列表重排与成员变化；失败读数打断比较，不推断对象销毁。
  拒绝混合进程、会话或注册 revision。需配合操作提示时间和用户确认解读。

## 实机工作受阻原因

本机另一个诊断向导仍持有库写锁，恢复旧实验配置被正常拒绝。
没有终止向导、绕过锁、修改协议、部署新探针或启动游戏。
详情见 `armory-crash-checkpoint-20260926.md`。先完成那个向导的恢复，
再用 Set-ArmoryControlStage.ps1 恢复 revision 45；不能在 leftover expanded_present
上混做刷新测试。

## 后续顺序

1. 确认诊断向导完成、游戏/管理器关闭、恢复协议与部署。
2. Debug CLI 构建，CLI plan/validate/apply 部署 V9（不重新编译映射）。
3. 自动 BindingPreflight 采 World.link_unit callee；离线审查，绝不调用。
4. 确有需要再让用户运行下述短向导；不要重复广域走动采样。

```powershell
pwsh -File E:\Data\source\repos\WpfApp1\HD2RuntimeArmorOverride\scripts\Watch-ArmorRefresh.ps1 -NativeOwnerTransitions
python E:\Data\source\repos\WpfApp1\HD2RuntimeArmorOverride\scripts\summarize_refresh_transitions.py <结果目录>
```

5. 定位谁消费注册并替换该装配。只有确认本地玩家身份、入口参数、线程、
   生命周期和视觉独立性，才尝试单次重建；link_unit 不作为刷新捷径。
6. 正式版刷新应增量、合并请求，处理过期请求、场景切换、预览、失败降级，
   并验证没有修改 gameplay 装备状态。此处仅是实现门槛，不是已完成能力。

## 本轮验证

- scripts/Test.ps1 全部 Lua 测试通过，包括时间序列限速、160 次上限、
  失败观察、只读约束、World 预检和新绑定捕获门槛。
- Python 时间序列摘要测试通过：重排/成员变化、失败读数、残缺日志、
  错误计数和跨会话拒绝。
- PowerShell 向导 AST 解析无错误。
- Core 嵌入 Lua 源测试 1/1 通过，包含 V9 与新模式断言。
- CLI 项目内 Debug 构建成功。Core 测试构建仍有既有平台/测试分析警告。
- 没有声称全量 .NET 测试通过；没有实机新证据，没有 Git 提交。
