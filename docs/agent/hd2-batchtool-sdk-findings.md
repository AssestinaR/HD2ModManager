# Estecsky 三仓库调研（HD2-PhysBoneTool / HD2SDK-AQ-Edition / HD2-BatchTool）

> 2026-10-11。目的：为「L4D2 模型整体移植到 HD2」找现成的「第三方模型 → HD2 动骨模型」映射数学。
> 本地副本：`tmp/pb-ref/batch-raw/`、`tmp/pb-ref/sdk-raw/`（git clone 全失败，走 GitHub API + raw.githubusercontent.com 下载）。

## 0. 一句话结论

**我们没有找错方向，但我们要造的东西他们已经造完了大半**：他们的「缝」是一个版本化的二进制声明文件 **`HD2IRG1`**，配套一个 Lua producer；而「第三方模型 → HD2」的映射**不是在运行时算的**，是**在 Blender 里由作者手工把骨骼吸附（snap）到 HD2 工作骨架上**，运行时只负责按声明驱动。我们此前的 `T = H·S⁻¹` 离线数学**等价于他们的「骨骼吸附」那一步**，但我们**漏了三个关键约束**（见 §5）。

---

## 1. 克隆/下载方式（重要环境经验）

- `git clone` 对 `HD2SDK-AQ-Edition` / `HD2-BatchTool` **全部失败**：`schannel: server closed abruptly`、`CONNECT tunnel failed, response 502`。重试 5 次 × 2 轮仍失败。
- **可用替代路径**（`HD2-PhysBoneTool` 的 clone 是成功的）：
  - 文件树：`curl -sL "https://api.github.com/repos/<owner>/<repo>/git/trees/main?recursive=1"`
  - 单文件：`curl -sL "https://raw.githubusercontent.com/<owner>/<repo>/<sha>/<path>"`
  - `WebFetch` 抓 `api.github.com` 也可以，但会**摘要**，不如 curl 保真。
  - 分支名要试 `main` / `master`（`HD2-BatchTool` = `main`，`HD2SDK-AQ-Edition` = `main`）。

---

## 2. HD2-BatchTool（Blender 插件，作者 Estecsky/AQ_Ex）

### 2.1 职责
「潜兵工作骨架、人物比例吸附、顶点组字典、权重整理和部位标记」。**假定模型最终要变成 HD2 骨架**，不做通用 retarget。

### 2.2 推荐流程（README 原文要点）
1. 导入人物模型 → 点「导入潜兵工作骨架」→ **将工作骨架转为 T-pose**（必须选「发布骨架类型」）。
2. 调整人物模型并同样摆成 T-pose 并对齐工作骨架，**腿部无需对齐**。
3. 选字典（`cats修复` / `mmd通用` / 自建）。
4. （可选）设置头部对齐偏移 + 打开「人物统一缩放」：**按脚部权重网格识别可见接地面，并围绕脚底统一缩放人物骨架和绑定网格**。
5. 依次执行「检查字典与骨架」「骨骼吸附」「顶点组重命名」。
6. **必须点「转回初态 + 原始骨轴」**。
7. 计算/调整持握骨 → 部位切分 → 指定部位标记。

### 2.3 `assets/body_pose_profile.json` = **HD2 静止姿态(A-pose) ↔ T-pose 的权威逐骨局部旋转表**
- `reference_group: TP_INITIAL`；`schema_version: 2`。
- `transform_policy`：
  - `pose`: `PoseBone.matrix_basis rotation only`
  - `axis`: `per-bone local post-rotation; preserve edit-bone head and length`
  - `excluded`: `all non-body bones, including attach/weapon/camera controls`
- `body_bones` **45 根**：`chest` + `l/r_clavicle` + `l/r_shoulderarmour` + `l/r_shoulder` + `l/r_shoulder_twist` + `l/r_elbow` + `l/r_hand` + `l/r_hand_twist` + 30 手指。
  ⇒ **不含脊柱 / 颈 / 头 / 腿** ⇒ **A↔T 只动手臂**。
- `tpose_basis_rotations_wxyz` 与 `initial_basis_rotations_from_baked_tpose_wxyz` **互为共轭**（`validation.maximum_inverse_pose_matrix_error ≈ 4.9e-5`）。
- 量级：`l_shoulder` ≈ **34.3° 绕 -Y**，`l_elbow` ≈ **39.7° 绕 +Z**，`l_hand` ≈ 3.1°。
- `symmetric_axis_deltas_wxyz` **只有右侧**（r_clavicle / r_shoulderarmour / r_shoulder / r_shoulder_twist / r_elbow / 右手 5 指）⇒ 用于把右臂局部骨轴镜像成与左臂一致（`AXIS_SYMMETRIC`），**这是「对称轴」不是「男女」**。

### 2.4 `utils/rigging.py` 的 `_convert_pose_space(TO_TPOSE | TO_INITIAL)`
- 纯局部旋转 → 写 `pose_bone.matrix_basis` → **`_bake_body_pose_to_rest` 把 pose 烘焙成 edit-bone 静止骨位 + 同步烘焙所有绑定网格**。
- **非身体骨的 `matrix_basis` 在求值期间临时清零**，避免 attach/weapon/camera 控制骨被重复烘焙。
- `_capture_deformed_mesh_coordinates` 用 `evaluated_get(depsgraph).to_mesh()` 精确求值蒙皮，**含形态键逐 key 求值**；写回时要求拓扑不变。
- 失败时**完整回滚**（骨架 rest、网格坐标、pose basis、自定义属性、pose_position、use_mirror_x）。
- `TO_INITIAL` 前**拒绝存在未应用形态键**（含 Basis / 值为 0 的）。
- 结束后统一 `_restore_reference_hand_axes(role)`：**手掌/手腕扭转/手指的绝对静止轴收敛回模板**（只改旋转，保留作者自己的 head/骨长）。注释解释：「姿态旋转表是在原模板父子坐标上采样的；独立骨架移动过各关节 head 后，直接往返会留下最多十余度手轴误差。」

### 2.5 字典格式（`presets/mapping_profiles.py` + `assets/mapping_mmd.json`）
```json
{ "schema_version": 1, "name": "...", "description": "...",
  "snap_map":   { "<HD2目标骨>": ["来源别名1", "来源别名2", ...] },
  "weight_map": { "<HD2目标骨>": [...] },
  "options": { "copy_unmapped_deform_bones": true, "max_total_bones": 255, "fallback_bone": "hips" } }
```
- `snap_map` = **放骨**；`weight_map` = **路由顶点组**（默认 = `snap_map`）。**两者可以不同**：`mmd通用` 里 `snap_map["spine2"]=["上半身2"]` 而 `weight_map` 把 `上半身2` 归到 `chest` ⇒ **多个来源变形组可并到一个 HD2 骨**。
- 校验：`107 <= max_total_bones <= 256`。
- **`copy_unmapped_deform_bones=true`（默认）= 未映射的实际加权辅助骨保留到工作骨架**（不是合并到父级）。选 `false` 才 `fallback_bone`（默认 `hips`）。
- `normalize_source_name`：NFKC + casefold + 去掉 `_ . - 空格 \t \r \n` ⇒ **匹配很宽松**。

### 2.6 MMD 字典里对我们直接有用的行
| HD2 目标 | MMD 来源别名 | 我们的推论 |
|---|---|---|
| `l_ball` | `左足先EX` / `足先EX.L` / `左つま先` / `つま先.L` / `toe_l` | **`l_ball` 就是脚趾/脚掌骨** ⇒ 我们该加 `L_Toe0 → l_ball` |
| `l_clavicle` | `左肩` / `肩.L` / `左肩P` / `肩P.L` | 我们的 `L_Clavicle → l_clavicle` **语义正确** |
| `l_shoulder` | `左腕` / `腕.L` | 我们的 `L_UpperArm → l_shoulder` **语义正确** |
| `neck` | `首` | ✓ |
| `head` | `頭` / `头` | ✓ |
| `spine1` | `上半身` / `上半身1` | 见 §5.1 —— 我们的 `Spine → spine1` 有问题 |
| `spine2` | `上半身2` | |
| `chest` | `上半身3` / `上半身2` | |

### 2.7 `publish_rig_gender`（用户问的「男性/女性」）
`ui/properties.py`：
```python
publish_rig_gender: EnumProperty(
    name="发布骨架类型",
    items=(("UNSET", "未设置（保存前必选）", "未声明；最终保存 Mod 时会报错，不按外观猜测"),
           ("MALE",   "男性", "最终包启用通用重定向，保持原版腿部动作；制作时与原潜兵站姿对齐"),
           ("FEMALE", "女性", "最终包启用通用重定向与女性腿部修正；仅支持当前直腿制作站姿")),
    default="UNSET",
    description="仅为发布声明，不改变导入的工作骨架；最终保存 Mod 时写入运行时包")
```
**⇒ 它不是建模期的骨骼变换，而是写进运行时包的一个 flag**（见 §3.4 `gender_male=4` / `gender_female=8`），由 Lua 端应用。
**⇒ 我们的动作**：① 我们的转换器必须能声明性别；② 我们的 Lua 运行时必须实现「女性腿部修正」；③ **制作期必须用直腿站姿**（源模型的腿不要摆成 HD2 的叉腿）。

### 2.8 HD2-PhysBoneTool（`physbone/blender/transfer.py` 等）
- ⚠️ **全仓无 `retarget`/`remap`/`ValveBiped`/`L4D2`/`第三方`**（只有 `THIRD_PARTY_NOTICES.md` 文件名命中）。**它假定模型已是 HD2 骨架。**
- 但可对照的数学：
  - `transfer.py`: `local_frame(name) = dst_rest.inverted() @ frame @ src_rest`，`frame = target.matrix_world.inverted() @ source.matrix_world` ⇒ **`D⁻¹·frame·S` 与我们的 `T = H·S⁻¹` 结构一致**。
  - `mmd_conversion.py::_uniform`：从 3x3 抽统一缩放，**明确拒绝**镜像（`det<=0`）/剪切（`col_i·col_j > scale²·1e-4`）/非均匀缩放（`max-min > scale·1e-4`）。
  - `rest_pose.py::_rigid`：**只接受纯旋转刚体**（det=1、列长=1、列正交、最后一行 `(0,0,0,1)`）；**方向量只取 `.to_3x3()`，点才用完整 4x4**；**先构造完再提交，失败 `restore()` 回滚**。
  - `binding.py`：Child Of 约束**刻意含缩放**（「disabling it changes HD2's rest/pose contract under nonuniform armature and bone transforms」）。
  - `light_adaptation.py`：为骨链补 `_HD2PB_Tip` 无权重尾骨（名字按 63 字节截断 + 递增序号去重）；身体避让 `clearance - .002`，`radius < .001` 则 `omitted_rest_overlap`。

---

## 3. HD2SDK-AQ-Edition（魔改版 SDK）

### 3.1 `hd2_system/avatar_source.json` = **权威 HD2 玩家骨架**
- `schema: HD2AvatarSourceSkeleton1`，`provenance: "Validated 63-bone Avatar source used by sampleb_custom_node_drive.active.json and installed runtime profiles; parent=-1"`
- **63 根**，`parent` 全为 `-1` ⇒ **`rest_local` 实际是「世界 rest」**（`rest_globals()` 因 parent<0 直接返回原值）。
- 字段：`name` / `parent` / `flags` / `rest_local`（**row-major 3x4，列向量**）。
- 骨名（顺序）：
  `boss`, `spine1`, `spine2`, `chest`, `neck`, `head`,
  `r_clavicle`, `r_shoulder`, `r_elbow`, `r_hand`, `r_thumb_finger1..3`, `r_index_finger1..3`, `r_middle_finger1..3`, `r_ring_finger1..3`, `r_pinky_finger1..3`, `r_hand_twist`, `r_shoulder_twist`,
  `l_clavicle`, `l_shoulder`, `l_elbow`, `l_hand_twist`, `l_hand`, `l_thumb_finger1..3`, `l_index_finger1..3`, `l_middle_finger1..3`, `l_ring_finger1..3`, `l_pinky_finger1..3`, `l_shoulder_twist`,
  `hips`, `l_thigh`, `l_knee`, `l_foot`, `l_ball`, `l_toe`, `r_thigh`, `r_knee`, `r_foot`, `r_ball`, `r_toe`,
  `support_mg`, `attach_intelpad`, `attach_samplepouch`, `root`
- **⇒ 以后读 HD2 骨架不必再解 TocData + bonehash**（名字、父子、世界 rest 全齐）。注意这是 **63 根公开动画通道**；Unit TocData 里还有 `cape1..cape7`、`attach_*` 等（我们的 A1 读到 107 条）。

### 3.2 `hd2_system/rig_format.py` = **`HD2IRG1` 二进制格式（= 我们说的「缝」）**
| 结构 | struct | 字段 |
|---|---|---|
| HEADER | `<8s6I` | magic `HD2IRG1\0`, version(1), headerSize, sourceCount, profileCount, payloadCrc32, extensions |
| SOURCE | `<64siI12f` | name64, parent, flags, rest_local×12 |
| PROFILE | `<QIIiII` | unit_id, targetCount, needleLen, needleToPalette, paletteSlots, flags |
| TARGET | `<64siiIIf12f` | name64, parent, sourceIndex, paletteSlot, flags, translationScale, rest_local×12 |
| GRIP | `<4I12f` | profileIndex, handTargetIndex, side, 0, hand_to_grip×12 |

- `SOURCE_FLAGS = {"root": 1}`
- `PROFILE_FLAGS = {"dynamic_pose_match": 1, "observe_only": 2, "gender_male": 4, "gender_female": 8}`（**`&12 == 12` 直接报错**，不能同时声明）
- `TARGET_FLAGS = { transfer_translation:1, attachment:2, left_hand:4, right_hand:8, backpack:16, scene_graph_index:32, anchor_only:64, diagnostic_motion:128, inherit_parent_pose:256, source_global_pose:512, source_parent_space:1024 }`
- `extensions`：bit0(1) = 有 `HD2GRIP1` 段；**bit1(2) = 已声明性别**（「Old receivers reject this extension bit instead of silently ignoring the gender policy」）。
- 上限：`source_bones` 1..256、`target_bones` 1..256、`profiles` 1..512、`palette_slots` 1..4096；name ≤ 63 UTF-8 字节；needle 16 B..1 MiB。
- `rest_local` 校验：**列单位长 ±0.002、列正交 ±0.002、det = +1 ±0.005**。
- 矩阵约定注释原文：「``matrix`` is the row-major 3x4 affine layout used by the wire format」+ 一条踩坑记录：行列式展开式里曾把 `c1[2]` 写成应为 `c2[1]`，导致「普通旋转被当成反射拒绝」。

### 3.3 `hd2_system/unit_rig_profiles.py` = 由 Unit 场景图生成 Rig Profile
- `snapshot_from_loaded_unit()`：读 `TransformInfo.NameHashes / TransformMatrices / TransformEntries`，**`raw_parent == index` ⇒ `parent = -1`**（根自指）；矩阵**转置**成列向量 3x4（`values[0],values[4],values[8],values[12], ...`）⇒ **再次确认我们的转置结论**。
- `_source_skeleton()`：`rest_local` 用 `avatar_source_rest_local`（**原版公共 Avatar Rest**）而不是作者拟合后的 `rest_local`。注释：「``rest_local`` is the fitted character target Rest used by PhysBone authoring. Animation deltas, however, must be measured from the pristine public Avatar Rest. **Keeping the two contracts separate is what preserves custom proportions.**」⇒ **「拟合后的 rest」与「动画基准 rest」是两份数据，混用会破坏自定义比例。**
- 上限 256（`公共工作骨超过运行时上限 256`）。
- **每根 target 骨的 flags 语义**（`build_rig_document`）：
  - 映射到 source 骨时：
    - 有 `runtime_source_bones`（校准路径）⇒ `source_parent_space` + `transfer_translation`
    - 否则若 `|world − source_global| <= 2e-4` ⇒ `source_global_pose`
    - 否则 ⇒ `transfer_translation`
  - 未映射但父级已映射 ⇒ `inherit_parent_pose`
- `OPTIONAL_FINGER_TARGETS` = 30 根手指（`{l,r}_{thumb,index,middle,ring,pinky}_finger{1,2,3}`）—— **只有「未被使用的手指输出骨」可以省略**。
- `hand_grips`：`hand_to_grip = inverse(world(hand)) @ world(attach_hand_X)`，且要求肩-肘-手链完整映射。
- `_mat34` 校验同 §3.2（单位长/正交）。

### 3.4 性别 flag 的最终落点
`build_rig_document(..., rig_gender=None)`：只接受 `None / 'MALE' / 'FEMALE'`（「不推断或默认性别」），写成
`profiles[].flags += ['gender_' + rig_gender.lower()]`，最终由 `rig_format.build()` 打包成 `gender_male=4` / `gender_female=8`。
**⇒ 「女性腿部修正」是运行时行为，数据侧只有一个 flag。**

### 3.5 其他
- `skin_weights.py::normalize_half4`：**half4 权重必须精确闭合到 1**（`math.fsum(quantized) == 1`），否则用 2048 整数分配兜底；「不新增骨影响」。= README 说的「半精度权重精确闭合，护甲分部件不再有裂缝」。
- `stingray/bones.py::StingrayBones`：`u32 numNames` + `u32 numLodLevels` + `float32[numLodLevels]` + `u32 hashes[numNames]` + `u32 lodLevels[numLodLevels]` + UTF-8 `\0` 分隔名字 ⇒ **与我们的 `UnitBoneNamesReader` 一致，可交叉验证**。
- `independent_bone_closure.py`：身体部位导出时把 **`avatar_source.json` 里出现的公共骨名**全部纳入闭包（含手指，供握持拟合消费无权重 Rest 地标）；「empty vertex groups are not consumers」。
- `core/authoring_metadata.py`：`PART_SLOTS = (Head, LeftArm, RightArm, Torso, Torso_Armor, Hips, LeftLeg, RightLeg)`；`SCHEMA_NAME = "HD2AuthoringMetadata1"`；写入时会**清掉 `Z_ObjectID` / `Z_SwapID*`**（防止换 archive 时沿用旧目标）。

---

## 4. 实测证据（`tmp/rig-lab/probe_chain.py` / `probe_weights.py`，Nick = `survivor_gambler`）

### 4.1 关节位置（m）
| source 骨 | 关节 z | HD2 骨 | 关节 z | 备注 |
|---|---|---|---|---|
| Pelvis | 0.819 | hips | 0.9735 | 差 +0.155 |
| Spine | 1.099 | spine1 | **0.9735** | **与 hips 同点（退化）** |
| Spine2 | 1.219 | spine2 | 1.1633 | |
| Spine4 | **1.219** | chest | 1.3437 | **与 Spine2 同点（退化）** |
| Neck1 | 1.453 | neck | 1.550 | |
| Head1 | 1.507 | head | **1.691** | 差 +0.184 ⇒ 长脖子 |
| L_Clavicle | x 0.059 | l_clavicle | x -0.072 | 段长比 2.11 |
| L_UpperArm | x 0.108 | l_shoulder | x -0.210 | 段长比 **3.195** ⇒ 肩部压扁 |
| L_Thigh | (0.077,-0.037,0.943) | l_thigh | (-0.087,-0.019,0.942) | **几乎重合 ✓** |
| L_Calf | 0.549 | l_knee | 0.516 | 0.78→1.09 |
| L_Foot | (0.077,-0.037,**0.008**) | l_foot | (-0.191,-0.118,0.104) | 段长比 0.78 |
| — | **两脚 x = ±0.0767（相距 0.153）** | — | **±0.1908（相距 0.382）** | **HD2 叉腿「稍息」vs 源直腿并拢** |

### 4.2 逐骨顶点权重（dominant）
| source 骨 | 顶点数 | 网格质心 | 关节 |
|---|---|---|---|
| Pelvis | **14116** | (−0.001, 0.019, **1.082**) | 0.819 |
| Spine | 3910 | (−0.000, 0.058, **1.166**) | 1.099 |
| **Spine2** | **0** | — | 1.219 |
| Spine4 | **12254** | (−0.000, −0.024, **1.341**) | 1.219 |
| Neck1 | 1580 | (0.000, −0.014, 1.466) | 1.453 |
| Head1 | **22305** | (0.001, −0.050, **1.634**) | 1.507 |
| **L_Foot / R_Foot** | **0** | — | 0.008 |
| `source_extra_62` / `_63`（**鞋子**） | **7182 / 7182** | (±0.076, −0.102, 0.057) | — |

