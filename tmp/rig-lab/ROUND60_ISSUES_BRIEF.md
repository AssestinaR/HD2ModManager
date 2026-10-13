# 【交流文档】第 60 轮实机结果：小臂"橡皮筋"与材质全错 —— 问题收集与初步判断

> **用途**：请另一个线程（模拟子代理 / 专家）**复核我的证据与判断**，并给出修复方向。
> **请把结论写在文件末尾的「## 6. 对方结论」一节**，我这边会读取。
> 文档自包含，不需要额外上下文。
>
> **本轮纪律**：只读。**没有修改任何既有代码**（`git status` 干净，见 §5.4）。
> 只新增了 5 个只读探针（`_probe_round60.py` / `_probe_forearm.py` /
> `_probe_texdecode.py` / `_probe_normals.py` / `_probe_matcompare.py` + `_probe_texcompare.py`），
> 全部只打印、只往临时目录写解码中间文件。

---

## 1. 本轮实机结果（用户报告）

**好消息（第 59 轮的失败全部消失）：**

- 模型**可见**（上一轮是全透明）；
- **走路大致正常**；
- **绝大多数动作、表情正常**；
- 战斗场景**影子正常**（上一轮是布朗运动的钢丝球）；
- 进 Blender 看**模型正常、骨骼正常**。

**问题 1（严重）：小臂整体发软、"Q 弹"，像橡皮筋。**
> 原话：「小臂整体有点软，像是面筋一样，特别 q 弹，很多动作不仅是让肘部移动，
> 而且还让小臂像是软软的橡皮筋绳子一样弹弹的，不过手和大臂等均正常移动了，
> 没看到有明显问题，就是只有小臂问题严重」

**问题 2（观感影响严重）：材质完全不对。**
> 原话：「整个人物为黑色，部分区域有金属光泽；在军械库，或准备出击前的装备管理页面
> **无法显示模型**」

---

## 2. 问题 1：小臂（已定位到具体数值）

### 2.1 只读实测：沿手臂轴的权重剖面

探针 `_probe_forearm.py`：把每条手臂骨的**世界位置**（来自我们 unit 的 TransformInfo，
idle 帧）取出，沿「关节 A → 关节 B」轴投影每个顶点，按 3 cm 分箱，统计每箱内顶点的
**平均权重**。左/右完全对称，下面只列左侧。

**对照组：上臂（`l_shoulder` → `l_elbow`，长 0.3028 m）**

| s (m) | n | 平均权重 |
|---|---|---|
| 0.069–0.234 | 742+1365+116+309 | **`l_shoulder` = 0.98–0.99**，`l_elbow` = 0.00–0.02 |
| 0.234–0.267 | 78 | shoulder 0.81, elbow 0.19 |
| 0.267–0.300 | 115 | elbow 0.62, shoulder 0.32 |
| 0.300–0.333 | 82 | elbow 0.74, hand 0.20 |

⇒ 上臂是**刚性段**，肘部过渡带只有约 **6.6 cm**。**干净。**（与用户"大臂正常"一致）

**问题组：小臂（`l_elbow` → `l_hand`，长 0.2874 m）**

| s (m) | n | 平均权重 |
|---|---|---|
| −0.030–−0.004 | 79 | `l_shoulder`=0.64, `l_elbow`=0.35, `l_hand`=0.02 |
| −0.004–0.022 | 89 | elbow 0.65, shoulder 0.29, hand 0.06 |
| 0.022–0.049 | 61 | elbow 0.76, hand 0.19, shoulder 0.05 |
| 0.049–0.075 | 59 | elbow 0.67, **hand 0.32** |
| 0.075–0.101 | 57 | elbow 0.54, **hand 0.45** |
| 0.101–0.127 | 29 | **hand 0.60**, elbow 0.40 |
| 0.127–0.154 | 30 | hand 0.71, elbow 0.29 |
| 0.154–0.180 | 31 | hand 0.84, elbow 0.16 |
| 0.180–0.206 | 30 | hand 0.94, elbow 0.06 |
| 0.206–0.232 | 72 | hand 0.62, elbow 0.38 |
| **0.232–0.259** | **416** | **elbow 0.61, hand 0.39** |
| **0.259–0.285** | **1265** | **elbow 0.51, hand 0.49** |
| 0.285–0.311 | 353 | hand 0.65, elbow 0.33, thumb1 0.03 |
| 0.311–0.337 | 193 | hand 0.58, thumb1 0.36, thumb2 0.05 |

