# HD2ModManager 项目长期记忆

## 工作约定
- 仓库WpfApp1；只本地提交、不推送。用户非专业程序员，用通俗语言解释，用户负责视觉验收，助手负责数值与自动检查。用户要求先诊断指导时，不擅修主管线或部署。
- 临时脚本放tmp/，不放_diagnostics/。docs/忽略，改前备份docs/_backup/。不删除.workbuddy-ai。故障包与所有依赖必须留版本/hash，候选另存目录，不覆盖唯一测试证据。
- bug测试做破坏关键行负对照；删除代码同步改测试；dead-code词匹配需双向比较。提交包含整个索引，先检查范围。
- 自定义数据属模组库，简单配置属settings.json，可由游戏重算的缓存属管理器目录。
- Python隔离环境 C:/Users/Administrator/.workbuddy-ai/binaries/python/envs/default/Scripts/python.exe（numpy/Pillow/scipy）；Blender D:/SteamLibrary/steamapps/common/Blender/blender.exe。脚本放文件避免Bash heredoc；删除钩子不得绕过。
- rig-lab CWD=tmp/rig-lab，OUT='out/blender'在该目录下。输出用绝对路径。

## 工程
- 管理器WPF/net10、CLI net10；Core/Adaptation/GameItemAssets/SourceModel/SourceMigration net8。CoreServices唯一装配点，SourceModel零项目引用。
- Nullable/ImplicitUsings/Deterministic、警告当错误（除NU1901..1904）。全测试需实际跑并分项报告；发布轻量标签，cliVersion带完整提交hash。
- legacy包需无Lua可用。运行时没有创建网格/改权重接口，迁移离线做。

## 模型与诊断
- 2026-10-13：摆姿已全部验收；第60轮确认59轮修复后的模型/骨骼/影子正常，剩小臂动作与材质。保持最终网格，摆姿源权重与后续运行时权重分离。
- 参数以dump_blender.py为准：hand_tilt=3、finger_pose=True；详细参数/证据见当日日志和FINGER_RETARGET_BRIEF §10。
- 手指在最终网格逐节残余摆姿：Rl=min_arc(s,Q.T@t)，Q父链旋转；枢轴须经过post，末节取HD2局部+X轴。发布rest_hd2不变；rest_sym是工作枢轴。arm_scratch下旧开关可能无效。
- mesh-deform-diagnosis：局部标量/绑定恒等不证明形状；固定顶点选区、同视角、先分壳并校准参照、单项消融。手指弯曲不要求整指Kabsch为0；out_idle不是实际游戏帧。
- 顶点与工作点同步变换；轴/单位/阶段明确。绘图连线也须过屏幕f()。手部按主导骨会吞前臂，需腕前空间裁切。
- HD2 +X右、+Y前、+Z上，左肢在-X；米。Unit存行向量矩阵平移flat12..14；read_hd2_rig转列约定，hd2_patch近期去转置，调用者须核查。

## 权重路线（WEIGHT_TRANSFER_BRIEF §10）
- 六区body_region+附件身份；同侧同指可信三角面重心转移+置信度回退；不用全身最近顶点/更近骨当真值，不做全局ICP。交界留渐变。
- 长发独立识别再显式混head/neck/躯干，沿发束参数试a_tip=1/.7/.4；静态权重没有碰撞。先腰腹局部、附件，再手部动作，尽早legacy实机。
- hips/spine1是同枢轴兄弟，真实动画行为未知；比较G(t)@inv(B)，body_pose_profile不含它们。禁止整下半身hips→spine1；clavicle不因参照未用就删。
- 距离分位数不是误配率；共享骨集合不证明平滑。边权重L1 p99=.615/max1.855、974边>1仅线索。head→neck23664点中z<1.40为0，不能全称长发。低垂868点竖轴转30°位移中位177.7/max245.6mm，非游戏实测。Slim/Stocky汇总一致不证明几何同。
- 第57轮已接weight_half4.py：先合并同骨再2048格点最大余数，Blender去round(w,3)。121704行half精确和1，几何未变。此为工具契约，不是已证引擎拒收条件；C#闭合仍待独立接入。