**关键推论**：
1. **`Spine2` 和 `L/R_Foot` 完全没有权重** ⇒ `Spine2→spine2`、`L_Foot→l_foot` 这两对**基本不影响网格**；真正决定外观的是**回退**（`source_extra_*` 落到最近的已映射祖先）。
2. **鞋子（`source_extra_62/63`）回退到 `L/R_Calf`（1 hop）** ⇒ 把 `L_Foot→l_foot` 的叉腿落点带进鞋子，**鞋被外推约 0.11 m** = 用户说的「脚位置不正确」。
3. **源网格质心 ≈ HD2 关节**：Spine 质心 1.166 ≈ HD2 spine2 1.163；Spine4 质心 1.341 ≈ HD2 chest 1.344；L_Hand 质心 0.687 ≈ |HD2 l_hand| 0.657；L_Thigh 质心 0.111 ≈ |HD2 l_thigh| 0.087。
   ⇒ **「按网格质心选 HD2 目标骨」比「按名字」更贴合实际。**

---

## 5. 由此得到的三条修正（已实现 2 条）

### 5.1 ② 脊柱三元组接错（已实现，`--spine-map mesh`）
`Spine → spine1` 是**错的**：`spine1` 与 `hips` 同点，所以 `Pelvis→hips` 把骨盆网格抬到 **1.237**，而 `Spine→spine1` 把脊柱网格压到 **1.041** ⇒ **躯干上下两半对穿**。
按网格质心改为 **`Spine → spine2`**（1.166 ≈ 1.163），`Spine2/Spine4 → chest`，`spine1` 无对应。
**实测（Nick，`swing`）**：edge max **49.21 → 27.07**、edge mean 1.0794 → 1.0585、bad>25% **9.25% → 7.77%**。

### 5.2 ③ 腿/鞋（已实现，`--transfer-bones legs`）
HD2 是叉腿站姿（双脚 0.382 m），源是直腿并拢（0.153 m）。Batch Tool 原文「腿部无需对齐」+「仅支持直腿制作站姿」。
把 **`L_Calf / L_Foot / R_Calf / R_Foot` 改为 `transfer`**（保留源自身相对父骨的偏移，只把旋转交给 HD2）⇒ 膝盖/脚落在**膝盖正下方**，鞋子不再外推。
**实测**：height 2.072 → **2.036**、edge mean → **1.0570**、p95 1.3987 → 1.2761、bad>25% → **7.60%**。
⚠️ 大腿**不要** transfer（源 `L_Thigh` 与 HD2 `l_thigh` 已几乎重合；transfer 会把腿整体抬高 0.15 m，实测高度爆到 3.03 m）。

### 5.3 ② 颈/头（**未实现**，下一步）
`Head1 → head` 把头的关节从 1.507 抬到 1.691（**+0.184**）⇒ 身高 1.857 → 2.036（**+9.6%**）= 「长脖子」。
但源的 **Head1 网格质心是 1.634**，离 HD2 的 head 关节（1.691）只差 **+0.057**。
⇒ **正解方向 = 「网格质心锚定」**：让 `T_head` 把源的**网格质心**送到 HD2 的关节，而不是把源的**关节**送过去。同理 Neck1（质心 1.466 → HD2 neck 1.550，仅 +0.084）。这样 neck→head 的网格间距变成 0.141 vs 源 0.168（**0.84，轻微压缩**），而不是现在的拉伸。
**代价**：需要把网格质心喂给 `build_bone_map`（脚本已加载网格，改动不大）。

---

## 6. 对我们的架构判断的影响

1. **「缝」不用自己发明**：`HD2IRG1` 就是现成的版本化声明文件（schema/version/crc/上限/纯旋转校验齐全）。**建议我们的转换器直接产出 `HD2IRG1`**；这样已装 AQ SDK + PhysBone 运行时的用户可以直接用，我们自己写的 Lua producer 也能读同一份数据。
   ⚠️ 但**硬约束「只依赖游戏本体」**仍在：**不能依赖他们的 Lua producer 存在**；我们要自带一个读 `HD2IRG1` 的 Lua 实现。
2. **`transfer_translation` / `source_parent_space` / `source_global_pose` / `inherit_parent_pose`** 四个 flag 正好覆盖我们需要的语义：**映射骨（可带平移补偿）/ 自定义追加骨（跟随父级姿态）**。
3. **X1 部分作答**：运行时上限 **256**（profile/palette 更宽：palette_slots ≤ 4096、profiles ≤ 512）；`copy_unmapped_deform_bones` 默认 **true = 追加保留**。我们「回退 → 追加骨」的计划与他们的默认行为一致。
4. **性别**：数据侧只有 `gender_male` / `gender_female` 两个 bit + 「直腿制作站姿」的约束；**「女性腿部修正」必须在我们的 Lua 运行时里实现**。
5. **HD2 骨架读取**：优先用 `avatar_source.json` 那份 63 根公开通道（干净、有名字、有世界 rest），TocData 那份 107 条作为「完整场景图」用。
6. **权重**：`skin_weights.py` 的 half4 精确闭合是「护甲分部件不再有裂缝」的原因 ⇒ 我们写蒙皮时必须做同样的精确闭合。

---

## 7. 【重大修正 2026-10-11 23:2x】两个按钮 + 吸附步骤的**完整数学** —— 之前我读漏了

> 触发：用户复述视频流程并质问「你有没有认真读过相关代码？」。
> **坦白：之前只读了 `body_pose_profile.json` 的数据和 `_convert_pose_space` 的函数名，没有读算子实现，更没有读吸附那一步。**
> 现已把整条链读完（`ui/operators.py`、`ui/panels.py`、`utils/rigging.py`、`workflows/mmd_mapping.py`）。
> 源码位置：`tmp/pb-ref/HD2-BatchTool-main/`（用户手动下载的 zip 解压）。

### 7.1 按钮 ↔ 算子 ↔ 函数的对应（已逐条核对）

| 界面文案 | 算子 | 函数 |
|---|---|---|
| `导入潜兵工作骨架` | `hd2bt.import_reference` | — |
| `转为烘焙 T-pose` | `hd2bt.convert_pose(direction=TO_TPOSE)` | `convert_pose_space(obj, "TO_TPOSE")` |
| `转回初态 + 原始骨轴` | `hd2bt.convert_pose(direction=TO_INITIAL)` | `convert_pose_space(obj, "TO_INITIAL")` |
| `骨骼吸附` | `hd2bt.snap_bones` | **`snap_independent_to_hd2(...)`** |
| `顶点组重命名+重绑` | `hd2bt.rename_vertex_groups` | `rename_vertex_groups(...)` |

按钮文案见 `ui/panels.py:62-65`；算子定义见 `ui/operators.py:203-252`。

### 7.2 `body_pose_profile.json` 实测（schema_version 2，`source="bundled_hd2_reference_profile"`，`reference_group="TP_INITIAL"`）

- `transform_policy` 原文：
  `pose = "PoseBone.matrix_basis rotation only"`、
  `axis = "per-bone local post-rotation; preserve edit-bone head and length"`、
  `excluded = "all non-body bones, including attach/weapon/camera controls"`。
- `body_bones` **列了 45 根**，但**只有 30 根有旋转值**；**没有值 = 单位旋转**（不动）。
  真正参与 A↔T 的 30 根全是**手臂 + 手**：`l/r_shoulder`、`l/r_elbow`、`l/r_hand`、`l/r_{pinky,ring,middle,index}_finger1..3`。
  ⚠️ **`chest` 在列表里但没有旋转值** —— **脊柱/颈/头/腿完全不参与 A↔T**（A↔T 只动手臂，与我的旧描述一致，但"45 根"的说法会误导）。
- 角度量级（实测）：`l_shoulder` **34.31°**、`l_elbow` **39.71°**、`l_hand` **6.28°**、手指 **3.45°~26.52°**。
- **两张表是精确共轭**（实测 `tpose × initial` 残差角 **0.0000°**，仅一根骨 0.0150° 浮点舍入）
  ⇒ 这就是「往返严格可逆」的原因。JSON 自带 `validation.maximum_inverse_pose_matrix_error = 4.896e-05`。
- `symmetric_axis_deltas_wxyz` **20 条、全部是右侧**（`r_*`），量级 **14.29°~64.42°** —— 这是**骨轴（roll）**的镜像差，**不是姿态**，用于 `AXIS_SYMMETRIC ↔ AXIS_INITIAL` 切换（**与性别无关**）。

### 7.3 `_convert_pose_space` 到底做了什么（`utils/rigging.py:613-673`）

两个正交的"空间"：
- **POSE 空间** ∈ {`INITIAL` = HD2 变体 A-pose，`TPOSE`}
- **AXIS 空间** ∈ {`INITIAL`（原版骨轴），`SYMMETRIC`（对称骨轴）}

**`TO_TPOSE`（从 INITIAL 出发）**：
1. `_clear_body_pose` —— 45 根 body 骨的 `matrix_basis` 清零；
2. `convert_body_axis_space(obj, AXIS_SYMMETRIC)` —— 逐骨 `new_local_rot = current_local_rot @ delta`（`delta` 只右侧有值），用 `Matrix.LocRotScale(bone.head_local, rot, (1,1,1))` 写回 **Edit 模式静止骨位**，`preserve_lengths=True` ⇒ **只改骨的朝向，head 与骨长不动**；
3. `_apply_body_pose_rotations(..., tpose_basis_rotations_wxyz)` —— `pose_bone.matrix_basis = Quaternion(wxyz).to_matrix().to_4x4()` ⇒ **纯局部旋转、零平移、零缩放**；
4. `_bake_body_pose_to_rest` —— ① 把所有**非** body 骨的 `matrix_basis` 临时清零（attach/weapon/camera 不许被重复烘焙）；② `desired = pose.bones[name].matrix`（body 骨 ∪ 额外骨）；③ **`_capture_deformed_mesh_coordinates`：真的用目标姿态求值骨架修改器，逐顶点（且逐形态键）记下形变后坐标**；④ `apply_bone_object_matrices(desired, preserve_lengths=True)` 写回 Edit 模式；⑤ `_write_deformed_mesh_coordinates` 把记下的顶点**写回静止网格/形态键**；
5. `_restore_reference_hand_axes(obj, "TP_TPOSE")` —— 把 `l/r_hand`、`*_hand_twist`、`*_finger*` 的**绝对静止轴**收敛回模板（保 head 与骨长）。

**`TO_INITIAL`（从 TPOSE 出发）**：把第 3 步换成 `initial_basis_rotations_from_baked_tpose_wxyz`（**精确共轭**），第 2 步换成 `convert_body_axis_space(..., AXIS_INITIAL)`，第 5 步换成 `TP_INITIAL`。
另外 `convert_pose_space(TO_INITIAL)` 会**拒绝带未应用形态键的绑定模型**（`ui_operators.py:236-239` 也明示"破坏性、只能 Undo 撤回"）。

### 7.4 `snap_independent_to_hd2` = 用户说的"缩放使其几乎重合"那一步（`workflows/mmd_mapping.py:859-959`）

文档字符串原文：
> **"把潜兵工作骨架直接吸附到当前人物自己的关节。不分类、不引用任何平均体型。先锁定可见脚底做保持比例的统一缩放，再让每根潜兵语义骨采用该人物映射骨的真实位置。"**

**步骤 ① `align_independent_head`（477-563）—— 一个均匀缩放 + 一个平移**
```
source_height  = (source_head_world  − source_ground_world) · up
desired_height = (reference_head_world + up·(−0.015) − reference_ground_world) · up
scale_factor   = desired_height / source_height        # 合法性检查 0.25..4.0
scale_transform = T(source_ground) · S(scale_factor) · T(−source_ground)
Δhead = desired_head_world − scaled_head_world          # 然后整体平移 Δhead
```
- 接地锚点优先取 **`l_ball`/`r_ball`**（没有才退到 `l_foot`/`r_foot`），再投影到可见脚底网格（`_ground_anchor`）。
- **这就是我们清单里的 ③「脚部改 `l_ball` 落点」** —— 他们本来就用的 ball。
- **这就是 `TorsoScale=1.042` 的来源**：它**不是**一个魔法常量，而是 `desired_height / source_height` 算出来的**单一均匀缩放**。

**步骤 ② `build_snap_heads`（595-649）—— 只改骨 head 的位置，不改朝向**
```
head_HD2[b] = world_position(source 映射到 b 的那根骨)     # 17 根必需骨
```
HD2 独有的骨用**确定性插值**补齐：
```
spine1          = lerp(hips,   spine2, 0.50)
chest           = spine2
{side}_shoulder_twist = lerp(shoulder, elbow, 0.42)
{side}_hand_twist     = wrist
{side}_shoulderarmour = lerp(clavicle, shoulder, 0.90)
{side}_toe            = ball + (ball − ankle)·0.35
head_tip              = head + (head − neck)
```
朝向：**保留 HD2 工作骨架自己的 `tail − head` 方向与骨长**；
**例外**：手/指从 `TP_INITIAL`/`TP_TPOSE` 模板恢复绝对静止轴 + `edit_bone.align_roll(...)`（`_reference_hand_rest_frames`，652-681）。

### 7.5 🔴 **结论：BatchTool 里根本没有 `T = H·S⁻¹` 这种跨骨架逐骨相似变换**

用户猜的"完善的数学变换模型"实际是**三件很朴素的东西**：

| # | 内容 | 量 |
|---|---|---|
| 1 | **一个均匀缩放 + 一个平移**（锁脚底、对头顶、−0.015 m） | 全局 1 个标量 |
| 2 | **把 HD2 骨的 head 赋值为源映射骨的世界位置**（HD2 独有骨确定性插值） | 逐骨**只有位置** |
| 3 | **一张 30 骨常量局部旋转表**（A↔T）+ 20 条右侧骨轴 delta | 常量表 |

**没有** roll/swing 分解，**没有** `poseToBone`，**没有**骨长比匹配，**没有**逐骨旋转映射。

**为什么这样反而更稳（也是我们一直在打空气的原因）**：
- 吸附发生在**绑定之前**。`rename_vertex_groups` 把网格重绑到已经吸附好的骨架 ⇒ **绑定态就是恒等**，静止时网格一点不变形 ⇒ **不会有身高膨胀、不会有躯干对穿、不会有长脖子**。
- 我们做 `T = H·S⁻¹` 时把两侧的 **roll 分歧烘进了网格**，才需要"只用 swing"来止损；BatchTool **压根不做逐骨旋转**，所以没这个问题。
- **`l_foot` 的 head := 源的脚踝关节** ⇒ **腿自然保持源自己的（直腿）站姿**，这就是「腿部无需对齐」的机制；`FEMALE` flag 只是给运行时再加一层修正。
- 我们 `--transfer-bones legs` 是在**手工逼近**同一件事，但走的是弯路。

### 7.6 对我们方案的直接影响（待与用户确认）

1. **Python 侧应改写**：把"逐骨 `T = H·S⁻¹`"换成
   `均匀缩放 → HD2 骨 head := 源关节 → 按字典重命名顶点组 → 绑定 → 施加 30 骨 A-pose 并烘焙`。
2. 我们已量化的很多"修正项"（② 关节语义、③ 脚 ball、⑤ TorsoScale）**在新框架下自动消失**，不再需要逐条修。
3. **`HD2IRG1` 仍然是那个"缝"**（§6 结论不变）。
4. 仍需向用户确认：**是改用 BatchTool 这套（更简单、更稳），还是继续修我们的逐骨 `T`**。

## 8. 【2026-10-11 深夜】BatchTool 式新管线：实现 + 5 模型实测 —— 旧路线正式退役

> 代码：`tmp/rig-lab/hd2_style.py`（核心 `retarget(...)`）、批量 `tmp/rig-lab/run_hd2style_all.py`、
> 新旧对比 `tmp/rig-lab/compare_style.py`；数字：`tmp/rig-lab/out/hd2style-report.json`。

### 8.1 实现了什么

严格按照 §7 读出来的 BatchTool 语义，四步：

```
v0   源顶点（米）
R    = Kabsch(20 个身体关节)              # 源 +X 左 / HD2 −X 左 ⇒ 实测恒为 180.00° 绕 Z，det +1
sole = 底部 1 cm 顶点片的质心              # 对应 _ground_anchor 把脚踝骨投到网格脚底
src_h= (R·head_bone).z − (R·sole).z       # 对应 (head − ground).dot(local_z_world)
s    = (HD2_head.z + (−0.015) − 0) / src_h  # ref_ground_z = 0（HD2 本体 l_ball z=0.0081、全骨 min z=0.0000）
v1   = R·(s·(v0 − sole)) + t，t 由 "head_bone 落到 HD2 head bone" 唯一确定
head_HD2[b] = 该 b 对应的源骨的 v1 位置     # ② 吸附：只搬 head，朝向与骨长仍是 HD2 自己的
rest[b]     = AXIS_SYMMETRIC delta 与 head 搬移【交错】在同一个父先子后循环里
D[b]        = D[parent] · rest[b] · basis[b] · inv(rest[b])     # basis = initial_basis_rotations_from_baked_tpose
v'          = Σ w_i · D[b_i] · v1
```

**三个必须记住的实现要点**（都踩过）：

1. **缩放高度必须在 HD2 空间沿 +Z 量**，不能在源空间直接取 z 差。BatchTool 是
   `(head − ground).dot(local_z_world)`，也就是先转进 HD2 空间再量。
2. **AXIS delta 与 head 搬移必须交错在同一个循环里。** 先算完整棵 `rest_sym` 再统一搬 head，
   会让**「父骨被搬走、自己没被映射」的 HD2 独有骨掉队**（例：`head_tip`、`l_shoulder_twist`）。
   虽然它不影响蒙皮（蒙皮只用被映射的骨），但导出真实骨架时会错位。
3. **`rest[b]` 的递归代数上恒等于 `convert_body_axis_space`**：
   BatchTool 是 `new_obj_rot = new_parent_rot @ inv(old_parent_rot) @ old_rot @ delta`
   （父非身体骨时退回 `old_parent_rot`），展开后与
   `rot[p] @ inv(rest_hd2[p]) @ rest_hd2[b] @ D[b]` 逐项相同。

### 8.2 5 模型实测（`--align kabsch --spine-map legacy`）

| 模型 | 模型名 | 顶点 | edge_mean | edge_p95 | edge_max | bad>25% | 缩放 | 脚距(源→出) |
|---|---|---|---|---|---|---|---|---|
| zoey | `survivor_teenangst` | 121704 | 0.9981 | 1.0009 | 2.87 | 0.58% | 1.0702 | 0.262→0.279 |
| rochelle | `survivor_producer` | 121704 | 0.9981 | 1.0009 | 2.87 | 0.58% | 1.0702 | 0.262→0.279 |
| coach | `survivor_coach` | 56303 | 1.0001 | — | 1.58 | 0.56% | 1.1261 | 0.245→0.276 |
| nick | `survivor_gambler` | 118014 | 0.9987 | — | 1.56 | 0.19% | 1.0813 | 0.238→0.257 |
| ellis | `survivor_mechanic` | 117750 | 0.9997 | — | 1.29 | 0.14% | 1.0830 | 0.244→0.265 |

**平均：edge_mean 0.9989 / edge_max ≤ 2.87 / bad>25% 0.41% —— 零逐模型调参。**

与旧管线（`T = H·S⁻¹`，`--roll-mode swing --spine-map mesh --transfer-bones legs --anchor neck`，
即 A4 找到的最优参数）对比，Zoey：

| 指标 | 旧 `T=H·S⁻¹` | 新 BatchTool 式 | 倍数 |
|---|---|---|---|
| edge_mean | 1.0543 | **0.9981** | — |
| edge_max | **27.07**（未加修正时 49.21） | **2.87** | **≈ 9.4× / 17×** |
| bad>25% | 7.67%（未加修正时 9.25%） | **0.58%** | **≈ 13× / 16×** |

