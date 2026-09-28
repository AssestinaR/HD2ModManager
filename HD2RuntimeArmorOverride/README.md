# HD2 Runtime Armor Registry

## Runtime 0.2.0

运行时现按目标的最终视觉定义增量发布：未变化目标不重新分配 clone、
不改写索引；已发布对象保留到本次 Lua 状态结束，重复 A/B/A 切换可复用。
定位与准备采用每帧软时间预算，协议格式仍为 schema 1/2。
本轮为自动化测试通过、待真实游戏压力验收的版本，不能据此保证消除所有闪退。
安全边界、内存上限、日志字段与验收步骤见
`notes/incremental-runtime-20260925.md`。

《绝地潜兵 2》护甲视觉组合的运行时注册与热更新系统。

正式运行时通过 Bingus Shared Loader 的 LuaJIT/FFI 定位游戏已经加载的
Armor Archive 注册表，构造保留目标身份与 gameplay metadata 的组合对象，
再只替换目标护甲的注册根指针。它不会修改磁盘上的
`generated_customization_armor_sets.dl_bin`。

## 已验证能力

- 当前游戏版本可发现 411 个 Kit、完整注册 blob 与根指针 index。
- B-08 可采用 RE-2310 的完整 23 Piece 视觉组合，同时保留 B-08 的名称、
  轻甲数值与被动技能。
- 与 B-08 共用可见 Unit 的 FS-38、CE-74 等护甲不受污染。
- 目标与来源槽位数量可以不同，运行时 clone 可扩展目标组合。
- 外置协议约 1 秒轮询一次，可在不重启游戏的情况下启用、撤销或更换映射。
- 新进程只做一次定位；同版本优先使用结构缓存快速重定位。
- 游戏版本或结构变化时会拒绝旧缓存并自动重新发现，不复用跨进程绝对地址。

这些结果证明注册重映射链路可用。磁盘、内存与显存节省比例仍需用实际
去重 Mod 组合单独测量，不能只由视觉测试推断。

## 正式文件

- `src/armor_registry_runtime.lua`：定位、clone、发布、回滚与热更新运行时。
- `src/armor_metadata_probe.lua` 与 `src/armor_metadata_probe_core.lua`：默认空闲、按请求生成 Kit/Piece 只读快照，并以与正式 clone 相同的多数 weight 规则模拟目标 weight 保留。
- `src/armor_registry_protocol.lua`：严格的 v1 协议解析与序列化。
- `scripts/build_armor_registry.py`：生成单文件 Lua 入口。
- `protocol/armor-registry.protocol`：默认关闭的协议示例。
- `notes/armor-registry-protocol.md`：管理器协议、安全边界与 v2 演进设计。
- `tests/test_armor_registry_protocol.lua`：LuaJIT 协议测试。

`HD2-Runtime-Armor-Registry` 正式入口包含默认空闲的元数据探针；没有请求文件时
不会遍历或采样护甲。其余 `composition_*`、旧探针、日志和 evidence 是定位
ABI 与验证消费链的研究材料，继续保留用于游戏更新后的诊断。

### 只读元数据探针

管理器托管运行时和 `HD2-Runtime-Armor-Registry.zip` 都包含此探针；direct 与
Shared Loader 部署都可使用。它不会读取或写入注册表协议，也不会写游戏进程
内存。准备请求文件：

```text
%LOCALAPPDATA%/CowboyBingus/Helldivers2/ArmorRegistry/metadata-probe.request
```

请求文件每次指定一个来源和最多 16 个目标，例如：

```text
source=820333399
target=488691438
target=3340614564
target=651522724
```

游戏内探针会消费请求，并将 JSONL 写入同目录的
`metadata-probe.report`，运行日志位于 `Logs/ArmorMetadataProbe.log`。报告包含
原始 Kit/Piece 字段、目标 identity header、来源 Piece 与私有模拟结果的差异。
weight 全量审计会逐目标报告精确键、同 Body 类别和全局类别各自解析的 Piece 数量；
恢复 weight 时按唯一众数逐级回退，完全平票仍会阻止映射。
私有模拟不发布指针。它能验证当前克隆逻辑的数据变更范围，但不能单独证明
某字段对游戏玩法属性的因果影响；该结论仍需与受控游戏内属性测试结合。

