# 项目长期记忆：HD2ModManager

> 只留**结论、硬约束、易忘的坑**。细节在 `docs/*.md`（**被 .gitignore，改前先备份到 `docs/_backup/`**）与 `.workbuddy-ai/memory/YYYY-MM-DD.md`（历史全文在 `_backup/`）。

## 项目定位
- 仓库 `WpfApp1`：《绝地潜兵 2》Mod 管理器与补丁适配工具集，GPLv3。`ManagedMain` 已过时。生产工程：`HD2ModManager`(WPF/net10)、`HD2ModCli`(net10，`hd2mod`)、`HD2ModCore`(net8)、`HD2ModAdaptation`(net8)、`Decryptor`、`HD2GameItemAssets` + `*.Tests`；依赖链 `Cli → Core → {Adaptation, GameItemAssets, Decryptor}`。
- `HD2RuntimeArmorOverride` = 护甲运行时映射的 Lua mod 源码（非 csproj，内嵌为 EmbeddedResource）。`CoreServices` 是**唯一装配点**（判"服务是否被生产使用" = `CoreServices.CreateXxx(` 调用点是否为 0）。

## 工作约定（硬约束）
- **【强制】只做本地提交，不推送远程**（用户手动推送）。**授权范围内的改动直接做，不反复请示。**
- **用户不是专业程序员**：涉及"要不要删/要做什么"，先用通俗语言说明作用与后果，再给建议。
- **临时脚本一律 `tmp/`（已 gitignore），不要写进 `_diagnostics/`**（后者是条件测试目录，一旦存在哪怕空的就会真跑并失败）。**`tmp/rig-lab/` = 本功能的离线「斧」**（`*.py` 已入库，`out/` 忽略）。
- **写完"应能抓住某 bug"的用例后，主动破坏被保护的那一行再跑**；仍通过 = 用例是假的。删除类改动**必须同时改造其测试**。
- **`tools/check_dead_code.py` 是词匹配**（注释里写类名会误判可达）→ **必须两方向一起看**（`comm -13` + `comm -23`）。
- `Directory.Build.props`：`ImplicitUsings`/`Nullable`/`Deterministic` 开、**`TreatWarningsAsErrors=true`**（排除 `NU1901..1904`）。TargetFramework/OutputType/引用**刻意不在此文件**。
- 耗时：全解决方案 `dotnet test` **6–7 分钟**；`dotnet build -t:Rebuild` **20 秒**。**报测试计数要分项+增量；基线会变，报数前先跑一遍。**
- 发布：**轻量标签**；溯源靠包内 `cliVersion`（`1.0.0+<git 全哈希>`）；校验产物**别用 PowerShell `Add-Type`**（被拦），用 Python `zipfile`。
- **【数据归属·硬约束】用户自定义数据一律存**模组库**；设置页简单设置存 `settings.json`；**能从游戏 data 重算的纯缓存**才放管理器目录。目标：**换管理器 = 无痛继承模组库；迁移模组库 = 只改库地址即可。** ⚠️ 2026-10-11 护甲页崩溃的根因就是违反了这条。**新增持久化文件前先问：属于库、属于设置、还是纯缓存？**
- **【技能·必须用】`mesh-deform-diagnosis`**（`~/.workbuddy-ai/skills/`）：**判形状不要只看边长比 / p99 / bad%**（25 cm 肢体弯 15°，每条边只被拉 0.3%）。三板斧：中线（`flips==0` 才是直）+ 单项消融 + **先用参照校准方法**。⚠️ **多层壳模型须先「只渲染目标层」**。
- **【发图前必须自己先用肉眼过一遍】**（用户 2026-10-12 严厉指出：曾发出"前后完全一样"的对比图，浪费一轮）。
- `git commit` 提交**整个索引**，与 `git add` 传了哪些路径无关。
- **【环境】** ① Python 用隔离 venv `...\python\envs\default\Scripts\python.exe`（有 numpy+Pillow；**系统 python 无 PIL**；**无 scipy**）；② `git clone`/`curl` 打 GitHub 常失败 → 让用户下 zip；③ **Bash heredoc 调 Python/PowerShell 常被拦**（SIGTERM）→ 写成 `tmp/*.py` 再执行；④ **`Edit` 对含大量反引号/emoji 的长中文行会匹配失败** → 用 Python `str.replace` 并回读校验；⑤ **`Remove-Item` 被 safe-delete 钩子接管** → 让用户自己跑，**不要**用 `[System.IO.File]::Delete` 绕过；⑥ Blender = `D:\SteamLibrary\steamapps\common\Blender\blender.exe`（4.3.2）。