**脚距没有变宽**：0.262 → 0.279 正好是 ×1.0702 的均匀缩放（0.262×0.0702 = 0.0184 ≈ 实测 Δ0.017）。
旧管线的叉腿（源 0.153 m 被撑到 HD2 的 0.382 m）在新管线里**根本不存在**，因为
`l_foot` 的 head 直接被搬到了源脚踝上 —— 这正是 README 说的「腿部无需对齐」。

### 8.3 两个自由度的实测结论

* **`--align`**：`kabsch`（对 20 个身体关节做最佳刚体拟合）在 5 个模型上**全部给出 180.00°、det +1.0000**，
  与旧项目 `CreateScale(-1,-1,1)` 完全一致。`rotz180`（硬编码 `diag(-1,-1,1)`）数值上略优
  （edge_max 2.78 vs 2.87、bad 0.38% vs 0.41%），差异在噪声级。**保留 kabsch 为默认**
  （它自带诊断输出，能发现"某个模型不是干净的 180°"这种异常）；`rotz180` 作为可选。
* **`--spine-map`**：`legacy`（源 `Spine→spine1`）与 `mesh`（源 `Spine→spine2`）**数值上几乎无差**
  （edge_mean 都是 0.9989）。原因很清楚：**bind 是恒等的，所以关节落在哪不影响静止形状**；
  旧管线里这个问题之所以严重，是因为那时是「把源关节搬到 HD2 关节上」+「逐骨 roll 烘进网格」。
  按 §7.6 的字典方向，**默认取 legacy**。

### 8.4 顺带确认的源骨架事实（Zoey）

| 源骨 | 关节 z | 网格质心 z | 主导权重顶点数 |
|---|---|---|---|
| `Pelvis` | 0.8193 | 1.0820 | 14116 |
| `Spine` | 1.0986 | 1.1662 | 3910 |
| `Spine1` | **1.0986**（与 Spine 同点） | — | **0** |
| `Spine2` | 1.2191 | — | **0** |
| `Spine4` | **1.2191**（与 Spine2 同点） | 1.3409 | 12254 |
| `Neck1` | 1.4531 | 1.4663 | 1580 |
| `Head1` | 1.5069 | 1.6343 | 22305 |

⇒ **有效躯干关节只有 3 个**（0.8193 / 1.0986 / 1.2191 + 颈头）；`Spine1`/`Spine2` 是零权重的退化骨。
另外源 `L_UpperArm`/`L_Forearm`/`L_Hand` 的 z **全等 1.4121** ⇒ **源 T-pose 的手臂是严格水平的**，
而 HD2 的 `l_shoulder` 1.4745 → `l_elbow` 1.3236 ⇒ **HD2 的手臂是 A-pose 下垂的**。两件事互不冲突：
新管线只把 HD2 的 head 搬到源关节上，姿态差由 §7.2 那张 30 骨常量表补。

### 8.5 已知的模型自身瑕疵（**不是**我们引入的）

* **Nick**：`source_extra_97..101`（右）与 `102..106`（左）是**长袖的 jiggle 骨链**，
  bind pose 里袖子朝 +Y 伸出 **1.778 m**（骨骼链本身到 1.518 m）。
* **Ellis**：`source_extra_124` 也有 1.036 m 的伸出。

这两个在**源 T-pose 里就存在**（`compare_style.py` 第 1 行就是源 bind pose），所以是模型作者
把袖子/飘带按"未受物理约束"的状态建模的。**这正好是「必须有额外骨 + Lua 驱动」的直接证据**：
不带物理的 legacy 包里，这些飘带会保持伸出的静态形状。

顺带：`drop_outlier_tris`（按**边长**判据剔除）**剔不掉**它们 —— 这些网格的中位边长只有
6~10 mm，最大边长 0.13~0.34 m，而这些飘带是**正常连接**的独立几何，不是长三角形拉出来的尖刺。
所以**不能用"剔除离群几何"来解决**，只能靠物理驱动或按部位裁剪。

---

# §9 用一份真实的参照 patch 反查新管线（2026-10-12）

用户提供了一份**在游戏内表现良好**的 legacy 补丁（`E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310`，
E 盘是用户的测试 mod 库），要求：正面图 + 侧面图 + 骨架对比，重点看**腿 / 躯干 / 头在侧视图的相对位置**。

工具：`tmp/rig-lab/patch_view.py`（多列多行对比渲染）、`tmp/rig-lab/side_skel.py`
（侧视骨架叠加）、`tmp/rig-lab/side_overlay.py`（侧视网格轮廓叠加）。

## 9.1 参照 patch 的解析结果

容器：`9ba626afa44a3aa3.patch_0`（216 KB）+ `.gpu_resources`（219 MB）+ `.stream`（0 B），**无 manifest.json**。
**35 条目** = 23 Unit + 2 Bone + 10 未知（含大贴图）。23 个 Unit 里**只有 9 个有几何**，
其余（`*_culling`、`*_Armor_*_mesh0`）都是 **1 顶点 / 0 三角形**的占位。

9 个几何 Unit 是**两套体型**：

| 部位 | Slim / female | Stocky / male |
|---|---|---|
| Torso | `0xcae35ee15168145f` (119578 v) | `0x9841b16544c8fbae` (119636 v) |
| Hip | **没有** | `0x17da843a4908511e` (95711 v) |
| LeftArm | `0x91eb7be84016ceab` (13237 v) | `0x9a5e6dc74cdb08a3` (13237 v) |
| RightArm | `0x7336c6b662522f0e` (8983 v) | `0x4f7bce4ca49ccda8` (8983 v) |

⇒ **腿/髋两性共用**（只有 Stocky 的 Hip）。另有两个可疑条目：`0xaf431352d9b95e83`
（也叫 `Torso_Undergarment_Slim_lod3`，但 bbox `z[0.911, 2.059]`，**空间不对**，疑似同一 patch 里
混进的另一套模型）与 `0x45d79d84b8933a7f`（Hip 的重复，数据逐位相同）。**渲染时选 `cae3,17da,91eb,7336`。**

### 三个必须记住的读取细节

1. **Unit 骨骼矩阵是行向量约定**（平移 flat **12,13,14**），与 `E.read_hd2_rig`（已转置成列向量）
   **相反**。读错不会报错 —— 全 0 是合法坐标，只是所有骨都在原点。
2. **Unit 的 bone 列表前 9 项是场景图节点**，不是骨：`StingrayEntityRoot`、`FbxAxisSystem_ConvertNode`、
   `?36e6e872`、`game_mesh`、`grp_*`、`g_*_shadow`/`_LOD1`/`_LOD2`/`g_*`。真骨从 `skeleton` 起；
   `boss` 是髋部根运动骨（z 0.9735）。
3. **Unit 的骨名是空的**，要靠 **32-bit hash 查 `bonehash` 文件**（与 HD2 原生同一张表）。

### 【关键判据】补丁的骨架与 HD2 原生逐位一致

`hips` 0.9735 / `spine1` 0.9735 / `spine2` 1.1633 / `chest` 1.3437 / `neck` 1.5500 / `head` 1.6907 /
`l_thigh` 0.9417 / `l_knee` 0.5163 / `l_foot` 0.1042 —— **与游戏本体一模一样**。
只**多出** HD2 原生没有的辅助骨：`torso_culling`、`hips_culling`、`helmet_culling`、`l/r_arm_culling`、
`l/r_shoulder_twist`、`l/r_hand_twist`。

⇒ **用户说的"该 mod 作者为比例风格做了独立优化"，完全发生在网格层面，骨骼是照抄 HD2 的。**
这是本轮最有价值的一条：**要"像"它，必须让网格长成 HD2 的站姿，而不是让骨骼去迁就网格。**

## 9.2 HD2 坐标约定（本轮实测坐实）

- **+Y = 前，+X = 左，+Z = 上。**
- 判据：补丁脚部网格 `x∈[-0.212,-0.102]`（左脚在 −X）、`y∈[-0.145,+0.119]`（脚长 0.264 m），
  而踝关节 `l_foot` 的 y = −0.1175 只离"脚跟端"2.7 cm ⇒ 脚趾必朝 **+Y**。
- **`l_foot` 关节不在脚心**：HD2 原生 hips y −0.019 / ankle y −0.1175 ⇒ 踝在髋后 **9.86 cm**。

## 9.3 用户描述的三条几何目标，逐条用网格坐实

| 判据 | 补丁实测 | 说明 |
|---|---|---|
| ① 髋中心对准**脚心**（脚长中点） | hip y −0.019 vs 脚部网格中点 y −0.013 | **对齐** ✔；"标准 T-pose 髋对脚跟"不成立（髋其实在脚长的 48.5% 处） |
| ② 脖子比髋中心**稍靠后** | neck y −0.0412 ⇒ **−2.22 cm** | 用户描述正确 |
| ③ 头 | head y −0.0251 ⇒ **−0.61 cm** | 基本在髋正上方 |

## 9.4 【根因】`--align kabsch` 把身体转歪了 2.8°

两侧的 20 个身体关节点集**不全等**：

- 源 `Pelvis` 关节 z = 0.8193（**在髋关节下方 12 cm**，是根位置锚点）
- HD2 `hips` z = 0.9735（**就在髋上**）

⇒ 自由 3-DOF 的 Kabsch 拟合出一个**偏离竖直 2.8°** 的 180° 旋转
（实测 `align: kabsch -> 180.00 deg about (-0.000, -0.049, -0.999)`，Z 分量 0.999）。
绕一根偏离竖直的轴转 180°，会把 **0.94 m 的身高差混进 Y 方向**，凭空造出 9.75 cm 的脚踝偏移。

**实测（Zoey，20 个身体关节，Δ 一律相对髋关节）：**

| 关节 | `kabsch` | **`yaw`** | HD2 原生 / 补丁 |
|---|---|---|---|
| `neck` | **+0.0175**（在前） | **−0.0359**（在后） | −0.0222 |
| `head` | +0.0296 | **−0.0293** | −0.0061 |
| `l_knee` | −0.0410 | **±0.0000** | −0.0155 |
| `l_foot` | **−0.0975** | **±0.0000** | −0.0986 |

- `kabsch`：**脖子在髋前 1.75 cm** —— 这就是用户看到的"从胯骨开始向前倾倒"。
- `yaw`：脖子在髋后 3.59 cm（HD2 是 2.22 cm）⇒ **躯干前倾消失，误差从 3.97 cm 降到 1.37 cm。**
- 但 `yaw` 让**腿变成严格竖直**（源 T-pose 的腿本来就是竖直的），踝关节从"髋后 9.75 cm"变成
  **"正在髋下方 0.00"**，丢掉了 HD2 的站姿。

**⇒ 一个刚体旋转修不好两件事。** 源的姿态是「腿竖直 + 躯干前倾 3.3 cm」，
HD2 的姿态是「腿后倾 9.86 cm + 躯干后倾 2.22 cm」—— **两套骨架对腿/躯干的约定本来就不同。**

已实现 `yaw_rotation()`（1-DOF，投影 XY 做 2D SVD）+ `--align yaw`；`kabsch` 分支打印 WARNING。

## 9.5 【新增判据】bind 恒等会掩盖真问题 —— 必须渲染"游戏里显示的那一帧"

我们的 `D` 在绑定态是恒等 ⇒ **绑定态网格完全不变形** ⇒ 任何绑定态指标（边长比、包围盒）
都会给出漂亮的结果，**哪怕对应表是错的**。

游戏里做的是 `A[b] · inv(S[b])`（A = 动画骨、S = 我们发布的 rest）。取 A = HD2 原生 rest
（"idle"），就是**玩家实际看到的那一帧**。为此在 `hd2_style.retarget` 增加 `out_idle`：

```
out      = D · src1                     # 绑定态（T-pose 网格 + A-pose 手臂）
S_A      = D · rest_sym                 # 发布的 rest（A-pose）
out_idle = (rest_hd2 · inv(S_A)) · out  # 等价于 rest_hd2 · inv(rest_sym) · src1
```

**只有这个渲染能与参照 patch 直接对比。** 用它立刻抓出两个绑定态完全看不见的错误：

### 错误 A：`Spine → spine1` 让躯干拉伸 26.5 cm

HD2 的 `spine1` **与 `hips` 完全重合**（都是 z 0.9735）。而源的 `Spine` 在骨盆上方 27.9 cm。
把它们对应起来，我们的 rest `spine1` 就落在 1.2387（HD2 是 0.9735）⇒ 每帧躯干被拉长 26.5 cm。

```
bone        our z     hd2 z        dz
spine1     1.2387    0.9735   -0.2653
spine2     1.3678    1.1633   -0.2044
chest      1.3678    1.3437   -0.0240
```

⇒ **`--spine-map mesh`（`Spine→spine2`、`Spine2→chest`、`Spine4→chest`）是必须的，不是可选调优。**
A4 里把它当成"数值几乎无差"是**因为只看了绑定态**。

### 错误 B：鞋被绑到**膝盖**上，导致穿地 13.5 cm

源的鞋（`source_extra_62/63`，质心 x = ±0.076）在 MDL 里**父级是 `Bip01_L_Calf`**，
于是"沿父链回退"把它们绑到 `l_knee`，而不是 `l_foot`：

```
source_extra_62 -> l_knee (via ValveBiped.Bip01_L_Calf, 1 hops)
source_extra_63 -> r_knee (via ValveBiped.Bip01_R_Calf, 1 hops)
```

我们的 rest `l_knee` 比 HD2 高 13.45 cm ⇒ **鞋整体下沉 13.5 cm，穿到地板下面**
（`out_idle` 最低 z 从 0.000 掉到 −0.138）。

⇒ 新增 `--fallback distance`：无对应骨的源骨**绑定到关节位置最近的已映射骨**，
而不是最近的已映射祖先。修好后 `out_idle` 最低 z = **+0.024**（站在地板上）。

| align | spine | fallback | `out_idle` bbox | 高度 |
|---|---|---|---|---|
| kabsch | legacy | parent | z[−0.150, 1.993] | 2.143 m |
| kabsch | legacy | **distance** | z[**+0.014**, 1.993] | 1.979 m |
| yaw | legacy | **distance** | z[**+0.024**, 1.999] | 1.975 m |
| yaw | **mesh** | **distance** | z[**+0.024**, 1.999] | 1.975 m |

## 9.6 【未解决】两个需要用户拍板的判断题

### ① 腿：竖直（源）还是后倾 9.86 cm（HD2）？
- `yaw` → 腿竖直、踝在髋正下方、躯干正确。
- `kabsch` → 腿后倾 9.75 cm（数值上≈HD2 的 9.86）、躯干前倾 4 cm。
- 参照 patch 用的是 **HD2 的腿**（后倾 9.86 cm）+ 一张**画成 HD2 站姿的网格**。
- BatchTool 官方说法是"腿部无需对齐"、"只支持直腿制作姿势" ⇒ 它的吸附会给出**竖直腿**。
- **尚未决定**：我们是否要显式保留 HD2 的腿骨位置（等价于"网格按 HD2 站姿重画"那一半工作）。

### ② 头/整体尺寸：按**头骨关节**还是按**头几何**对齐？
- 源 `Head1` z 1.5069、模型顶点顶 z 1.7948 ⇒ **头几何在关节上方 28.8 cm**（含帽子/发型）。
- HD2 `head` z 1.6908、`head_tip` 1.7102、补丁网格顶 1.715 ⇒ **头几何只在关节上方 2.4 cm**。
- 即 **`Head1` 是颅底、HD2 的 `head` 是颅顶** —— `neck→head` 段长 5.4 cm vs 14.08 cm，
  **是"关节语义不同"，不是尺寸差**（A2 早就量到 2.6154 这个比值，本轮确认它会真的把模型撑大）。
- 现有规则（BatchTool 的 `align_independent_head`）按**头骨关节**配高 ⇒ 我们的模型总高
  **1.975 m**，而 HD2/补丁只有 ~1.71 m ⇒ **高出约 27 cm（~16%）**。
- **尚未决定**：改按"头几何顶部"配高（模型变矮但头会掉到脖子下面）、
  还是按**头部网格质心**锚定（旧管线的 `--anchor neck` 思路）、还是显式给一个 `--head-offset`。

## 10. 【2026-10-12 第 34 轮】第四列（参照 patch）到底做了什么 —— 用它的网格坐实

### 10.1 RE-2310 头盔 = `0xaf431352d9b95e83`（第四列此前"没有头"的原因）
- 参照 patch 容器里 **9 个 Unit 有几何**：`af431352d9b95e83`（头+长发）、`cae35ee15168145f` /
  `9841b16544c8fbae`（躯干+裙，slim/stocky）、`17da843a4908511e` / `45d79d84b8933a7f`（髋+腿）、
  `9a5e6dc74cdb08a3` / `4f7bce4ca49ccda8`（左/右臂，stocky）、`91eb7be84016ceab` / `7336c6b662522f0e`
  （左/右臂，slim）。其余 14 个是 1 顶点 0 三角的**空占位**（`*_Armor_*_mesh0`、`hips_culling`）。
- **没有任何 Unit 叫 helmet。** `af431352d9b95e83` 的标签是 `Torso_Undergarment_Slim_lod3`
  （和 `cae35ee15168145f` 同名），**但它的几何就是"脸 + 猫耳 + 长发"**（`out/ref-units.png`、
  `out/ref-unit-af43.png`）。⇒ **它就是 RE-2310 的头盔槽**（mod 版头+发）。
  **不需要、也不应该从游戏本体取头盔。**
- 之前第四列"缺头"= 我漏选了这一个 unit。加上后完整参照 **z[−0.054, +2.059]，总高 2.113 m**。
- ⚠️ **更正上一轮的一个错误比较**：上一轮说"参照 1.77 m / 我们 1.99 m ⇒ 我们高了 22 cm" ——
  1.77 是**去掉头的身体**。**含头比：参照 2.113 m > 我们 1.987 m**，我们其实**矮 12.6 cm**。

### 10.2 参照作者的骨架 = HD2 原生（逐位，delta = 0.000000）
逐骨对比 13 根（`hips / spine1 / spine2 / chest / neck / head / l_clavicle / l_shoulder /
l_elbow / l_hand / l_thigh / l_knee / l_foot`）：**全部 0.000000 差**。
⇒ **作者一根骨都没动。** 所有"比例优化"都在网格层。

### 10.3 网格是**围着骨架**做的（这就是"骨骼不与网格重合"的真相）
| 量 | 参照 patch | 我们 | HD2 原生 |
|---|---|---|---|
| 头关节 z | 1.6907 | 1.6908 | 1.6908 |
| 头几何在头关节**之上** | **0.3678 m** | 0.2931 m | 0.024 m |
| 头顶 z | 2.059 | 1.984 | ~1.715 |
| 头部区（z>1.62）宽×深×高 | 0.488×0.414×0.439 | 0.455×0.287×0.364 | — |
| 袖口超出 `l_hand` 关节 | +0.0887 m | — | — |
| 裙摆带宽×深（z 0.75–1.05） | 0.756 × 0.759 m | — | — |

- 头：**同一个 `head` 关节，几何却高 36.8 cm**（HD2 只有 2.4 cm）⇒ 作者把头部几何**整体上移并放大**，
  让它"骑"在头关节上，再用权重把颈/头骨混进去。**我们已经在做同一件事（0.293 m）**，
  只是比参照小 ~20%（宽 −7%、深 −31%、高 −17%）⇒ 用户说的"头放大非常多"**相对 HD2 是对的，
  但相对参照我们只是略小**。
- 袖口/裙摆同理：几何故意伸出骨架之外，靠权重摊到最近的骨上。
- **结论：不需要"把骨搬到几何上"，要"把几何围在骨上"。** 我们的管线已经是这个方向。

### 10.4 `--leg-tilt` 之前是**坏的**（已修，`hd2_style.resolve_bone_map`）
- 症状：tilt 0→10° 髋落脚长只从 28.1% 动到 35.9%，**参照是 51.1%**，看着像"倾斜没用"。
- 根因：腿倾的网格变换用 **`src_index_to_hd2`（只有那 50 对映射）** 查骨名，
  鞋骨（`source_extra_62/63`）**不在里面** ⇒ 查不到 → 落进"其他骨"分支 →
  **鞋跟着躯干一起平移**，正好抵消了髋的前移。
