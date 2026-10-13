# 第 60 轮 · M5 报告（手臂修复 + uv1/uv2 通道修复）

日期：2026-10-13　候选：`E:/tmp/rig-lab/cand-m5`　部署：`E:/mod/Lunalice-RE2310-M5/`
TOC sha256 `c7ba606b6901e47fbf3e4ffadf6857504b9223fa83ec8b586512302698292a44`（576444 B）
GPU 67992000 B（与源一致，长度未变）

---

## 1. 回答用户的问题

「先发手臂版本还是先修材质？」——**两条一起发，一个测试周期覆盖两件事。**

M5 = M4W（小臂权重修复）+ uv1/uv2 通道修复。两者互不影响：
手臂修复只改 `bone_weight`/`bone_index`，uv 修复只改 `uv1`/`uv2`。
所以手臂的结论仍然干净可用。

---

## 2. 手臂部分（沿用 M4W，未改动）

- `reweight_forearm.py` 改动 4142 顶点；half4 闭合 `worst 0.000e+00`
- 两条模型流都改到；`_probe_weightdiff.py` 显示只动权重字节
- 待验收：③ 小臂是否还 Q 弹 ④ 腕部旋前/旋后是否难看 ⑤ 回归可见性/影子/两体型

---

## 3. 材质部分：本轮查清了什么

### 3.1 已排除（都有实测证据）

| 假设 | 证据 | 结论 |
|---|---|---|
| UV 被我们弄坏 | 新增 `_probe_sourceuv.py`：源模型材质 6 `shinano_hair_ear` UV 包围盒 `u .7969-.9970 v .0071-.1821`（0.200×0.175），与发货包**逐位相同**；整体 UV 范围 `-21.06..22.98 / -2.74..3.87` 也一致 | 排除 |
| 贴图搬错 / 取错变体 | `tmp_variants.py`：14 张贴图在 7 个 VPK 里**各只有一份**，无同名变体 | 排除 |
| 贴图解码错 | `_probe_hairpng.py`：`hair-crop-ours-raw` 与 `hair-crop-src-raw` **逐像素一致** | 排除 |
| V 翻转缺失 | `_probe_refrender.py` + `_probe_uvplot.py`：能正常显示的参照模组，在 `row = v*H`（verbatim）下领口/面部/躯干都对得上；V 翻转版领口变白、腿出现墙砖纹 | 排除 |
| VMT 贴图变换 | `tmp_vmtdump.py`：`$basetexturetransform` 在**注释里**，未生效 | 排除 |
| mip 链差异 | `tmp_mips.py`：参照与我方都是 `ddsMips=1` | 排除 |

### 3.2 定位到「哪一节是什么」

`_probe_secmap.py`（按 section 上色渲染）+ `_probe_uvplot.py`（UV 空间三角覆盖）：

| sec | 三角形 | 材质 | 备注 |
|---|---|---|---|
| 0 | 9876 | `shinano_face` | 脸 |
| **1** | **5828** | **`white`** | **主体头发**（UV 铺满整张发丝拼版） |
| 4 | 22324 | `shinano_body` | 身体 |
| **5** | **1080** | **`shinano_hair_ear`** | **猫耳**，UV 岛仅 0.200×0.175 |
| 6 | 5164 | `headdress_white01_catears` | 头饰（u 方向平铺 3.05×） |
| 7 | 6954 | `lace_white01_catears` | 蕾丝（u 平铺 44×） |
| 8-13 | — | bra/glove/dress/underwear/shoes×2 | — |

### 3.3 用户截图对应关系

`tmp_cmp_user.py` 生成 `out/uv-crops/user-vs-islands.png`：
用户截图里那片**大而平滑的扇形**与 `shinano_hair_ear` UV 岛**按原样（不翻转）**裁出来的
大片扇面吻合（V 翻转版是深蓝十字发饰，截图里没有）。

⇒ 游戏采样的就是我们发的那块图，方向也没翻。

### 3.4 ★ 本轮找到的唯一结构性差异：uv1 / uv2 通道

`tmp_uvch.py` 对比两个能读到的护甲单位：

| | uv0 | uv1 | uv2 |
|---|---|---|---|
| **参照模组**（游戏内显示正常） | 真实 UV，全在 0..1 内 | **恒为 (0, 1)** | **恒为 (0, 1)** |
| **我方 M4W** | 源 UV | 源 UV（**与 uv0 完全相同**） | 源 UV |

`hd2_unitbuild.pack_mesh_vertices` 把同一套源 UV **同时写进 uv0/uv1/uv2**。
参照模组（游戏内正常）这两个通道是**常量**。

我方 uv0 里含 `-21.06 … 22.98` 这类大幅平铺值（来自 lace 44× 等）。
若引擎用 uv1/uv2 采样任何次级贴图（细节图 / 图案 / LUT / 光照图），
就会得到**剧烈平铺或巨大放大**——这与用户描述的
「贴图没正常缩放、应用了过多的区域」方向一致。

---

## 4. M5 改了什么

`set_uv12.py`：把两个护甲单位**所有流**的 `uv1`、`uv2` 写成常量 `(0.0, 1.0)`，
与参照模组一致。外科式字节改写。

### 独立校验

```
TOC sha256 M4W = c7ba606b…2a44
TOC sha256 M5  = c7ba606b…2a44      TOC identical = True
gpu len 17457848 vs 17457848        equal = True
differing bytes: 3489558 of 17457848 (19.988%)
by field: {'uv1': 1744788, 'uv2': 1744770}      <- 只有这两个字段
```

改后实测（`tmp_uvcheck.py`）：

```
stream 0 uv0: range [-21.058 -2.742]..[22.983 3.867]  distinct 61056   <- 原样保留
stream 0 uv1: range [0. 1.]..[0. 1.]  distinct 1  first [0. 1.]       <- 常量
stream 0 uv2: range [0. 1.]..[0. 1.]  distinct 1  first [0. 1.]       <- 常量
（stream 1 / 2 / 3 同）
```

`uv0` 一字未动、位置/法线/权重/索引/TOC/GPU 长度全部不变。

---

## 5. 未做 / 待验证

- **未动 alpha**：本轮查证参照模组的两个材质**不一致**——`advanced` 那个 slot5
  alpha≈0（与我方一致），`original` 模板那个 slot5 alpha≈255。
  所以「alpha 全置零」不是普适规则，本轮不改，留作下一步候选。
- **未动 mip**：双方都是 mipCount=1，不是差异点。
- **原版单位 UV 通道无法直接读**：`E:/tmp/hd2-mgroot/re2310-full/unit/` 只有 `.toc`，
  没有 GPU 顶点数据，所以「原版」结论来自参照模组这个从原版改出来的单位。
- 若 M5 后头发仍是「放大的一块」且图案**完全没变**，说明 uv1/uv2 未被使用，
  下一步转向 alpha / 材质模板 / 阴影采样。

---

## 6. 验收要点（请用户看）

1. **手臂**：③ 小臂还 Q 弹吗 ④ 腕部扭转难看吗 ⑤ 可见性/影子/两体型回归
2. **材质**：头发那块「放大的一小片贴图」有没有变化？变好、变坏、还是完全没变？
   （「完全没变」也是有价值的信息）
3. 其余部位（蕾丝、头饰、裙摆）有没有一起变好或变坏？

回退：把 data 目录两个符号链接指回
`E:\mod\Lunalice-RE2310-M4W\`（记录在 `E:\tmp\deploy-revert-note.txt`）。
