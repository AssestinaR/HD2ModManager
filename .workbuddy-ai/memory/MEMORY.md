# HD2ModManager 项目长期记忆

## 工作约定
- 仓库 WpfApp1；只做本地提交，不推送。授权范围内直接完成。用户非专业程序员，用通俗语言说明操作与后果；用户视觉判断强，由助手负责数值验证。
- 临时脚本放 tmp/，不得放 _diagnostics/（存在即触发条件测试）。docs/ 被忽略，修改前备份到 docs/_backup/。不删除 .workbuddy-ai。
- 保护 bug 的测试必须做破坏关键行的负对照；删除代码同时改造测试。dead-code 检查仅词匹配，需比较两个方向。提交会包含整个索引。
- 用户自定义数据属于模组库；简单配置属于 settings.json；能由游戏数据重算的缓存才属于管理器目录。
- Directory.Build.props 开 Nullable/ImplicitUsings/Deterministic、警告当错误（排除 NU1901..1904）。全测试约6–7分钟，计数须先实跑并分项报告。发布轻量标签；包内 cliVersion 带完整提交哈希。
- Python 隔离环境 C:/Users/Administrator/.workbuddy-ai/binaries/python/envs/default/Scripts/python.exe（numpy/Pillow/**scipy 1.18.1**，cKDTree 可用；参考工具链需要）。Blender D:/SteamLibrary/steamapps/common/Blender/blender.exe。Bash heredoc 易被拦，写 tmp/*.py。删除钩子拒绝不得绕过。
- rig-lab 脚本的 CWD 是 tmp/rig-lab/，`OUT='out/blender'` 是相对路径 ⇒ 产物在 **tmp/rig-lab/out/**，不在仓库根 out/。

## 工程
- 管理器 WPF/net10，CLI net10，Core/Adaptation/GameItemAssets/SourceModel/SourceMigration net8；ManagedMain 过时。CoreServices 唯一装配点。
- SourceModel 零项目引用；SourceMigration 接两侧；CLI 薄命令。Source 格式权威见 SourceModel 注释。
- Lua 源 HD2RuntimeArmorOverride 内嵌资源。游戏无创建网格/改权重接口；迁移必须离线，legacy 包没有 Lua 也能运行，不依赖 HD2IRG1 动态骨。

## 模型迁移目标与当前状态
- 固定数学操作流：源 T-pose 几何摆成参照 A-pose 变体 → 后续重刷权重 → legacy 包 → 用户进游戏验收。
- 2026-10-13 用户确认手臂问题已解决、手掌与参照基本重叠、各指根已对齐；当前只剩手指自动蜷曲，目标放松半虚握，拇指自然握持。保持已验收身体/手掌不动。专家交接 tmp/rig-lab/FINGER_RETARGET_BRIEF.md §10。
- 参数以 dump_blender.py 为准：yaw、spine_map=mesh、fallback=region、leg_tilt=5.7、leg_splay=4、mesh_shift=(0,-.100,-.090)、upper_scale=1.12/pivot_z=1.15/blend=.45、head_scale=8/7、upper_gain=.05、elbow_open=6.5、**hand_tilt=3**（第55轮用户要求；他说"7度改3度"但实际 ship 的是10度，`zoey-before-hand7.blend` 命名有歧义内容是10度——用户目标绝对值，故取3）、**finger_pose=True**。旧局部指标不是否决依据。
- 手指残余摆姿（第55轮，hd2_style §4f，专家§10.4-10.7）：源手是摊平T-pose（十链实测0.00°弯曲），HD2 原生手是放松A-pose；管线把手当前臂刚性延长件 ⇒ 手指继承摊平且 HD2 指端关节在我们网格外 42-48mm。修法=在**最终网格**上逐指逐节旋转到 HD2 骨向，`Rl=min_arc(s[k], Q.T@t[k])`，`Q=Gp[:3,:3]`（**漏掉 Q 共轭错 11.3-14.3°**）；`J` 必须过 mesh_shift+上身缩放的 `post`，不能用裸 `rest_sym`；末节无子骨取 `rest_hd2[last][:3,0]`。实测：未触及顶点 max|d|=4.4e-16、指根不动、段长差≤1e-14、残差0.00°、纯单骨 Kabsch 0.00000mm、指端关节距 42-48→1.1-5.0mm。**已验收的 palm/wrist/root 逐位不动。**
- arm_scratch=True 时旧 arm_fit/arm_up/arm_rot 等分支跳过；apose=False 未必关闭 scratch。消融须核对实际分支。
- 发布 rest_shipped=rest_hd2 逐位不变；rest_sym 是源 T-pose 工作枢轴，不能当最终网格位置。src1 是摆姿前顶点，D 用于烘焙，out 后还有全局/上身/头处理；最终 out_idle 才可对参照。idle恒等只作骨架护栏，不证明形状对。
- 肘问题独立复核见 ARM_ELBOW_ARTIFACT_BRIEF.md §10：仅移动旋转枢轴 d 引起 Q(I-R)d，不是 Qd；需顶点与枢轴同步。远端工作链含手指/辅助点也必须随动。后处理非线性缩放影响段长；不能用 D@rest_sym 验最终网格。

## 诊断硬约束
- 使用 mesh-deform-diagnosis；发图前必须自己看。多壳先分层、先校准参照，同视角固定顶点集做消融。
- 边长p99、bad%、bind一致不能证明宏观形状；已设计的轴向拉伸验仿射残差，段内保形验Kabsch；关节过渡单列。弯曲手指不能要求整指Kabsch残差为零。
- 过滤后的PCA长轴受窗口影响；骨权重主导集会吞进前臂；左右先分开，再限空间/拓扑。固定旧顶点选择，不随版本重选。截面质心/多壳像素中线可能是假信号。
- 阶段/单位/旋转轴须明确；顶点变换要同步工作标志点。全局scale不能复用为正弦或逐顶点scale；闭包变量被覆盖会静默改变 joint()。
- 绘图函数必须传实际投影轴，否则三视图都成XY。**画线也必须过屏幕变换 `f()`**：直接用投影后数据坐标画线会跑到画布外，只剩点可见（第55轮骨链连线就这样消失）。blender_render.py 必须传 .blend；所有输出用绝对路径。
- 手部选区：`dominant bone ∈ 手部骨` = 3933 顶点但半径 0.204m，**吞掉前臂**；改用前臂轴投影 `(P−wrist)·(wrist−elbow) > −0.03` 切割。

## 权重（第56轮调研，专家交接 WEIGHT_TRANSFER_BRIEF.md）
- 我们的权重来源：导出阶段用 `snap_body()`/`resolve()` 按**名字/区域**表把源骨改名成 HD2 骨名；`spine_map='mesh'`（Spine→spine2，因源关节在区域底部、HD2 在中心），fallback=region+肢体测试。
- **我们权重无硬伤**：121704 顶点、归一 min=p50=max=1.000000、影响数 `[0,49403,43705,28596,0]`、均值1.829、用骨49/107、**真硬缝0条**（274577 边中主导骨变化 4686=1.71%，但无一共享<1/3影响）。
- **🔴 半精度闭合（我第一版判据错了）**：`round(w*2048)` 当 half 代理是**错的**（[0.25,0.5) 步长2^-12）。真实判据 = SDK `sdk-raw/hd2_system_skin_weights.py::normalize_half4`：要求 `math.fsum(half(w))==1` **精确成立**。实测 **49.417% 顶点不成立**（max偏差3.662e-4），SDK 助手可全修（最大单权重改动3.624e-4<1/1024）。量级 0.24mm ⇒ 不是视觉问题，是引擎接受/护甲裂缝问题。**C# 侧同样缺**：`CanonicalMeshPreparation.TryNormalizeSkinning` 只 float 归一后直接 `HalfToUInt16Bits`。
- **🔴 hips vs spine1**：实测 `boss`/`hips`/`spine1` head 完全相同 (0,−0.0215,+0.9735)，`hips` 与 `spine1` 是 boss 下**兄弟**，分别驱动**腿链**与**躯干链**。我们**从不用 spine1**，参考躯干用39.9%/髋18.1%，差异 22317 顶点(18.3%)。**静止重合 ⇒ 所有静态检查都瞎，只在骨盆/脊柱相对转动时显形。** 离线判不了，已列专家问题 Q2。
- 参考骨表**有 spine1、无 clavicle**；我们**有 clavicle(686顶点)、无 spine1**。差异几乎全是相邻骨差一格（head→neck 19.4%、hips→spine1 16.7%、spine2→spine1 1.6%…），唯一跨远的是 head→hips 546个(d50 66.6mm，双马尾下端)。主导骨一致率 53.7%（5mm 内 63.9%）。
- 几何吻合：我们→参考 **p50 7.8 / p90 20.8 / p99 56.3 / max 141.8 mm**，5mm 内29.8%、20mm 内89.3%。约 10% 顶点 >20mm ⇒ 最近点转移会选错表面；参考自己设了 `--max-residual 0.05` 整块拒绝门槛。
- 参考的 `weight_transfer.py` **对我们不可直接用**（是"他们自己的源 blend→mod"，依赖 DEFAULT_GLB/MOD=None 的自有工程）；走照搬得自己写转移。
- 双马尾：`source_extra_74/77` 各~245 顶点在 z0.759–1.042，head 主导 868 顶点（z<1.40）邻居 108 次检查 100% 也是 head ⇒ **连通的双马尾下端**，非采样错误；离 head 关节 1.011m，头转30°摆动约0.5m。
- Slim/Stocky 变体几何完全相同（同名 tag 下 p50/p90 一致到 0.1mm）⇒ 不存在比错体型的系统偏移（虽然我们混着取）。
- 探针：`_probe_weightaudit.py` / `_probe_halfclosure.py` / `_probe_refweights.py` / `_probe_refpalette.py` / `_probe_refalign.py` / `_probe_weightcompare.py` / `_probe_headdom.py` / `_probe_variants.py`。
- **度量口径**：判断"影响距离"要用**到骨头（关节枢轴）**的距离，**不能用骨段**（HD2 子节点侧向，child-direction deviation p95=91.45°，会把正确的骨报成150mm）。到关节 p50 238.6mm 这类数字**不是缺陷**（LBS 本就允许远骨，如整腿挂 hips）。

## 坐标、参照与数据
- HD2 +X角色右、+Y前、+Z上；左肢在-X。单位米。原生踝不在脚心。
- 参照 E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310；9ba626afa44a3aa3.patch_0 + gpu_resources，无manifest。E为测试库，D Steam为在用。
- out/patchdump：左右臂unit 91eb7be84016ceab(LeftArm_Undergarment_Slim_lod3) / 7336c6b662522f0e(RightArm_..._Slim)；上衣cae35ee15168145f(Torso_..._Slim)、Hip17da843a4908511e(Hip_..._Stocky)、af431352d9b95e83(**Torso_Undergarment_Slim_lod3**，z0.911..2.059 质心1.769，是上半身/头发外壳，**不是头盔**；仅2骨 neck83.9%+chest16.1%，不含head)。完整高约2.113m。
- Unit矩阵行向量（平移flat12..14），read_hd2_rig已转置；前9骨为场景节点，空骨名按hash解码。骨轴Source/HD2局部+X，Blender+Y；末骨无子节点不能用下一骨头推轴。
- tmp/rig-lab/ 一次性旧脚本已归档 _archive/；找旧探针先查此处。详细历史留每日日志和 docs/hd2-batchtool-sdk-findings.md 等，不把旧猜测当现状。

## 其他未决
- 脚底Y锚点前移、Nick过冲等历史问题不随手指任务扩展处理。
- 信息中心 RequestAsync<T> 唯一入口，ModInformationFactRegistry 25类；缓存不可替代真实性，“读不到”不等于“没有覆盖”。护甲持久化归属曾引发崩溃；ModLibraryResidualCleanupService递归清空库目录的回归仍待处理。
