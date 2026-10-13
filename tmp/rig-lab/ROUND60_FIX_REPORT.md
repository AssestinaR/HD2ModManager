# 第 60 轮修复执行报告（按 ROUND60_ISSUES_BRIEF §6 专家裁定执行）

生成时间：2026-10-13 · 主线程执行记录 · **只本地提交，未推送**

---

## 0. 一句话结论

- **材质**：专家指出的 P0 是**真的**——材质贴图表读写错位。已修好，部署包 28/42 个贴图引用从"指向垃圾"变成 **0 个未解析**。
- **小臂**：已按 §6.6 重绑为"刚性段 + 腕部窄过渡"。离线 30° 腕转下，小臂中段位移从 **52.1 mm 降到 0.0 mm**。
- **已部署 M1**（只修材质），等待实机验收；**W1**（= M1 + 小臂权重）已离线就绪，一行命令即可切换。

---

## 1. 材质：P0 二进制缺陷（已修，已部署）

### 1.1 根因

`hd2_asset.Material.read/write` 把贴图表读成 **N 条交错的 `(u32 槽位键, u64 贴图ID)`**，
每条 12 字节。SDK 与管理器都不是这样：

```python
# docs/HD2SDK-CommunityEdition-3.8.0/stingray/material.py:32-33
self.TexUnks = [f.uint32(x) for x in self.TexUnks]   # N × u32，连续
self.TexIDs  = [f.uint64(x) for x in self.TexIDs]    # N × u64，连续
```

```csharp
// HD2ModCore/Infrastructure/StingrayMaterialReferenceReader.cs:23
var textureIdsOffset = checked(textureRecordsOffset + numTextures * 4);   // 136 + 4N
```

正确布局：`136 .. 136+4N` = 槽位键；`136+4N .. 136+12N` = 贴图 ID。

**三槽模板下，ID 起点应为 148 / 156 / 164，旧写法读成 140 / 152 / 164 —— 只有最后一个（法线）碰巧正确，PBR 与底色全部错位。**

这正是"整体黑 + 局部金属光泽"的直接解释：底色贴图引用坏了，引擎拿不到 albedo。

**为什么此前自校验全过**：同一个错误读写器自读自写会原样往返；换完 ID 再用错误读取器看，仍然看到"想写进去的 ID"。只有外部权威（SDK 解析器 / 管理器 C# 读取器）能暴露它。这也是为什么负对照必须是**冻结的旧实现**，不能用待修模块自己证明自己。

### 1.2 修改

| 文件 | 改动 |
|---|---|
| `tmp/rig-lab/hd2_asset.py` | `read` 先读 N×u32 再读 N×u64；`write` 先写全部键再写全部 ID；头部布局注释改正并写明权威出处 |
| 同上 | `set_texture` 改为**严格**：槽位越界直接抛错，不再凭空追加零槽位键（槽位表属于模板的着色器契约） |
| `tmp/rig-lab/_legacy_matwriter.py` | **新增**：冻结的旧交错实现，只作负对照，永不用于构建 |
| `tmp/rig-lab/build_patch.py` | 新增 `--template basic\|basic+\|advanced` 与 `--pbr-b N`；材质一律从**干净 SDK 模板**重建，不再从已损坏材质二次导出 |

### 1.3 独立验证（`_probe_matfix.py`，权威 = SDK 解析器）

```
[1] 5 个 SDK 模板 (basic / basic+ / advanced / original / armorlut) 读→写 逐字节相同   PASS
[2] 槽位键落在 136+4i                                                        PASS
[3] 贴图 ID 落在 136+4N+8i                                                   PASS
[4] 全部 ID 换成互不相同的哨兵值后，SDK 读回逐个一致；前缀/变量区未动              PASS
[5] 冻结旧写入器被 SDK 拒绝（键错、ID 错；末位碰巧正确；尺寸不变——正是它藏得住的原因）PASS
[6] 部署基线 14 材质 / 16 贴图 → 28 个未解析引用；三个候选 → 0 个                PASS
```

### 1.4 端到端材质链（`_probe_matchain.py`，从 Unit section 一路走到贴图）

| 包 | 承载 unit 断链 | 结论 |
|---|---|---|
| 部署基线（黑的那个） | **14 处**（每个材质 2 个未解析贴图） | FAIL |
| M1 / M2 / M3 / W1 | **0** | PASS |

非承载 unit（20 个隐藏占位）有 60 处引用指向**原版 RE-2310 自己的材质**（8 个不同 id，
已用 `E:/tmp/hd2-mgroot/re2310-full/material/` 交叉确认），这是占位 unit 的正常形态。
另有 1 个 id（`0x75f87ad2ae08e9c2`）既不在包内也不在原版——**与基线完全相同**，是本轮之前就存在的已知项。

