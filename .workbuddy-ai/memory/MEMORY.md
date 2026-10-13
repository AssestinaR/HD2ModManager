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
- 2026-10-13：摆姿已全部验收；58轮实机包失败，59轮已改结构但未实机闭环。保持最终网格，摆姿源权重与后续运行时权重分离。
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

## 数据与历史
- 参照E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310；af431352为上身/头发外壳，仅neck/chest，非头盔。E为测试库、D Steam在用。
- 旧探针先查tmp/rig-lab/_archive；细节留每日日志及ARM_ELBOW_ARTIFACT_BRIEF、FINGER_RETARGET_BRIEF、WEIGHT_TRANSFER_BRIEF、UNIT_TRANSFORM_BRIEF，不把早期猜测当现状。