另外，以「主导骨」统计（`_probe_round60.py` §4）：
**主导骨为 `l_elbow` 的 1701 个顶点，平均权重 = `l_elbow` 0.641 / `l_hand` 0.344 /
`l_shoulder` 0.015** —— 也就是说，**"小臂骨"名下的顶点里，平均有 1/3 的权重给了手骨。**

### 2.2 我的判断（请复核）

1. **肘→腕的权重过渡带被摊平到了整条小臂上**（从 s≈0.05 一直延伸到 s≈0.29），
   而**肩→肘的过渡带只有 6.6 cm**。同一条管线、同一个模型，两个关节的处理严重不对称。
2. 更糟的是**非单调**：在 **腕端**（s = 0.232–0.285，共 1681 个顶点）
   权重又回到 **elbow ≈ 0.51–0.61 / hand ≈ 0.39–0.49**。
   正常绑定在腕端应该是 `l_hand` ≈ 0.9–1.0。**腕端一半权重挂在肘骨上。**
3. 后果：腕部一旋转（几乎所有持枪/行走/待机动作都有腕部旋转），
   小臂表面得到的是"一半肘 + 一半手"的混合变换 ⇒ **整条小臂被拉成弹性绳**。
   这就是「像橡皮筋一样弹弹的」的机理。

**为什么上臂没事而小臂有事？** 上臂的过渡带紧贴肘部，且几乎没有远端骨（手）的渗入；
小臂则同时被 `l_hand` 从远端大面积渗入。**症状与"手骨吞掉了小臂"完全吻合**
（本项目长期记忆里就记着一条待办：「手部按主导骨会吞前臂，需腕前空间裁切」）。

### 2.3 已经排除的可能（都做了实测）

| 可能原因 | 结论 | 证据 |
|---|---|---|
| 权重未归一化（和 ≠ 1） | **排除** | `weights: half4 closes exactly (worst deviation 0.000e+00); 188356 slots filled` |
| 小臂被绑到 **twist 骨**（`l_shoulder_twist` / `l_hand_twist`） | **排除** | 全 unit 只有 49 个骨槽带权重，`l_shoulder_twist` / `l_hand_twist` **都不在其中** |
| 小臂被绑到我们**额外追加的非 avatar 节点**（如 slot 103 `0xa5c8115b`，父链挂在 `l_elbow`、位置在小臂中段） | **排除** | 同上，49 个带权骨全是标准 avatar 骨名 |
| 骨骼层级 / 父链错 | **排除** | 骨表逐条正确：`l_clavicle → l_shoulder → l_elbow → l_hand → 各指节`，世界位置与 HD2 一致 |
| 几何被拉长（绑定态就不对） | **排除** | 用户 idle/走路正常，且 Blender 导入正常 |

⇒ **这是一个纯粹的"权重分布"问题，且只影响小臂。**

---

## 3. 问题 2：材质

### 3.1 决定性发现：我们用错了材质模板

`build_patch.py` 用的是 SDK 自带模板 `docs/HD2SDK-CommunityEdition-3.8.0/materials/basic.material`。
SDK 自己在 `__init__.py:202-212` 的 `Global_Materials` 表里写明了每个模板的来源与用途：

```python
("advanced", "Advanced",  "…which renders in the UI. Sourced from the Illuminate Overseer.")
("basic+",   "Basic+",    "A basic material with a color, normal, and PBR map which renders in the UI, Sourced from a SEAF NPC")
("alphaclip+","Alpha Clip+","…does not render in the UI…")
("alphaclip", "Alpha Clip","…does not render in the UI…")
("original", "Original",  "…bloated with additional unnecessary textures. Sourced from a terminid")
("basic",    "Basic",     "A basic material with a color, normal, and PBR map. Sourced from a trash bag prop")   # ← 我们用的
("emissive", "Emissive",  "…Sourced from a vending machine")
("armorlut", "Armor LUT", "…multiple mask textures and LUTs to texture the mesh… Sourced from the base game material on Armors")
```