## Unit失败复核（以UNIT_TRANSFORM_BRIEF §10为准）
- 58轮9份全模型旧包被覆盖，Git无完整失败导出器。3545674首次提交修复脚本，但仍有未跟踪依赖/未提交修改，不能整仓回滚恢复旧包。
- 59轮现包hash前缀c7a9425dc5de；证据tmp/rig-lab/out/unit-review-evidence-c7a9425dc5de.zip；探针review_unit_contract.py。115节点/唯一hash，local×parent误差1.02e-7，全4LOD逆绑定8.81e-8通过。
- 确定遗漏：MeshInfo.unk2前7float是bounds，仍抄旧躯干；66290/121704点越界。实际z=-.091181..2.029500，记录=.997623..1.717627；两个culling亦旧bbox。不能据此断言透明唯一根因。代理culling可能是cutout，放大全身不一定安全。
- TransformInfo同hash复用/覆写avatar，parent重映射；SDK导入骨位置来自世界矩阵。根local为pos0/rotationI/scale1，非全零。
- mesh transform不必I，5/6/7/8仅旧donor。行向量源S/目标T/骨B：p_target=p_source*S*inv(T)，J=T*inv(B)。当前Python只支持I是简化限制。
- 用户所指“重建patch”是CanonicalSameKeyReconstructionService→SameKeyCanonicalUnitRebuilder，从GameData同AssetKey原版取框架，不是CrossArmor。版本只读TOC0x2c，当前0x00a4cd36。fresh新ID缺同键目标；不得伪造低版本绕入口。索引更新进程锁不得绕过。
- 目标ABI能承载则保留，不够compiler回退。当前56B声明与编码一致；原版60B不同不证明致命，但换模板不能继续固定56B打包。多组bone_index不自动证明12影响。
- fresh1资源/9piece/0mapping不等于单实例或无Lua。先一个原生载体；隐藏原槽若需额外占位unit须解释并尊重用户严格1unit要求。rev93运行历史active_mappings0，缺失声明不证明主动加载，不能倒推旧失败。
- 原52项自校验漏bbox、全骨全LOD、section区间、材质链；补独立负对照。SDK实际反导入自动检查骨/角色空间几何/关节动作，再无Lua固定装备冷启动实机；用户无需自己查权重。未验证前不称全部修好。

## Unit交付路线（第59轮下半，以UNIT_TRANSFORM_BRIEF §11为准）
- CLI `armor-normalize` **只认原生 Unit id**：`ModEquipmentSourceFactsReader.cs:184-186` 在 UnitPartMapping 下用 GameData 装备目录的 mappedUnitKeys 过滤，改名 id（d… 前缀）一律 armors:[]。⇒ 交付包必须用原生 id。
- 链路：`hd2mod library sync` 注册新包进 E:/mod（只动节点，先备份 library.json）→ `equipment armor-normalize inspect|plan|validate|apply`（产物只写库外目录）。
- `ArmorNormalizationService` 行57-61 自动隐藏整个 archive 的非目标 Unit（占位=CanonicalPlaceholderMinifier 3顶点0.0001m，culling壳保留）；`SelectPair` 要求 Slim+Stocky 成对，否则目标须 BodyVariant==Any（单 unit 只能落 Any）。
- CLI 逐字节保留我们的 unit（TocData/GPU），风险面只在新增占位 unit。实测：DS-10 1+21、RE-2310 2+20，产物 512788/563900 B。
- 我们 unit 两个 culling 壳 0xa620a54c(r=.457118)/0x800b8ea2(r=.243766) 与 RE-2310 躯干逐字节一致（DS-10 只1个）⇒ RE-2310 是结构最贴合载体。
- 独立审计 `tmp/rig-lab/_verify_normalized.py` 27项，RE-2310 包 26过/1已知：CLI 隐藏占位 unit 丢零索引 section 的材质短 id（3处，num_indices==0 惰性），原版同类悬空引用亦存在（culling 引 0x093fc934）⇒ 倾向不阻断，未改管理器。
- 部署：data/9ba626afa44a3aa3.patch_0[.gpu_resources] symlink → E:/mod/Lunalice-RE2310-Test/；回退目标 E:/tmp/deploy-revert-note.txt（→ Lunalice-RigTest）。待用户实机验收。

