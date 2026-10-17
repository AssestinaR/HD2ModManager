# 护甲实例 bank：**代码级证据**（2026-10-17）

> 本文把 `armor-piece-capacity-observations-20261016.md` 里那条"可检验线索"
> （`bank = 0 if type==0 else 1 if type==1 else 2`）升级成**代码事实**，
> 并记录一次活进程读取的基线。**仍未**解释 21/22 那条经验边界（见 §6）。

## 1. 为什么要抓内存镜像

盘上的 `data/game/game.dll` 只有 15,522,408 B（0xECDA68），而它 PE 头里的
`SizeOfImage` 是 74,727,424 B（0x4744000）—— 和运行时 `runtime.status` 的
`game_image_size` 一致。盘上那份是壳，**RVA 只对加载后的内存有意义**。

`scripts/capture_game_image.py` 只读地（`PROCESS_QUERY_INFORMATION|PROCESS_VM_READ`，
不写进程、不挂线程、不改保护属性）把整个模块读出来，落成**镜像布局**：
文件偏移恒等于 RVA，未提交的页留 0。这次抓取 23 个区间全部可读，0 失败，耗时 0.4 s。

```
python HD2RuntimeArmorOverride/scripts/capture_game_image.py \
  --pid <pid> --module game.dll \
  --output E:\tmp\hd2-mem\game.image --meta E:\tmp\hd2-mem\game.image.meta.json
```

## 2. 三个 bank 的容器形状（**结论**）

### 2.1 每个 bank 是 `uint32[10]`

`0x81e010` 是一个清空函数，`rcx` = 数组基址，`edx` = 记录序号：

```
0081e02c: mov esi, 0xa                 ; 10 个槽
0081e031: imul rbp, rax, 0x1b8         ; rec = base + index * 0x1b8
0081e03a: add rbp, rcx
0081e040: lea rbx, [rbp + 0x546b4c]    ; bank A
0081e047: mov ecx, [rbx]               ; 槽 0 起
0081e0a1: add rbx, 4
0081e0a5: sub rdi, 1
0081e0a9: jne 0x81e047                 ; 覆盖 +0x00 .. +0x24（10 个 dword）
```

同一形状在另外三处独立代码里逐字重复（`mov e??, 0xa` + `imul r*, rax, 0x1b8`）：
`0x794fd9`、`0x796489`、`0x81e02c`。所以：

* 三个 bank **连续**放在同一个 0x1b8 步长的记录里，间隔 **0x28 = 40 B**：

  | bank | 偏移 | 承载 |
  |---|---|---|
  | A | `rec + 0x546b4c` | `piece_type == 0` |
  | B | `rec + 0x546b74` | `piece_type == 1` |
  | C | `rec + 0x546b9c` | `piece_type >= 2` |

* 紧跟在 C 之后是 `+0x546bc4` / `+0x546bc8` 两个 dword，即 `0x904ac1` 传进
  生产者作为"槽 3 / 槽 2 专用值"的那两个参数。

### 2.2 索引顺序是 **`bank[slot]`**，不是 `slot[bank]`

生产者 `0x81f470` 的收尾（`0x8201f5` 就是 note 里那个 bank 选择点）：

```
008201f5: mov ecx, dword ptr [r12 + 0xc]    ; piece_type
008201fa: mov eax, dword ptr [r12 + 8]      ; slot
008201ff: test ecx, ecx
00820201: jne 0x82020a
00820203: mov rcx, [rsp + 0x58]             ; bank A
00820208: jmp 0x820253
0082020a: cmp ecx, 1
0082020d: jne 0x82024e
0082020f: mov rcx, [rsp + 0x60]             ; bank B
00820214: mov dword ptr [rcx + rax*4], edi  ; bankB[slot] = handle
0082024e: mov rcx, [rsp + 0x68]             ; bank C
00820253: mov dword ptr [rcx + rax*4], edi  ; bank[slot] = handle
```

这同时**独立确认了 Piece 的 ABI**：`+8 = slot`、`+0xc = piece_type`。
三个 bank 的基址分别是调用者的第 8/9/10 个栈参数，由 `0x877c70..0x877cf9` 传入。

### 2.3 推论：同 `(type, slot)` 只有最后写入者存活

写入是裸的 `bank[slot] = handle`，没有"已占用"检查。所以两个 Piece 只要
`(piece_type, slot)` 相同，前一个的句柄就被覆盖 —— 这就是 12 组配置里
"别名组 / 被压住的 Piece 数"的机制，现在是**代码证明**而非推测。

**槽位不会越界**：`slot` 观测值域是 0..9，bank 恰好 10 个 dword，
`bank + slot*4` 最远到 `+0x24`，仍在 40 字节内。之前担心的"槽 9 踩到下一个 bank"
不成立。槽 8/9 是 MaterialLut/PatternLut，本来就该用满。