## 大功能 B（当前方向）：L4D2 模型整体移植到 HD2
> 细节：`docs/hd2-batchtool-sdk-findings.md`（§8 管线 / §9 三个真 bug / §12 骨轴往返 / §13 发布骨架 / §14 枢轴解耦 / §15 网格平移 / §17 躯干缩放 / §18 区域兜底 / §19–20 手臂位置 / §21–23 手臂方向与段内刚体）、`docs/l4d2-to-hd2-physbone-migration.md`、`docs/l4d2-hd2-boundary.md`。

### 目标与判据（第 39 轮，最高优先级）
- **不是"一键转换"黑盒，而是"录一个人工操作流的宏"**：① 把源 T-pose 模型摆成参照的站姿/比例（**纯数学变换**，只借 BatchTool 的变换部分）；② 按参照风格重刷权重；③ 全自动 legacy 流程。
- **骨架与模型没有直接联系，骨架只和权重有关（间接影响模型）。** 参照 patch 骨架**一根骨没动**（= HD2 原生），"比例优化"全在**网格层**。
- **【渲染判据·最重要】bind 恒等会掩盖真问题**：绑定态网格永不变形、指标全漂亮，**哪怕对应表是错的**。只有 `A[b]·inv(S_bind[b])` 的渲染（`out_idle`）能与参照 patch 直接对比。

### 当前管线（`tmp/rig-lab/hd2_style.py`）—— 必用组合
```
--align yaw --spine-map mesh --fallback region --leg-tilt 5.7 --leg-splay 4.0
--mesh-down 0.090 --mesh-back 0.100 --upper-scale 1.12 --upper-pivot-z 1.15
--upper-blend 0.45 --head-scale 1.1429 --arm-elbow-open 6.5
```
🔴 **`arm_scratch=True` 是默认、且不在 CLI 上** ⇒ `arm_fit` / `arm_up` / `arm_perp` / `arm_taper` /
`arm_rot` / `arm_rot_smooth` / `arm_rot_mode` 那**整块（§19–23）是死代码**（消费条件带
`and not arm_scratch`，见 L1179 / L1306）。`HD2_ARM_ROT` 等环境变量同理无效。
`arm_scratch=True` 下活跃的手臂机制**只有两个**：① `arm_scratch_upper_gain`（上臂沿源肩→肘轴
+5 cm 拉伸，§3b）；② **§4 的肩/肘 min-arc**（`arm_elbow_open` 就加在这里）。
**真相在 `dump_blender.py` 的常量**：`MESH_SHIFT / HEAD_SCALE / ARM_ELBOW_OPEN / ARM_UPPER_GAIN`。
⚠️ `probe_invariants.py` 已同步到当前管线（之前还写着 −0.060）。

### 不变量（每次改完必跑 `probe_invariants.py`，不得回退）
```
mesh down 0.0900 back 0.1000 | upper x1.120 pivot z1.150 blend 0.45
head scale x1.1429 | arm upper gain +0.0500 | elbow open +6.50
our zmin -0.0912 height 2.1207 | ref zmin -0.0542 height 2.1127
idle vs bind 6.38e-16 m | shipped rig vs HD2 7.22e-16 m   <- 这两条是硬不变量，必须恒为 0
edge mean 1.0955 p99 1.2826 max 1.84 bad>25% 19.75%
   ⚠️ bad>25% 已**不再是变形判据**：upper_scale 1.12 + head_scale 1.143 是**刻意**缩放，
   按设计就会拉伸边。要判变形请用 Kabsch 刚体拟合残差 / 参照校准（见 mesh-deform-diagnosis）。
```