## 第60轮专家复核（以ROUND60_ISSUES_BRIEF §6为准）
- 用户实机确认模型/多数动作/影子/Blender骨架正常；只剩小臂发软与材质黑/UI缺失。当前包SHA前缀9c0cd8518278；本轮只诊断指导，未改生产/部署。
- **确定材质打包bug**：hd2_asset.py:121/152交错读写(key,id)，SDK实际136起N个u32键再N个u64 ID。14材质/16贴图中28底色/PBR引用错误，14法线正确。三ID正确偏移148/156/164，旧140/152/164；自读自写掩盖。C# StingrayMaterialReferenceReader正确。探针review_round60_contract.py直接调SDK，14份内存修复和旧写法负对照均通过，证据out/round60-independent-review.json。
- 材质路线：修表basic对照→PBR.B白AO→完整basic+（同三槽且明确UI支持）→必要时advanced。缺PaletteSlot乘零无证据；basic来源不证明不支持UI。advanced槽2=NAR(RG法线/B AO/A粗糙度)、3金属度、5底色/A发光，其余8槽依赖须查；不得只换parent。56B布局保留，多组bone_index不证明12影响。
- Slim主流0顶点/语义权重与冻结NPZ完全相同；源权重→骨映射→量化路径没有参考表面权重转移，摆姿空间裁切不改变runtime权重。
- 小臂10cm轴柱混壳，腕端hand≥.85一刀切撤回。443点壳左1968/右1975中段hand=.630269、腕端hand=1；总表腕端低值混附件。合成腕横轴30°中段位移中位52.03/52.10mm；固定中段手子树质量转elbow消融后0，旧权重最大63mm。非游戏帧、非发布修复。
- 局部主体elbow+肘腕真实缝渐变、附件分层、掌指保留；冻结几何另存runtime权重。twist未用只排除误绑，不能排除缺扭转支持；验收屈伸/侧偏/轴扭/组合动作及分壳形状。报告outputs/round60-independent-review.html。

## 第60轮下半（材质P0已修+已部署M1；小臂W1离线就绪）
- **材质P0根因**：`hd2_asset.Material` 贴图表读成 N 条交错 (u32 key,u64 id)。权威=SDK `stingray/material.py:32-33`（N×u32键后接N×u64 ID）+ `StingrayMaterialReferenceReader.cs:23`（ID起点 136+4N）。三槽应 148/156/164，旧读 140/152/164 ⇒ 只有法线对 ⇒ 底色/PBR坏 ⇒ 黑+金属光泽。**自读自写检不出**，负对照必须用冻结旧实现（`_legacy_matwriter.py`）。
- 已改：hd2_asset read/write 两段式；set_texture 严格（越界抛错，不追加零键）；build_patch 新增 `--template basic|basic+|advanced`、`--pbr-b`，材质一律从干净SDK模板重建。
- 校验工具（长期保留）：`_probe_matfix.py`(SDK权威)、`_probe_matchain.py`(Unit section→材质→贴图端到端)、`_probe_canddiff.py`、`_probe_weightdiff.py`(逐字节只允许权重字段)、`_probe_forearm_roi.py`/`_probe_forearm_profile2.py`(分壳层剖面)、`_probe_handrigid.py`、`_probe_forearm_action.py`(30°腕转位移)、`fix_materials.py`、`reweight_forearm.py`、`deploy_candidate.py`。
- 候选：M1(修表,basic,B=0) / M2(+PBR.B=255) / M3(+basic+模板)；均 SDK闭合0失败、端到端PASS、27项审计1失败(=基线已知项)。**M1 已部署** → `E:/mod/Lunalice-RE2310-M1/`，sha256 fdcd6c7c…，563900/67992000B，回退记录 E:/tmp/deploy-revert-note.txt。
- **小臂根因**：皮肤 hand 权重是肘→腕单调斜坡 0.077→1.000，无刚性段。**不能按 hand≥0.95 排除**（与皮肤同连通块comp276/277、半径重叠，会撕皮肤）。规则=ROI距轴<10cm；连通块ROI内t跨度<60mm为刚性块取附着点单一比例；其余 hand 驱动到 smoothstep(0.78L,0.92L)，差值只在手骨子树↔同侧肘骨间搬运。30°腕转中段位移 52.1mm→0.0mm。只改unit GPU 0.244%字节，两条模型流都要改（`vertex_buffer_offset` 绝对基址，勿用流内偏移）。
- 待实机：①M1 颜色/军械库 ②切W1 测小臂+腕部扭转(twist骨未用，旋前旋后集中在腕缝)。