- 修法：把"源骨 → HD2 骨（含 fallback）"抽成 `resolve_bone_map(...)`，
  **腿倾段与蒙皮段共用同一份答案**。
- 修好后（同一口径）：

| tilt | 0 | 3.0 | 5.0 | 6.5 | 8.0 | 10.0 |
|---|---|---|---|---|---|---|
| 髋落脚长 | 28.1% | 45.9% | 57.6% | 66.5% | 75.2% | 86.9% |
| 踝相对髋 | 0.000 | −0.0523 | −0.0872 | −0.1132 | −0.1392 | −0.1737 |

- **HD2 原生踝在髋后 9.86 cm ⇒ tilt ≈ 5.6° 精确复现 HD2/参照的腿部站姿**；
  参照网格口径（51.1%）对应 ≈ 3.5°（差异来自我们的鞋比参照长 1.5 cm）。
- 副作用：总高 1.987 → 1.983 m（tilt 5.7），可忽略；脚掌保持贴地。

### 10.5 【新增·第 4 个真 bug】我们的**脚落点整体前移了 ~6 cm**
- 管线里 `t` 是**把头关节锁到 HD2 头关节**（`t = head_hd2 + [0,0,offset] − aligned_head_src`），
  **脚的 X/Y 没有任何约束**。文档字符串写 "locking the visible foot sole"，
  **只有 Z 是**（且只是因为缩放由 sole→head 高度推出来）。
- 实测脚底贴片质心：我们 `y +0.0427` / 参照 `y −0.0198` ⇒ **前移 6.25 cm**；
  `x −0.1138` / 参照 `−0.1576` ⇒ **内收 4.4 cm**。
- 参照作者是**把脚对齐到 HD2 的脚**的（他的网格脚底贴片就在 HD2 的脚下方）。
  ⇒ 我们应当**把 XY 的锚点从"头关节"改成"脚底贴片"**（Z 仍锁头关节）。**未改，待用户拍板。**

### 10.6 这一轮之后，"头/尺寸"这个未决项怎么看
- §9.6 ② 的担心（"我们的模型比 HD2 高 27 cm"）**在参照面前不成立**：参照也高 35 cm，
  而且比我们更高。
- 仍然成立的是**语义差**（`Head1` 是颅底、HD2 `head` 是颅顶），但**参照作者用同样的
  "几何骑在关节上"的做法解决了它** ⇒ 我们照做即可，不必改缩放。
- ⇒ **§9.6 ② 降级为"可选微调"**（把头部几何放大 ~15–20% 向参照靠拢），不是阻塞项。

## 11. 【2026-10-12 第 36 轮】正视「稍息」站姿 —— 参照的骨与网格是两套角度

### 11.1 症状与量法
- 侧视（`--leg-tilt 5.7`）已经和参照一致；**正视**里我们的腿是**笔直竖直**的（外张 0.00°），
  而参照是「稍息」式外张。用户要求照第四列正视图把腿掰开。
- 量法：把 `|x|`（左腿在高度 z 处的质心）**按身体高度归一**。参照 2.113 m、我们 1.983 m，
  不归一会把参照的每个数放大 1.7 倍。
  ⚠️ **踩过一次**：第一版拿「腿那个 unit 自己的高度 1.244 m」当分母，结果参照的鞋底变成 0.126 h
  （真值 0.0741 h），整张表全错。**分母必须是全身高。**

### 11.2 参照的骨架 = HD2 原生的 7.07°，但它的**网格**只等效 4.0°
| | 髋 x | 膝 x | 踝 x | 髋→踝 |
|---|---|---|---|---|
| HD2 原生 = 参照骨架 | ±0.0870 | ±0.1445 | ±0.1908 | **+7.07°**（髋跨 0.174 → 踝跨 0.382 m）|
| 参照**网格** | — | 小腿中心 ±0.1453 | 鞋底中心 **±0.1566** | 等效 **4.0°** |

⇒ **作者的骨比网格更外张**：踝骨在 `±0.1908`，而鞋底网格中心只有 `±0.1566` ——
骨落在鞋的**外侧 1/3** 处，**网格比骨内收 3.4 cm**。
这正是用户看到的「骨架和脚掌网格并不重合」，但方向与直觉相反：
**不是网格张得不够，是骨张得更多。**（作者没动骨，7.07° 是 HD2 自带的。）

### 11.3 我们只能二选一（我们的网格是刚性跟着骨走的）
管线里没有「重新刷权重」这一步，网格的腿和脚是刚体跟着骨走的，
所以一次旋转只能给一个答案，**「对齐网格」和「对齐骨」是两个不同的角度**：

| `--leg-splay` | 鞋底 x / h | 小腿@0.23h / h | 踝跨 m | 对齐的是 |
|---|---|---|---|---|
| 0（现状）| 0.0395 | 0.0476 | 0.164 | — |
| 3.0 | 0.0658 | 0.0634 | 0.251 | |
| **4.0** | **0.0746** | 0.0688 | 0.303 | **网格**（参照 0.0741 / 0.0785）|
| 5.57 | 0.0884 | 0.0771 | 0.357 | **骨**（HD2 踝跨按身高折算 = 0.358）|
| 7.07 | 0.1009 | 0.0847 | 0.409 | HD2 原角 |

- 在 0.03–0.30 h 取 11 个切片做拟合，**RMS 最小 = 4.0°**（0.0060 h ≈ 12 mm），4.5° 起反号。
  **4.0° 时鞋底与参照精确相等（0.0746 vs 0.0741）。**
- **5.57°** 让我们的**踝骨**落在 HD2 踝骨按身高折算的位置（0.357 vs 0.358）。
  ⇒ 若更看重「骨与 HD2 一致」（对动画更友好）而不是「看着像参照」，选这个。

### 11.4 实现（`--leg-splay`，`hd2_style.py` 步骤 2c）
- 绕**腿根**（`{l,r}_thigh` 的 head）在正视平面内（绕 +Y）旋转：左腿 `+θ`、右腿 `−θ`。
- `thigh`/`knee` 拿这个旋转；`foot`/`ball`/`toe` 拿**反向旋转**。因为踝是刚体跟着腿走的，
  反向旋转**恰好塌缩成一个纯平移** `trans(ankle_new − ankle_old)` —— 也就是用户说的
  「再从脚踝反向转回来」，而「脚掌再额外平移半个脚掌」是**几何自动给出的**，不是魔法数字。
- 网格同步（按权重混合），否则腿会被剪切。
- **实测（sp 0 → 7.07°）**：鞋底法向恒为 `(0,−0.008,1.000)`、倾角恒 **0.45°**（脚掌始终贴地平行）；
  脚在水平面内不旋转（89.00° 不变）；鞋底 y 跨度不变（0.2929，**无剪切**）；
  **侧视逐点完全相同（max|Δy| = 0.00000 m）** ⇒ 这是纯正视操作，不与 `--leg-tilt` 打架。
- 副作用：`zmin` 从 −0.0036（sp=0）→ −0.0012（sp=4）→ +0.0040（sp=7）
  ⇒ **反而更贴地**，不需要额外补偿。
- 5 模型（zoey/rochelle/coach/nick/ellis）在 sp=4.0 下鞋底 x/h 全部落在 **0.072–0.076**
  （参照 0.0741），**零逐模型调参**；edge_mean 不变
  （nick 1.0054→1.0056、ellis 1.0003→1.0005）。
- ⚠️ **踩坑**：第一版把旋转矩阵命名成 `R`，**遮蔽了 `retarget` 外层的对齐矩阵 `R`**
  （Python 是函数级作用域，赋值即让整个函数体的 `R` 变成局部）⇒ `align_pts` 拿 4×4 去乘 3 维点
  ⇒ `matmul` 维度报错。**改名为 `Rsp`。**（同名的 `sole`/`s`/`t` 也要当心。）

---

## §12 【2026-10-12 · 第 37 轮】我们发布的骨架「骨轴」是错的 —— 必须做 BatchTool 的「往返」

> 这是**动手掌之前的第一号问题**，比「手臂链短 10 cm」更靠前：它让**整副骨架的朝向**相对 HD2 原生偏掉，
> 最多 **180°**、107 根骨里有 **45 根**超 5°。而参照 patch 的骨架与 HD2 原生**逐位一致**。

### 12.1 结论先行

| | 与 HD2 原生骨架的**朝向**差 | >5° 的骨 | idle 帧网格位移 max |
|---|---|---|---|
| 我们现在发布的（legacy） | mean **43.90°** / max **180.00°** | **45 / 107** | **0.568 m**（nick 3.65 m） |
| 走完 BatchTool 往返后 | mean **0.02°** / max **0.05°** | **0 / 107** | **0.183 m**（nick 0.207 m） |
| 参照 patch | **0**（逐位一致） | 0 | **0** |

5 模型（zoey/rochelle/coach/nick/ellis）全部 `0/107`，**零逐模型调参**；
`edge_mean` 不变或略好（zoey 0.9989→0.9987、coach 1.0014→1.0006、nick 1.0056→1.0029、ellis 1.0005→1.0004）；
`bad%` 基本中性（nick 反而 1.02%→**0.50%**）；发布的**网格本身**只动 mean 6 mm / max 9.2 cm。

### 12.2 根因：BatchTool 的「工作骨架」和「发布骨架」不是同一副

`utils/rigging.py` 的 `_convert_pose_space` 是**两个独立状态机**：

- `POSE_SPACE`：`INITIAL`（初态/A-pose）↔ `TPOSE`
- `BODY_AXIS_SPACE`：`INITIAL`（**原始骨轴**）↔ `SYMMETRIC`（对称骨轴）

**工作态**（第 1–5 步，吸附就在这一步做）：`TO_TPOSE` 分支 = 清 pose → `convert_body_axis_space(AXIS_SYMMETRIC)`
→ `_apply_body_pose_rotations(tpose_basis_rotations_wxyz)` → **烘焙** ⇒ 得到 `SYMMETRIC` 轴 + T-pose。

**发布态**（第 6 步「转回初态 + 原始骨轴」）：`TO_INITIAL` 分支 = `_apply_body_pose_rotations(initial_basis_rotations_from_baked_tpose_wxyz)`
→ **烘焙** → `convert_body_axis_space(AXIS_INITIAL)` ⇒ 得到 `INITIAL` 轴 + A-pose。

**关键：两套 basis 表互为逆元**（实测：30 条全部满足 `initial ≈ inverse(tpose)`，误差 ≤ 2e-5，
与 profile 自带的 `validation.maximum_inverse_pose_matrix_error = 4.9e-5` 一致）。
所以两次烘焙在**骨架的局部旋转上互相抵消**：

```
L_0                                  (INITIAL 轴, A-pose)
  --(轴 -> SYMMETRIC)-->  L_0 · Dd
  --(烘焙 basis_t)-->     L_0 · Dd · basis_t      <- 工作态 T-pose
  --(烘焙 basis_i)-->     L_0 · Dd · basis_t · basis_i = L_0 · Dd
  --(轴 -> INITIAL)-->    L_0 · Dd · Dd⁻¹ = L_0   <- 发布态 = HD2 原生
```

而**网格被烘焙两次**（先 T-pose 再 A-pose），净效果 = 源 T-pose 网格变成 A-pose 网格。
**⇒ 发布出去的骨架 = HD2 原生骨架（朝向完全不动），网格 = 源模型的 A-pose。**
这正是参照 patch 的样子（骨架 13 根骨 delta 全 0、作者一根骨都没动）。

**我们错在哪**：`hd2_style.py` 只做了**后半段** —— `rest_sym` 用的是 HD2 **初态（A-pose）朝向** + 源 T-pose 的关节位置，
然后只烘焙一次 `basis_i`，并且**从不把骨轴转回 `INITIAL`**。于是：
- 少烘焙 `basis_t` ⇒ `L_0 · basis_i`，多出 `basis_i`（`l_shoulder` 34.32°、`l_elbow` 47.36°、`l_hand` 49.24°）；
- 没转回轴 ⇒ 再多出累积的 `Dd`（`r_clavicle` **180.00°**、`r_shoulder` 179.04°、`r_elbow` 145.89°、`r_hand` 144.64°）。

**⚠️ 更正上一轮的一个错误断言**：上一轮说「20 条对称轴 delta 里 18 条是 180° 翻转」——**那是把四元数当成 `xyzw` 读的**。
`..._wxyz` 是 `w` 在前的。按 `w` 在前重算：**只有 2 条是 180°**（`r_clavicle` 绕 +Z、`r_shoulderarmour`），
其余是 1.0°–98.8°（`r_shoulder` 64.4°、`r_elbow` 14.8°、手指 1–99°）。所以「关掉 delta」（`--no-axis-deltas`）
**不是**正确的修法 —— 那只是把 `Dd` 从骨架里删掉、却同时破坏了与 `basis_i` 的配对（实测把右肘/右手关节位置推到 0.44 / 0.69 m 外）。

### 12.3 实现（`hd2_style.py`，新增 `axis_roundtrip=True`）

1. **步骤 3**：`rest_sym` 的局部旋转再乘一个 `basis_t`（`tpose_basis_rotations_wxyz`），
   让它成为**真正的 T-pose 工作静止姿态**（`... @ rest_hd2[h] @ Dd) @ basis_t`）。关节位置仍由 `new_head` 覆盖。
2. **步骤 4**：照旧用 `initial_basis_rotations_from_baked_tpose_wxyz` 烘焙（T-pose → A-pose）。
3. **新增步骤 4b**：把 `S_A = D · rest_sym` 的骨轴从 `SYMMETRIC` 转回 `INITIAL`：
   局部旋转 `L ← L · inv(Dd)`，**并且把 head 放回烘焙后的位置**。
   ⚠️ Blender 的 `convert_body_axis_space` 只转骨自己的坐标系、**head 原地不动**
   （`desired = Matrix.LocRotScale(bone.head_local, new_rot, 1)`，head 漂移 >1e-5 就抛异常）。
   第一版我按「矩阵链乘」写、没保住 head，结果右肩/肘/手被推到 0.25/0.81/1.27 m 外 —— 加上 head 回填后降到 0.18 m。
4. **步骤 5**：idle 帧改用 `rest_shipped`（而不是 `D · rest_sym`）算 `rest_hd2 · inv(S_A)`。

**向后兼容**：`axis_roundtrip=False` 时 `basis_t={}`、不做 4b、`rest_shipped = D · rest_sym`，
与旧行为**逐位相同**（实测 `out` 与旧基线差 `0.00000 m`）。

CLI：`patch_view.py --legacy-axis`（关掉往返，仅供 A/B）、`hd2_style.py --legacy-axis`；
`--leg-splay` / `--leg-tilt` 也补进了 `hd2_style.py` 的 `main()`。

### 12.4 为什么这条排在「手臂链短 10 cm」前面

`--leg-tilt/--leg-splay` 修的是**关节位置**；这一条修的是**骨头的坐标系朝向**。
骨架朝向错了，游戏里**任何**动画都会把每个骨转到一个错误的绝对朝向 —— 不只是静止帧。
所以它是「必须先解决」的第一条，而且它顺带把 idle 撕裂从 0.568 m 压到 0.183 m。

### 12.5 探针

- `probe_rest_gap.py`：三方（legacy-ON / legacy-OFF / 往返）的 head / 旋转 / head_slip / idle 位移。
- `probe_roundtrip.py`：**不修改管线**、纯用 `retarget()` 的返回值重建整条往返，用来验证假说
  （往返前 43.90°/180°，往返后 **0.02°/0.05°**）。
- `rigfix_overlay.py`：三列对比图（legacy / 往返 / 参照），**每列都叠骨架**
  —— 青+洋红 = 我们发布的 rest，琥珀 = HD2 原生 rest。
- `run_rigfix_all.py`：5 模型 × legacy/往返 回归。
- `probe_out_diff.py`：发布的网格本身变了多少 + legacy 路径与基线逐位一致的证据。
---

## §13 【2026-10-12 · 第 38 轮】发布骨架 = HD2 原生骨架（一字不改），让**网格**去适应它

> 用户的原话：**「参考模型的骨架没有要求网格一定要和他重合，而是骨架完全在原始位置不变，
> 允许模型错位，以保证模型观感」**、**「我们要确保我们在外面摆什么样，进游戏也摆什么样，
> 动画只是驱动它们原始 HD2 骨架的轴点进行移动 —— 即使这些轴点不在模型网格的内部」**。
> 这是本轮的头号问题，也是最根本的一条。

### 13.1 机制：idle 帧为什么会把网格拽走

运行时对每个顶点做 LBS：

```
v_world = Σ_b w_b · A[b] · inv(S_bind[b]) · v_bind
```

- `A[b]` = 当前动画帧的骨世界矩阵；`S_bind[b]` = **我们发布**的绑定姿态；`v_bind` = 我们发布的网格顶点。
- 游戏静止（idle）时 `A[b] = rest_hd2[b]`（HD2 自己的静止姿态）。
- ⇒ 只要 **`S_bind[b] == rest_hd2[b]`**，`A·inv(S_bind) = I`，**顶点一个不动**。

我们之前的做法是「把 HD2 的关节吸附到源模型的关节上」（`skeleton='snap'`），
于是 `S_bind ≠ rest_hd2`，游戏每帧都在用 `rest_hd2·inv(S_bind)` 把网格拽走 ——
**这就是用户看到的：鞋跟不见了、脚踝/小腿断裂、脖子被拉长、裙子被拆成两片。**
（上一轮把骨轴修对只把撕裂从 0.568 m 压到 0.183 m，**并没有归零**。）

### 13.2 修法：`skeleton='native'`（现为默认）

```
rt[b]        = HD2 自己的 T-pose 工作静止姿态
               = rt[p] · inv(rest_hd2[p]) · rest_hd2[b] · Dd[b] · basis_t[b]
               （heads 全部由链上自然算出，**没有任何吸附**）
D[b]         = D[p] · rt[b] · basis_i[b] · inv(rt[b])      ← T-pose → A-pose 的变形
out          = Σ_b w · D[b] · src1                          ← **网格**被摆成 A-pose
S_bind[b]    = rest_hd2[b]                                  ← **骨架一字不改**
```

- `src1` = 源 T-pose 网格经过 **yaw 旋转 + 均匀缩放 + 平移**，拟合到 **HD2 自己的 T-pose 关节**
  （注意：拟合目标从「HD2 的 A-pose 关节」改成了「HD2 的 T-pose 关节」，因为源模型是 T-pose）。
- `--leg-tilt` / `--leg-splay` 在 native 模式下**只动网格**，不再动骨架。
- **验证**：`max |A·inv(S_bind) − I| = 8.67e-16`；**idle 帧与绑定帧的网格差 = 0.00e+00 m**（5 个模型全部）。
- `skeleton='snap'` 保留为旧行为，供 A/B。

### 13.3 出图改成 4 行（用户要求）

`rigfix_overlay.py` 原来是「行 1 = bind 正视 / 行 2 = idle 侧视」，**视角和状态同时变了**，
所以看不出问题到底出在姿态还是视角。`quad_overlay.py` 改成
**4 行 × 3 列**：bind 正视 / bind 侧视 / idle 正视 / idle 侧视 ×（native / snap / 参照）。
⇒ 列 A 的 1≡3、2≡4 完全重合；列 B 明显被拽；列 C 也完全重合。

### 13.4 用户对第一行图的确认（红蓝 = 我们，黄 = HD2 原生）—— 正确

**红点蓝线 = 我们发布的 rest；琥珀色 = HD2 原生 rest。** 用户以此为前提提出的两条观察**都对**：