### 2.4 一处真实的不对称（不是 21/22 的原因，但值得记）

* 生产者 `0x81f470` 的清场循环：`r14d = 9`、首偏移 `+4` → 只清 **槽 1..9**。
* 清空函数 `0x81e010`：`esi = 0xa`、首偏移 `+0` → 清 **槽 0..9**。

也就是说每帧重建时**槽 0 的旧句柄不会被生产者清掉**，只有整体销毁时才清。

## 3. Piece 的存储是**动态数组**，不是定长

`0x81f470` 与消费者 `0x81ed60` 都用同一套读法：

```
count   = dword [kit + 0x38]          ; 组数
groups  = qword [kit + 0x30]          ; 每组 24 字节
group_i = { dword 选择子; qword Piece数组; dword Piece数 }   ; +0 / +8 / +0x10
Piece   = group.Piece数组 + j * 0x60  ; 0x60 = 96 = Piece 记录大小
```

**没有任何按 `piece_type` 索引的定长数组。** 定长的东西只有两样：
三个 10 槽 bank（§2），和下面这两个 20 项指针数组。

## 4. 唯一找到的定长 Piece 容器：**20 项，且静默截断**

`0x81f470` 在 `[rbp-0x10]`（20 项）里收集"属于当前 body 变体的 Piece 指针"：

```
0081f709: mov edi, dword ptr [rbx + 0x38]   ; 组数
0081f720: cmp r12d, 0x14                    ; <- 20
0081f724: jae 0x81f77a                      ; 到 20 就直接跳出去
0081f72e: mov eax, dword ptr [r9 + r8*8]    ; group.选择子
0081f732: cmp eax, r14d
0081f735: je 0x81f73c
0081f737: cmp eax, 3                        ; 或 == 3（通配）
0081f73a: jne 0x81f772
0081f74e: lea rcx, [rdx + rdx*2]
0081f755: shl rcx, 5                        ; j * 0x60
0081f763: mov qword ptr [rbp + rax*8 - 0x10], rcx   ; 收集
```

消费者 `0x81ed60` 在 `[rsp+0x70]` 里做**完全相同**的事，同样 `cmp edx, 0x14; jae`
（`0x81ee10` / `0x81ee37`）。

所以：**一个 Kit 在某个 body 变体下最多只有 20 个 Piece 被这一对函数处理，
第 21 个起被静默丢弃，不报错。**

## 5. 活进程基线（游戏在跑，`active_mappings=0`）

全局在 `game.dll + 0x3326d20`（`0x796497: add r14, [rip+0x2b90882]`）：

```
*(game.dll + 0x3326d20) = 0x194a7dcabd8        ; 数组基址
rec(k) = 基址 + k * 0x1b8
```

`scripts/peek_armor_banks.py --pid <pid> --global-rva 0x3326d20 --bulk 4096` 能扫到
4096 条记录、其中约 1600–1800 条在该偏移上有非零数据。

**但 `rec[0]` 不是护甲实例 —— 这是一个已推翻的假设。** `rec[0]` 确实很像
（16 个唯一、连号的小整数 `0x874..0x883`，分布 9×type0 + 6×type1 + 1×type≥2），
可是切换 Piece 配置后它**逐字节不变**。判据不能靠"看起来像句柄"，只能靠跨配置差分。

同样地，`+0x546b4c` 这段地址空间是**多类型对象共享**的：大量记录里是浮点
（`440d323c` ≈ 565.5、`3f800000` = 1.0）或指针，只有一部分是句柄。

### 5.1 一次"协议惰性"的实测（避免把噪声当信号）

2026-10-17 实机：军械库选中 RE-2310"荣誉卫队"，屏幕上显示的是**已安装的
RE-2310 基础包**（不是协议映射）。此时协议里 `map_model` 为 **0 条**，
所以 `use_armor_piece_config.py --config S8 / Y` 只改了 `model_piece`，
**没有落到任何护甲上**。三份证据互相印证：

| 证据 | S8（21 件 / 15 生效） | Y（28 件 / 22 生效） | 结论 |
|---|---|---|---|
| 记录集合大小 | 1643 条非零 | 1643 条非零 | 结构未变 |
| 差分 | — | 93 条变化，全部是浮点动画值 | 无结构性变化 |
| 窗口截图 | `armory-S8.png` | `armory-Y.png` | 只差镜头远近，模型一致 |

**教训：以后每次实验必须先确认 `map_model` 条数 > 0，否则量到的是噪声。**

### 5.2 顺带交叉验证了 RE-2310 基础包

同一张截图（`armory-S8.png`）显示：军械库选中 RE-2310"荣誉卫队"，
3D 预览是**自定义模型的裸体 + 长发**，没有任何服装/配件；
列表缩略图里还能看到原版披肩（slot 1）。

