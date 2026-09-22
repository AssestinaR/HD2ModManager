# HD2 Runtime Armor Override

《绝地潜兵 2》护甲 Unit 运行时重映射原型。

本项目使用 Bingus Shared Loader 暴露的 Lua 环境和 Stingray 原生资源 API，
将一个护甲请求的 Unit 资源身份映射到另一个已存在的 Unit。它不修改
`generated_customization_armor_sets.dl_bin`，也不直接扫描或写入进程内存。

## 已验证基线

2026-09-22 已在游戏中验证：

- 将 RE-824“执旗手”的 22 个身体 Unit 全部映射到 RE-2310“荣誉卫队”。
- 同时启用替换 RE-2310 的外观 Patch 后，RE-824 能完整显示相同的 Mod 外观。
- 两种身形均使用按部位匹配的 Unit。
- 22 次 `Application.set_resource_override` 调用全部成功。
- 未观察到缺失部件、骨骼错位、三角面爆炸或游戏崩溃。

这证明不必先找到并修改完整的护甲注册表缓存，也可以通过引擎自身的资源
覆盖层实现 Unit 复用。当前结果只证明视觉重映射成功，尚未证明显存释放或
启动峰值降低。

## 目录

- `src/re824_to_re2310_override.lua`：已经通过实机验证的 22 Unit 覆盖原型。
- `src/lua_surface_probe.lua`：无 FFI 的 Lua/Stingray 能力探针。
- `tests/`：可在本地 LuaJIT 中运行的隔离测试。
- `notes/re824-to-re2310-mapping.csv`：本次完整映射及语义。
- `notes/`：路线审查与 Lua 能力调查结论。
- `evidence/`：成功运行日志和只读采样。
- `scripts/Test.ps1`：运行全部 LuaJIT 测试。
- `scripts/Build.ps1`：使用 Bingus Shared Loader 的标准打包器生成测试包。

## 测试

从仓库根目录运行：

```powershell
.\HD2RuntimeArmorOverride\scripts\Test.ps1
```

脚本优先使用 PATH 中的 `luajit.exe`，也可显式指定：

```powershell
.\HD2RuntimeArmorOverride\scripts\Test.ps1 -LuaJit C:\path\to\luajit.exe
```

## 构建

```powershell
.\HD2RuntimeArmorOverride\scripts\Build.ps1
```

默认输出到 `HD2RuntimeArmorOverride/dist/`。构建依赖仓库内用于研究的
`docs/BingusSharedLoader-main/scripts/build_addon.py`；也可通过
`-LoaderSource` 指向另一个 Bingus Shared Loader v15+ 源码目录。

## 当前测试配置

管理器测试库 `E:\mod` 的活动配置当前包含：

1. Bingus Shared Loader v15
2. RE-824 to RE-2310 Runtime Override Test
3. 替换 RE-2310 的 Chiffon-LUNALICE 外观 Patch

覆盖脚本在插件启动阶段注册映射，使之后请求 RE-824 Unit 的实例改为使用
对应的 RE-2310 Unit。全局对象 `HD2ArmorRE824ToRE2310` 提供 `clear()`，
可在同一进程内撤销已注册的 22 条映射。

## 下一阶段

下一阶段研究目标是让槽位数量和结构不同的护甲复用同一套标准 Unit：

1. 确认 `set_resource_override` 只能替换资源身份，不能改变注册表的槽位基数。
2. 找到护甲组合被消费时的结构化接口，或构建能提供目标槽位结构的中间资源。
3. 将“结构伪造”和“Unit 资源复用”分层验证。
4. 最后再测资源去重后的内存、显存和加载时间。