| 观察 | 实测 |
|---|---|
| 我们整体需要下移，参照的最下方骨点在**脚尖上表面**，我们的在**鞋底** | 我们网格最低点 z = **+0.0057**（正好等于 HD2 `l_toe` 的 z）；参照 = **−0.0542** ⇒ **差 6.0 cm，需要把网格下移约 6 cm** |
| 潜兵骨架肩太宽，我们的手掌够不到 HD2 的手掌骨 | 网格质心 vs 所绑骨：`l_hand` 偏 **0.15 m**、`r_hand` 偏 **0.69 m**（右臂还额外被轴 delta 放大） |
| 参照的整体比例更大 | 参照网格高 **2.1127 m**，我们 **1.9644 m**，**比值 1.0755** |

### 13.5 还没做（下一轮，等用户拍板）

1. **网格整体下移 ~6 cm**（让脚尖对齐骨架最低点，肩线随之抬高到胸部上方）。
2. **上半身放大**：目标是让手掌够到 HD2 的手掌骨 —— 用户指出参照的**上衣连衣裙与我们是同一个模型**，
   可以用特征点比对直接求出缩放比（整体高比 1.0755 是一个起点）。
   ⚠️ 缩放要在躯干/大腿接缝处**渐变**（类似 PS 羽化），避免接缝弯折。
3. **手臂少弯**：参照作者的做法是让手臂**尽量伸直**（肘部几乎在肘尖），
   用「两点之间直线最短」把短手臂模拟成长手臂；做法是**降低 `basis_i` 里手臂的旋转量**，
   而不是拉长手臂。叠加上面的放大后，可能只需要极少的额外缩放。
4. **头部比例**：参照的头部做得很大（二次元大号头），既匀称也能掩盖肩宽等问题。
5. ⚠️ **右臂额外问题**：清空 `symmetric_axis_deltas_wxyz` 后 `r_hand` 的偏移从 0.69 m 降到 0.24 m
   （最大顶点位移 2.02 → 0.86 m），但左臂反而略差 ⇒ **轴 delta 是放大器、不是根因**；
   根因是源模型的手/手指朝向与 HD2 的 T-pose 不一致，全局刚体拟合修不了。

### 13.6 探针 / 图

- `probe_native_rig.py`：**不修改管线**地重建整套 native 架构（含 `rt`/`D`/`S_bind`），验证 idle 恒等。
- `probe_skeleton_modes.py`：`snap` vs `native` 的骨架偏差、idle 位移、edge 指标。
- `probe_stretch.py`：找出被拉伸最厉害的顶点及其权重所属骨。
- `probe_mesh_vs_rig.py`：**网格质心 vs 所绑骨**的偏移，以及归一化后的关节位置（我们 vs 参照）。
- `probe_delta_effect.py`：清空轴 delta 的对照实验。
- `run_rigfix_all.py`：5 模型 × snap/native。
- `quad_overlay.py`：4 行 × 3 列对比图（`out/quad-overlay.png`、`out/quad-arms.png`）。

## §14 mesh 烘焙枢轴 与 发布骨架 必须解耦（第 39 轮）

### 14.1 症状
第 38 轮的 `skeleton='native'` 让 idle 帧恒等了，但**绑定态网格本身**就坏了：
左手变成"伞状手掌 / 细长鬼爪"，被甩到身前 3 个头远；左臂严重撕裂拉伸，右臂基本正常。

### 14.2 根因：A-pose 烘焙是**绕关节旋转**，枢轴必须在网格内部
```
D[b] = D[p] · rs[b] · basis_i[b] · inv(rs[b])
```
`D[b]` 是"绕骨 b 的关节把网格从 T-pose 转到 A-pose"的变换，`rs[b]` 的 head 就是那个枢轴。

| 模式 | `rs` = | 枢轴位置 | 结果 |
|---|---|---|---|
| `snap` | `rest_sym` | **源**模型的关节（吸附后），**在网格内部** | 旋转自然 ⇒ 视觉正确 |
| `native`（第 38 轮） | `rest_t` | **HD2 自己的**关节；HD2 骨架比 L4D2 网格宽得多 ⇒ 枢轴在网格**外面** | 旋转把网格甩飞 ⇒ 撕裂 / 鬼爪 |

第 38 轮的错在于：**一个开关同时改了两件互相独立的事**——
① 发布哪套骨架（`S_bind`）；② 绕哪套 rest 烘网格（`rs`）。
前者确实必须改成 HD2 原生（否则 idle 拽走网格），后者**必须保持不变**（否则绑定态就坏了）。

### 14.3 修法
**只把这两件事拆开**：

* 网格烘焙**永远**用吸附后的 `rest_sym`（= 第 2 列 `snap` 的行为，视觉正确的那一套）；
* `skeleton='native'` 只改**步骤 4b**：`S_bind[b] = rest_hd2[b]` 一字不改。

于是
```
idle[b] = rest_hd2[b] · inv(S_bind[b]) = I     ⇒ 游戏帧 == 绑定帧
```
而绑定帧 = `snap` 那套好看的网格。

代码改动（`tmp/rig-lab/hd2_style.py`）：删掉步骤 1a(`B`)、1b(`head_hd2`)、2(`new_head`)、
HD2-独有骨补齐、2b/2c 守卫里的 `if native:` 分支；只保留步骤 4b 的分支。
`rest_t` 保留但不再被发布结果使用（它是"HD2 处于 T-pose"的参考姿态）。

### 14.4 实测（`quad_overlay.py` / `run_rigfix_all.py`）
```
A bind vs B bind max diff 0.00e+00 m        ← 列 A 的绑定网格与列 B 逐位相同
A idle == bind      max diff 6.59e-16 m     ← 机器精度，即"零"

model     skeleton  edge_mean  edge_max  bad%   height rig head  idle max
zoey      snap        0.9987      3.37  0.67%   1.981  4.0e+00   0.1829
zoey      native      0.9987      3.37  0.67%   1.981  0.0e+00   0.0000
rochelle  native      0.9987      3.37  0.67%   1.981  0.0e+00   0.0000
coach     native      1.0006      1.88  1.24%   1.964  0.0e+00   0.0000
nick      native      1.0029      5.83  0.50%   2.002  0.0e+00   0.0000
ellis     native      1.0004      2.47  0.41%   1.973  0.0e+00   0.0000
```
关键两点：
1. **native 的网格指标与 snap 逐位相同** ⇒ 第 38 轮那个 `edge_max` 70–127 的恶化**完全消失**，
   回到 1.88–5.83（`bad%` 0.41–1.24%），而且**零逐模型调参**；
2. `rig head = 0`（发布的骨架 = HD2 原生，head 偏差 0）且 `idle max = 0`（游戏帧 = 绑定帧）。

### 14.5 出图
`quad_overlay.py` = **4 行 × 3 列**：
行 = bind 正视 / bind 侧视 / GAME 正视 / GAME 侧视；
列 = A `native`（snap 网格 + HD2 原生骨架）/ B `snap` / C 参照 patch。
每列都叠骨架（cyan/magenta = 该列发布的 rest，amber = HD2 原生 rest）。
**列 A 的 1≡3、2≡4 完全重合**；列 B 的 3/4 行明显被拽歪。
裁图辅助：`crop_cols.py`（任选列）、`crop_colA.py`。

### 14.6 教训
**"idle 恒等"和"绑定态好看"是两个独立条件，各有各的自由度**：
* idle 恒等 ⇐ 由**发布的骨架** `S_bind` 决定（必须是 `rest_hd2`）；
* 绑定态好看 ⇐ 由**烘焙枢轴** `rs` 决定（必须是吸附后的 `rest_sym`）。

把它们绑在同一个开关上，就会出现"修好一个、弄坏另一个"的来回摆动。


## §15 网格整体平移（第 40 轮）

### 15.1 需求
用户要求**一步一步来**，本轮只做"整体平移"，并指出**前后方向也有偏移**（不只下移），
出图改成 **2 列 × 3 行 = 6 图**（左列我们、右列参照；正 / 侧 / 俯），两边都叠 HD2 原生骨架。

### 15.2 实现
`hd2_style.retarget(..., mesh_shift=(dx, dy, dz))`：**只平移烘焙后的 `out`**。
骨架绝不移动 —— 那是 idle 恒等的前提；`out_idle` 由 `out` 派生，所以自动跟随。
CLI：`--mesh-down <m>` / `--mesh-back <m>`（+Y 是前方）。

### 15.3 取值：拟合，不靠猜
`probe_fit_shift.py` 在网格上暴力搜索"把我们网格平移到参照网格"的最优刚性平移，
目标函数 = 我们采样点到参照最近点的**中位距离**（用中位数，头发/尖刺不能主导）：

| 拟合范围 | dy（后移） | dz（下移） | 中位残差 |
|---|---|---|---|
| 全身 | **−0.100** | **−0.060** | 44 mm |
| 仅腿（下半 45%） | −0.082 | −0.080 | 31 mm |
| 躯干+头 | −0.100 | −0.070 | 42 mm |

采用**全身最优：后移 0.100 m、下移 0.060 m**。用户明确说**不必把鞋底锁死在参照鞋底上**
（"来源模型的腿部较长，鞋底陷入地板几乎是必定的"）⇒ 鞋底比参照低 7 mm 是可接受的。

### 15.4 ⚠️ 踩坑：脚底贴片质心不是好锚点
先按"脚底贴片质心/中位数"测：我们 `+0.145` vs 参照 `−0.087`（差 23 cm），
而全身质心只差 12 cm —— 两者对不上，看似矛盾。
根因：两个网格的**顶点密度分布不同**（我们的脚集中在脚尖、参照集中在脚跟），
中位数**根本不是同一个几何位置**。
⇒ 几何对齐**必须用"点到面"的最近邻距离**，不能用逐轴分位数。

### 15.5 新工具
* `tri_overlay.py` —— 2 列 × 3 行 6 图；**每行独立缩放**（俯视足迹 ~0.7 m，不与 2.1 m 正视图同尺度），
  **同一行的两列共享缩放**；带 **Y=0 / X=0 参考竖线**，方便两列对读。
* `probe_shift_view.py` —— **通道叠加诊断**：我们=绿、参照=品红、重叠=白，骨架叠在最上层。
* `crop_tri.py` / `crop_panel.py` / `crop_cols.py`。
* `probe_axes.py`（核实左右轴）、`probe_fit_shift.py`。

### 15.6 顺带纠正的长期笔误
HD2 的 **+X 是角色的「右」**（角色左侧在 −X）：实测 HD2 原生 `l_foot` x = −0.1908、
`r_foot` x = +0.1908，参照 patch 与之逐位一致。早期笔记写的"+X = 左"是笔误。

### 15.7 验证
```
mesh shift down 0.0600 back 0.1000 m | our zmin -0.0612 | ref zmin -0.0542
idle vs bind 6.38e-16 m | shipped rig vs HD2 0.00e+00 m
```
平移**没有破坏**前两轮的成果。

### 15.8 自我纠错
一度以为两列参考竖线位置不一致；程序化核对后 `guide x` 与 `amber x` 在两列**逐像素相同** ——
是 `crop_tri.py` 的 10 px 步距误差骗了眼睛。**判断渲染是否对齐要数像素，不要靠肉眼。**


## §16 【2026-10-12 · 第 41 轮】模型完整性 —— 几何一直在数据里，是**出图函数**把它删了

### 16.1 用户的观察
「原求生之路 2 模型其实是双马尾，但现在似乎变成了短发，马尾没了，同时还有鞋子后面的厚底粗高跟也不见了。」

### 16.2 结论：**几何 100% 在数据里**
VPK `Lunalice_Zoey.vpk` 里只有一个候选主模型
`models/survivors/survivor_teenangst.mdl`（`load_source` 按“最短文件名”选主）。
加载得 **121704 verts / 159363 tris / 127 源骨**，body part 齐全：
`platform shoes`(11472v/13122t)、`CatEars`(877)、`HeadDress_BigRibbon`(3896)、
`HeadDress_Ribbon`(3503)、`HeadDress`(4775)、`Dress_Skirt`(25041)、
`Dress_Back_Ribbon`(4240)、`Base`(18557)、`head`(6450) …。

**没有任何“读不到”的问题**：`build_geometry` 只读 LOD0（VVD 也只有 1 个 LOD），
`load_source` 只取一个 `.mdl` —— 但这两个限制对 Zoey 不产生缺失。

### 16.3 真凶：`drop_outlier_tris` 的 `floor=0.06 m`（display-only）
原规则 = `max(8 × median_edge, 0.06 m)`。实测：
- 三角形最大边长分布：p50 **0.0064**、p99 **0.0645**、p99.9 0.1070、**p100 0.1911 m**
  ⇒ 阈值 **0.0600 m** 正好落在 p99 附近；
- `lim 0.06` ⇒ **丢 510 顶点**；`lim 0.10` ⇒ 丢 54；**`lim ≥0.20` ⇒ 丢 0**。

被删的 510 顶点按 body part 归属：

| body part | 丢失 | bbox | 主导骨 | 是什么 |
|---|---|---|---|---|
| **`Base`** | **270** | z[0.964,1.596] | `source_extra_72/73/75/76` + `Bip01_Head1` | **马尾**（从头顶垂到腰） |
| **`platform shoes`** | **133** | z[−0.062,0.008]，y 到 −0.219 | `source_extra_62/63` | **鞋后跟厚底** |
| `Dress_Skirt` | 54 | z[0.866,1.170] | `source_extra_106/107/108/118` | 裙摆 |
| `Dress_Back_Ribbon` | 30 | z[1.106,1.201] | `source_extra_119..122` | 背后丝带 |
| `HighSocks_left/right` | 10/8 | z[0.263,0.486] | `Bip01_L/R_Calf` | 长筒袜口 |
| `Knife` | 4 | z 0.827 | `L_Thigh` | 刀 |
| `Dress_Corset` | 1 | z 1.268 | `Spine4` | 胸衣 |

**⇒ 用户看到的两处缺失，逐位对上。**

### 16.4 顺带证伪了旧注释：**没有“1.8 m 尖刺”**
原 docstring 声称“Nick 有一根 1.8 m、Ellis 一根 1.0 m 的尖刺，靠一根极长三角形挂在身上”。
逐模型实测**源网格 / 对齐网格 / 重定向网格**的最大边长：

| 模型 | src max | src1 max | out max |
|---|---|---|---|
| zoey | 0.1911 | 0.2045 | 0.2282 |
| rochelle | 0.1911 | 0.2045 | 0.2282 |
| coach | 0.1211 | 0.1365 | 0.1365 |
| nick | 0.3370 | 0.3655 | 0.3643 |
| ellis | 0.1310 | 0.1419 | 0.1511 |

**到处都没有 1.8 m 的三角形。** 远离身体的几何是**一整块 body part**，不是“长三角形扇”：

| 模型 | body part | n | bbox | 桥接三角形 |
|---|---|---|---|---|
| nick | **`LongSleeve`** | 23262 | y 到 **1.78**（源 T-pose 里就伸出 1.78 m） | 19 根，**最长只有 0.16 m** |
| ellis | **`Tail`** | 1041 | y 到 **1.04** | 32 根，**最长 0.04 m** |

⇒ **旧的边长规则既没能删掉这块几何（它边长很短），又把真实几何（马尾/鞋底，大平片）删了。**

### 16.5 修法：`drop_outlier_tris` 改成**按身体半径**（display-only）
```
centre = median(P);  d = |P - centre|;  cut = 0.85 * height
三角形只在「任一顶点 d > cut」时隐藏
```
`0.85 × height` 的依据是**每个 body part 的最大半径**（米）：

| part | zoey | coach | nick | ellis |
|---|---|---|---|---|
| shoe / boot | 1.26 | 1.38 | 1.39 | 1.43 |
| `Base`（含头发） | 1.19 | 1.36 | 1.34 | 1.37 |
| `LongSleeve` | – | – | **1.85** | – |
| `Tail` | – | – | – | **1.67** |
| **cut = 0.85·height** | **1.58** | **1.49** | **1.58** | **1.56** |

实测（`probe_filter_check.py`）：
```
zoey      159363 -> 159363 kept (0 hidden)      <- 马尾 + 鞋底回来了
rochelle  159363 -> 159363 kept (0 hidden)
coach      93844 ->  93844 kept (0 hidden)
nick      154066 -> 153487 kept (579 hidden)    <- 只隐藏 LongSleeve 的远端
ellis     165458 -> 164767 kept (691 hidden)    <- 只隐藏 Tail 的远端
```
`LongSleeve` / `Tail` 都是**源 bind pose 的瑕疵**（jiggle 骨链在绑定态就伸出去），
游戏里由物理骨拉回 —— 且这两个模型不是当前要渲染的。

### 16.6 出图证据
`probe_filter_compare.py` → `out/filter-compare.png`（旧=红 / 新=绿，三行 = 头+发 / 侧视脚 / 全身）：
- 行 2 侧视脚：旧版**鞋后跟厚底整块消失**，新版**在**；
- 行 1、3：旧版头发轮廓缺块，新版完整且能看到垂下的马尾。

`tri_overlay.py` → `out/tri-complete.png`（2 列 × 3 行 6 图）仍保持
`idle vs bind 6.38e-16 m` / `shipped rig vs HD2 0.00e+00 m`。

### 16.7 教训
**“数据里没有”和“图里看不到”是两件事。** 这个 display-only 的启发式规则被当成了真相的过滤器，
结果反过来让人以为模型缺件。**任何会删数据的启发式，都必须先证明它删的确实是垃圾。**

---

## §17 第二步：躯干与参照模型的尺寸比例对齐（第 42 轮）

### 17.1 用户指令
「第二步主要是负责将**躯干与参考模型的尺寸比例缩放的合适**，并**顺手让手掌能够到手掌骨**
（虽然这其实是主要目标，但我们这轮并不要求一定要够到，而是提供一些进度，后续我们还需要额外的
对手臂进行略微拉长、减少手臂弯折等手段来均衡，避免为了让手掌能够到手掌骨而过分放大躯干，
所以说**手掌这边仅作为次要目标，主要目标放到躯干对准即可**）」
用户推荐"视觉对比两模型上衣相似区域 → 特征定位点 → 按特征点缩放平移拟合"，并明确
"我只做推荐，按你的想法来就行"。

### 17.2 为什么不用"人工挑特征点"
参照 patch 与我们**是同一个模型、坐在同一套 HD2 原生骨架**上（§10：参照骨架与 HD2 原生逐位一致，
13 根骨 delta 全 0）⇒ **骨架关节就是公平的公共坐标系**，无需人工挑点。改用"按骨/按 unit 隔离"
的**最近邻中位距离拟合**：

- **我们的躯干** = 主导 HD2 骨 ∈ {`hips`, `spine1`, `spine2`, `chest`} 的顶点
  ⇒ **46485 / 121704** 顶点（不含头、不含手臂）；
- **参照的躯干** = unit `cae35ee15168145f`，tag = `Torso_Undergarment_Slim_lod3`
  ⇒ 215289 / 119578 v（z[0.423, 1.715]）；
- **两侧都排除了头发与手臂** ⇒ "像对像"比较成立；
- 目标函数 = 我们躯干采样点到参照躯干最近点的**中位距离**。

⚠️ 反例（都踩过）：
- `probe_torso.py` 用 `|x| p50` / `wy` 当躯干度量 ⇒ **被头发和裙子污染**
  （参照 wy 在 z 1.34 是 0.638，而 p50 |x| 只有 0.093）。
- `probe_torso2.py` 把 `Hip_Undergarment_Stocky` 也算进参照躯干 ⇒ 该 unit 跨全身
  （z −0.054..1.189）⇒ 参照"躯干"高度虚高到 1.77 m。**只保留 `Torso_Undergarment_Slim`。**

### 17.3 量化需求
逐层宽深比（`probe_torso3.py`，仅躯干 vs 仅躯干）：中位 **width 1.1666 / depth 1.1133**
（wz 1.02–1.42 段稳定在 ~1.10–1.18；z 1.42 以上被参照的头/猫耳污染到 2.11）。

最近邻中位距离（`probe_torso4.py`，限 `hips−0.05 .. neck` 带，即 0.923..1.550）：

| scale | 最优 (dy, dz) | 中位残差 |
|---|---|---|
| 1.00 | (+0.00, +0.02) | 0.0155 |
| 1.08 | (+0.00, −0.02) | 0.0124 |
| **1.16** | (+0.00, −0.06) | **0.0068** |
| 1.20 | (+0.00, −0.06) | 0.0144 |
| 1.30 | (−0.04, −0.06) | 0.0315 |