**我们用的是 `basic` —— SDK 明确标注它来自"垃圾袋道具（trash bag prop）"，
而只有 `advanced` 与 `basic+` 被标注为 "renders in the UI"。**

**军械库/装备管理页就是 UI。** 这直接对上用户的第二句抱怨（UI 里看不到模型）。

### 3.2 我们包里实际的材质 vs 参考模组

探针 `_probe_matcompare.py`（`shadervariables.txt` 把变量 id 翻译成名字）：

| | 我们的材质 | 参考模组（游戏里显示正常） | 原版 RE-2310 护甲材质 |
|---|---|---|---|
| `parent` | **`basic`** | **`advanced`**（及一个自定义 parent） | `0x9e0a4db5c90ed6d3`（自定义） |
| 贴图槽数 | **3** | 11 / 13 | 11 |
| 着色器变量数 | **13** | 42 / 37 | 25 |
| 变量内容 | 全是 **dirt/weathering**：`DirtGlobalAmount`、`DirtAoCoverage`、`WeatheringVariant`、`Porosity`、`Wetness`… **没有任何颜色/调色板变量** | `TintMaskColor`、`EmissiveColor`、`SubsurfaceDiffusion`、`MicroAoIntensity`、`Clearcoat*`… | `PaletteSlot`、`BloodColor`、`GunkScale`、`Weathering*`… |
| 贴图槽语义（SDK `TextureTypeLookup`） | `("PBR","Base Color","Normal")` | — | — |

**⇒ 我们的材质和"HD2 护甲材质"根本不是一类东西。** 用户的原话
「你可能使用了错误的材质全部填充了整体模型」方向是对的。

### 3.3 但材质二进制本身是**正确**的（已逐项核对）

- 字段顺序与 SDK `stingray/material.py::StingrayMaterial.Serialize` **逐项一致**
  （12B undat1 → u32 EndOffset → u64 undat2 → u64 ParentMaterialID → 32B → u32 NumTextures
  → 36B → u32 NumVariables → 12B → u32 VariableDataSize → 12B → NumTextures×(u32 TexUnk + u64 TexID)
  → NumVariables×(5×u32) → 变量值区）。
- 我们只是**把 `basic.material` 原样搬过来，替换了 3 个贴图 id**；
  13 个变量与 SDK 模板**逐值相同**（`DirtGlobalAmount = 0.153` …）。
- `parent_material_id` 确实是全局 `basic`（`0xD51F…`，与 `MATERIAL_PARENTS` 一致）。
- 贴图槽顺序 = `TextureTypeLookup["basic"]` = (PBR, Base Color, Normal)，与我们写入的
  `[shared_pbr, 底色, shared_normal]` 一致。

⇒ **不是"打包写错了"，是"模板选错了"。**

### 3.4 贴图本身**不是黑的**（已解码实测）

探针 `_probe_texdecode.py`：把包里的 Texture 资产写成 DDS，用 SDK 自带的
`deps/texconv.exe` 解码回 PNG，再统计像素。

| 资产 | 格式 | 尺寸 | 平均 RGBA |
|---|---|---|---|
| shared PBR（槽 0） | BC7_UNORM (98) | 4×4 | **(0, 158, 0, 255)** ← 正是我们写入的 R=metallic 0 / G=roughness 0.62 / B=0 |
| shared flat normal（槽 2） | BC7_UNORM | 4×4 | (129, 128, 255, 255) |
| 底色贴图 ×11 | BC7_UNORM_SRGB (99) | 2048×2048 | **平均亮度 155–228（很亮，接近白）** |

⇒ 底色贴图是**白的**，模型却渲染成**黑的**。所以"黑"不是贴图内容造成的。

### 3.5 顶点 ABI 与原版护甲**不一样**（重要差异，供裁定）

