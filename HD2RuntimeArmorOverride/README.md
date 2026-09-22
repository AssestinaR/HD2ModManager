# HD2 Runtime Armor Registry

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
- `src/armor_registry_protocol.lua`：严格的 v1 协议解析与序列化。
- `scripts/build_armor_registry.py`：生成单文件 Lua 入口。
- `protocol/armor-registry.protocol`：默认关闭的协议示例。
- `notes/armor-registry-protocol.md`：管理器协议、安全边界与 v2 演进设计。
- `tests/test_armor_registry_protocol.lua`：LuaJIT 协议测试。

其余 `composition_*`、探针、日志和 evidence 是定位 ABI 与验证消费链的研究
材料，继续保留用于游戏更新后的诊断，不属于正常运行时部署面。

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

后续协议会把“真实游戏护甲”与“Lua 自有模型素体”分离。模型素体使用稳定
字符串 key，不占用或伪造游戏 Item ID，也不需要原版注册表中存在对应护甲。
管理器可定义完整 Body/Piece/Unit 组合，多个真实护甲再引用同一 model key。

虚拟模型只提供视觉组合，不携带名称、护甲数值或被动技能。目标护甲始终
保留自己的身份和 gameplay metadata。具体 v2 语义与发布条件见协议文档。
