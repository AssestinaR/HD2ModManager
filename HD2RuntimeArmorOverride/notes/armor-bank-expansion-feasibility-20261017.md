# 扩容到 8 个 bank：可行性分析（2026-10-17）

> 结论先说：**可行**。技术上没有阻断项，但这是一个需要逐点验证的代码补丁工程。

## 1. 前置条件：模组有原生调用能力 —— **已确认**

`src/armor_metadata_probe.lua:12-24`：

```lua
if not ok or ffi.os~='Windows' or not ffi.abi('64bit') then ...
ffi.cdef[[ ... ]]
local kernel=ffi.load('kernel32')
...
kernel.ReadProcessMemory(process, ffi.cast('const void *',address), ...)
```

模组跑在 **LuaJIT + 完整 FFI** 上，已经能 `ffi.load('kernel32')` 并调用任意导出函数。
所以 `VirtualProtect` / `WriteProcessMemory` / `FlushInstructionCache` 都可用。

## 2. 代码段是 RX —— 需要 VirtualProtect 才能写

实测（pid 16928，`VirtualQueryEx`）：

| RVA | 保护 | 可写 |
|---|---|---|
| `0x81f470`（生产者） | RX | ✗ |
| `0x81ed60`（消费者） | RX | ✗ |
| `0x81e010`（销毁） | RX | ✗ |
| `0x8201f5`（分派） | RX | ✗ |
| `0x877c70`（调用者） | RX | ✗ |
| `0x3326d20`（全局） | **RW** | ✓ |

所以补丁流程是：`VirtualProtect(RWX) → 写字节 → VirtualProtect(恢复 RX) → FlushInstructionCache`。

## 3. 需要几个 bank：**7 就够，8 更整齐**

Lunalice 模型：身体 38 件（slot 2..7）+ 头盔 5 件（slot 0）。

* 身体：6 个 slot × N 个 bank ≥ 38 → **N ≥ 7**
* 头盔：1 个 slot × N ≥ 5 → **N ≥ 5**

所以 **N=7 刚好装下 43 件**，N=8 留一格余量。目标取 **8**。

## 4. 四个补丁点

### 4.1 分派 `0x8201f5`（决定 type 落到哪个 bank）

现状是三分支比较链：

```
008201f5: mov ecx, [r12+0xc]      ; piece_type
008201fa: mov eax, [r12+8]        ; slot
008201ff: test ecx, ecx
00820201: jne 0x82020a
00820203: mov rcx, [rsp+0x58]     ; bank A
00820208: jmp 0x820253
0082020a: cmp ecx, 1
0082020d: jne 0x82024e
0082020f: mov rcx, [rsp+0x60]     ; bank B
00820214: mov [rcx+rax*4], edi
...
0082024e: mov rcx, [rsp+0x68]     ; bank C
00820253: mov [rcx+rax*4], edi
```

要改成 `bank = table[min(type,7)]`。**这条链比它要替换的空间长**，所以大概率需要
代码洞（`int3` 填充区），不能原地改。

### 4.2 调用者 `0x877c70..0x877cf9`（计算并传三个 bank 指针）

```
00877c70: lea r8,  [r13 + 0x546b9c]   ; bank C
00877c77: add r8,  r12
00877c7a: lea rcx, [r13 + 0x546b74]   ; bank B
00877c8d: add rcx, r12
00877c86: lea rdx, [r13 + 0x546b4c]   ; bank A
00877c9a: add rdx, r12
```

三个指针作为第 8/9/10 个栈参数传入。要传 8 个指针，栈参数位置不够 ——
**必须改成"传一个表指针，让被调方自己取"**，这又要改 4.1 和 4.4 的读法。

### 4.3 存储位置：**不能原地续，必须自己分配**

三 bank 之后：

| 偏移 | 内容 |
|---|---|
| `+0x546bc4` | `3, 1` ← **在用**（slot3/slot2 分派值） |
| `+0x546bec` | 全零 |
| `+0x546c14` | slot2 = `3f800000` ← **在用** |
| `+0x546c3c` / `+0x546c64` | 全零 |

原地续 8 个 bank 会踩到 `+0x546bc4` 和 `+0x546c1c`。所以正确做法是
**自己 malloc 一块 8×40 字节/记录 的缓冲区**，把指针换成它。

### 4.4 销毁 `0x81e010`（清 10 槽 × 3 bank）

```
0081e02c: mov esi, 0xa        ; 10 槽
0081e038: mov edi, esi
0081e040: lea rbx, [rbp + 0x546b4c]
... 清完一个 bank，rdi=esi 重置，rbx 换成下一个 bank 偏移
```

要清 8 个 bank，这个循环得改成用表驱动。

### 4.5 消费者 `0x81ed60`

它同样接收三个指针（`[rsp+0x178]` / `[rsp+0x180]` / `[rsp+0x188]`），
但实测 `r13d` 恒为 0，只读第一个 bank。**需要先搞清它到底读哪些 bank**，
否则改了生产者它可能不跟。

## 5. 风险与回滚

| 风险 | 说明 | 缓解 |
|---|---|---|
| 崩溃 | 补丁写错会破坏记录，游戏直接崩 | 先只读验证每个字节；补丁按点分批；每批单独回滚 |
| 游戏更新 | RVA 全变 | 补丁前用特征码定位，不硬编码 RVA；找不到就整体跳过 |
| 反作弊 | 用户判断：只做运行时外部保护，不干扰官方 Lua 插件 | 记录该判断，出问题可整体关闭 |
| 指令空间不足 | 4.1/4.2 都比原地空间长 | 用代码洞；先确认洞的位置和大小 |

## 6. 建议的执行顺序（每步都可独立回滚）

1. **只读**：把 4.1–4.5 五处逐条反汇编确认，画出精确的补丁字节和代码洞位置。
2. **只读**：确认消费者 `0x81ed60` 到底读哪些 bank（这决定要不要改它）。
3. **最小验证**：先只把 bank 数从 3 改成 **4**（一个额外 bank），看是否稳定；
   成功再推到 8。4 比 8 的补丁面小得多，失败代价也小。
4. 稳定后推到 8，再跑 43 件全量组合验证。

## 7. 尚缺的信息

* 代码洞（`int3` 填充区）的实际位置和大小 —— 决定能不能放得下分派逻辑。
* 消费者 `0x81ed60` 的 bank 选择逻辑（`r13d` 恒 0 是编译器产物还是真只读一个）。
* 模组能否安全调用 `VirtualProtect`（需要确认 loader 没有拦 FFI 的 kernel32 句柄）。