## 外置协议

默认目录：

```text
%LOCALAPPDATA%/CowboyBingus/Helldivers2/ArmorRegistry
```

文件所有权：

- `armor-registry.protocol`：管理器写入的期望状态。
- `.manager.lock`：CLI 协调并发写入的零内容锁文件。
- `runtime.status`：Lua 写入的当前进程状态与地址摘要。
- `locator.cache`：Lua 写入的游戏指纹和结构形态，不含可跨进程复用的地址。

使用 CLI 修改协议：

```powershell
hd2mod armor-registry status --json
hd2mod armor-registry show --json
hd2mod armor-registry plan armor-registry-spec.json --output armor-registry-plan.json --json
hd2mod armor-registry validate --plan armor-registry-plan.json --json
hd2mod armor-registry apply --plan armor-registry-plan.json --confirm --json
```

规格文件示例：

```json
{
  "schema": "hd2mod.armor-registry-spec",
  "enabled": true,
  "mappings": [
    {
      "targetItemId": 488691438,
      "sourceItemId": 820333399
    }
  ]
}
```

CLI 仅原子替换 `armor-registry.protocol`，不会写 Lua 拥有的状态或缓存文件。
`armor-registry status` 还会验证状态中的 PID 是否确为正在运行的游戏进程，
并报告该进程是否已经确认当前协议 revision。

管理器顶栏的护甲映射页使用资产索引中的护甲名称选择目标与视觉来源，
并显示 Lua 是否已确认当前 revision；用户不需要手工查询 Item ID。
自定义素体及组件启用状态保存在当前 Mod 库的
`.hd2modmanager/armor-registry.workspace.json`。点击“应用”时，管理器
按实际部署的 Unit 编译运行时协议到上述 C 盘目录；Lua 只消费编译结果。
GUI 与 CLI 使用同一把 `.manager.lock`、同一份指纹漂移检查和原子写入流程。

`src/armor_registry_composition.lua` 的默认 `full` 模式可以按视觉来源重组
目标的 Body/Piece；`existing_only` 仅用于军械库崩溃的隔离对照，
保留目标原有槽位并只替换匹配位置的 Unit ID。它会跳过放不进目标槽位的
来源组件，不能作为完整映射或修复方案。便携诊断工具位于
`HD2ModCli`，不会随正式 Lua 入口执行；测试包与报告不属于正式部署资产。

## 测试与构建

```powershell
.\HD2RuntimeArmorOverride\scripts\Test.ps1
.\HD2RuntimeArmorOverride\scripts\Build.ps1
```

正式包输出为：

```text
HD2RuntimeArmorOverride/dist/HD2-Runtime-Armor-Registry.zip
```

构建依赖 `docs/BingusSharedLoader-main/scripts/build_addon.py`，也可通过
`Build.ps1 -LoaderSource <目录>` 指定 Bingus Shared Loader v15+ 源码。

## 虚拟模型素体

协议 `schema=2` 已把“真实游戏护甲”与“Lua 自有模型素体”分离。模型素体
使用稳定字符串 key，不占用或伪造游戏 Item ID，也不需要原版注册表中存在
对应护甲。当前首版模型定义以一个真实护甲作为经过验证的 Body/Piece 模板，
多个真实护甲可以引用同一 `model key`：

```text
schema=2
revision=8
enabled=true
model=base/re2310,820333399
map_model=651522724,base/re2310
```

模型素体只能作为视觉来源，不能作为映射目标；目标护甲的名称、护甲值、
被动技能、解锁状态和其他 gameplay metadata 始终保留。管理器已支持
编辑自定义 Body/Piece/Unit 组合及来源、目标组件的启用状态。

虚拟模型只提供视觉组合，不携带名称、护甲数值或被动技能。目标护甲始终
保留自己的身份和 gameplay metadata。具体 v2 语义与发布条件见协议文档。