| | 我们的 unit | 原版 RE-2310 躯干 |
|---|---|---|
| 流数 | **4**（s0、s1 两份**完全相同**的 121704 顶点主流 + 2 个 culling） | 3 |
| 主流 stride | **56** | **60** |
| 分量 | `color(rgba8)` + position + normal + **uv0/1/2 全是 `vec2_float`** + bone_weight + **`bone_index` ×1（4 影响）** | position + normal + **uv0/1/2 全是 `vec2_half`** + bone_weight + **`bone_index` ×3（12 影响）** |
| 顶点色 | **有**（我们全部写 255 白） | **没有** |
| 顶点数 | 121704（= 全模型，4 个 LOD 共用） | 12871 |

我们这一套 ABI 是**照抄 Chiffon donor** 的，不是原版护甲的。既然游戏接受了它（模型能显示、
能动画），说明 ABI 本身合法；但它和护甲渲染路径期望的 ABI 不同，**不能排除护甲着色器
按固定布局读取属性（例如 12 影响、`vec2_half` UV、无顶点色）而我们的布局错位**。

### 3.6 已经排除的可能（都做了实测）

| 可能原因 | 结论 | 证据 |
|---|---|---|
| 法线反了 | **排除** | `_probe_normals.py`：存储法线 vs 由索引重建的几何法线，**dot 均值 +0.9023 / 中位 +0.9965，97.5% > 0**；法线长度精确为 1.0（解码器已用往返自检确认：`decode(encode(v))` 最差 **0.451°**） |
| 三角形绕序反了 | **排除** | 全网格有符号体积 **+0.758553 m³（正 ⇒ 朝外）** |
| 顶点色是黑的 | **排除** | `hd2_unitbuild.pack_mesh_vertices`：`col = np.full((nv,4), 255, np.uint8)`，且解码实测全 255 |
| 底色贴图内容黑 | **排除** | §3.4 实测平均亮度 155–228 |
| 贴图编码坏了 | **排除** | 用社区标准工具 `texconv.exe` 编 BC7；解码回来与写入值一致 |
| 材质二进制写坏 | **排除** | §3.3 |

### 3.7 我目前对"黑 + 金属光泽"的两个候选解释（都需要专家裁定）

**H1（我倾向这个）：护甲的"颜色 / 调色板"通道缺失，把反照率乘成了 0。**
HD2 护甲可以被玩家染色。原版护甲材质里有 `PaletteSlot = 1`，参考模组的 `advanced`
材质里有 `TintMaskColor = (1,1,1)`。我们的 `basic` 材质**没有任何调色板/染色变量**。
如果引擎的护甲染色 pass 用一个"未声明 ⇒ 默认值"去乘反照率，结果就是**整体黑**，
而**金属/高光项不受反照率染色影响 ⇒ 局部金属光泽**。

**H2：`basic` 的 PBR 槽 B 通道被当作 AO 使用，而我们写的是 0。**
我们写入 `(R=0, G=158, B=0, A=255)`；SDK 的 Blender 预览只把 R 当 metallic、G 当 roughness
（`__init__.py:1433-1435`），**没有用 B**。若游戏着色器把 B 当 AO ⇒ AO=0 ⇒ 整体压黑。
（但 H1 更能解释"局部金属光泽"这个细节。）

**H3（备选）：游戏对无法正确使用的材质回退到"默认/错误材质"**（黑 + 金属光泽），
同时这个回退材质不具备 UI 渲染能力 ⇒ 军械库看不见。若成立，则 §3.1 一个原因同时解释两个现象。

---

## 4. 我的候选修复方向（**本轮不实施**，仅供专家裁定）

**小臂（问题 1）：**
1. 把小臂重新绑定成"刚性段 + 两个窄过渡带"：
   `l_elbow` 独占小臂中段；肘侧留 2–3 cm 的 `l_shoulder`↔`l_elbow` 过渡；
   腕侧留 2–3 cm 的 `l_elbow`↔`l_hand` 过渡；腕端必须是 `l_hand` ≈ 0.9–1.0。