## 材质（第60轮 M4 起）
- **护甲必须用 `advanced` 模板**，不能用 `basic`。SDK 说明：basic = "Sourced from a trash bag prop"；advanced = "...renders in the UI"。参照模组【4.28】Chiffon-LUNALICE 的护甲 unit（正是我们替换的 cae35ee1…/9841b165…）用 advanced + original。
- **槽位键零重合**：护甲家族 = `0f5ff78d a17b45a8 81d4c49d c2eb8d6e 30e2d136 d2f99d38 05a27dd5 b281e5f2 37831285 7e662968 6834aa9b`（armorlut 对上 10/11）；basic = `756f6fa6 ac652e43 caed6cd6`。键不对 ⇒ 贴图永远绑不上 ⇒ 全黑，且修 ID 位置毫无效果（M1 实测零变化）。
- **advanced 自有三槽**：2=Normal/AO/Roughness、3=Metallic、5=Color/Emission Mask（SDK TextureTypeLookup 只标这三个，其余 8 槽留模板自带的游戏贴图 id，参照模组同形状）。槽5 的 **alpha 必须=0**（=关闭发光遮罩）；参照模组实测底色 (209,197,199, **0**)。
- `fix_materials.py`：`TEMPLATE_SLOTS={basic/basic+:(0,1,2), advanced:(3,5,2)}`（顺序=共享/底色/法线），`--metallic`、`--alpha-zero`（advanced 默认开）。改 basic 行为前先用"同输入重建 == 已部署 M1 逐字节"做回归。
- `_probe_matchain.py` 按模板区分**自有槽位**（advanced=2/3/5），非自有槽报 `external`，只有自有槽未解析才算断链。
- 排错坑：PIL 对 **alpha=0 的 RGBA 做缩放会预乘**，看起来全黑；判断贴图内容要用全分辨率统计。

## 贴图映射调查（第60轮 M4W/M5）
- **UV 没坏**：源模型材质 6 `shinano_hair_ear` UV 包围盒 `u .7969-.9970 v .0071-.1821`（0.200×0.175），与发货包**逐位相同**；整体 `-21.06..22.98/-2.74..3.87` 也一致。贴图我方 crop 与源 VTF crop 逐像素一致；14 张贴图在 7 个 VPK 里各只有一份（无变体歧义）。
- **没有 V 翻转问题**：HD2 约定 = `row = v*H`（verbatim）。判据 = 参照模组（游戏内正常）带贴图渲染 + UV 空间三角覆盖，verbatim 下领口/面部/躯干对得上，V 翻转版领口变白、腿出墙砖纹。VTF→DDS 是直通（`hd2_texpack` 无翻转），`$basetexturetransform` 在 VMT 注释里，双方 mipCount 都=1。
- **section→材质**：sec0 face / **sec1 `white`=主体头发（UV 铺满发丝拼版）** / sec4 body / **sec5 `shinano_hair_ear`=猫耳（岛仅 0.2×0.175）** / sec6 headdress(u平铺3.05) / sec7 lace(u平铺44) / sec8-13 服装鞋。
- `_probe_uvrange.py` 的「0.98×0.98」是**包围盒**不是覆盖密度；判形状必须用 UV 三角覆盖图（`_probe_uvplot.py`）。
- ★ **uv1/uv2 通道**：参照模组 uv0=真实UV、**uv1=uv2=常量(0,1)**；我方 `pack_mesh_vertices` 把同一套源UV灌进三个通道。`set_uv12.py` 改成常量，TOC sha256 不变、字节差异只在 uv1/uv2。M5 已部署。
- **alpha 不是普适规则**：参照模组 `advanced` 材质 slot5 alpha≈0（与我方一致），`original` 模板材质 slot5 alpha≈255。故不统一置零。
- 原版单位只有 `.toc`（`E:/tmp/hd2-mgroot/re2310-full/unit/`），无 GPU 顶点数据，读不到原版 UV 通道；「原版」结论来自参照模组。
- 新增长期工具：`_probe_sourceuv.py`、`_probe_refrender.py`、`_probe_uvplot.py`、`_probe_secmap.py`、`_probe_texrender.py`、`set_uv12.py`。

