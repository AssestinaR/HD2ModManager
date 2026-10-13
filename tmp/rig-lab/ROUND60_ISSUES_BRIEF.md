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

### 6.1 裁定概要与证据范围（2026-10-13 独立复核）

**小臂方向判断基本正确，但采样混层、归因与腕部修复指标需要纠正。材质判断有重大遗漏：不是“二进制已经排除”，而是已证实 Material 的贴图表读写错位，导致底色/PBR 引用损坏。先修二进制，再评价模板。**

- 本轮只新增独立探针及诊断报告、回填指导，没有修改主管线、生产权重、SDK 或游戏部署。
- 已验收的几何、骨架、可见性、影子及部署作为基线。不因这两个局部问题重做 Unit/LOD/绑定矩阵或摆姿。
- 实查 `E:/mod/Lunalice-RE2310-Test/9ba626afa44a3aa3.patch_0`，SHA256=`9c0cd8518278d9af35ffb971ac1f61ba7ddaf0d0ded1a82fdb6b634e9c0092da`。实际 14 材质、16 贴图（14 底色+2共享），与 §3.4 的11张统计不同，后续按产物 hash 区分。
- `review_round60_contract.py` 直接调用 SDK `StingrayMaterial.Serialize` 读取材质，不用待修复的 Material.read 作为真值。SDK 模块按文件加载，不依赖 Blender。
- 输出 `out/round60-independent-review.json` 包含包/GPU/主要脚本 SHA256、逐材质实际引用、内存候选检查和分连通块的动作数据。运行脚本只写此诊断 JSON，不保存修复包。
- 小臂数值实验读取 Slim 可见主流0：其 121704 个顶点坐标、按骨名称归并后的权重均与冻结 `out/blender/model.npz` **逐项完全相等**。本轮未声称测试了全部游戏动画、Stocky 和所有 LOD；主线程候选验收仍须覆盖它们。

### 6.2 材质 P0：贴图表读写布局错误，独立 SDK 已确认

SDK `stingray/material.py:32–33` 的真实读写顺序为：

```python
self.TexUnks = [f.uint32(x) for x in self.TexUnks]
self.TexIDs  = [f.uint64(x) for x in self.TexIDs]
```

也就是：

```text
offset 136          : N × uint32 槽位键
offset 136 + 4*N    : N × uint64 Texture ID
offset 136 + 12*N   : ShaderVariable 表
```

`hd2_asset.py:121–126` 却循环读取 `(u32 key, u64 id)`；`:152–153` 同样交错写回。文件开头的布局注释也错了。`build_patch.py:686–690` 用该读取器载入模板、替换 ID，再用该写入器输出，破坏了槽位键和贴图 ID。

仓库管理器已有正确实现：`HD2ModCore/Infrastructure/StingrayMaterialReferenceReader.cs:23` 使用 `textureIdsOffset = 136 + numTextures * 4`。本轮不需要修改这个正确的 C# 读取器。

三贴图材质的正确 ID 起点为 **148、156、164**，旧写法为 **140、152、164**。所以最后的法线碰巧正确，前两项错误。

实测结果：

| 检查 | 结果 |
|---|---|
| SDK 读取当前14份材质 | 每份均为 PBR/底色引用错误，normal引用正确 |
| 按 SDK 解出的42个引用在包内查找 | 28个失败，14个通过 |
| 原模板＋按正确偏移替换自有3个ID，仅在内存构造 | 14份均3/3通过，槽位键恢复为模板原值 |
| 重新调用旧生产写法作为负对照 | 14份全部被独立 SDK 检查拒绝 |
| 变量区/头部（内存修正候选对比原模板） | 未改动；说明不必通过改材质变量掩盖引用错误 |

例：材质 `a7e9750adf9bb6ab` 的预期 PBR/底色/normal 为 `b4948ba8ac86844a` / `b9f84331454d8282` / `ba1a40900756a7f3`；SDK实际读取成 `454d8282cfac644d` / `6d6eb716b9f84331` / `ba1a40900756a7f3`。

