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

## 数据与历史
- 参照E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310；af431352为上身/头发外壳，仅neck/chest，非头盔。E为测试库、D Steam在用。
- 旧探针先查tmp/rig-lab/_archive；细节留每日日志及ARM_ELBOW_ARTIFACT_BRIEF、FINGER_RETARGET_BRIEF、WEIGHT_TRANSFER_BRIEF、UNIT_TRANSFORM_BRIEF，不把早期猜测当现状。