⚠️ **纯 scale 的理想枢轴落在胸口（z ≈ 1.35）**，会把**髋部往下压进腿里** ⇒ 必须做成
"**枢轴在髋部 + smoothstep 渐变**"的变换，这也正好与待办第 ③ 步"接缝渐变缩放"合流。

### 17.4 实现（`hd2_style.py` 步骤 4d，mesh-only）
```python
if upper_scale != 1.0:
    hips = next(h for h in hd2 if h['name'] == 'hips')
    piv = rest_hd2[hips['index']][:3, 3].astype(np.float64).copy()
    if upper_pivot_z is not None:
        piv[2] = float(upper_pivot_z)
    z0 = piv[2] - upper_blend
    z1 = piv[2] + upper_blend
    tt = np.clip((out[:, 2] - z0) / max(z1 - z0, 1e-9), 0.0, 1.0)
    w = tt * tt * (3.0 - 2.0 * tt)                      # smoothstep
    s = (1.0 + (upper_scale - 1.0) * w)[:, None]
    out = piv + (out - piv) * s
```
签名：`retarget(..., mesh_shift=(0.0, 0.0, 0.0), upper_scale=1.0, upper_pivot_z=None,
upper_blend=0.20, verbose=True)`；CLI：`--upper-scale` / `--upper-pivot-z` / `--upper-blend`。
**只动网格，骨架一动不动**（与 §13/§14 的"骨架 = HD2 原生"完全相容）。

### 17.5 拟合"实际变换"的参数（`probe_upper2.py`）
把 4d 的公式**原样复刻**成 `apply_upper(ours, scale, pivot_z, blend)`，做
scale × pivot z × blend 三循环（**对整个网格应用**，不是只对躯干权重顶点）：

- baseline（1.00 / 1.15 / 0.45）= **0.0276 m**；
- **最优 = scale 1.12 / pivot z 1.15 / blend 0.45 ⇒ 0.0178 m**；
- 最差（1.24 / 0.95）0.0496–0.0647 ⇒ **目标函数确实在区分**，不是平坦的。

候选对比（`probe_upper3.py`）：

| config | scale | pivot | blend | torso med | height | head top | hand gap | head-top − joint |
|---|---|---|---|---|---|---|---|---|
| baseline | 1.00 | 1.15 | 0.45 | 0.0276 | 1.9808 | 1.9196 | 0.1171 | +0.2289 |
| **A fit optimum（选中）** | **1.12** | **1.15** | **0.45** | **0.0178** | **2.0731** | **2.0120** | **0.0795** | **+0.3212** |
| A narrower blend | 1.12 | 1.15 | 0.35 | 0.0189 | 2.0731 | 2.0120 | 0.0773 | +0.3212 |
| A lower pivot | 1.12 | 1.10 | 0.30 | 0.0228 | 2.0791 | 2.0180 | 0.0739 | +0.3272 |
| A2 | 1.13 | 1.12 | 0.32 | 0.0254 | 2.0847 | 2.0236 | 0.0723 | +0.3328 |
| A3 | 1.14 | 1.10 | 0.35 | 0.0281 | 2.0955 | 2.0344 | 0.0705 | +0.3436 |
| B narrow waist | 1.15 | 1.05 | 0.20 | 0.0371 | 2.1112 | 2.0501 | 0.0689 | +0.3593 |
| reference | — | — | — | — | **2.1127** | **2.0585** | — | **+0.3678** |

⇒ **B 的身高更接近参照（2.1112 vs 2.1127），但躯干残差 0.0371 比 baseline 还差** ——
这正是用户说的"**避免为了让手掌能够到手掌骨而过分放大躯干**"。**选 A（1.12 / 1.15 / 0.45）。**

### 17.6 结果与副作用
- 躯干中位残差 **0.0276 → 0.0178 m**；
- 身高 **1.9808 → 2.0731**（参照 2.1127，仍小 1.9%）；
- **手掌间隙 0.1171 → 0.0795 m**（顺手拿到的进度；本轮**不要求够到**）；
- 头顶点 2.0120（参照 2.0585）；
- **腿完全没动**：鞋底仍 z **−0.0612**，`l_foot` / `l_knee` 质心**逐位不变**；
- 不变量未回退：`idle vs bind 6.38e-16 m`、`shipped rig vs HD2 0.00e+00 m`；
- **副作用（已接受）**：躯干变大而腿没动 ⇒ **腿看起来偏细**，正好留给后续
  "略拉长手臂 / 减少手臂弯折 / 下半身放大"来均衡。

### 17.7 探针踩坑
`probe_upper2.py` 第一版**只对躯干权重顶点应用变换** ⇒ 得到
"best = 1.14/1.30/0.45 但**手掌间隙完全不变**"的**假结果**。
**真实变换作用于枢轴以上所有顶点（手/头一起走）** —— 改成 `apply_upper(ours, ...)` 后结论才正确。
另：`probe_torso4.py` 首次运行被 SIGTERM（网格 16×9×9 × 400×9000 点太多）⇒ 采样降到
`NO=200 / NR=2500`、先粗后精。

### 17.8 出图
`probe_torso_view.py` → 躯干特写通道叠加（我们 = 绿 / 参照 = 品红 / 重叠 = 白，叠 HD2 原生骨架，
z 带 `hips−0.10 .. neck+0.12`）：`out/torso-view.png`（1.00）、`out/torso-scaled.png`（1.12/1.15/0.45）、
`out/torso-scaled-b.png`（1.15/1.05/0.20，明显过度放大裙子/手臂）。
`tri_overlay.py`（2 列 × 3 行 6 图）新增 CLI 第 5/6/7 参数 `up_scale` / `up_pivot_z` / `up_blend`
并在打印里报告身高 ⇒ **`out/tri-step2.png`（本轮主图）**：
```
mesh down 0.0600 back 0.1000 | upper scale x1.120 pivot z 1.150 blend 0.45 |
our zmin -0.0612 height 2.0731 | ref zmin -0.0542 height 2.1127 |
idle vs bind 6.38e-16 m | shipped rig vs HD2 0.00e+00 m
```

---

## §18 2.5 阶段：T→A 转换误伤 —— 头发被绑到了手臂（第 44 轮）

### 18.1 用户观察
「我们在做 tpose 扭转到变体 apose 时，应当有一个区域被误伤了」：
1. 头部右侧**发带被异常拉伸**（红圈）；
2. 源模型在 A-pose 里这处发带**离手臂很近**；
3. **背后的两条长发**（图 3 = 源 T-pose 背面）在结果里
4. **被手臂向下弯折、向内卷**（图 4 红线圈出的两条发丝）。
用户结论：「必须考虑到原始模型的骨骼，判断这片区域到底是属于手臂，还是头部，还是其他区域……
后续我们可以遇到大量大袖子、或较多装饰品的模型」。

### 18.2 诊断（`probe_orphan.py`）：5 根头发/发带骨被绑到了肩膀
源骨里没有 HD2 对应物的（`source_extra_*`，即 jiggle / 头发 / 丝带骨）走 `resolve_bone_map`
的兜底。`fallback='distance'` 只按**骨关节最近**选，于是：

| 源骨 | 父链归属 | `distance` 选了 | 应属 | 驱动顶点 | 顶点质心 |
|---|---|---|---|---|---|
| `source_extra_73` | → `Bip01_Head1` | **`l_shoulder`** ✗ | `head` | 194 | (−0.05, −0.16, 1.25) 背后 |
| `source_extra_76` | → `Bip01_Head1` | **`r_shoulder`** ✗ | `head` | 191 | (+0.05, −0.16, 1.25) 背后 |
| `source_extra_85` | → `Bip01_Head1` | **`l_shoulder`** ✗ | `head` | 117 | (−0.27, −0.02, 1.62) 肩旁发带 |
| `source_extra_71` | → `Bip01_Head1` | **`r_shoulder`** ✗ | `head` | 105 | |
| `source_extra_69` | → `Bip01_Head1` | **`l_shoulder`** ✗ | `head` | 99 | |

按部位：`Base`（含头发）**11.4% 顶点落在手臂骨上**，`HeadDress_Ribbon` 3.0%。
A-pose 烘焙里 `D[l_shoulder]` 含"手臂从水平转到下垂"的旋转 ⇒ 头发被整片甩下去。

### 18.3 第一版修法（子树）为什么失败 —— HD2 的骨盆是"劈开的"
先试「候选骨必须落在**最近映射祖先的子树**里」。头发修好了，但**裙子炸了**：
`source_extra_91/92/93` 等从 `spine2` 跳到 `l/r_thigh`。查 `probe_hd2_tree.py` 才看清 HD2 的骨架形状：

```
boss ├── hips  ── l_thigh ── l_knee ── l_foot ── …      (腿, 12 根)
     └── spine1 ── spine2 ── chest ── {neck ── head, l_clavicle ── l_shoulder ── …}
```

**`hips` 和 `spine1` 是 `boss` 下的兄弟**（这正好解释"`spine1` 与 `hips` 完全重合"）。
⇒ **`subtree(hips)` 只有腿**，把骨盆区域的候选集砍成了腿 ⇒ 裙子只能落在大腿上。

### 18.4 最终修法：`fallback='region'` = 距离 + **同肢体**约束
新增"肢体"判定（`resolve_bone_map`）：沿 HD2 层级往上走，遇到
`l_clavicle`/`r_clavicle`/`l_thigh`/`r_thigh`/`neck` 就归类为
`l_arm`/`r_arm`/`l_leg`/`r_leg`/`head`，否则 `torso`。

```
pool = { c ∈ cand : limb(c) == limb(ancestor) }     # 空则退回全部候选
```

- 头发（祖先 `Bip01_Head1` → `head`，肢体 `head`）⇒ 只能落 `head` ✓
- 鞋（祖先 `Bip01_L_Calf` → `l_knee`，肢体 `l_leg`）⇒ `l_foot` ✓（与旧行为一致）
- 裙子（祖先 `Bip01_Pelvis` → `hips`，肢体 `torso`）⇒ `hips`（**不再被砍成腿**）✓
- 手臂/袖子的孤儿骨（祖先在臂链上）⇒ 仍落手臂 ✓

⚠️ 关键是：**`distance` 是逐骨独立的，会把一条源骨链拆到不同肢体**（裙链的父骨在 `spine2`、
子骨却在大腿上）；`region` 让整条链跟着同一个祖先走。这正是用户说的"判断这片区域属于谁"。

### 18.5 结果（`probe_region.py`，distance → region）
35 / 127 根源骨改变归属，方向全部合理：

| 源骨链 | 旧 | 新 | 顶点 |
|---|---|---|---|
| 69/71/73/74/76/77/85（马尾、发带、耳侧发） | `l/r_shoulder`、`chest` | **`head`** | 1195 |
| 91/92/93/98/100/109（裙主体） | `spine2` | **`hips`** | 10619 |
| 99/101–108（裙左摆） | `l_thigh` | **`hips`** | 1852 |
| 110–118（裙右摆） | `r_thigh` | **`hips`** | 1899 |
| 124/126（背后长丝带） | `l/r_thigh` | **`spine2`** | 274 |
| 89/90（后颈/衣领） | `neck` | `chest` | 355 |

按部位：`Base` 手臂占比 **11.4% → 8.2%**；`HeadDress_Ribbon` **3.0% → 0.0%**；
`Dress_Skirt` 位移 mean 0.0044 / max 0.0355 m；`Dress_Corset` **位移 0**（那 7.2% 是源模型自己
`Bip01_L_Clavicle` 的权重，不是我们造成的）。

**改完后仍绑在手臂上的源骨全部是"真手臂"**：
```
ValveBiped.Bip01_L_UpperArm   name-mapped 3356 -> l_shoulder
ValveBiped.Bip01_L_Forearm    name-mapped 1742 -> l_elbow
ValveBiped.Bip01_R_Forearm    name-mapped 1716 -> r_elbow
ValveBiped.Bip01_R_UpperArm   name-mapped 1136 -> r_shoulder
source_extra_78/79            FALLBACK      650 -> l_hand / r_hand  (手指/手套, 祖先在手上)
ValveBiped.Bip01_L/R_Hand     name-mapped   531/533 -> l_hand / r_hand
ValveBiped.Bip01_L/R_Clavicle name-mapped   349/337 -> l_clavicle / r_clavicle
```

### 18.6 出图证据
`probe_hair_view.py` → `out/hair-view.png`（背视，左 `distance` / 右 `region`）；
Blender Cycles 背视 `out/blender/before/blend-ours-back.png` vs `out/blender/blend-ours-back.png`：
- 旧版：**背后裙子布满尖刺**（裙链被拆到 `spine2` + 大腿 ⇒ 被撕），马尾短而贴头；
- 新版：**马尾笔直垂在背后**，裙子变成光滑的钟形。

### 18.7 不变量（未回退）
```
our zmin -0.0612 height 2.0731 | ref zmin -0.0542 height 2.1127
idle vs bind 6.38e-16 m | shipped rig vs HD2 0.00e+00 m
```

### 18.8 教训
- **逐骨"最近邻"兜底会把一条源骨链拆到不同肢体**，表现出来就是"某块几何被撕开/被拽走"。
  兜底必须**带上源骨架的结构信息**（祖先 → 肢体），这也是用户从一开始就指出的方向。
- **不要假设层级是"教科书形状"**。HD2 把 `hips` 和 `spine1` 并列挂在 `boss` 下，
  "祖先的子树"这种看起来天经地义的规则因此在骨盆上完全失效 —— **先打印骨架树再写规则**。
- 这套规则对"大袖子 / 多装饰品"同样适用：装饰品挂在哪条骨链上，就跟着哪条链走。


## §19 第 4 步：手臂 —— “歪”的根因是**工作关节 ≠ HD2 关节**，不是手臂弯了（第 45 轮）

### 19.1 用户的观察（6 张截图）
- **正视图**：参照手臂几乎是一条**斜线**；我们的手臂“有些歪掉了”（用人物背后的刻度线对比）。
- **俯视图**：参照（粉红）手肘正好在**骨骼拐角（肘尖）**，手掌正好在几根手指骨上；我们**大臂略短**，“把小臂平移出去一点就刚好对得上”。
- **手指例外**：大多数骨不必让网格包住，但**手指最明显**（第一人称握枪），骨没对准会出现玩家很容易注意到的扭曲。**先按标准数学模型把手转到位置，再判断误差**；用户**不情愿做骨骼对齐**（怕手掌/腕部变形）——“仍保持与其他身体部位一致，尽可能优化数学变换模型到接近完美”。
- **侧视图**：灰 = 我们，橙 = 参照。

### 19.2 诊断：把手臂投影到 HD2 自己的肩→手骨轴上（`probe_arm_target/axis/seg.py`）
沿 `l_shoulder→l_hand`（骨长 **0.5553 m**）量 t：

| | t |
|---|---|
| HD2 `l_elbow` 骨 | **+0.2859** |
| HD2 `l_hand`（腕）骨 | **+0.5553** |
| HD2 `l_index_finger3` | +0.6796 |
| HD2 `l_middle_finger3` | +0.6971 |
| 我们 上臂点云 | [−0.1268, **+0.1999**] |
| 我们 前臂点云 | [+0.1941, +0.5100] |
| 我们 手掌点云 | [+0.2934, +0.5765] |
| 我们 手指点云 | [+0.4853, **+0.6425**] |
| 参照袖套 | t [0.028, 0.714] |

⇒ 肘边界比 HD2 骨**靠内 8.6 cm**、指尖靠内 **5.5 cm**（= 用户看到的“大臂略短”）；整条链再**沿 +Y 后移 ~9 cm**。
⇒ **每段长度其实是对的**（`probe_arm_fit2.py` 的 PCA 分段）—— 不是“手臂弯了”，而是**工作关节（源关节）与 HD2 关节错位**：源是 T-pose、HD2 是 A-pose，吸附只把“关节位置”搬过去，**整条手臂跟着平移/内移**。

### 19.3 为什么不能靠“骨骼对齐”解决
把 HD2 手指骨吸附到源手指骨会让 `S_bind ≠ rest_hd2` ⇒ 违反 §13 的 `native` 不变量（每帧被拽），用户也不愿意（手掌/腕部变形）。**必须继续在网格层用数学变换解决。**

### 19.4 修法：`arm_fit`（`hd2_style.py` 步骤 **4e**，mesh-only）
逐骨残差 `d[b] = 发布头(rest_shipped[b]) − 工作头(D[b]·rest_sym[b])`，**非手臂骨置零**（`is_arm_bone`：名以 `l_/r_` 开头且含 `clavicle|shoulder|elbow|hand|finger`）：

| 骨 | d | \|d\| |
|---|---|---|
| l_clavicle | (−0.0089, −0.0503, −0.1029) | 0.115 |
| l_shoulder | (−0.0947, −0.1220, −0.0956) | 0.182 |
| l_elbow | (−0.1210, −0.1071, −0.0854) | 0.183 |
| l_hand | (−0.1018, −0.0936, −0.0942) | 0.167 |
| 各手指 | ≈(−0.09, −0.08, −0.11) | 0.159–0.180 |

顶点位移 = **按 LBS 权重混合残差**，**只加到网格 `out`**（骨架绝不移动）。两个必须的补充：

1. **`arm_perp`（默认 0.0）**：只取**沿臂轴分量** `u·(d·u)`，垂直分量乘 `arm_perp`。因为 `mesh_shift`（back 0.100 / down 0.060，已按参照拟合）已经承担了垂直部分 —— 全 3D 残差会**重复计算**（arm_perp=1 时手臂比参照**后移 13 cm**）。arm_perp=0 时 y 范围 [−0.129, +0.324] 与参照 [−0.103, +0.294] 吻合。
2. **`arm_smooth`（默认 8）**：Laplacian 平滑。**不加平滑 `edge max` 2.15 → 12.43**（14 个 >5x 的极小三角，原始边长 3.9–24 mm，全在**胸口/锁骨边界**：主导骨 chest 得 0、l_clavicle 得 0.115 m ⇒ 权重跳变）。平滑 8 遍 ⇒ >5x 只剩 1 个、max 6.90；arm_perp=0 时 max **3.15**、bad>25% **1.00%**。

### 19.5 结果（A/B，`probe_arm_fit_ab.py`，沿 HD2 骨轴）
| 段 | arm_fit=0 | arm_fit=1 | HD2 骨 |
|---|---|---|---|
| 上臂 t | [−0.1268, +0.1999] | [−0.0834, **+0.2913**] | 肘 +0.2859 |
| 前臂 t | [+0.1941, +0.5100] | [+0.2850, +0.6067] | |
| 手掌 t | [+0.2934, +0.5765] | [+0.3916, +0.6734] | 腕 +0.5553 |
| 手指 t | [+0.4853, +0.6425] | [+0.5762, **+0.7388**] | 指尖 +0.6796 / +0.6971 |

⇒ 肘 **0.194 → 0.282**（骨 0.286，差 3 mm）、指尖 **0.642 → 0.729**。手掌区间 [+0.392, +0.673] 覆盖 HD2 腕（0.5553），手指区间 [+0.576, +0.739] 覆盖 HD2 指骨（0.680/0.697）⇒ **手指落在 HD2 手指骨上，无需例外绑定。**

### 19.6 5 模型回归（`run_arm_all.py`，完整当前配置，arm_perp=0，**零逐模型调参**）
| 模型 | 肘 err（fit 0 → 1） | 指尖 err（fit 0 → 1） | edge_mean | bad% |
|---|---|---|---|---|
| zoey | −0.0859 → **+0.0055** | −0.0546 → +0.0417 | 1.0675 → 1.0703 | 0.36 → 1.00 |
| rochelle | −0.0859 → **+0.0055** | −0.0546 → +0.0417 | 1.0675 → 1.0703 | 0.36 → 1.00 |
| coach | −0.0445 → **+0.0259** | −0.0237 → +0.0404 | 1.0734 → 1.0792 | 1.12 → 2.60 |
| nick | +0.0896 → **+0.1908** ⚠️ | +0.1260 → +0.2510 ⚠️ | 1.0803 → 1.0906 | 0.97 → 1.84 |
| ellis | L+0.0054 / R−0.0978 → **L+0.0741 / R−0.0070** | −0.0797 → +0.0467 | 1.0727 → 1.0740 | 0.26 → 0.83 |