## 贴图映射调查（第60轮 M6 · uvplot 红线错位专项）
- **M5 实机负证据**：uv1/uv2 置常量(0,1) 后贴图**完全没变** ⇒ 游戏采样的是 **uv0**，uv1/uv2 不是原因。
- **uvplot 位置计算没错**：`_probe_uvplot_check.py` 合成自检，512² 画 uv(.25,.25)/(.75,.25)/(.75,.75) 落在 x128..384/y128..384 OK；uv(0,0)→像素左上、uv(1,1)→右下；先画线后 thumbnail 无相对位移。
- **但老 uvplot 有真实缺陷 = 逐顶点 `uv%1`**：跨 0/1 接缝的三角形被画成横贯整图的长条假线。逐 section 统计：sec6 headdress 70 条、sec7 lace 1051 条（15%）、sec9 glove 16 条；**sec1 头发 / sec5 猫耳 = 0 ⇒ 这两块红线可信**。修复 = `_probe_uvplot2.py` 按**三角形整体**取整数偏移 + u/v 刻度尺 + TILED 标记。
- **UV/贴图/材质链忠实（独立复核）**：`_probe_matvars.py` 导出的 42 个 shader 变量与参照模组 `advanced` 材质**逐字段相同**；材质槽位结构（slots 2/3/5 指包内、其余指原版）与参照**一致**；4 个 LOD 的 UV 完全相同；贴图 toc 头同构。`_4`、`ornament_metal_underwear` 在**源 VPK 里本来就是 1×1**（不是 bug）；`shinano_hair_ear` 源 VTF 2048²，我方同。
- ★ **（已被 M6 真正根因取代，降级为「若 M6 仍不对」的下一候选）与「游戏内正常」参照模组的唯一结构性差异 = UV 平铺**：参照 9 个单位**全部顶点 UV 在 0..1，越界 0 个**；我方 5 个 section 越界（源模型本来如此，L4D2 靠 wrap）：sec6 headdress u×3.05、sec7 lace u×44、sec9 glove u×1.47、sec11 underwear v×6.61、sec12 highheels v×1.29。**假设（未证）**：HD2 护甲材质若 clamp 而非 wrap，这 5 块会显示成「贴图最边一条被拉长铺满」。验证方案 = 把这 5 块 UV 归一化进 0..1 + 对应贴图预平铺 N 次再打包。
- **头发的「放大感」可能本来就正常**：游戏截图浅色大叶片与发丝图集 `white` 大花瓣区（u .30–.72, v .62–.99）视觉一致，每片约 200px（2048 的 0.1）；截图是头部局部特写，若覆盖头发 UV 约 0.4 尺寸正好吻合。待用户确认截图拍的是头发/猫耳/头饰。
- 长期工具新增：`_probe_uvplot_check.py`、`_probe_uvplot2.py`、`_probe_uvsec.py`、`_probe_matvars.py`；`_probe_uvalign.py`（岛边界距离，信号弱）、`_probe_shotlocate.py`（模板匹配，投影失真）**未作为判据**。