**为何此前自校验全过：** 同一个错误读写器，未修改时可能原样往返；替换资源ID后，再用错误读取器仍然看到“想要写入的ID”。这不证明 SDK/游戏读取正确。变量表总起点在两种布局下都是136+12N，所以变量仍与模板一致，进一步掩盖了问题。

因此撤回 §3.3、§3.6“材质二进制排除”以及 §3.7 的 H1 优先排序。底色图解码明亮只证明资产内容正常，**不能证明材质实际绑定到了它**。已证实的错误足以解释底色不生效；引擎最终 fallback 如何产生黑色/光泽仍待游戏对照，不能声称已经证明像素着色器的全部行为。

主线程修复要求：

1. `Material.read` 先读 N 个 u32，再读 N 个 u64；`write` 先写全部 keys，再写全部 IDs。保留其余未知字段和变量区。
2. 不用已损坏材质重新导出；从**干净 SDK 模板**构建，用构建清单恢复预期贴图身份。
3. `set_texture` 应核验已有槽位范围，不要无依据扩展零槽位键；切换模板须采用该模板的完整槽位表与变量。
4. 同步纠正类头部布局注释、`_probe_matcompare.py` 等检查器的依赖；不要再以同一个待测读取器证明序列化正确。
5. 补 SDK 交叉读、已知不同ID替换、资源闭合、槽位键保持、旧交错写法负对照。完整文件 roundtrip 可保留，但单独 roundtrip 检不出这个 bug。
6. 对最终规整包再检查 Unit section→材质→槽位键/TextureID→实际贴图，不止检查生成前对象。

### 6.3 黑色与 UI 缺失分开验证；首选 basic+ 的最小模板对照

`basic+` 与 `advanced` 确被 SDK `__init__.py:203–204` 标记为 renders in the UI；`basic` 仅写来源是垃圾袋道具，没有明确写“必定不支持 UI”。模板来源本身也不能证明它不允许绑定角色。因此 UI 是强候选，需要修正引用后实机分辨。

**推荐顺序改为：修贴图表 → basic 的最小对照 → basic+ → 必要时 advanced。** 不再要求先证明 H1/H2/H3 才允许修复已证实的二进制错误。

建议候选使用独立输出目录并保留当前基线，不必每个都交给用户盲测，可先全部离线闭合：

| 候选 | 唯一计划变化 | 用途 |
|---|---|---|
| M1 | 仅修材质贴图表，保留 basic 与旧贴图像素 | 定位引用错误对黑色的贡献 |
| M2 | M1 的 PBR.B 从0改255，其余不变 | AO/环境通道的单变量对照 |
| M3 | M2 换完整 SDK basic+ 模板，保留同语义贴图 | 检查 UI 支持，亦是优先可用候选 |
| M4（有必要才做） | 完整 SDK advanced＋正确语义贴图和依赖 | basic+ 不满足外观时再升级 |

材质诊断阶段先固定权重；权重实验另存一支。通过后合并，避免两类更改无法归因。最终部署包保持现有原生ID/体型/占位结构，仅计划修改相应资源。

贴图契约（槽位从0计）：

- basic/basic+：0=PBR/MRA，1=Base Color，2=Normal。中性候选 PBR=`(0,158,255,255)`：R金属度0、G粗糙度约0.62、B白AO、A白制作约定。
- advanced：2=Normal/AO/Roughness（RG法线、B AO、A roughness），3=Metallic，5=Color/Emission Mask（RGB底色、A发光遮罩）。平法线/粗糙度0.62候选 NAR=`(128,128,255,158)`；金属度黑；无发光底色A=0。不要把当前底色A=255直接继承为全身发光。
- advanced 其余8槽在SDK界面虽名称为空，却有资源引用；应保留可解析的模板依赖，不能删掉或塞0。不要只换parent，也不要把三张旧图按0/1/2强塞进去。
- SDK basic 预览仅连R/G，未模拟B/A；社区制作文档支持MRA.B为AO、A白约定，不等于拿到了游戏shader内部全部公式。修引用后再验证B通道效果。
- 当前 flat normal `(129,128,255,255)` 的RG近中性；没有证据要求交换RG、改AG或翻轴来修整体黑。advanced的法线槽蓝/透明通道用途不同，须重打包。
- H1“没PaletteSlot/TintMaskColor就全局乘零”没有依据：不同parent的参数合同不同，basic+也没有这两个变量却被明确标注UI可用。不要给basic任意追加别的parent变量。