- zoey 与 rochelle 数字**逐位相同**（同一套基础身体几何）。
- **nick 过冲**：它是唯一“本来超长”的模型（fit=0 就已 +0.090 / +0.126）⇒ 残差把它推得更远。**待用户拍板**（`arm_fit` 是全局的，不做逐模型调参）。
- **ellis 左臂** fit=0 时本来就准（+0.0054），fit 后变 +0.0741，右臂反而从 −0.0978 修到 −0.0070 ⇒ **残差 = “把源关节搬到 HD2 关节”，对本来就准的那一侧是过冲。**

### 19.7 不变量（未回退）
```
mesh down 0.0600 back 0.1000 | upper scale x1.120 pivot z 1.150 blend 0.45
our zmin -0.0612 height 2.0731 | ref zmin -0.0542 height 2.1127
idle vs bind 6.76e-16 m | shipped rig vs HD2 7.22e-16 m
edge mean 1.0703 p99 1.2320 max 3.15 bad>25% 1.00%
```

### 19.8 出图
- `probe_arm_view.py` → `out/arm-view.png`（3 行 × 3 列：front / top / side × ours / 参照 / 叠加，**每列都叠 HD2 骨架**）；`out/arm-view-off.png` 是 fit=0 的备份。
- `probe_arm_zoom.py` → `out/arm-zoom-off.png` / `arm-zoom-on.png` / `arm-zoom-perp1.png`。
- `probe_hand_zoom.py` → `out/hand-zoom.png`（HD2 手指骨粗线 + 标签）。
- Blender Cycles：`blender_build.py` + `blender_render.py`，新增 `blend-ours-arm-top` / `blend-compare-arm-top` / `blend-ours-arm-front-l` / `blend-ours-arm-top-l`；换臂前的旧图在 `out/blender/before-arm/`。

### 19.9 教训
- **“手臂歪了”要先投影到骨轴上量 t，再下结论。** 肉眼看像“弯”，实际是**整段沿骨轴内移 + 垂直后移**；量完才知道是 **8.6 cm / 5.5 cm / 9 cm** 三个数。
- **任何 mesh-only 的位移都要先问：“垂直分量是不是已经被别的步骤承担了？”** 否则会**重复计算**（`arm_perp` 就是因此才存在）。
- **权重跳变会惩罚“逐骨位移”**：几何上正确的一步，会在权重重叠的接缝（胸口/锁骨）把极小三角形拉爆 ⇒ **必须配 Laplacian 平滑**，且要看 `edge max` 而不只是目标误差。
- **全局参数对“离群模型”会过冲**（nick）。零调参是优点，但**要如实报告离群值**，不能只报平均改善。


## §20 第 45b 轮：手臂的**两个真缺陷** —— 腋窝被撕开、小臂/手掌偏高（第 45 轮复审）

> 用户验收第 45 轮的 Blender 模型后报告：① 腋窝处与手臂处出现一条**裂开的接缝**，大臂处还有**断裂**迹象（左右一致，左臂更重）；参照模型**也有这条接缝，但腋窝没裂、大臂没断**。② 小臂与手掌对齐仍差 —— 正视图中“小臂与大臂平行”（= 肘部有错位台阶），**手掌被小臂带得略偏上**。

### 20.1 缺陷 A：`arm_fit` 把**上胸/腋窝**也推了 3 cm 以上

`probe_arm_disp.py`（把每个三角形按 arm_fit 的位移量着色：灰 = 未动，黄 0.5–3 cm，红 >3 cm）显示：
**红区（>3 cm）不只覆盖手臂，还伸进上胸与胸口**（旧参数下 max 位移 0.1061 m，>3 cm 的顶点 16602 个）。
机理：源骨架把**腋窝/上胸**的顶点 100% 权重给了 `l_shoulder` ——
`probe_arm_dir.py` 的按权重分割显示，我们的 `l_shoulder` 主骨点云沿臂轴跑 **t ∈ [−0.085, +0.301]**，
即**比肩关节还向内 8.5 cm**（参照的同类点云从 **t=+0.036** 才开始）；只有 **62.4%** 的点落在肩→肘骨线 8 cm 内（参照 **99.8%**）。
这些顶点被沿臂轴推 18 cm ⇒ 从躯干上滑出去 ⇒ **腋窝裂开**。
`l_shoulderarmour`（肩甲）残差 **0.1525 m** 同理，是“大臂处断裂”的嫌疑来源（肩甲被推离肩膀）。

**修法 `arm_taper=(t0, t1)`**：按顶点在**该侧臂轴**上的位置做 smoothstep 斜坡，t≤t0 位移为 0，t≥t1 为满值。
选 **t0=−0.05 / t1=0.22**（sweep 见 20.4）。⇒ **红区退回到肩膀为止，胸口不再被碰**。
副作用是斜坡段（上臂/三角肌）承担拉伸，但**没有任何三角形被拉过 2 倍**（旧参数有 7 个 >2x、最大 3.46）。

### 20.2 缺陷 B：小臂与手掌相对骨头**偏高 2.5–3 cm**

`probe_arm_dir.py` 用**骨线段**（肩→肘 / 肘→腕 / 腕→中指1）度量网格管子的偏心：

| 点云 | d_perp | **d_up** | d_fwd | ang | near% |
|---|---|---|---|---|---|
| 我们 上臂 | 0.0768 | +0.0016 | +0.0638 | 59.06 | 62.4% |
| 我们 小臂 | 0.0452 | **+0.0334** | +0.0125 | 10.25 | 99.9% |
| 我们 手掌 | 0.0330 | +0.0089 | +0.0168 | 5.44 | 100% |
| 参照 上臂 | 0.0509 | −0.0095 | +0.0217 | 56.69 | 99.8% |
| 参照 小臂 | 0.0338 | **+0.0081** | +0.0110 | 7.15 | 100% |
| 参照 手掌 | 0.0466 | −0.0220 | +0.0157 | 24.77 | 91.0% |

⇒ **上臂高度是对的（+0.16 cm）**，**小臂高 3.3 cm、手掌高 0.9 cm** ⇒ 肘部出现一个**垂直台阶**，
正视图中就表现为“小臂与大臂平行但错位”，手掌也跟着被带高。**这正是用户描述的两件事。**

**为什么 `arm_perp=1` 不行**：残差的垂直分量**几乎全在前后方向**。
以 `l_elbow` 为例，`d=(−0.1210,−0.1071,−0.0854)`，沿轴分量 0.0975，垂直分量 `(−0.0424,−0.1430,−0.0404)`
（模 0.1536，**主导是 −Y = 向后 14.3 cm**）。所以全量应用会把手臂推到参照**后面 12 cm**
（d_fwd −11.6 cm vs 参照 +1.1 cm，near% 塌到 0.3%）。

**修法 `arm_up`（默认 0.55）**：把垂直分量**再投影到“竖直”方向** `w_up = normalize(+Z − u(u·+Z))` 上，
只应用 `arm_up · (perp·w_up) · w_up`，前后分量仍交给 `mesh_shift`。
因为 `w_up ⊥ w_fwd`（构造上正交），**改高度不会动前后**。
⇒ 小臂 d_up **+0.0334 → +0.0022**（参照 +0.0081），手掌 d_up **+0.0089 → −0.0260**（参照 −0.0220）；
小臂 d_perp **0.0452 → 0.0294**（参照 0.0338，比参照还紧）。

### 20.3 最终默认（`hd2_style.py`）
```
--arm-fit 1.0 --arm-up 0.55 --arm-perp 0.0 --arm-smooth 8 --arm-taper -0.05 0.22
```

### 20.4 参数 sweep（`_sweep_arm.py`，edge ratio 相对 **arm_fit=0** 的网格）
| combo | p99 | max | >2x | bad% |
|---|---|---|---|---|
| up0.00 taper(0,0.18) sm8 | 1.335 | 1.61 | 0 | 2.12 |
| up0.55 **无 taper** sm8 | **1.148** | 3.46 | 7 | 1.28 |
| up0.55 taper(0,0.10) sm8 | 1.442 | 2.19 | 250 | 2.04 |
| up0.55 taper(0,0.18) sm8 | 1.369 | 1.66 | 0 | 2.32 |
| up0.55 taper(0,0.18) sm16 | 1.370 | 1.66 | 0 | 2.25 |
| up0.55 taper(0,0.26) sm8 | 1.273 | 1.60 | 0 | 2.21 |
| **up0.55 taper(−0.05,0.22) sm8** | **1.290** | **1.47** | **0** | 2.29 |
| up0.55 taper(0.02,0.18) sm8 | 1.378 | 1.74 | 0 | 2.30 |

⇒ 选 **(−0.05, 0.22)**：max 最低（1.47）、零个 >2x。
**代价要如实说**：taper 把“少数三角形被拉爆”换成“上臂较均匀地拉伸”，所以
`bad(>25% 或 <80%)` 从 1.28% 升到 2.29%，但 **max 从 3.46 降到 1.47**。

### 20.5 结果（A/B，沿 HD2 臂轴；`probe_arm_fit_ab.py`）
| 段 | arm_fit=0 | 第 45 轮 | **第 45b 轮** | HD2 骨 |
|---|---|---|---|---|
| 上臂 t_hi（肘边界） | +0.1999 | +0.2913 | **+0.2873** | **+0.2859** |
| 前臂 t | [+0.1941,+0.5100] | [+0.2850,+0.6067] | [+0.2803,+0.6039] | |
| 手掌 t | [+0.2934,+0.5765] | [+0.3916,+0.6734] | [+0.3898,+0.6692] | 腕 +0.5553 |
| 手指 t | [+0.4853,+0.6425] | [+0.5762,+0.7388] | [+0.5728,+0.7334] | 指尖 +0.6796/+0.6971 |

⇒ 肘 **+0.1999 → +0.2873**（骨 0.2859，**差 1.4 mm**）。

### 20.6 5 模型回归（`run_arm_all.py`，零逐模型调参）
| 模型 | 肘 err（0 → 45 → **45b**） | 指尖 err | edge_mean | bad% |
|---|---|---|---|---|
| zoey | −0.0859 → +0.0055 → **+0.0014** | +0.0417 → +0.0363 | 1.0744 | 0.36 → **2.29** |
| rochelle | −0.0859 → +0.0055 → **+0.0014** | 同上 | 1.0744 | 0.36 → 2.29 |
| coach | −0.0445 → +0.0259 → **+0.0251** | +0.0404 → +0.0363 | 1.0821 | 1.12 → 4.28 |
| nick | +0.0896 → +0.1908 → **+0.1888** ⚠️ | +0.2510 → +0.2475 ⚠️ | 1.0938 | 0.97 → 4.28 |
| ellis | L+0.0054/R−0.0978 → L+0.0741/R−0.0070 → **L+0.0722/R−0.0134** | +0.0467 → +0.0410 | 1.0769 | 0.26 → 2.32 |

- zoey/rochelle 的肘误差再降到 **+1.4 mm**；**nick 仍过冲**（唯一“本来超长”的模型）。
- **bad% 普遍翻倍**（zoey 0.36 → 2.29）：这是 taper 把拉伸摊到上臂的代价，见 20.4。

### 20.7 不变量（未回退）
```
mesh down 0.0600 back 0.1000 | upper scale x1.120 pivot z 1.150 blend 0.45
our zmin -0.0612 height 2.0731 | ref zmin -0.0542 height 2.1127
idle vs bind 6.38e-16 m | shipped rig vs HD2 7.22e-16 m
edge mean 1.0744 p99 1.4403 max 2.99 bad>25% 2.29%
```

### 20.8 出图
- `probe_arm_disp.py` → `out/arm-disp.png`：**位移着色图**（灰/黄/红），一眼看出“红区是否伸进胸口”。
- `probe_armpit.py` / `probe_armpit_ab.py` → `out/armpit.png` / `out/armpit-ab.png`（OFF / 第45轮 / 第45b轮 / 参照 × front/top/side）。
- `probe_arm_dir.py`：按**权重**（我们）与**最近骨段**（参照）双分割，给出 d_perp / d_up / d_fwd / ang / near%。
- Blender：`blender_render.py` 新增 **`blend-ours-armpit-l` / `blend-compare-armpit-l` / `blend-compare-armpit-l-top`**（`shot_at()` 用 `to_track_quat` 对准腋窝，700×700）。旧图备份在 `out/blender/before-armfit2/`。

### 20.9 教训
- **“位移场”要画出来看，不能只看目标误差。** `arm_fit` 的残差在手臂上是“对的”，但它顺着权重流到了**上胸**——
  只有把每个三角形的位移量着色，才看得出红区伸进了胸口。
- **权重分割能暴露“这条骨到底管着哪块几何”。** `l_shoulder` 的点云比肩关节还向内 8.5 cm，
  这不是“手臂”，是**躯干**被绑到了肩骨上。**任何沿骨轴的位移都必须先按位置做斜坡。**
- **正交分解要按“可信度”分开给系数。** 同一个“垂直分量”里，**竖直方向可信、前后方向不可信**（已被 `mesh_shift` 承担）。
  一个标量 `arm_perp` 表达不了这件事 ⇒ 拆成 `arm_up` / `arm_perp`，并利用 `w_up ⊥ w_fwd` 保证互不干扰。
- **“max 变小”和“bad% 变小”可以同时往反方向走。** 摊开拉伸会升高 bad%，但把 3.46x 的尖峰压到 1.47x。
  报数时必须两个都给。

### 20.10 决定性量化：`arm_fit` 到底拖动了多少**躯干**顶点（`probe_arm_tear.py`）
把顶点按**主导权重的 HD2 骨**分成「手臂」与「躯干」两类（躯干 = 不该跟着手臂动），
再看 `arm_fit` 把它们挪了多远：

| 区域 | 第 45 轮（无 taper） | 第 45b 轮（taper+arm_up） |
|---|---|---|
| 躯干顶点总数（>2 cm / >5 cm） | **656** / 0，max **0.0413 m** | **139** / 0，max 0.0284 m |
| 肩带（t < +0.05） | **330** / 0，max 0.0413 m | **0** / 0，max 0.0068 m |
| 左侧（x < −0.05） | 476 / 0 | 139 / 0 |
| **左腋窝（t<+0.05 且 x<−0.05）** | **150** / 0，max **0.0413 m** | **0** / 0，max 0.0068 m |

⇒ 第 45 轮有 **150 个躯干顶点在左腋窝被拽走 4 cm 以上**（这就是“裂开”的机制）；
第 45b 轮**归零**（残余 6.8 mm 是 Laplacian 的扩散，不是位移本体）。
注：`t` 是沿**左**臂轴量的，所以“肩带/左侧”两行才是干净的左臂区域。


---

## §21 【2026-10-12 · 第 45c 轮】手臂**角度**：方向误差不是平移能修的 —— 绕世界 Y 轴的三段旋转

### 21.1 用户验收（第 45b 轮）
> 「第一个已经解决了，也就是破裂和断裂的问题已经解决，现在是第二个问题**几乎没有改善的同时还更差了**，
> 我们从正视图来看……当前我们的模型的手臂**向下太多了**，我看可能需要**少向下 14 度**左右……
> **小臂仍然有一些弯曲**（同时小臂则需要**反向转 10 度**左右），**手指也没能尽量与参考对象所几乎融合**
> ……不过从俯视图来看，除了手指手掌部分以外，**小臂和大臂几乎已经和参考对象合在一起了**。」

⇒ 两个判据：① **正视（X-Z）角度不对**；② **俯视（X-Y）已经对了**。这直接把修正轴锁死。

### 21.2 量化（`probe_arm_angle.py`）：沿 HD2 肩→手轴分箱取质心，最小二乘拟合各段方向

| 前视图 X-Z（低于水平） | 我们 | 参照 | HD2 骨 |
|---|---|---|---|
| 大臂 | **39.38°** | 28.49° | 29.92° |
| 小臂 | **17.88°** | 24.92° | 29.60° |
| 手掌 | **57.11°** | 49.34° | 43.30° |
| **肘折角** | **35.13°** | 24.96° | 39.64° |

| 俯视图 X-Y（向前偏出） | 我们 | 参照 | HD2 骨 |
|---|---|---|---|
| 大臂 | 13.66 | 13.55 | 3.27 |
| 小臂 | 41.58 | 39.43 | 46.48 |
| 手掌 | 57.76 | 51.23 | 56.07 |

⇒ 前视图**大臂要抬高 ~11°**、小臂**相对大臂反向 ~18°**；俯视图**本来就对上** ⇒ 修正必须是**绕世界 Y（前后轴）的旋转**。
（`elbow bend` 在前视图的折角：我们 21.5° / 参照 3.6° —— 这就是用户说的“小臂仍有弯曲”。）

### 21.3 实现：步骤 **4e-2**（`arm_rot=(SH, EL, WR)`，只动网格）
```
--arm-rot 9.90 -17.25 21.50        # 拟合结果（见 21.5）
--arm-rot-smooth 8                 # 权重平滑次数（见 21.4-B）
```
- 三段**嵌套**旋转，各绕自己的关节；**正 = 抬高**（左侧绕 +Y，右侧镜像 `sgn=-1`）。
- **ROOT → TIP** 顺序（肩→肘→腕），每转完一段就把**子关节**一起搬走 ⇒ 与真实姿态链一致。
- **只加到 `out`（网格），骨架一字不动**；`out_idle` 自动跟随 ⇒ 不变量不受影响。
- 绕世界 Y 转的副作用：手臂**前摆**时（小臂 X-Y ≈ 42°）X-Y 投影也会变 ⇒ 俯视图漂 ~2–4°（见 21.7）。

### 21.4 我在这一轮踩的**两个坑**（都已修，都记下来）
**A. 用“沿臂轴的位置斜坡”当权重 ⇒ 整个下半身被转了 19°，网格下沉 19 cm。**
`t = (out − J_shoulder) · u`，而 `u`（肩→手）**指向斜下方** ⇒ **脚也投影在 `t` 上**
（实测一只脚在 `t = +0.6`，比腕关节 0.555 还远）⇒ `wrist` 权重 = 1.0，三段全量叠到腿上。
后果：`zmin −0.0612 → −0.2525`、`height 2.0731 → 2.2644`、`edge max 14.34`、`bad>25% 15.84%`。
**修法**：权重改用**源模型自己的 LBS 权重**——把"骑在某个关节上的骨"分成三组
（0 肩：`shoulder/shoulder_twist/shoulderarmour/elbow/hand/fingers…`；1 肘：`elbow` 及更远；2 腕：`hand/fingers`），
`w[k] = Σ_j w_j·[group(bone_j) ≤ k]`。**腿/躯干拿到的权重恰好是 0**，肘/腕的过渡由权重本身给出（天然平滑）。
**教训：任何"沿某条轴的位置"都要先问一句"这条轴指向哪、还有谁会投影到它上面"。**

**B. LBS 权重在肘/腕处只跨**一条边**跳变 ⇒ 26° 的腕旋转把 1 cm 的边拉成 5.08x。**
**修法**：`arm_rot_smooth`（默认 8）对**权重场**做 Laplacian 平滑（不是对位移），把过渡摊到几条边上。
`edge max 5.08 → 2.93`（旋转前的基线是 **2.99**）⇒ **代价归零**。

另外**顺序**也踩过：先转腕、再肘、再肩（"最深优先"）时，肘的旋转是绕**未旋转的** `J_elbow` 做的，
而抬肩已经把它搬走了 **5.5 cm** ⇒ 小臂整体偏 5.5 cm。**改成 ROOT→TIP 并同步搬子关节**。

### 21.5 拟合（`_fit_armrot.py`：数值 Jacobian 的 Newton）
| 选择方式 | (SH, EL, WR) | 冻结选择误差 | 实际几何误差 |
|---|---|---|---|
| 冻结（推荐） | 8.90 / −15.75 / 19.82 | 0.00° | 2.63 / −0.47 / 0.97 |
| 实际几何 | 10.92 / −18.75 / 23.10 | −2.46 / 1.02 / −1.46 | 0.00° |
| **取中（已采用）** | **9.90 / −17.25 / 21.50** | −1.22 / 0.52 / −0.75 | 1.32 / −0.29 / 0.52 |

