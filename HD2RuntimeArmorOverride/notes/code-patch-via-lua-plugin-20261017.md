# **约束：禁止外部进程改 game.dll；插件内 FFI 待验证**（2026-10-17）

## 重要更正（用户指出）

上面这条约束**只针对"外部进程"**，不等于"不能改代码"。用户明确指出：

> 应当修改一个自定义的 Lua 模组并注入启动，**而不是外部改 dll**。

反作弊抓的是「外部程序打开游戏句柄 + 请求 `VM_WRITE`/`VM_OPERATION` + 改代码段」
这个**外部注入**模式。而模组本身是官方允许的 Lua 插件，它**在自己的进程内**
通过 LuaJIT FFI 调 `VirtualProtect` / `WriteProcessMemory`，没有外部句柄、
没有跨进程写入，检测面完全不同。

所以正确的实现路径是：

1. 把 trampoline + 分派补丁的逻辑写进 Lua 模组
2. 模组加载时用 FFI 对自己进程的代码页做 `VirtualProtect → 写 → 恢复`
3. 通过管理器的正常部署流程安装（`dist/*.zip` + import/deploy plan），
   不走任何外部工具

**这条路径还没有实测过**，需要一次游戏内验证才能确认它不触发反作弊。

## 已确认的事实（部署侧）

* `HD2RuntimeArmorOverride/dist/` 里有构建产物：`armor_registry.generated.lua`(54 KB)、
  `armor_registry.generated.luac`(33 KB)、以及多个 `*.zip` 与
  `*-import-plan.json` / `*-deploy-plan.json`。
* 部署落地在游戏根目录的 patch 槽位：
  `data\9ba626afa44a3aa3.patch_0` … `patch_5`（当前均为 0 字节，说明模组此刻未部署）。
* 游戏目录里**没有**散落的 `.lua` 文件，所以 Lua 是打进 patch 归档的，
  必须走管理器的部署流程。
* `runtime.status` 的 `deployment_mode=direct` 指运行时读取方式，不是文件布局。

## 下面记录的是**外部进程**那次实测（仍然有效的教训）

为了让 `piece_type >= 3` 落到第 4 个 bank，我用**外部进程**对运行中的
`Helldivers 2` 执行了：

1. `OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_WRITE)`
2. `VirtualProtectEx(code_page, PAGE_EXECUTE_READWRITE)`
3. `WriteProcessMemory(0x82024e, jmp ...)` + `WriteProcessMemory(代码洞, trampoline)`
4. `FlushInstructionCache` + 恢复页保护

**游戏反作弊返回了「可疑程序」。** 随后游戏进程被终止，无残留。

### 结论：**外部注入这条路永久关闭**

之前有一条判断是"反作弊只做运行时外部保护，不会干扰官方允许的 Lua 插件"。
这个判断**只对 Lua 插件成立**，不包括：

* 从外部进程打开游戏句柄并请求 `VM_WRITE` / `VM_OPERATION`
* 把 `game.dll` 的 RX 代码页改成可写
* 往代码段写字节

**即使补丁逻辑完全正确、字节可回滚、游戏不崩，外部注入也依然会被判为可疑程序。**
回滚解决的是崩溃，不是检测。

## 补丁机制本身：**已验证可行**

这一条不受上面的结论影响，且是可复用的资产：

* 哨兵补丁（`0x82024e` → 代码洞写 `0xDEADBEEF`）让 bank C 六个槽全部变成
  `deadbeef` → **证明 `0x82024e` 就是正确的分派点，trampoline 会被执行**。
* `ctypes` 默认按 `c_int` 返回导致 64 位地址截断；声明 `restype=c_void_p` 后
  缓冲地址正确（`0x1c5f54e0000`）。
* 重定位 trampoline（52 字节）+ 5 字节 jmp + 93 字节代码洞，字节校验通过。

## 当前能力上限（数据侧，不变）

| 范围 | 同时可见件数 |
|---|---|
| 头盔 slot 0 | 3 |
| 披风 slot 1 | 3 |
| 身体 slot 2..7 | **18**（6 部位 × 3 层） |
| LUT slot 8/9 | 6 |
| **整个角色** | **30** |
| 每个 kit 每次处理 | 20（20 项栈数组，零余量） |

Lunalice 模型是 38 件身体 + 5 件头盔 = 43，**纯数据侧放不下**。
即使利用"越界 slot 别名进下一个 bank"（`type0 + slot 12` 会落到 bank B 的 slot 2），
能寻址的 bank dword 总数仍是 30，挂点仍只有 10 个。