### 机制索引（全部已实测，勿回退）
- **§4c 手腕（第 53 轮·活跃）**：`arm_hand_tilt`（CLI `--arm-hand-tilt`，**正 = 手腕伸展 = 手掌翘起**）。**T-pose 手部帧实测**（`_probe_hand_tpose.py`）：**z 钢管轴 = ±X**、**x 拇指 = +Y（正好！）**、**y = z×x = ∓Z**；两臂拇指都 +Y ⇒ 双掌旋前（掌心向下）⇒ 手背 = 左 +Z / 右 −Z ⇒ **伸展 = 左 +θ 绕 +Y、右 −θ 绕 +Y**（镜像）。注入 `basis[l_hand]`（排在肩/肘之后，手指刚性跟随）。**+10° 实测**：手长轴 10.00°、Kabsch 9.95° 绕 `R_sh·R_el·ŷ`（=搬过去的拇指轴）✓。🔴 **第 53b 轮度量复核：三个指标全都反对这个角度，而且反对得很干脆** —— 掌面法线 vs 参照：**tilt 0 = 3.50°**、−7 = 6.67°、−10 = 9.40°、+7 = 8.92°、**+10 = 11.77°**；对称最近邻（`_diag_hand_tilt2.py`）同向：ours→ref 中位 0.0206(0) / 0.0167(−10) / 0.0369(+10)，ref→ours 中位 0.0078(0) / 0.0112(−10) / 0.0159(+10)。⇒ **tilt 0 的掌面就已经和参照差 3.50°，加角度只会更远；用户指定的 +10 在三项里都是最差。** 所以 **10° 是用户目测值，非拟合值**；若仍对不上，**下一嫌疑不是手腕 roll，而是手指或平移**（用户原话"像是参照的手掌放在我们的正上方"本来就是平移描述）。⚠️ 参照手的 PCA 长轴读数（差 20.77°）是**假的**：若真刚性转 20.8° 掌面法线也必须转 20.8°，实测只差 3.50° ⇒ **在被自己的过滤器切过的部分点云上做 PCA 长轴不可信，只有平面法线能用**。
- **§4d 手指（第 54 轮·当前主线）**：🔴 **根因 = 源模型是「手指摊平的 T-pose」，HD2 原生手是「放松垂下的 A-pose」，差 ~45°；管线把手当前臂的刚性延长件（§4c）⇒ 手指继承了源模型的摊平姿势。** 实测：我们手指轴向 vs HD2 指骨 **38.3–50.0°（均值 44.7°）**；vs 参照 **36.4–48.8°（均值 39.5°）**；**参照 vs 指骨只有 7–13°（参照基本贴在骨头上）**；我们 5 指轴向**几乎互相平行**（食指 vs 小指只差 2.6°）而 HD2 与参照都扇形张开。**最致命**：**HD2 指端关节到我们的手指网格 34.7–48.3 mm，到参照的只有 4.2–7.1 mm** —— 运行时游戏**绕 HD2 关节**转网格，关节在手指外 ⇒ 手指绕**空气**甩。⇒ **修法不只是"看起来对"，必须让手指"长在 HD2 关节链上"。** 一个整体旋转（Kabsch over 5 指）**46.4° 绕 (0.574,0.595,0.563)** 能把 44.7° 降到 7.5°（残差 1.3–13.5°）⇒ 主体是"整体姿势差"，剩下 7.5° 才是逐指差异。前提已确认：骨架**同构**（`export_obj.py: FINGERS`，5 指×3 节×2 侧 = 30 对）、网格**确实蒙皮在指骨上**（被任意指骨触及 **4696/121704 = 3.9%**，每根指骨主导独立 bbox）。方案与问题见 `tmp/rig-lab/FINGER_RETARGET_BRIEF.md`。
- **§8 主干**：一个均匀缩放 + 一个平移（围绕脚底）+ **HD2 骨 head := 源映射骨世界位置** + 30 骨常量旋转表 + 20 条右侧骨轴 delta。**吸附在绑定之前 ⇒ 绑定态恒等。** ⚠️ 高度沿 HD2 **+Z** 量（脚底 = 底部 1 cm 顶点片质心）；**AXIS delta 与 head 搬移必须交错在同一"父先子后"循环**。
- **§12/§13/§14 发布骨架**：`skeleton='native'`（默认）⇒ **骨架 = HD2 原生逐位不变**（`S_bind = rest_hd2` ⇒ **idle 网格差 0**），网格 = 源模型的 A-pose。**枢轴必须解耦**（网格烘焙永远用吸附后的 `rest_sym`）。
- **§15/§17 网格平移/缩放**：`mesh_shift` 只平移烘焙后的 `out`（−9.0/−10.0 cm）；`upper_scale/pivot_z/blend`(1.12/1.15/0.45) = 只动网格的 smoothstep 缩放。**骨架绝不移动。**
- **§18 区域兜底**：`fallback='region'` ⇒ **距离 + 同肢体约束**。**⚠️ 不能用「祖先的子树」**（`hips` 与 `spine1` 是 `boss` 下的**兄弟**）。
- **§19/§20 手臂位置**：`arm_fit`(1.0)/`arm_up`(0.55)/`arm_perp`(0)/`arm_smooth`(8)/`arm_taper`(−0.05,0.22)，逐骨残差**按 LBS 权重混合**、只动网格 + Laplacian。**平移修位置、旋转修方向。** **`arm_taper` 必须开**（否则腋窝裂开）。
- **§4b 肘铰链（第 52 轮·活跃）**：`arm_elbow_open`（CLI `--arm-elbow-open`，**正数 = 掰直**，弯曲角**精确减少**该度数）。**实测**：`R_el` 的轴 = `d_up_tgt × d_fo_tgt` 的平面法线，**完全同轴**（夹角 0.000°，`|dot|=1.00000`）；左臂 `R_el = 39.64° 绕 (0,0.02,−1.000)`（**≈竖直**，所以效果在**俯视图**可见）。实现 = 在 `R_el = min_arc_rotation(...)` 前把 `d_fo_tgt` 在平面内转。**+6.5° 实测**：前臂俯视前倾角 **45.96° → 39.48°**，参照 **39.43°**（误差 6.49° → **0.96°**）；身体位移 ≤ 0.09 mm、手+指刚性跟随（Kabsch 残差 ≤ 1.2 mm）；`out_idle == out` 仍成立。
- **§21–23 手臂方向（⚠️ 当前死代码，见上）**：`arm_rot`(**10.46/−18.55/29.63**)，修正轴 = **绕世界 Y**；三段嵌套、**最内层（腕）先做**、每层用**原始骨点**当枢轴。**g 用「沿臂轴位置斜坡 × 手臂掩码」，不能用 LBS 权重** —— 模型是**一叠壳**，按权重转会撕开。**肩部也要 `ramp(0)`**。
- **§23 `rigid`（45e-2 已实现）**：二值 mask + **1.5 cm 窄过渡** + 按角度插值 `J + R(gθ)(v−J)`。Kabsch 残差 **上臂 0.30/1.01 mm、前臂 0.86/2.03 mm** ⇒ 段内真刚体。**代价是零和**：18.5° 掰直量守恒 ⇒ 窄过渡把 **18.4 mm 集中在肘部**，宽过渡摊开但**段内就弯**。`HD2_ARM_GAP`（0.015/0.030/0.050）**待拍板**。
- **⚠️ 判据只能用「固定顶点集的 Kabsch 刚体拟合残差」**（选择器限制到**手臂权重顶点**）。**顶点域切片质心**（±15 mm 噪声 + 斜切伪影）与**像素域中线**（手臂 BOX 混多壳，参照自己都 flips 5）**都不可用**。
- `--leg-tilt`(5.7) 绕踝转 thigh/knee、foot/ball/toe 钉住；`--leg-splay`(4.0) 绕腿根绕 +Y 转 ±θ、末端反向（**侧视逐点不变**）。⚠️ 腿倾段网格变换**必须**用 `resolve_bone_map(...)` 共用同一份映射。

