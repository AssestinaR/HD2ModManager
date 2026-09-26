# 路线重审：旧转储内的外观装配消费链（非当前版本调用地址）

## 本轮边界

仅离线读取现有源码、历史日志和`tmp/game-runtime-55640-20260921.bin`；
未启动/结束游戏、附加调试器、部署探针、修改源JSON或运行时协议。
新增可重复的离线分析脚本`../scripts/review_refresh_static.py`。
机器可读结果、展开链分段反汇编：
`../evidence/refresh/offline-route-review-20260926/`。
没有自动刷新实机成果，但已从“装配前后差异”转为具体静态消费链。

## 版本与解码

历史转储timestamp=1787171630，image size=61255680。
当前先前实测game.dll timestamp=1790161983，image size=74727424。
**以下RVA全部属于历史版本，不允许复制进当前Lua直接读写或调用。**
脚本核验历史指纹、记录SHA256；依据PE exception directory及
UNW_FLAG_CHAININFO将分段恢复到所属展开链；所有选中段完整解码、
选中指令边界检查通过。展开链不是动态调用栈，也不证明可调用ABI。

## 1. 为什么旧Lua表采样不能排除上层接口

旧probe只递归名称命中terms的根表，并显式选择部分stingray模块。
即使sample_end.truncated=false，也只表示这个筛选范围内完成，
不是穷尽Lua全局、闭包upvalue、所有组件或其他Lua state。
早期样本只列boot的init/render/shutdown和已包装update，
没有确认任何游戏专用装备入口。不能继续靠重复同一成员清单找调用过程。
本地加载器只负责启动模块；存在armory_preview_cache资源名，
但本地提供的加载器源码没有该模块实现，不能从模块名推断刷新能力。

## 2. 顶层UI名不是可执行装备命令

旧字符串EquipItem的引用已解析：
- +1281c90写对象+80并发属性变更通知；
- +1282419属于+12822b4属性注册链，注册getter等成员。
这不是“调用EquipItem即可换装”的证据。停止把UI名称当动作入口。

## 3. 实际更有价值的链：状态差异 -> 待更新位 -> 装配函数

### 输入/同步侧（不是最窄刷新入口）

+11ce6b0对配置字段逐项比较，差异时调用+86b190/+86bdb0/
+86bfa0/+86c190等，并写自身+212921。
该+212921的读取者+11c4290还处理UI状态/提示；**不是已证明的模型dirty位**。
+162d2f0也调用上述设置函数，但还复制整段配置并跳到+10d56f0。
不应使用这一整套配置应用作为“只刷外观”的捷径。

### 真正值得重定位的装配更新门槛

旧+86af90循环小型实例表，调用+86a730。
在+86a730：

```text
instance entry = manager + 0x948[index]
requested state = manager + 0xa7c + index*0x40
applied state   = manager + 0x96c + index*0x44
compare requested/applied fields -> local changed flag (SIL)
applied[0x29] = applied[0x29] OR changed
resolve target identity; if unavailable, skip remaining work
if applied[0x29] != 0:
    call +86db90(manager, entry.key08, resolved_identity)
    applied[0x29] = 0
```

直接指令：
- +86abfe..86ac0f：读取/合并+29待更新位；
- +86ae09：检查+29；
- +86ae0f..86ae1a：传manager、entry+8及解析出的身份，call +86db90；
- +86ae1f：清零+29。

**这提供了具体的、由游戏自身更新过程消费的待更新位候选，
不再只是一个“换装时变动的指针”。但未证明当前版本同结构。**

### 下游确实读取了我们修改的Kit/Body/Piece形状

+86db90通过实例key查询+930映射，访问applied state；
同时通过另一个管理器的+f8映射查实例，步长0x1b8，
取得若干输出槽位地址，调用：
- +8157e0
- +8164b0
- +815da0

这三个函数不是仅通用link：
- 查manager首部的Kit索引（指针表+0，count+8）；
- Kit的+30是Body表、+38为数量；Body步长24；
- Body的+8是Piece表、+10为数量；Piece步长96；
- Body种类与输入匹配或等于3时纳入；
- 读取Piece的Unit ID及slot，调用引擎表，并写出实例句柄。

这些字段与项目已验证的`composition_schema.lua` Kit/Body/Piece布局吻合。
这是本轮最强的语义交叉验证：小型实例状态链能走到实际视觉定义消费者。
尚未独立解码所有间接引擎函数，不能把每个间接call命名为spawn/destroy。

## 4. 可解释“注册变化快，但当前模型不刷新”的假设

若只换Kit/Piece内容，而装备ID/身形等requested字段不变，
requested/applied比较可能看不到差异，+29也不会因此置位。
于是新装配使用新注册，但已装备实例不会自动重建。
这与用户表现相容，**仍是由旧代码提出的可检验假设，不是当前根因定论**。

旧+86b190还有相同值早退，简单“重新应用相同值”也可能无效；
不能据此去临时篡改装备ID或属性来强迫变化。

## 5. 必须纠正的历史笔记

`tmp/armor-consumer-cache-static-20260922.md`等旧笔记有错误/过度命名：
- +a7c在这条链的步长是`shl rax,6 = 0x40`，不是0x60。
- +d3e370可解码，实际构造(entity-like ID, discriminator, payload pointer)
  三元记录并入队/提交；不能仅凭它使用type常量就叫“资源解析器”。
- +948最多4项的表更像每个实例/玩家的状态，不是402/411个Kit表。
  数量4本身不证明本地玩家身份；新的语义依据来自调用方和下游Kit读取。
保存原笔记作历史记录，不再把旧注释直接当证据。

## 6. 风险与窄范围实验前门槛

+86db90不是已证明的纯视觉函数：末尾还调用+86d2e0，
后者明确引用Player_ArmorWeight/Light/Medium/Heavy并调音频接口；
还有其他数值/状态下游未解释。因此**不立即写+29，不直接调+86db90**。
三个装配函数依赖实例输出槽位和有效句柄，不能凭静态参数形状拼调用。

三个装配消费者都有0x14（20）条收集上界。这是旧版局部列表上限，
不证明当前游戏全部槽位上限，更不证明军械库崩溃由此造成；
该支线保留，不在本轮扩展。

下一步只验证一个候选链，不再做泛化快照：
1. 当前版本中重定位这组函数/全局，匹配数据布局和分支关系，
   不按版本差值平移，不以单个短签名作为写入授权。
2. 只读验证小型实例表key与本地avatar身份的关系，以及+29的更新门槛。
3. 优先隔离已有视觉装配调用；仅在实例、生命周期、线程及额外作用
   已理解时，设计一次单目标刷新实验。
4. 若当前版本无法结构性匹配，停止这条候选；不让用户重复无关换装。

本轮没有当前可调用入口，没有制作宣称有效的刷新补丁，也没有Git提交。