## 贴图映射调查（第60轮 M6 续 · 🔴 真正根因：VTF mip 链读取顺序）
- **现象**（用户描述，极准）：正方形贴图右上角顶点不在最右上像素；只有**左下角约 60%** 是正确图像；上方图像**被压扁、完整重复非常多份、每层宽度是下一层的 1/2**。⇒ 这是 **mip 金字塔**特征，不是 UV 问题。
- **根因**：`hd2_vtf.VTF.read` 从 `header_size` 起**按 largest-first** 切 mip。真实布局是
  **`[16×16 低清缩略图 128B][mip N-1]…[mip 1][mip 0]`** —— 缩略图在**链首**，mip 链 **smallest-first**，**mip0 在图像块尾部**。
  把整链当一张图解码 ⇒ 小 mip 被重解释为行 ⇒ 重复/压扁/减半，只有下方 1398104/2097152 = **66.7%** 是真图（对上用户「60%」）。
- **尺寸账**（14 张全对，trailing=0）：`header_size + lowres + Σmips == 文件长度`。white: `128+699048+2097152 = 2796328`。
- **判据（要复用）**：① 把 `data[0:128]` 当 16×16 DXT1 解码，能看出是图集缩略图 ⇒ 缩略图在链首；② 16×16 缩略图 vs mip0 下采样的 Pearson r（`_probe_verifylayout.py`，13/14 新顺序胜）；③ 肉眼看三栏图（`_probe_mipviz.py`）。
- **反例（别再用）**：`_probe_miporder2.py` 的「box2(mip_m) vs mip_{m+1}」自洽判据被 BC1 重压缩噪声污染（white/face 误判为 largest-first）；`_probe_vtfmiporder.py` 的行跳变判据不够锐利（正确版仍有 7–12 条残差）。
- **修复**：`VTF.read` 先算全部 mip 尺寸，`chain_start = header_size + lowres`、`chain_end = chain_start + Σsizes`，从尾部逐个剥出；`lowres = data[header_size:header_size+lo]`。下游只用 `v.mips[0]`（`hd2_texpack.vtf_to_dds`）。
- **`fix_textures.py`（外科替换范式，值得复用）**：不重跑整链（管理器 armor-normalize 是 GUI 步骤），只在既有候选上替换目标条目的 GPU 像素；**内建反证** = 用旧配方重算一遍必须与既有字节逐字节相同（14/14 通过），否则说明源/配方找错，立即中止。容器校验照抄 `fix_materials.py`。
- **`zero_alpha` 安全**：BC7 解码→alpha 置 0→重编码，RGB mean|Δ| 0.48–1.24/255（p99 ≤ 8），长度不变。参照模组 slot5 alpha 本来就=0。
- **M6 结果**：TOC **逐字节相同**（sha256 `c7ba606b…2a44`，与 M5 一致 ⇒ 材质/网格/权重/uv 全未动）；GPU 37032980 字节不同，**100% 在 14 张底色贴图区间内、区间外 0**。gpu sha256 `84a519ce…cbf6`（M5 为 `303a0034…`）。
- 长期工具新增：`_probe_mipviz.py`、`_probe_vtflayout.py`、`_probe_verifylayout.py`、`_probe_m6diff.py`、`_probe_patchtex.py`、`_probe_alphargb.py`、`_probe_m5m6one.py`、**`fix_textures.py`**。