### 历史 bug（勿回退）
- **§9 三个真 bug**：① `--align kabsch` 把身体转歪 2.8° ⇒ **`yaw`**；② `Spine→spine1` 让躯干每帧拉伸 26.5 cm ⇒ **`--spine-map mesh`**；③ 鞋被绑到膝盖 ⇒ 穿地 13.5 cm ⇒ **`--fallback region`**。
- **§16**：旧阈值 0.0600 m 正好切在边长 **p99** ⇒ **误删 510 顶点** ⇒ 改为 `cut = 0.85 × height`。**「数据里没有」≠「图里看不到」。**
- **【第 4 个真 bug·未修】脚落点前移 ~6 cm**：管线 `t` 只锁头关节。**X 那一半已被 `--leg-splay` 顺手修掉**；**Y 那一半仍未动**。修法 = XY 锚点改成"脚底贴片"。
- 头/尺寸（已降级）：源 `Head1` 是**颅底**、HD2 `head` 是**颅顶**；参照头部几何在 `head` **上** 0.368 m、我们 0.293 m ⇒ 我们小 ~20%（非阻塞）。
- **⚠️ 探针踩坑**：① 改网格的变换**作用于枢轴以上所有顶点**；② **bind 恒等掩盖真问题**；③ 分段窗口若按当前几何重算 ⇒ 数字乱跳；④ **"沿某条轴的位置"要先问这条轴指向哪、还有谁会投影到它上面**（`t` 是肩→手且朝下，脚投影到 t=+0.6）；⑤ 改动"按蒙皮权重"的旋转后**必须跑壳分离度量** —— 边长比/半径剖面/绑定态网格差**都看不见"壳与壳错开"**；⑥ `blender_render.py` **漏 `.blend` 会静默渲空场景**；⑦ 🔴 **`rest_sym` ≠ `rest_shipped`**：前者是**吸附后的源 T-pose**骨架（手腕 z **1.5701**），后者是 **HD2 原生 A-pose**（手腕 z **1.2189**）。**判网格几何一律用 `rest_shipped`**；用 `rest_sym` 框选已处于 A-pose 的网格会**选中 0 个点**（第 53 轮画图踩过）。判枢轴/变换则用 `rest_sym`。⑨ 🔴 **"某块几何"的顶点集不能按主导骨取**：`is_hand`（主导骨 ∈ `l_hand` 子树）给出 3933 顶点，但**半径 0.204 m** —— 把**前臂**也吞了，而前臂**根本不动**，只会把指标稀释。改按**位移**取 `|P(+tilt) − P(0)| > 20% × max`（max 3.51 cm）；但 **① 管线左右对称一起转** ⇒ 不限定 `x<0` 会拿到**两只手**（11126 顶点、半径 **1.435 m**）；**② 只限定左半仍留肘部**（5574 顶点、半径 0.308 m、y 到 −0.072，而肘在 y = −0.054）⇒ 最终 = 位移阈值 **+ `x<0` + `|P−wrist|<0.20`**。
⑩ 🔴 **画图的 `blob()` 忘了传轴索引**：`_diag_hand_tilt2.py` / `_diag_fingers.py` 里
`blob(f, p, col)` 内部写死 `f(p[0], p[1])` ⇒ **三个面板全画 XY 投影**，FRONT/SIDE 看起来
"没有点云"（其实是被裁掉了）。**已修**（改成传 `ia, ib`）。⚠️ 上一轮那张手掌图就少了两块。
⑪ 🔴 **"一块几何"的顶点集不能按主导骨取**：`is_hand`（主导骨 ∈ `l_hand` 子树）= 3933 顶点，
但**半径 0.204 m**，把**前臂**也吞了（前臂根本不动 ⇒ 稀释指标）。改按**位移**取
`|P(+t)| − P(0)| > 20% × max`；但**管线左右对称一起转** ⇒ 不限定 `x<0` 会拿到**两只手**
（11126 顶点、半径 1.435 m）；只限定左半仍留肘部（5574 顶点、半径 0.308 m）。
⑧ 🔴 **闭包变量会被覆盖**：`retarget` 里 `leg_tilt` 块写 `c, s = cos/sin`，而 `s` 是 `align_pts` 用的**均匀缩放系数** ⇒ 之后所有 `joint()` 被缩小 ~0.11 倍（日志 pivot 打到踝高；对网格无害，因为只用于取方向且 `resolve_bone_map` 只比距离）。**已修（改名 `ct/st`）。教训：给闭包里的单字母变量赋值前先 grep。**