### 1.5 三个候选（全部离线闭合）

| 候选 | 唯一变化 | toc / gpu | 与基线的差异 | 27 项结构审计 |
|---|---|---|---|---|
| **M1** | 只修贴图表，保留 `basic` 与旧贴图像素 | 563900 / 67992000 | 14 个 Material toc | 1 失败（=基线的已知项） |
| M2 | M1 + PBR.B 0→255 | 563900 / 67992000 | +1 个 Texture gpu | 同上 |
| M3 | M2 + 换完整 SDK `basic+` 模板 | 562556 / 67992000 | 同上（材质 toc 512→416 B） | 同上 |

- 三个候选的 SDK 闭合检查 **0 失败**，端到端材质链 **PASS**。
- 结构审计的 1 个失败 = CLI 隐藏占位 unit 的惰性悬空材质短 id，**基线本身也有**，非新引入。
- M2/M3 的 PBR 贴图已解码实测 = `(0,158,255,255)`（R 金属度 0、G 粗糙度 0.62、B AO 255）。

### 1.6 部署

```
D:\SteamLibrary\steamapps\common\Helldivers 2\data\
  9ba626afa44a3aa3.patch_0[.gpu_resources]  →  E:\mod\Lunalice-RE2310-M1\
```
- 部署后 sha256 = `fdcd6c7cb077864ffc0af6dcee8cd19cd340bafaeaa0a8a91a55a7f1cfa74c36`，与候选包一致。
- 大小 563900 / 67992000 B，与之前实机通过的包**完全同尺寸**，unit / 占位 / LOD 结构逐字节未动。
- 回退目标已写入 `E:\tmp\deploy-revert-note.txt`（→ `E:\mod\Lunalice-RE2310-Test\`）。

---

## 2. 小臂：权重重绑定（已离线完成，**尚未部署**）

### 2.1 根因（§6.4 的修正版）

按专家要求先做**壳层/材质语义分离**（`_probe_forearm_roi.py`、`_probe_forearm_profile2.py`）：

| 沿肘→腕 t | 皮肤 `shinano_body` 的 hand 权重 |
|---|---|
| 0–19 mm | 0.077 |
| 77–96 mm | 0.441 |
| 153–172 mm | 0.834 |
| 211–230 mm | 0.979 |
| 249–268 mm | 0.999 |

**皮肤是一条从肘到腕的单调斜坡，没有任何刚性段** —— 这就是"橡皮筋"。专家对"腕端非单调"的撤回是对的：那是若干刚性小件（手镯/珠串/扣件）混进了同一根圆柱选区。

### 2.2 一个被推翻的假设（记录下来避免重犯）

我原本打算"跳过已经 hand ≥ 0.95 的顶点"（它们看起来像手掌/手套，应当保持）。
`_probe_handrigid.py` 证明**不行**：那些顶点和皮肤**在同一个连通块里**（comp 276 / 277），
半径区间也重叠（1.2–51.4 mm vs 4.4–75.3 mm）。按权重一刀切会在皮肤中间撕出剪切。
⇒ 改成完全由"斜坡 + 刚性块"两条几何规则决定，权重阈值不再参与。

### 2.3 实施（`reweight_forearm.py`）

- **选区**：每侧，到"肘→腕"轴线距离 < 10 cm，t ∈ (0, L)。
- **刚性块**：连通块在该选区内 t 跨度 < 60 mm（手镯、珠串、扣件、手套面板）→
  取**自身附着点**在斜坡上的**单一比例**，整块统一，只刚体运动不剪切。
- **其余（皮肤 / 紧身袖）**：hand 质量驱动到 `smoothstep(0.78L, 0.92L, t)`，
  差值**只在"手骨子树"与同侧肘骨之间搬运**，其它一切不变。
- **编码**：合并同骨 → 1/2048 网格最大余数 → float16 精确闭合。
- **就地改写**：只改顶点流里的 `bone_weight`(8B) 与 `bone_index`(4B)，容器布局不可能位移。

### 2.4 结果

| | 改前 | 改后 |
|---|---|---|
| 皮肤 hand 权重 t<224 mm | 0.08 → 0.98（斜坡） | **0.000（刚性）** |
| 腕部过渡带 | 整条小臂 | **224 → 264 mm（40 mm）** |
| 离线 30° 腕转，小臂中段位移（左/右） | **52.1 / 52.6 mm** | **0.0 / 0.0 mm** |
| 腕尖（t>0.9L）位移 | 5.3 mm | 13.9 mm（腕区跟随手骨，正确方向） |

- 只改了 unit GPU 的 **0.244%** 字节，全部落在 `bone_weight` / `bone_index`，
  **两条模型流都改到**（流 1 承载 LOD3/2/1）。
- float16 权重精确闭合：`worst deviation 0.000e+00`，0 个顶点丢失影响。
- 563900 / 67992000 B（与 M1 同尺寸）；与 M1 的差异 = 2 个 Unit 的 gpu，仅此。

> **过程中修掉一个自己的 bug**：第一版把权重写到了"流内偏移"而不是 GPU 内的绝对偏移
> （`vertex_buffer_offset`），结果只有流 0 被改到、流 1 原封不动。
> 是 `_probe_weightdiff.py` 的逐字节比对抓出来的——**这正是"不要只做自己写自己读的闭环"的价值**。

### 2.5 已知限制（需实机验收，不假装已解决）

- **没有使用 twist 骨**。前臂旋前/旋后会集中在腕缝，可能难看。按 §6.6 的告诫，没有凭名字加骨。
- 肘侧过渡沿用原权重（本来就好，肩权重只在 t<57 mm 有意义）。
- 全部是**离线候选**：合成腕转不是游戏帧；实机必须复测走路、持枪/瞄准、换弹。

---

## 3. 复现命令

```bash
PY="C:/Users/Administrator/.workbuddy-ai/binaries/python/envs/default/Scripts/python.exe"
cd E:/Data/source/repos/WpfApp1/tmp/rig-lab
SRC="E:/mod/Lunalice-RE2310-Test/9ba626afa44a3aa3.patch_0"     # 基线（黑材质）
MAN="E:/mod/Lunalice-Native-RE2310/lunalice-native-re2310.manifest.json"