这与交付包的构造完全对得上：23 个 Piece 里 11 个换成自定义模型的 7 个身体部件、
12 个换成隐藏占位符，所以屏幕上**只剩身体**。也就是说
"看起来没穿衣服"是 `E:\tmp\lunalice-shop` 里 **A1 主体**（裸体本体）的正常表现，
不是交付缺陷；服装来自 B/C/D/E/F 那些独立 VPK。

这条同时说明：`capture_window.py` 的截图可以作为**独立于内存读取**的第二证据源，
后面判断"是没渲染还是被换回原版"时要用它。

## 6. 这次**没有**解释的

1. **21/22 那条线。** 10 槽 bank 不可能溢出（§2.3），20 项数组的截断也**对不上**：
   实测 `effective <= 21` 身体完好、`>= 22` 开始掉部件，而数组容量是 20。
   更关键的是：截断只会让个别 Piece 不渲染，**不能解释"整条胳膊消失"**。
   所以还存在第二个结构，或者"消失"另有机制（例如被换回原版、或子网格被整体剔除）。
2. **为什么 Y 掉双臂、M3 只掉右臂。** 分布 → "掉哪个槽"的映射仍未知。
3. **掉的是"没渲染"还是"被换成原版"。** 观察只能看到"看不见"。

## 7. 下一步（可立即执行）

§5.1 说明：**没有 `map_model` 就没有实验**。所以第 0 步是先把映射补上，
再谈容量。补法（`notes/armor-registry-protocol.md`、`src/armor_registry_protocol.lua:139`）：

```
map_model=820333399,custom-eaf880703fa94f398d620d2288b25d1f
```

`820333399` = RE-2310 的 armor itemId（军械库里当前选中的就是它）。
或者对全部 armor item 各写一条，让任意选中的护甲都生效。

补齐之后，流程是"套配置 → 重读 → 差分 → 截图"：

```powershell
# 0) 确认映射真的在生效
Select-String -Path "$env:LOCALAPPDATA\CowboyBingus\Helldivers2\ArmorRegistry\armor-registry.protocol" -Pattern '^map_model=' | Measure-Object

# 1) 基线快照（存成 JSON 才能差分）
python HD2RuntimeArmorOverride/scripts/peek_armor_banks.py --pid <pid> `
  --global-rva 0x3326d20 --bulk 4096 --label clean --save <banks>\clean.json

# 2) 逐级套用（Lua 约 1 秒轮询，热切换，不重启游戏）
python HD2RuntimeArmorOverride/scripts/use_armor_piece_config.py --config S8
python HD2RuntimeArmorOverride/scripts/peek_armor_banks.py ... --label S8 --save <banks>\S8.json
python HD2RuntimeArmorOverride/scripts/capture_window.py --pid <pid> --output <shots>\S8.png

# 3) 越过 21/22
python HD2RuntimeArmorOverride/scripts/use_armor_piece_config.py --config Y
python HD2RuntimeArmorOverride/scripts/peek_armor_banks.py ... --label Y --save <banks>\Y.json
python HD2RuntimeArmorOverride/scripts/capture_window.py --pid <pid> --output <shots>\Y.png

# 4) 差分：只有真正承载护甲实例的记录会结构性改变
python HD2RuntimeArmorOverride/scripts/diff_armor_banks.py `
  --before <banks>\S8.json --after <banks>\Y.json
```

要判定的三件事：

* 句柄总数是否在某个值上**封顶**（若是，那个数就是真正的实例池上限）。
* 越过 21/22 后，是"少了几个句柄"还是"句柄仍在但槽分布变了"。
  **若句柄数没变而身体消失，则问题在 bank 之后（网格/子部件装配），不在 Piece 计数。**
* 是否出现**重复句柄**（同一实例被写进两个槽），那会让两个槽共用一份资源。

> 差分工具会自动跳过只有浮点抖动的记录（本次实测：93 条变化全是动画值），
> 所以只要看到"某条记录的槽集合变了"，那就是候选。

## 8. 复现本文全部数字的命令

```powershell
python HD2RuntimeArmorOverride/scripts/capture_game_image.py --pid <pid> --output <img>
python HD2RuntimeArmorOverride/scripts/disasm_armor_consumer.py --image <img> `
  --start 0x81f470 --length 0xe00 --head 0 --full --save <txt>
python HD2RuntimeArmorOverride/scripts/find_code_refs.py --image <img> `
  --image-base <base> --target 0x81f470 --dword 0x546b4c --dword 0x546b74 --dword 0x546b9c
python HD2RuntimeArmorOverride/scripts/scan_region.py --image <img> --start <rva> --length <n> --match <regex>
python HD2RuntimeArmorOverride/scripts/peek_image.py --image <img> --rva <rva> --count 4
python HD2RuntimeArmorOverride/scripts/peek_armor_banks.py --pid <pid> --bulk 4096
```