参考 advanced 具有实机先例，但不能直接当作依赖完备的模板：子核查发现其11槽中只有2/3/5三张图在包内，其余8个ID在现有游戏索引也未命中；可能是旧依赖或不参与当前分支，不可武断。参考整套模型还同时使用另一个自定义parent。优先使用当前 SDK 干净模板与可解析依赖。

参考的可见主流与我们的56-byte分量布局相同，故当前没有理由为材质先切60-byte原版ABI。§3.5的“3组bone_index=12影响”仍应撤回：必须结合真实权重/索引用途解读，不能只数索引字段。

补充来源（制作约定，不冒充游戏shader源码）：
- https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/materials/basic.html
- https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/materials/advanced.html
- SDK `__init__.py:1428` basic预览、`:1478` normal、`:1508` advanced粗糙度、`:1516` 发光连接。

### 6.4 小臂：权重确有问题，但“腕端非单调”的证据混合了不同壳层

原 `_probe_forearm.py:95` 只做距离骨轴<10cm及轴向范围筛选，未分皮肤/袖子/附件。TransformInfo 是绑定世界矩阵，不是采集的 idle 动画。主导骨统计也不是可靠的部件身份标注。

独立探针在冻结拓扑上求连通分量，使用相同固定顶点选区，得到左右各49个至少10点的候选连通块。连通块仍不是语义标签，UV缝可能将一层分裂成多个块，合并壳层须结合材质、位置和相邻关系确认，不能只依赖组件编号。

443点手臂壳（本次连通编号左1968/右1975，仅作为本次拓扑的诊断种子）：

| 沿肘→腕位置 t=s/L | 样本/侧 | 平均hand | 平均elbow |
|---|---:|---:|---:|
| 0.25–0.70（中段） | 115 | 0.630269 | 0.368576 |
| 0.70–0.90 | 49 | 0.980887 | 0.019113 |
| 0.90–1.00 | 20 | 1.000000 | 0.000000 |

其它大型连通块中段也出现hand≈0.64–0.70。反而原表“腕端退回hand≈0.4”的区域混入大量小附件块，不能描述为同一皮肤层从0.94突然掉回0.4。

**实证成立的是：多块小臂中段过多跟随手骨；不成立的是：所有腕端都缺少hand权重、应该统一抬到0.85以上。** 当前443点壳腕端已经hand=1，继续增加腕端hand无法修中段。

离线动作对照（不是游戏采样）：

- 固定肘和上游，仅让手腕及全部后代绕腕部横轴旋转30°，使用实际包权重做线性蒙皮。
- 443点壳中段位移中位数：左 **52.03mm**、右 **52.10mm**；最大约63mm。
- 固定小臂中段选区内，将手骨子树权重转给elbow作极限消融，区外权重完全不变且总和保持1；中段在该孤立腕部动作下位移为 **0**。
- 恢复旧权重作负对照，同一中段的最大位移左63.28mm/右63.34mm，检查明确失败。

这证明当前权重足以制造明显的腕部牵扯，也支持局部调整。极限消融有硬边界，**不是可发布的新权重**，没有因此宣称肘腕接缝或实际握枪已修好。

### 6.5 权重来源：当前发布的是源权重映射，不是参考表面权重转移

追到实际生产路径：

