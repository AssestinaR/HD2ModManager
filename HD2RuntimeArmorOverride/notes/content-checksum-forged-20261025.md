# 内容校验和可伪造：数据级注册表修改已解锁（2026-10-25）

## 1. 结论

`generated_customization_armor_sets.dl_bin` 的明文被改动 9 个字节、同时把
MurmurHash64A 伪造回原值之后，**游戏正常启动并进入大厅**，没有出现
`Fatal Error! Game resource mismatch`。

这条结论推翻了 `wardrobe-schema-v2-20261017.md` §18 的终局判断：

> §18.3 用"字节置换"（保持和与异或不变）失败，据此写下"构造校验值不变的改动这条路也不成立"。

那个测试只排除了**弱校验**。它没有测过**密码学哈希的碰撞构造**——而后者是可解的。
本文件记录的解不是碰撞搜索，是**闭式解**。

## 2. 实测记录

```
decrypted 453204 bytes
target content hash 0x57a949680bbda9e1
kit at subdata 0: payload 0x1c, body0 pieceOffset 0x88 count 7, first piece at 0xa4
free zone 0xfd..0x113: 00000000000000FFFFFFFFFFFFFFFF0000000000000000
free aligned word at 0x100: 00000000FFFFFFFF
touching free byte 0xfd: 00 -> 11
solved word 3d2f9deb214b05e3 (was ffffffff00000000)
achieved 0x57a949680bbda9e1  matches_target=True
bytes changed: 9 -> 0xfd,0x100..0x107
changed outside the free zone: none
wrote 453252 bytes
```

部署后：进程正常启动 → 31 s 出现 `HELLDIVERS™ 2` 窗口 → 进入大厅。
按 §18.1 的实测，校验发生在启动后约 18 s 之前，所以窗口出现即已通过校验。

原文件已备份到 `tmp/sc-mod-analysis/backup/`，
SHA256 `53F7923EE890618A213181506C4B4925C64CB41493EC6710FD677DA1354DAA3D`。

## 3. 哈希算法

MurmurHash64A，与那个超级货币 mod 用的一致：

```
MUL  = 0xC6A4A7935BD1E995
SEED = 16045690983977655786

h = SEED ^ (len * MUL)
for each 8-byte little-endian word w:
    h = (h ^ mix(w)) * MUL
tail（1..7 字节按无符号小端整数）: h = (h ^ tail) * MUL
h ^= h >> 47 ; h *= MUL ; h ^= h >> 47
```

## 4. 逆运算（关键）

`mix` 可逆，且模 2^64 的乘法逆元存在（MUL 是奇数）：

```
mix(w)   = (w*MUL ^ (w*MUL >> 47)) * MUL
unmix(y) = ((y*INV) ^ ((y*INV) >> 47)) * INV        // 因 x ^ (x>>47) 自逆
```

`INV` 用牛顿迭代求，六轮收敛到 64 位（**不能用费马小定理**，模 2^64 不是素数）：

```
inv = 1
repeat 6: inv *= 2 - v*inv      // 每轮正确位数翻倍
```

## 5. 求解：闭式，不是搜索

给定任意改过的缓冲 `D'`、目标哈希 `H`、以及一个完全自由的 8 字节对齐字 `w`：

```
h_before = 从文件头顺序哈希到 w 之前
need     = 从 H 逆序哈希回到 w 之后
w        = unmix( (need * INV) ^ h_before )
```

**O(n) 一次正向 + 一次反向，O(1) 求解。与改动内容、改动数量无关。**
这正是"结果文件有无数种可能"也不需要试错的原因——解是唯一确定的。

### 为什么必须至少 8 个自由字节

自由位 b 位、目标 64 位 ⇒ 期望解数 2^(b-64)。

| 自由区 | 位数 | 期望解数 |
|---|---|---|
| subdata 头 7 个保留字节 | 56 | 2⁻⁸ ≈ 0.4%，实际无解（已实测 2²⁴ 候选零命中） |
| 一个对齐的 8 字节自由字 | 64 | 1，闭式解直接命中 |
| 那个 mod 的 7 字节 + 12 位 float 尾数 | 68 | 16，所以它要 2²⁴×2¹² 的搜索 |

那个 mod 之所以需要"大量试错"，是因为它把自由位拆在了两个半字里且其中一半固定。
**我们选一个完整的对齐字就没有搜索。**

## 6. 我们的自由区在哪里

### 6.1 piece 记录尾部（采用）

`PieceSize = 112`，实际解码到偏移 88（tone）。偏移 **89–111 共 23 字节**未使用，
且实测 46 个件**取值完全相同**：

```
00000000000000 FFFFFFFFFFFFFF 000000000000000000     (7×00, 7×FF, 9×00)
```

23 ≥ 15，所以这段里**必然含一个完整对齐的 8 字节字**。
112 是 8 的倍数，同一 body 内所有件的对齐一致。

全库约 3000 件 × 23 字节 ≈ 6.9 万字节自由空间。

### 6.2 subdata 头的 platform 字段（备用，单独不够）

422 个 subdata 的 `platform` 全部是 `0x0000000000000001`：
字节 20 = `01`，字节 21–27 = 0。与那个 mod 的 `data[20]==1, data[21:28]==bytes(7)` 完全同构。

但 7 字节 = 56 位 < 64 位，**单独无法命中**。可作为"杠杆"配合 piece 尾部使用。

## 7. 性能

453204 字节：

| 操作 | 耗时 |
|---|---|
| 完整哈希 | 0.895 ms |
| 哈希 + 逆推 + 求解 | 0.875 ms |

**单次伪造 < 2 ms。** "改注册表求解哈希慢"不成立，不需要为此做能力分层。

## 8. 仍然未知

| 未知 | 状态 |
|---|---|
| 那 23 字节是否真的不被读取 | 只改了其中 1 个字节，游戏正常。**需要更大范围改动验证** |
| 校验是整文件还是每 subdata | 未定。自由字与被改字节在同一 subdata 时两种假设都成立 |
| 语义字段改动是否也能通过 | **未测**。这是下一步 |
| 期望哈希从哪来 | 仍未找到存储位置；但"伪造到原值即可通过"已被证明 |

## 9. 下一步

1. **语义改动验证**：改一个真实字段（如某件的 unit id 换成另一件），伪造哈希，进游戏确认外观变化。
2. 这一步成功即证明整条路线：注册表可改 → 每套护甲可有独立 unit id → 污染从数据层消失。
3. 之后才是工程化：分层模式、批量改写、与运行时映射配合。

## 10. 参考与授权

那个超级货币 mod（`preserve_content_checksum.py`，GPL-3.0-or-later，改编自
SHODAN Stat Editor）证明了可行性方向。本文件的求解器是独立推导的闭式解，
未复制其代码；MurmurHash64A 与其逆运算是公开数学。

工具：`tmp/WardrobeKitProbe/Forge.cs`（`forge` / `bench` / `headers` 命令）。