### 待办（网格变换；**用户要求一步一步来**）
1. ✅ 整体平移；1.5 ✅ 完整性；2. ✅ 上半身放大；2.5 ✅ 区域兜底；**4. ✅ 手臂**（肩 min-arc 30.0° / 肘 min-arc 33.1° 含 `arm_elbow_open=6.5` / 腕 `arm_hand_tilt=10°`，用户已验收）；**7. ◐ 手指（当前主线，见 §4d）** —— 已定位根因、已出专家简报 `FINGER_RETARGET_BRIEF.md`，**等专家回复 Q1–Q6 再动手**；原第 4 项剩下的部分是 （45 `arm_fit` → 45b `arm_up`+`arm_taper` → 45c `arm_rot` → 45d `axis` → 45e 锁定小臂歪根因 → **45e-2 ✅ `rigid` 段内刚体 → 待拍板 `HD2_ARM_GAP`**）；3. **⏸ 接缝渐变缩放（用户拍板：暂缓）**；5. **头部比例**；6. **下半身放大**。
> **分工**：用户**空间感强、善于发现问题**但**对距离/数值不敏感**；我**视觉空间判断弱**但**数值/拟合可靠** ⇒ 用户"指出哪里不对"，我"用数值定出多少"。
- 后续：提取干净网格 → 按参照风格重刷权重 → 上 C#（`HD2SourceMigration`）→ 出离线 legacy 版 patch 包 → 进游戏实测（用户负责）。**动态物理骨骼仍要支持**；**硬要求：没有 Lua 支持 legacy 包也必须能跑**；**不依赖 `HD2IRG1` 的动态骨骼**。
- ⚠️ 四个未决项：① **nick 过冲**（+0.189）；② **俯视图漂 2–4°**；③ 若腋窝缝再现 ⇒ 下一嫌疑是 **A-pose 烘焙在肩部的旋转**；④ **手臂 twist（绕骨轴 ≤90°）+ 段内均匀拉伸**（用户 45d 提出，未实现）。