1. `hd2_style.py:108–114` 将 Source Forearm→HD2 elbow、Hand→HD2 hand。
2. `resolve_bone_map():486` 处理未映射源骨，采用骨身份/祖先/区域内关节距离回退；这是骨映射，不是参考三角面权重插值。
3. `dump_blender.py:214–259` 逐顶点取原始 `v['w']` 和 `v['bones']`，按 `r['resolved']` 换骨名、合并同骨、量化，然后写入 `w_bone/w_val`。
4. `build_patch.py:166–175` 读取冻结NPZ，`:331–340` 把3个权重槽复制到GPU half4；本轮已确认实际Slim主流语义权重与NPZ逐项相等。

**因此不要再称“参考权重已经刷好了”。** 此前六区/附件/可信三角面转移是实施建议，当前路径没有完成这项工作。摆姿中的手部空间裁切、轴向旋转只影响几何选区，不能自动修正运行时 `w_bone/w_val`。

更细的来源仍待主线程追踪：小臂影响中多少直接继承源 Hand，多少来自辅助骨映射到Hand，需要输出每个源骨→目标骨的权重质量统计。现有数据不允许把所有责任归给某一个回退分支。

### 6.6 小臂的最小可实现修复（冻结几何，仅改 runtime 权重）

首版采用**小臂主体跟elbow、肘腕局部渐变**是合理的，但它是待动作检验的起点，不能把“2–3cm”“hand≥0.85”写成所有模型的生理定律。

实施步骤：

1. 从已经验收的 `model.npz` 读取最终顶点、骨架及源运行时权重，另存候选。不要用新权重再跑T→A摆姿。
2. 先固定左/右小臂、袖子、袖口附件、掌部/手指的语义选区；使用几何和拓扑定位，原权重只作辅助。禁止以hand主导来界定“手”，禁止在全身圆柱内直接改权重。
3. 只在确认的小臂皮肤/贴身衣物上压低中段hand；移除的质量归同侧elbow。可先做中段hand=0的稳定基线，必要时再比较少量hand版本，但不得保留指骨影响来补偿腕扭。
4. 在腕缝附近恢复与已接受掌部边界一致的权重；真正的hand侧应跟hand，小臂侧不必在s>.9L一律跟hand。肘侧保留现有可用shoulder↔elbow过渡。手指、手掌内部权重不变。
5. 宽袖/腕带/饰件单独决定跟随位置：刚性饰件可按附着点固定比例，布料沿附着表面平滑，不要对每个高密度装饰点套全轴相同斜坡。多个贴合壳的对应缝环须同步，防止层间分离。
6. 只允许在这些局部过渡邻域内平滑；限制平滑跨壳、跨指、跨侧传播；处理UV缝/重复顶点的权重一致性。
7. 候选完成后合并同骨、保留有效影响、half4量化，再检查打包回读；不要用量化步骤来代替权重设计。

可采用的标量候选公式（仅适用于已确认的小臂皮肤/贴身衣物；掩码固定）：

```text
s = dot(p - elbow, normalize(hand - elbow))
H(s) = smoothstep(a_w, b_w, s)   # 仅腕缝的窄邻域，端点与掌侧边界匹配
S(s) = 1 - smoothstep(a_e, b_e, s)  # 肘缝邻域，与大臂边界匹配
w_shoulder = S
w_hand = (1-S) * H
w_elbow = (1-S) * (1-H)
```

三项和为1。不要把这条示意公式套到指根、装饰或整条上臂；如肘侧已经良好，应直接保留原肘侧权重，在更远的小臂内渐变至新主体。

**扭转注意：** “twist骨当前没有权重”只能排除误绑这些骨，不能证明省略twist一定正确。前臂旋前/旋后可能需要扭转分摊；但单纯把hand混到整条小臂会把手腕屈伸也带进去。先测原生elbow/hand/twist的真实动画变换与参考权重，再决定是否使用已有twist；不要凭名字加骨。纯刚性首版可能造成腕缝扭转集中，需验收。

