# HD2ModManager 项目长期记忆

## 工作约定
- 仓库 WpfApp1；只做本地提交，不推送。授权范围内直接完成。用户非专业程序员，用通俗语言说明操作与后果；用户视觉判断强，由助手负责数值验证。
- 临时脚本放 tmp/，不得放 _diagnostics/（存在即触发条件测试）。docs/ 被忽略，修改前备份到 docs/_backup/。不删除 .workbuddy-ai。
- 保护 bug 的测试必须做破坏关键行的负对照；删除代码同时改造测试。dead-code 检查仅词匹配，需比较两个方向。提交会包含整个索引。
- 用户自定义数据属于模组库；简单配置属于 settings.json；能由游戏数据重算的缓存才属于管理器目录。
- Directory.Build.props 开 Nullable/ImplicitUsings/Deterministic、警告当错误（排除 NU1901..1904）。全测试约6–7分钟，计数须先实跑并分项报告。发布轻量标签；包内 cliVersion 带完整提交哈希。
- Python 隔离环境 C:/Users/Administrator/.workbuddy-ai/binaries/python/envs/default/Scripts/python.exe（numpy/Pillow，无scipy）。Blender D:/SteamLibrary/steamapps/common/Blender/blender.exe。Bash heredoc 易被拦，写 tmp/*.py。删除钩子拒绝不得绕过。

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

## 坐标、参照与数据
- HD2 +X角色右、+Y前、+Z上；左肢在-X。单位米。原生踝不在脚心。
- 参照 E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310；9ba626afa44a3aa3.patch_0 + gpu_resources，无manifest。E为测试库，D Steam为在用。
- out/patchdump：左右臂unit 91eb7be84016ceab / 7336c6b662522f0e；上衣cae35ee15168145f、Hip17da843a4908511e、头盔af431352d9b95e83（脸猫耳长发）。完整高约2.113m。
- Unit矩阵行向量（平移flat12..14），read_hd2_rig已转置；前9骨为场景节点，空骨名按hash解码。骨轴Source/HD2局部+X，Blender+Y；末骨无子节点不能用下一骨头推轴。
- tmp/rig-lab/ 一次性旧脚本已归档 _archive/；找旧探针先查此处。详细历史留每日日志和 docs/hd2-batchtool-sdk-findings.md 等，不把旧猜测当现状。

## 其他未决
- 脚底Y锚点前移、Nick过冲等历史问题不随手指任务扩展处理。
- 信息中心 RequestAsync<T> 唯一入口，ModInformationFactRegistry 25类；缓存不可替代真实性，“读不到”不等于“没有覆盖”。护甲持久化归属曾引发崩溃；ModLibraryResidualCleanupService递归清空库目录的回归仍待处理。