# 材质：验证 + 三个候选
$PY _probe_matfix.py "$SRC"                       # 独立 SDK 校验（基线 1 项失败，候选 0）
$PY fix_materials.py --src "$SRC" --manifest "$MAN" --out E:/tmp/rig-lab/cand-m1 --name lunalice-m1 --template basic  --pbr-b 0
$PY fix_materials.py --src "$SRC" --manifest "$MAN" --out E:/tmp/rig-lab/cand-m2 --name lunalice-m2 --template basic  --pbr-b 255
$PY fix_materials.py --src "$SRC" --manifest "$MAN" --out E:/tmp/rig-lab/cand-m3 --name lunalice-m3 --template "basic+" --pbr-b 255

# 小臂：候选（以 M1 为基底，保证实机看得见）
$PY reweight_forearm.py --src E:/tmp/rig-lab/cand-m1/9ba626afa44a3aa3.patch_0 \
    --out E:/tmp/rig-lab/cand-w1 --name lunalice-w1

# 校验
$PY _probe_matchain.py  <patch>                   # Unit section → 材质 → 贴图 端到端
$PY _probe_canddiff.py  <before> <after>          # 逐条目差异
$PY _probe_weightdiff.py <before> <after>         # 逐字节：只允许权重字段变化
$PY _probe_forearm_action.py <before> <after>     # 30° 腕转中段位移
$PY _verify_normalized.py E:/tmp/rig-lab/native-re2310/9ba626afa44a3aa3.patch_0 <patch> \
    --our-unit cae35ee15168145f,9841b16544c8fbae --vanilla-dir E:/tmp/hd2-mgroot/re2310-full/unit

# 切换部署（幂等，自动写回退记录）
$PY deploy_candidate.py --cand E:/tmp/rig-lab/cand-w1 \
    --mod-dir E:/mod/Lunalice-RE2310-W1 --name lunalice-re2310-w1
```

---

## 4. 待实机验收（用户只看结果）

**第一步（当前已部署 = M1）**
1. 场景内：人物颜色是否正常（不再是黑色 + 局部金属光泽）。
2. 军械库 / 出击前装备管理页：模型能否显示。

**第二步（说一声即可切换 W1）**
3. 走路、持枪/瞄准、换弹，以及最容易出现"Q 弹"的动作：小臂是否还像橡皮筋。
4. 腕部扭转（旋前/旋后）是否在腕缝处集中得难看（twist 骨的已知限制）。
5. 回归：可见性、影子、两个体型。

---

## 5. 纪律

- 只本地提交，**未推送**。
- 交付包保持现有原生 ID / 体型 / 占位结构，**只改对应资源**（材质 toc 或权重字节）。
- 原故障包与全部依赖保留；候选各自独立目录；回退记录已写入文件。
- 所有"已通过"均为**离线**结论，**不声称已实机验收**。