### 6.7 主线程验收与交付顺序

**离线自动检查（助手负责）：**

- 候选网格顶点/拓扑/UV/骨架/绑定矩阵与已通过基线一致；只改允许的局部权重或材质资源。更新哈希、输入来源、修改顶点清单；保留原故障包及所有依赖。
- 权重改动掩码外逐项相同，掌指内部、身体/头腿保持不变；每行有限、非负、骨身份合法、合并同骨且编码后闭合；两个体型与所有可见LOD读回检查。
- 动作应至少分别测：腕部屈伸、腕部侧偏、前臂轴向扭转、肘部屈伸、组合抬臂/握枪。转肘时hand和指链作为后代一同传播，不得把手钉在世界坐标制造假拉伸。
- 固定小臂选区，按皮肤/袖子/附件分开输出同视角正侧轮廓；比较中线、截面和缝隙。边长比/权重曲线只能作为辅助，不能代替形状验收。
- 使用实际SDK反导入最终候选包，自动检查材质ID/贴图槽、骨权重和形状。SDK预览节点不等于游戏shader；最终UI显示必须实测。
- 每项回归保留负对照：旧权重应触发腕部牵扯检测；旧交错写法应触发SDK贴图引用检测。不要只做“自己写、自己读”的闭环。

**游戏验收（用户只负责看结果）：** 先材质候选确认场景内颜色/光泽，再军械库和出击准备UI；随后权重候选复测走路、持枪/瞄准、换弹及最易出现Q弹的动作；合并后快速回归可见性、影子和两体型。报告清楚区分“离线候选通过”和“实机已验收”。

若M3颜色正常但UI仍缺失，再检查预览路径/LOD/特殊cutout；不要为了猜UI原因先推翻已经通过战斗场景的骨架与布局。若小臂不再弯但腕缝扭转难看，再处理twist分布，避免一次盲改整条手臂。

**本次交付是经验证的根因定位与修复指导，尚未生成新的游戏候选包，也未宣称两个问题已修好。**

---

## 7. 主线程执行记录（2026-10-13，按 §6 执行）

详见 **`tmp/rig-lab/ROUND60_FIX_REPORT.md`**。摘要：

- **§6.2 P0 已修并验证**：`hd2_asset.Material` 的贴图表由"交错 (u32,u64)"改为
  "N×u32 键 + N×u64 ID（ID 起点 136+4N）"；`set_texture` 改为严格；
  负对照改用**冻结的** `_legacy_matwriter.py`，不再用待修模块自证。
  5 个 SDK 模板逐字节往返一致；部署包 28/42 未解析引用 → 候选 **0/42**。
- **候选 M1 / M2 / M3** 全部离线闭合（SDK 交叉读 + 端到端材质链 + 27 项结构审计）。
  **M1 已部署**（`E:/mod/Lunalice-RE2310-M1/`，sha256 `fdcd6c7c…`，563900 B，结构与旧包同尺寸）。
- **§6.4–6.6 小臂已重绑**：先按壳层/材质做 ROI 分离，确认皮肤是"肘→腕单调斜坡"（无刚性段）；
  `_probe_handrigid.py` 推翻了"按 hand≥0.95 排除手掌"的假设（同连通块、半径重叠，会撕皮肤）。
  最终规则 = 斜坡 `smoothstep(0.78L, 0.92L)` + 刚性块取自身附着点单一比例。
  离线 30° 腕转中段位移 **52.1 mm → 0.0 mm**；只改 unit GPU 的 0.244% 字节，两条模型流都改到。
  **W1 尚未部署**（按 §6.7 先材质后权重）。
- **过程中修掉自己的一个 bug**：第一版把权重写到"流内偏移"而非 `vertex_buffer_offset`，
  只有流 0 生效——由 `_probe_weightdiff.py` 的逐字节比对抓出。
- 全部结论均为**离线**；未声称实机验收。只本地提交，未推送。