## 目录卫生（2026-10-12 清理）
- `tmp/rig-lab/`：**250 → 25 个 .py**。236 个一次性脚本移到 `tmp/rig-lab/_archive/`
  （**移动不是删除** —— 其中 166 个是 git 跟踪的；`tmp/` 下 `_archive/` 被 gitignore）。
- **保留集**：核心 9（`hd2_style` / `export_obj` / `patch_view` / `dump_blender` /
  `blender_build|render|verify` / `probe_invariants` / `hd2_patch`）+ 手指 6 + 腕 4 +
  肘 3 + 骨架 2 + `probe_layered_render.py`（分层壳诊断，判据工具）。
- ⚠️ **`probe_arm3.py` 清理前就已损坏**（`import silhouette_centreline`，该模块**全仓库不存在**）。
- 保留脚本的本地依赖只有 `export_obj` / `hd2_style` / `patch_view` —— 改动前先核一遍。

## 坐标约定 & 参照 patch
- **HD2 坐标：+Y = 前，+Z = 上，+X = 角色的「右」**（左侧在 −X）。⚠️ 早期笔记"+X = 左"是**笔误**；实测 `l_foot` x = **−0.1908**。**`l_foot` 关节不在脚心**：HD2 原生踝在髋后 9.60 cm。
- 关键关节（`rest_hd2`，与参照 patch 逐位一致）：`hips` z **0.9735**；`spine2` 1.1633；`chest` 1.3437；`neck` **1.5500**；`head` 1.6908；`l_shoulder` x −0.2101 z 1.4745；`l_elbow` x −0.4723 z 1.3236；`l_hand` x −0.6566 z 1.2189。`l_shoulder→l_hand` 骨长 **0.5553 m**（沿轴 t：elbow 0.286 / hand 0.555 / 指尖 ≈0.7505）。**HD2 原生 rest：上臂 29.9° / 小臂 29.6° 下垂 ⇒ 骨本身近似共线**；**参照网格：28.5° / 24.9°（折角 3.6°）**。
- 参照 patch = `E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310`（E 盘 = 用户**测试**库；D 盘 steam = **实际在用**）。容器 `9ba626afa44a3aa3.patch_0` + `.gpu_resources`，**无 manifest.json**；35 条目、**只 9 个 Unit 有几何**。对比 5 个：`cae35ee15168145f` 上衣、`17da843a4908511e` Hip、`91eb7be84016ceab`/`7336c6b662522f0e` 左右臂、`af431352d9b95e83` **RE-2310 头盔 = 脸+猫耳+长发**（容器里**没有 unit 叫 helmet**）。加头后完整参照 **z[−0.054,+2.059]、总高 2.113 m**。
- **【关键判据】补丁骨架与 HD2 原生逐位一致**（13 根骨 delta 全 0）⇒ **"比例优化"全在网格层。** **读取三坑**：① **Unit 骨矩阵是行向量约定（平移 flat 12,13,14）**，与 `E.read_hd2_rig`（已转置）相反，读错**不报错**；② bone 列表**前 9 项是场景图节点**；③ **Unit 骨名是空的**，靠 32-bit hash 查 `bonehash`。