**⚠️ 测量方法学的坑（很重要）**：如果**每跑一次都按当前几何重算分箱窗口**，旋转会把顶点**挪过窗口边界**，
于是"同一段"其实换了一批顶点 —— 数字会乱跳，**甚至变号**（实测 `0 -18 0` 单独跑时手部从 57.11 变成 83.02，
n 从 2732 变 3981）。**必须把分段冻结在 `arm_rot=None` 的那一次上**，再比较不同参数。
冻结拟合与"实际几何"拟合相差 ~2.5°，两者都合理 ⇒ **取中点**，任一边误差 ≤1.3°。

### 21.6 结果与代价
| | 我们（第 45b） | **我们（第 45c）** | 参照 |
|---|---|---|---|
| 大臂 X-Z | 39.38 | **29.81** | 28.49 |
| 小臂 X-Z | 17.88 | **24.63** | 24.92 |
| 手掌 X-Z | 57.11 | **49.87** | 49.34 |
| 正视折角 | 21.5 | **5.2** | 3.6 |
| 肘折角（3D） | 35.13 | **29.42** | 24.96 |

**不变量（`probe_invariants.py`）**：
```
our zmin -0.0612 height 2.0731      (与第 45b 逐位相同)
idle vs bind 6.76e-16 m | shipped rig vs HD2 7.22e-16 m   (不变)
edge mean 1.0733 p99 1.4368 max 2.93 bad>25% 2.29%        (第45b: 1.0744/1.4403/2.99/2.29%)
```
⇒ **均值、p99、max、bad% 全部不劣于旋转前**，身高/最低点/两条恒等式**完全不动**。

**腋窝没有复发（`probe_arm_tear.py`）**：第 45c 把 **254** 个躯干主导顶点挪 >2 cm（max **0.0343 m**）、
左腋窝带内只有 **5** 个（max 0.0230 m），**>5 cm 为 0**（第 45 轮是 150 个 >2 cm、max 0.0413 m）。

### 21.7 剩余误差（如实记录）
- **俯视图漂 ~2–4°**：小臂 X-Y `41.58 → 43.29`（参照 39.43）。根因是**绕世界 Y 转不是"只改仰角"**——
  手臂前摆 42° 时，Y 旋转同时改变 x，于是 X-Y 投影也动。想两全其美需要**每段自己的旋转轴**
  （把当前方向转到参照方向的最小旋转，轴 = 两方向叉积），那就不是 3 个标量而是 3 个"轴+角"常量。
  用户明确说"俯视图已经合在一起了"，故本轮**先不做**，留给视觉验收决定。
- **肘折角 3D 29.42 vs 24.96** 同上（X-Y 残差抬高了 3D 折角）。
- **nick 过冲**（`+0.1869`，唯一"本来超长"的模型）与旋转无关，仍是待决项。
- **指尖**：zoey 指尖 t `+0.7453` vs 骨 `+0.6971`（+4.8 cm，源模型手指本身更长）；5 模型零逐模型调参。

### 21.8 工程
- CLI：`--arm-rot SH EL WR`、`--arm-rot-smooth N`。实验室用环境变量 `HD2_ARM_ROT="SH EL WR"` /
  `HD2_ARM_ROT_SMOOTH=N` 让**所有**写死参数的探针一起切换（探针显式传 `arm_rot` 时以探针为准）。
- 顺手修了一个**预先存在**的 CLI 崩溃：`r['scale']` 是 1 元素数组，`'%.4f' % r['scale']` 抛 `TypeError`
  ⇒ 改 `float(np.asarray(r['scale']).ravel()[0])`。
- 出图：`probe_arm_zoom.py`（on/off）、`probe_hand_zoom.py`、`probe_arm_view.py`、`probe_armpit.py`；
  Blender 重建 + 15 张图，旧产物备份在 `out/blender/before-armrot/`。

### 21.9 教训
- **"平移修位置，旋转修方向"，两件事不能互相替代。** 第 45 轮把肘关节搬到骨头上（差 1.4 mm），
  但手臂**朝向**一直没动 —— 所以用户看到"手臂下垂太多"。**先问"错的是位置还是方向"。**
- **用户给的两个视角（正视 vs 俯视）就是一条约束方程。** 一个视角错、另一个对 ⇒ 修正轴被唯一确定。
  **不要只用"总的 3D 距离"当目标**，要分投影面看。
- **权重场必须来自数据（LBS），不能自己造。** 自造的"位置斜坡"在一条指向下方的轴上会静默地把腿卷进来，
  而且**绑定态/静止态指标全漂亮**（因为指标都在手臂上）—— 只有 `zmin/height/edge max` 这种**全局**量才暴露它。
- **拟合的"分段"必须冻结。** 参数改变会改变几何、几何改变会改变分段、分段改变会改变目标 —— 闭环会给出乱跳甚至变号的结果。

---

## §22 【2026-10-12 · 第 45d 轮】手臂**裂缝**：模型不是一张皮，而是**一叠壳**，每层权重不同 ⇒ 按权重旋转必然把它们撕开

### 22.1 用户验收（第 45d 轮）
用户确认第 45c 轮**角度问题已全部解决**（大臂角度、小臂角度、手指对准骨骼都对上了），但报告新问题：
- `armpit.png` 第 1 行第 3 列 vs 第 4 列（参照）：**手掌对齐了，但小臂扭曲成了怪异的形状**；
- Blender 里复核：**从小臂开始出现严重裂缝**、**手掌虎口处破裂**、**腕带脱离**。

并给出**方法论质问**（他自称机加工 / 3D 机械设计建模师）：

> "你早先造成的问题更像是**没有使用这些办法来移动模型，而是从结果反推过程，并且没有经过原始起源骨骼的约束**，
> 导致小臂异常的扭曲，模型破裂等情况，是不是这样的？我们接下来是不是应该稍微调整一下操作办法？"

他提的"办法" = **只把腕 / 肘 / 肩的起源原始骨骼点位当枢轴，只旋转这三处**（手掌内指骨另算）；
**大臂**允许**均匀扭转（≤90°）+ 小幅均匀拉伸**；**小臂**同；**腕部几乎不允许扭转、允许近 180° 弯折**。

### 22.2 视觉定位（`probe_arm_wrist.py`，左小臂+腕+手，正视 X-Z / 俯视 X-Y，6 列并排）
- `no arm_rot`：**干净直管**；
- `45c blend`：小臂出现**扇形 / 螺旋阴影 + 波浪形轮廓**；
- `45d axis`：明显变平滑。

关键对照是 `no arm_rot`（干净）→ `arm_rot`（波浪）：**是 arm_rot 引入的**，与 `arm_fit` 残差无关
（第 45b 轮已证明残差单独是干净的）。

### 22.3 三个假说，逐一被数据否掉
1. **"平均两个不同旋转矩阵 ⇒ 非正交 ⇒ 剪切（candy wrapper）"** —— 听起来最合理，但**错**。
   把 4e-2 改成"**嵌套旋转、最内层（腕）先做、每层一个旋转 + 一个标量权重**"（`seq` 模式），
   理论上不存在"同层平均两个不同旋转"，**但数值与 `blend` 几乎逐位相同**
   （p99 1.50 vs 1.49、max 2.25 vs 2.25、>2x 9 vs 9）。⇒ **剪切不是原因。**
2. **"`arm_rot_smooth`（对权重场做 Laplacian）把混合带拉宽成 S 形"** —— 扫 0 / 2 / 8 次：形状**不变**。
   ⇒ 平滑不是原因（它只让拉伸更小：max 3.06 → 1.74）。
3. **"旋转让手臂鼓包"** —— 沿臂轴做**半径剖面**（`_ab_armdispmap.py`）：每档 mean r 几乎不变
   （t 0.10–0.15：0.0354 → 0.0360）。⇒ **不是鼓包。**

### 22.4 真机制：**一叠壳，各自权重不同**
沿臂轴把每层皮**各自的**权重打出来（`_dbg_gprofile.py`）—— 手骨权重 `a2`：

| t | 0.25 | 0.30 | 0.35 | 0.40 | 0.45 | 0.50 | 0.55 | 0.60 | 0.65 |
|---|---|---|---|---|---|---|---|---|---|
| a2 | 0.022 | 0.160 | 0.387 | **0.674** | 0.889 | **0.429** | 0.566 | 0.998 | 1.000 |

**非单调！** t≈0.42 处已经是 67% 手骨，而 t≈0.52 处只有 43%。
原因是**同一位置有多个壳**：裸臂 / 袖子 / **腕口（袖口荷叶边）** / 肘部荷叶边 ——
它们各自的 LBS 权重不同（腕口是单独一件，绑法不一样）。
⇒ **按权重旋转 = 不同壳转的量不同 = 壳与壳分开** = 用户看到的**裂缝 / 虎口破裂 / 腕带脱离**。

### 22.5 新度量：**裂缝 = 原本贴在一起、但不在同一条边上的两个顶点，被拉开了多少**
`_dbg_crack.py`：在臂区找**非连通**且原距 ≤4 mm 的顶点对（就是相邻的壳），看变换后距离。

| build | max 分离 | >5 mm | >10 mm |
|---|---|---|---|
| no arm_rot | 4.0 mm | 0 | 0 |
| 45c blend | **29.2 mm** | 405 | **270** |
| 45c seq | **29.2 mm** | 406 | **270** |
| **45d axis** | **4.8 mm** | **0** | **0** |

**这是本轮唯一的决定性证据。** 之前所有指标（边长比、半径、绑定态网格差）都看不见它，
因为"壳与壳分开"不改变任何**单层**的形状。

### 22.6 修法 `arm_rot_mode='axis'`（**新默认**）
把 `g` 从"**源模型的 LBS 权重**"换成"**沿臂轴的位置斜坡 × 手臂掩码**"：

```
W    = a0 + a1 + a2                                   # 手臂总权重，只用来做掩码
mask = smoothstep(clip(W / 0.5))                      # 腿 / 躯干 = 0
t    = (v - J_shoulder) · u                           # 沿 肩->手 轴
ramp(c) = smoothstep(clip((t - (c - band)) / (2*band)))      # band = 0.090 m
g = [mask*ramp(0), mask*ramp(t_elbow), mask*ramp(t_wrist)]
```

**同一位置的所有壳拿到同一个 `g`** ⇒ 一起动 ⇒ 不裂。

- **肩部也必须加 `ramp(0)`**（不只是 mask）：肩胛 / 颈根（t = −0.24）还留了一点手臂权重，
  10° 绕肩在 20 cm 外就是 24 mm —— 它制造了**全网格最差的一条边**（4 mm 边拉到 5.65x）。
  加 ramp 后 max 4.93 → **2.96**。
- 旋转顺序仍用 **最内层（腕）先做**，每层用**原始骨点** `J[k]` 当枢轴 —— 不需要复合枢轴。
  （试过"复合枢轴 + 混合角度/枢轴"：seg1 的复合枢轴跑到**腕外 38 cm**，混合它反而更糟：max 2.97 / 25 个 >2x。）
- `arm_rot_smooth` 在 axis 模式下**不参与**（斜坡本身已经平滑，再平滑权重会把掩码糊到躯干上）。
- band 扫描：0.040→0.120，mean 1.0768→1.0746、bad 2.72%→2.26%，取 **0.090**。

### 22.7 重新拟合
`_fit_armrot.py`（axis 模式）：冻结 `9.24 −17.22 27.41`（误差 0）、live `11.67 −19.88 31.85`（误差 0）
⇒ **取中 `10.46 −18.55 29.63`**。（`arm_rot` 是"相对父级"的角度，枢轴 = 原始骨点。）

### 22.8 结果与代价
角度（X-Z 低于水平 / X-Y 前偏）：

| | 大臂 | 小臂 | 手掌 | 肘折角(3D) |
|---|---|---|---|---|
| 我们 45b（无 arm_rot） | 39.38 / 13.66 | 17.88 / 41.58 | 57.11 / 57.76 | 35.13 |
| 我们 45c（seq） | 29.81 / — | 24.63 / — | 49.87 / — | 29.42 |
| **我们 45d（axis）** | **28.69** / 13.02 | **26.12** / 43.47 | **47.96** / 51.93 | 29.02 |
| 参照 patch | 28.49 / 13.55 | 24.92 / 39.43 | 49.34 / 51.23 | 24.96 |

- 大臂误差 **+0.20°**（45c 是 +1.32°）。
- **不变量**：`zmin −0.0612 height 2.0731`；`idle vs bind 6.76e-16`；`shipped rig vs HD2 7.22e-16`；
  `edge mean 1.0752 p99 1.4399 max 2.96 bad>25% 2.32%`（45c 是 1.0732 / 1.4379 / 2.94 / 2.31% —— 持平）。
- **腋窝未复发**：`t<+0.05` 的躯干主导顶点 **0 个 >2 cm**（max 0.0151 m）；45c 是 23 个（max 0.0245）。
- **5 模型零调参**：zoey / rochelle 肘 −0.0003；coach +0.0232；**nick +0.1911**（唯一"本来超长"的模型，旧值 +0.1869）；ellis L+0.0697 / R−0.0150。
- **裂缝度量：270 个 >10 mm → 0 个。**

### 22.9 教训
- **"看起来像剪切"不等于"是剪切"。** 我按 candy-wrapper 改了两次实现（复合枢轴 → 最内层优先），
  **数值几乎没动**。真正的判据必须是**物理量**（壳间距），不是形状印象。
- **模型是"一叠壳"，不是"一张皮"。** 任何"按顶点蒙皮权重做的位移 / 旋转"都会让壳互相错开。
  **判据：非连通近邻对的分离量** —— 这个量在**所有**旧指标里都是盲区。
- **同一位置必须拿到同一个变换。** 想要"段内刚性"，参数就必须是**几何位置**（沿轴），不能是**蒙皮权重**。
  这正是用户说的"**只把起源原始骨骼点位当枢轴、段内均匀扭转 / 拉伸**"。
- **掩码要两头都收。** 只按权重做掩码，会让"远在颈根"的顶点吃到肩部旋转（20 cm 外 = 24 mm）。

---

## 23. 第 45e-2 轮：`arm_rot_mode='rigid'` —— 段内刚体 + 关节窄过渡 + 按角度插值

### 23.1 病灶（第 45e 轮锁定）
用户判据：**正视图里参照的手臂边缘 / 中线几乎直，我们只有大臂直、小臂总歪；人类小臂骨不能弯。**
`axis`（45d）模式有两个叠加病灶：
1. **斜坡太宽**：`band=0.090` ⇒ 每条斜坡跨 **0.180 m**，而上臂 0.286 / 小臂 0.269 m
   ⇒ 斜坡吃掉每段 **~65%** ⇒ **段内被连续掰弯、不是刚体**。累计角沿轴**非单调**
   （t=0.090 +10.46° → 0.286 +1.18° → 0.376 −8.09° → 0.555 +6.73° → 0.645 +21.54°）。
2. **`mask` 是连续的**：`smoothstep(W/0.5)`，而 `W`（手臂骨 LBS 权重和）沿轴**非单调**
   （模型是一叠壳）⇒ ramp 到 1 之后旋转量仍在变。

### 23.2 修法（`arm_rot_mode='rigid'`，现为默认）
1. **mask 改二值**：`W > HD2_ARM_MASK`(0.5)，再按边邻接扩张 `HD2_ARM_MASK_GROW`(2) 层。
   *实测手臂本体壳 #42540 的 443 个顶点 W 全部 = 1.000 ⇒ 二值 mask 100% 覆盖，无硬边。*
2. **斜坡收窄**：`band = HD2_ARM_GAP`，默认 **0.015 m**（1.5 cm）。
3. **按角度插值**：`out = J + R(g·θ)(v−J)`（= `exp(gθA)`），替代 `v + g(Rv−v)`。
   后者是**恒等与 R 的加权混合**，线性部分 `(1−g)I + gR` **不正交**
   （缩 `cos((1−g)θ/2)`、剪 `sin((1−g)θ/2)`）⇒ 额外扭转。
   **按角度缩放则任意 g 都是纯旋转**，三层复合仍是刚体。

### 23.3 验收：**用"刚体拟合残差"而不是中线**
中线（顶点域切片质心 / 像素域）在本项目**不可用**：
- **顶点域**：手臂壳只有 443 顶点，每档 8–20 个 ⇒ 质心噪声 **±15 mm**
  （实测连 `no arm_rot` 都抖：t=0.47 档 36.1 vs 相邻 22.4 / 20.5）。
- 且**旋转会移动网格**，用固定的"肩→手"轴去切**旋转后**的网格是**斜切**，
  混入不同来源的顶点 ⇒ **假弯曲**（rigid 的"改善"38→29.8 mm 就全是这个伪影）。
- **像素域**：BOX 内混有袖子 / 裸臂多个壳 ⇒ **参照自己也量出 83.8 px / flips 5**
  ⇒ 按技能"参照必须直"的判据，**该测量方法在此不成立**。

**可靠判据 = 对一个固定顶点集（用未旋转几何选出、且限制在手臂权重顶点）做 Kabsch
刚体拟合的残差**；残差 0 ⇔ 该段是刚体 ⇔ 不可能被弯。
⚠️ **选择器必须限制到手臂权重顶点**，否则裙摆 / 腿 / 头发会混进来 ——
第一版这样写，报出假的 **69 mm**（`t` 是"肩→手"轴的投影，而这条轴朝下，脚投影到 t=+0.6）。

| 区域（按未旋转 t） | `rigid` gap 1.5 cm | `rigid` gap 5 cm | `axis` 18 cm |
|---|---|---|---|
| 上臂 0.16–0.26 | **0.30 / 1.01 mm** | 0.34 / 1.93 | 1.32 / 7.40 |
| 肘带 0.26–0.31 | 6.19 / **18.41** | 4.23 / **8.77** | 2.57 / 5.52 |
| 前臂 0.32–0.52 | **0.86 / 2.03** | 0.98 / 3.81 | 1.74 / 5.99 |
| 手 0.56–0.62 | 9.80 / 19.72 | 5.53 / 13.16 | 2.89 / 7.26 |

（mean / max）⇒ **`rigid` 把上臂、前臂都变成了刚体（≤2 mm）；`axis` 全段都有 5–7 mm 的变形。**

### 23.4 代价：这是**零和**权衡
"掰直手臂"（源模型肘折角 **21.5°** → 参照 **3.6°**，要掰 **18.5°**）的变形量是**守恒**的，
只能选择"放在哪"：
- **窄过渡** ⇒ 段内完美刚体（1–2 mm），但变形集中在关节
  （肘带 **18.4 mm** ⇒ 视觉上一个"台阶 / 瘤"）。
- **宽过渡** ⇒ 变形摊到全段（5–7 mm，视觉平滑），但**段内就弯**
  （= 用户看到的"小臂歪"）。

`HD2_ARM_GAP` 就是这根旋钮：`0.015`（小臂最直）/ `0.030` / `0.050`（关节最平滑）。
**⇒ 待用户看图拍板。**

### 23.5 环境变量 / CLI / 探针
- 新 `--arm-rot-mode rigid`（**默认**），保留 `axis`（45d）/ `seq` / `blend` 作 A/B。
- `HD2_ARM_GAP`（rigid 的过渡带，默认 0.015）/ `HD2_ARM_BAND`（axis 的，默认 0.090）
  / `HD2_ARM_MASK` / `HD2_ARM_MASK_GROW`。
- 新探针：`_dbg45e2.py`（刚体拟合残差）、`_dbg45e3.py`（固定选择器中线）、
  `probe_arm_compare.py`（我们 vs 参照）、`probe_layers_report.py`（已改：带 `_env` 的过渡带扫描）。
- **不变量全部保持**：`zmin −0.0612 height 2.0731`；`idle vs bind 6.38e-16`；
  `shipped rig vs HD2 7.22e-16`；`edge mean 1.0744 p99 1.4403 max 2.99 bad 2.29%`。