2. 关键判据（可直接写成自动检查）：
   - **沿小臂轴，`l_hand` 权重必须是"腕前才上升"的单调曲线**，在 s < 0.6·L 处 ≤ 0.1；
   - 腕端（s > 0.9·L）`l_hand` ≥ 0.85；
   - 小臂中段（0.25·L < s < 0.7·L）`l_elbow` ≥ 0.9。
3. 需要先确认：**这些权重是从哪一步产生的**（`out/blender/model.npz` 的 `w_bone`/`w_val`），
   以及"手骨吞前臂"的裁切为什么没有生效。**建议只改权重，不碰几何、不碰骨架。**

**材质（问题 2）：**
4. **把模板从 `basic` 换成护甲适用的模板。** 最有把握的对照是
   **参考模组 `【4.28】Chiffon-LUNALICE替换RE-2310`（同一个护甲、同一个 archive、游戏里正常）**：
   它的材质是 `parent = advanced`、11 贴图、42 变量。
   因此候选顺序建议：**`advanced`（有实机先例）> `basic+`（SDK 标注 UI 可用、且与 `basic` 同槽语义）> `armorlut`（原版护甲真身，但需 11 个 mask/LUT 贴图）**。
5. 在换模板之前，先请专家确认 §3.7 的 H1/H2/H3 哪个成立 —— 否则换了模板可能还是黑。
6. 军械库看不见：先假设由 §3.1 的模板问题一并解决；若换模板后仍看不见，
   再单独查 LOD/预览路径（我们 unit 的 LOD 布局是 `lod3/2/1 → s1`、`lod0 → s0`，
   s0 与 s1 是**两份相同**的顶点数据）。

---

## 5. 证据与复现

### 5.1 交付物（已部署、待复核）

| 产物 | 路径 | 字节 |
|---|---|---|
| 已部署的规整包 | `E:/mod/Lunalice-RE2310-Test/9ba626afa44a3aa3.patch_0`（+`.gpu_resources` 67992000 B） | 563900 |
| 部署方式 | `data/9ba626afa44a3aa3.patch_0[.gpu_resources]` 两个符号链接指向上面 | — |
| 回退 | `E:/tmp/deploy-revert-note.txt` → `E:/mod/Lunalice-RigTest/` | — |
| 承载 unit | `0xcae35ee15168145f`（Slim）/ `0x9841b16544c8fbae`（Stocky），各 243456 顶点 | — |

### 5.2 复现命令

```bash
PY="C:/Users/Administrator/.workbuddy-ai/binaries/python/envs/default/Scripts/python.exe"
cd E:/Data/source/repos/WpfApp1/tmp/rig-lab
$PY _probe_forearm.py                     # §2 手臂权重剖面（小臂）
$PY _probe_forearm.py "<参考模组 patch>"   # 同口径对照
$PY _probe_round60.py                     # 材质变量名 / 顶点 ABI / 骨表 / 手臂普查
$PY _probe_matcompare.py                  # 我们 vs 参考 vs 原版 vs SDK 模板 的材质
$PY _probe_texdecode.py                   # 把包里贴图解码回 PNG 统计亮度
$PY _probe_normals.py                     # 法线方向 + 绕序（含解码器往返自检）
$PY _probe_texcompare.py                  # 贴图格式/尺寸/字节数对比
```

### 5.3 参考数据

- 原版 RE-2310 抽取：`E:/tmp/hd2-mgroot/re2310-full/`（archive `d923906f8b815987`，
  22 unit toc + material + texture + bones）
- 参考模组：`E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/`（35 条目，8 贴图，2 材质）
- SDK：`E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0/`
  （`materials/*.material`、`hashlists/{bonehash,shadervariables}.txt`、`stingray/material.py`、
  `__init__.py` 的 `TextureTypeLookup` 与 `Global_Materials`）

### 5.4 本轮纪律

**没有修改任何既有代码或既有文档。** 只新增 6 个只读探针脚本
（`_probe_round60.py`、`_probe_forearm.py`、`_probe_matcompare.py`、`_probe_texcompare.py`、
`_probe_texdecode.py`、`_probe_normals.py`）。既有的 `hd2_*.py` / `build_patch.py` 一行未动。

---

## 6. 对方结论

（待填）