## 透明/镂空（第60轮 M7 · 🔴 根因是材质父类，不是贴图）
- **HD2 的透明是"材质父类"属性，不是贴图 alpha 属性**。SDK 权威表 `docs/HD2SDK-CommunityEdition-3.8.0/__init__.py :: Global_Materials`（= SDK 选材质的下拉框）：`advanced` 明写 **"No transparency"**（槽5 是 Color/**Emission** Mask）；**`alphaclip` = "supports an alpha mask"**（3 槽 = Normal/AO/Roughness + **Alpha Mask** + BaseColor/Metallic，变量 `OpacityThreshold`=0.5）；`translucent` 能软透明但**无底色贴图槽**；**`armorlut`（原版护甲用的）没有任何 alpha 槽 ⇒ 原版护甲本身不支持透明**，带蕾丝的护甲模组必须换父类。`original` 系（老 Nexus 默认）有 `SclarFieldOpacity` 槽 + `OpacitySharpness`/`OpacityOffset`。
- **遮罩存在 RGB 灰度里，不在 alpha 通道里**（第一版普查看错通道，白忙）。`SclarFieldOpacity`/`OpacityClipMap` 都是纯灰度（gray≈0）、自身 alpha=255。判据用 `_probe_maskcensus.py`（同时报亮度与 alpha）。
- **极性**：白=可见、黑=透明。官方 Wiki（HD2-Modding-Wiki dev/materials/alphaclip）原话 "The White of the mask is visible and the black will be transparent"，且 "no capabilities for partial transparency"（只能硬镂空）。公式近似 `alpha = saturate((mask + OpacityOffset) * OpacitySharpness)`；`alphaclip` 用 `OpacityThreshold`。
- **L4D2 侧**：透明在 **VTF alpha**（DXT5），白=有布/黑=洞；VMT 用 `$translucent 1` 才读它；绿色 RGB 只是 alpha=0 区的无关底色。**🔴 同一 `$basetexture` 被多个 VMT 共用**，必须遍历模型自己的 92 槽（`_probe_slots.py`）而不是只看每组"主"VMT —— 只做主 VMT 会漏掉 glove/dress/lower_white01（它们的 `*_Lace_*` 变体 VMT 里 `$translucent 1` 是生效的）。实测 30 槽有生效 `$translucent`、38 槽有生效 `$alphatest`（装饰件 `$alphatest 1` 生效但贴图是 1×1 RGB888 **无 alpha** ⇒ 必须靠"贴图真有 alpha"判定）。
- **参照模组 Chiffon-LUNALICE**：只 2 个材质；第 2 个父类 `0x8f669f365f24594e`（槽位布局与 `original` **逐字节相同**），`OpacitySharpness=64 / OpacityOffset=−0.5`，`SclarFieldOpacity` 槽（键 `479fb1ef`）挂 **1024² 纯灰度蕾丝图案**（黑40%/白42%/中间18%）。⇒ 灰度遮罩+阈值裁切；没用 `alphaclip` 只因早期模板是 `original`。
- **`fix_alphaclip.py`（M7 构建器）**：只重建 5 个材质为 `alphaclip`（槽0←4×4 平面法线 `0xba1a40900756a7f3`；槽1←新遮罩；槽2←原底色 id 不变），**新增 5 张 2048² BC7_UNORM 灰度遮罩**（源 VTF alpha，新 id = `make_id(0xB,'tex/mask/<stem>')`），其余 verbatim。校验：条目 54→59、49 条 verbatim 且 gpu 偏移未动、材质回读确认父类/三槽、遮罩回读 mean|Δ|≤0.115/255。
- **阈值 0.5 经覆盖率验证**：裁后保留面积与 L4D2 alpha 混合平均覆盖率差 < 4 个百分点。单一旋钮：洞太小→0.35，掉太多→0.65。
- **🔴 部署踩坑**：`New-Item -ItemType SymbolicLink` 在本机失败（无 SeCreateSymbolicLinkPrivilege）且**先删后建** ⇒ 一度删掉 patch_0 链接。**以后一律 `MSYS=winsymlinks:nativestrict ln -s <target> <link>`**，并先建后删。
- M7 = `E:/mod/Lunalice-RE2310-M7/`（patch 573744 B sha256 `693cc6be…b320`，gpu 88963520 B sha256 `3d3172b3…4485`），回退目标 M6。细节见 `tmp/rig-lab/TRANSPARENCY_BRIEF.md`。

## 外观通用化（第60轮 M8 · 🔴 取代 M7 的硬编码；金属变黑的根因）
- **用户要求**：不能指定区域/硬编码，要做**通用 L4D2→HD2 转化工具**；金属光泽区变纯黑要修。待办（暂不做）：军械库渲染模式支持 `alphaclip`。
- **🔴 金属变黑根因 = 源侧是 NekoToon 着色器，观感来自 matcap，我们整个丢掉了**。92 槽**全部** `NekoToon`，取色 `底色×光照 + matcap贴图×$matcaptint`（`$matcap 2`=相加）。装饰件 38 槽（`Ornament_metal_*`/`_jewel_*`/`_Pearl_*`）`$basetexture` 全指向 `Ornament_metal_Underwear` = **1×1 RGB888 (15,15,15) 近黑**；matcap 覆盖面 `11`(82%, 亮银 (199,196,198)) / `6`(14%, 中灰=宝石) / `Matcap8`(4%, 珍珠)。只搬底色 ⇒ 纯黑。
- **🔴 SDK 权威槽位语义**（`__init__.py :: SetupAdvancedBlenderMaterial` 1490-1521）：`advanced` 槽2 `Nar`(4c567810) RGB=法线、**Alpha=粗糙度**；槽3 `IlluminateData`(90b84a53)→**金属度**；槽5 `AlbedoEmissive`(e67ac0c7) RGB=底色、Alpha=自发光强度（预览里乘 0，等于不用）。`alphaclip` 槽0=Normal/AO/Roughness、槽1=Alpha Mask、槽2=Base Color/Metallic（1456-1471）。⇒ 我们共享平面法线 alpha=255 ⇒ 粗糙度拉满；金属度=6/255 ⇒ 全非金属。
- **M8 两条数据驱动规则**（零硬编码）：**A** `opts_in`（组内任一 VMT 生效 `$translucent`/`$alphatest`）**且** `has_alpha`（该组 VTF alpha 跨度≥3/255 且 <250 像素≥0.5%）→ `alphaclip` + alpha 遮罩@0.5，否则不动 ⇒ 14 组里 **8 组 alphaclip**。**B** `max($matcaptint)`≥0.9 → 底色 `:=clamp(底色+matcap按三角形加权均值×tint)` + 槽3 金属度 + 槽2 alpha 粗糙度；<0.9 → 一字节不改（只有装饰件是 `[1 1 1 1]`，其余 `[.5…]`/`[.1…]`）。
- **一个 HD2 材质只能一个底色**：组内多 matcap 只能加权平均。**Unit 里材质表 14 项是已实机验证部分，不能改数量** ⇒ 不能按 matcap 拆组。
- **`fix_appearance.py`（M8 构建器，取代 `fix_alphaclip.py`）**：基于 M6 一次成型；8 组→`alphaclip`（含新补 glove/dress/lower），装饰件→`advanced`（底色 (202,198,202)、金属 191、粗糙 89），其余 5 组 + Unit/Bone/既有贴图 verbatim；新增 11 张贴图（8×2048² 遮罩 + 底色/法线粗糙度/金属度），新 id `tex/mask|albedo|rough|metal/<stem>`。校验：54→65 条、verbatim 逐字节 + gpu 偏移未动、遮罩回读 mean|Δ|≤0.177/255。旋钮 `--metallic`(0=纯银最保守)/`--roughness`/`--matcap-min-tint`。
- M8 = `E:/mod/Lunalice-RE2310-M8/`（patch 573384 B sha256 `7ef71c40…3666`，gpu 101546624 B sha256 `43a23288…b25e`），回退目标 M7。细节见 `tmp/rig-lab/APPEARANCE_BRIEF.md`。
- **🔴 通用工具待补**：模型 `cdtextures=['matchacheese\Lunalice\opt\','matchacheese\Lunalice\']`，**`opt\` 优先**，但 `Packs` 只按 basename 索引（当前 41 vtf/102 vmt 无同名冲突，暂时无影响）。法线贴图与 `$emission` 仍未接（`Shinano_face_e` 真发光但与 `shinano_face` 同底色同组）。

## 数据与历史
- 参照E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310；af431352为上身/头发外壳，仅neck/chest，非头盔。E为测试库、D Steam在用。
- 旧探针先查tmp/rig-lab/_archive；细节留每日日志及ARM_ELBOW_ARTIFACT_BRIEF、FINGER_RETARGET_BRIEF、WEIGHT_TRANSFER_BRIEF、UNIT_TRANSFORM_BRIEF，不把早期猜测当现状。
