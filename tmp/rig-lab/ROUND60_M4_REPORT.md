# 第 60 轮 · M4：材质模板错用（真正的根因）

**结论一句话**：护甲用的材质模板从头就是错的。我们给护甲套的是 `basic`（SDK 说明：*"Sourced from a trash bag prop"*，道具模板，3 槽），而**已在游戏中正常渲染**的参照模组 `【4.28】Chiffon-LUNALICE替换RE-2310` 给同样的护甲槽位用的是 **`advanced`**（SDK 说明：*"color, normal, emission and PBR capable **which renders in the UI**"*）。两套模板的槽位键**一个都不重合**，所以之前修贴图表引用当然毫无效果。

---

## 1. 为什么上一版（M1）零效果

M1 只修了 `hd2_asset.Material` 的贴图表**读写布局**（确实是个真 bug）。实测确认 M1 的字节**真的进了游戏**：

```
_probe_canddiff  M1 vs 旧包 Lunalice-RE2310-Test
  toc-changed entries : 14     ← 全是 Material
  gpu-changed entries : 0
```

改到位了，画面**一点没变**。原因在下一节。

（顺带排除：`9ba626afa44a3aa3.patch_1` 是管理器默认部署的护甲映射 Lua 包，824903 B、`gpu_resources`/`stream` 均为 0 字节，与 `patch_0` **没有任何 file_id 重叠**，且 Lua 已关闭——不影响。）

## 2. 决定性证据：槽位键完全不重合

```
原版 RE-2310 护甲材质  0f5ff78d a17b45a8 81d4c49d c2eb8d6e 30e2d136 d2f99d38 05a27dd5 b281e5f2 37831285 7e662968 6834aa9b
SDK armorlut.material  632a8b80 a17b45a8 81d4c49d c2eb8d6e 30e2d136 d2f99d38 05a27dd5 b281e5f2 37831285 7e662968 6834aa9b   ← 11 项对上 10 项
我们（basic）            756f6fa6 ac652e43 caed6cd6                                                                        ← 一个都不沾
```

`armorlut` 的 SDK 说明就是 *"**Sourced from the base game material on Armors**"*。

再看参照模组的护甲 unit（**正是我们替换的那两个 id** `0xcae35ee15168145f` / `0x9841b16544c8fbae`）：

```
unit 0xcae35ee15168145f  →  material 0x0d04d273fa5a4646  parent=advanced   ntex=11 nvar=42
                            material 0x0228cdef42ee1e6e  parent=0x8f669f365f24594e  ntex=13 nvar=37  (键=original 模板)
```

**参照模组用的是 `advanced` + `original`，不是 `basic`。** 同时解释了用户报的两个症状：场景内全黑 + 军械库看不见（SDK 只把 `advanced`/`basic+` 标为 "renders in the UI"）。

## 3. `advanced` 的三个自有槽位（实测参照模组的贴图）

SDK 的 `TextureTypeLookup['advanced']` 只给三个槽标了名，其余 8 个是空串，所以只能拿**真在跑**的材质当真相：

| 槽 | 键 | SDK 名称 | 参照模组实测 |
|---|---|---|---|
| 2 | `4c567810` | Normal/AO/Roughness | 4096²，B=254 → 法线图 |
| 3 | `90b84a53` | Metallic | 1024²，均值 6.4 → 近黑（非金属） |
| 5 | `e67ac0c7` | Color/Emission Mask | 4096² 明亮底色，**alpha 全 0** → 发光遮罩关闭 |

槽 5 的 alpha=0 是专家 §6.3 明确警告过的那一点（我们的底色 alpha 是 170–255，直接当发光遮罩会让整套护甲自发光）。

> 排错记录：`ref-slot5-color.png` 第一版看起来全黑，是 **PIL 对 alpha=0 的 RGBA 做缩放时的预乘假象**，不是贴图真的黑。全分辨率实测 RGB 均值 209/197/199。

## 4. 本版做了什么（M4）

`fix_materials.py` 扩展为支持模板与槽位映射：

| 模板 | 自有槽位（共享 / 底色 / 法线） |
|---|---|
| `basic` / `basic+` | 0 / 1 / 2 |
| `advanced` | **3（金属度） / 5（底色+发光遮罩） / 2（法线）** |

- 14 份材质全部从**干净的 SDK `advanced.material`** 重建；
- 槽 2 ← `shared_flat_normal`（4×4，128/128/255，BC7_UNORM）
- 槽 3 ← `shared_pbr` **改用途**为金属度图，像素重画为 (6,6,6,255)（与参照模组一致）
- 槽 5 ← 各组自己的底色，**alpha 强制归零**（texconv 解码→置零→重编码 BC7_SRGB，长度不变）
- 其余 8 槽保留 SDK 模板自带的游戏贴图 id
- 其余一切（22 个 Unit、2 个 Bone、所有 GPU、权重）**逐字节不动**

## 5. 验证

| 检查 | 结果 |
|---|---|
| `basic` 回归（同输入重建） | sha256 与已部署 M1 **逐字节相同** `fdcd6c7c…74c36` |
| 槽位键 vs 参照模组 | **11/11 完全相同**，nvar 42/42 相同 |
| `_probe_matchain.py`（SDK 解析器权威） | carried unit 断链 **0** → PASS |
| `_probe_pbrsemantics.py` | 槽 2/3/5 落位正确；底色 alpha 实测 **0.0** |
| `_probe_canddiff.py` vs M1 | 14 Material TOC + 15 Texture GPU，无 id 变动 |
| `_verify_normalized.py` | 3 失败，**与 M1 的 3 个完全相同**（既有的旧默认值问题），**无新增失败** |

## 6. 部署

```
E:/mod/Lunalice-RE2310-M4/9ba626afa44a3aa3.patch_0                576444 B
sha256 c7ba606b6901e47fbf3e4ffadf6857504b9223fa83ec8b586512302698292a44
gpu_resources 67992000 B（与 M1 同尺寸）
data 目录两个 symlink 已指向它；回退目标 Lunalice-RE2310-M1（记录 E:/tmp/deploy-revert-note.txt）
```

## 7. 待实机验收（用户只看结果）

1. 场景内颜色是否正常（不再整体发黑）；
2. **军械库 / 出击前装备管理页能否显示模型**（`advanced` 是 SDK 标注的 UI 可用模板，这是本版的主要赌注）；
3. 是否出现**异常发光**（若出现 → alpha 归零没生效，需再查）。

## 8. 已知未验证项（如实记录）

- `advanced` 模板的 **8 个非自有槽位**指向基础游戏贴图 id（来自 SDK 的 Illuminate Overseer 源材质）。参照模组也是同样形状的 8 个外部 id，说明这种用法可行；但**这些 id 在当前游戏版本能否解析，离线无法证明**。其中 `e21128ca48e27a13`、`17c4075f57e4211a` 已在本机原版 RE-2310 抽取中确认存在，其余未逐一核对。
- 法线用 4×4 平面法线、金属度用纯色——只为验证契约，不是最终外观。
- 未使用 twist 骨的问题仍然存在（与上一版相同），本版未触碰权重。