## 旧项目 / 其他（细节全在 `docs/`）
- **旧项目编译不过（预先存在）**，最后提交 2026-09-03；**用户已拍板在 WpfApp1 内重写。** `HD2SourceModel`(net8,零引用) + `HD2SourceMigration`(唯一同时引两侧) + `HD2ModCli` 薄子命令；矩阵统一 `System.Numerics.Matrix4x4`。**Source 格式权威在 `HD2SourceModel/*.cs` 注释。**
- **大功能 A（已被 B 取代）**：引擎 = Stingray(Bitsquid)，`Unit`/`Mesh` **零 `create_*` / 零顶点权重接口** ⇒ **运行时 Lua 改不了权重、加不了骨**。**改内存边界：改「数据」✅ / 改「代码」❌**。**【硬约束】只允许依赖游戏本体。**
- **信息中心 / 护甲页回归**：`ModInformationFactRegistry` = 25 kind 单一真相，`RequestAsync<T>` = 唯一读取入口；铁律 **缓存不得取代真实性**、**「读不到」≠「没有覆盖」**。护甲页 20261011 崩溃：兜底已落实、**根因未动**（`deployment` 守护 `protocol`）；**未修的破坏性回归**：`ModLibraryResidualCleanupService.Cleanup(...)` 递归删库根空子目录。
- **Blender 观察工具**：`dump_blender.py` → `blender_build.py` → `blender_render.py`；**骨轴**：两引擎沿局部 +X、Blender 沿 +Y ⇒ `head=rest_hd2[:3,3]`、`tail=head+L·rest_hd2[:3,0]`、`align_roll(rest_hd2[:3,2])`；骨系差一个全骨共用旋转 ⇒ 在 `A·inv(S)` 里抵消。⚠️ 相对 `filepath` 会解析到 `C:\out\` ⇒ 传绝对路径。
