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
- 2026-10-13 用户确认全部摆姿结束。**已进游戏实测（第58轮包失败，第59轮定位并修复）**；冻结最终几何；摆姿使用的源权重与后续运行时权重分开，避免新权重重跑摆姿移动已验收手部。专家权重指导见 WEIGHT_TRANSFER_BRIEF.md §10；本轮失败诊断与修复见 UNIT_TRANSFORM_BRIEF.md。
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
- 121704顶点，float归一通过，用骨49/107；旧“真硬缝0/权重无硬伤”结论无效：探针仅计共享骨集合，不看比例或跨壳拆点。独立边权重L1 p99=.615/max1.855，974边>1仅作动作复查线索，不等于裂缝。
- **🔴 半精度闭合（我第一版判据错了）**：`round(w*2048)` 当 half 代理是**错的**（[0.25,0.5) 步长2^-12）。真实判据 = SDK `sdk-raw/hd2_system_skin_weights.py::normalize_half4`：要求 `math.fsum(half(w))==1` **精确成立**。实测 **49.417% 顶点不成立**（max偏差3.662e-4），SDK 助手可全修（最大单权重改动3.624e-4<1/1024）。此为工具输出契约，尚无证据证明不闭合会被引擎拒绝或必致裂缝。**C# 侧同样缺**：`CanonicalMeshPreparation.TryNormalizeSkinning` 只 float 归一后直接 `HalfToUInt16Bits`。
- **🔴 hips vs spine1**：实测 `boss`/`hips`/`spine1` head 完全相同 (0,−0.0215,+0.9735)，`hips` 与 `spine1` 是 boss 下**兄弟**，分别驱动**腿链**与**躯干链**。我们**从不用 spine1**，参考躯干用39.9%/髋18.1%，差异 22317 顶点(18.3%)。真实动画行为尚未知，可离线解码动画比较 `G_b(t)@inv(B_b)`；body_pose_profile不含两骨。out_idle实际为绑定矩阵代入，不是真实待机。禁止整片下半身hips→spine1。
- 参考骨表**有 spine1、无 clavicle**；我们**有 clavicle(686顶点)、无 spine1**。差异几乎全是相邻骨差一格（head→neck 19.4%、hips→spine1 16.7%、spine2→spine1 1.6%…），唯一跨远的是 head→hips 546个(d50 66.6mm，双马尾下端)。主导骨一致率 53.7%（5mm 内 63.9%）。
- 几何吻合：我们→参考 **p50 7.8 / p90 20.8 / p99 56.3 / max 141.8 mm**，5mm 内29.8%、20mm 内89.3%。距离分布不是误配率；近邻也能跨指/跨壳配错。参考5cm门槛不是通用标准。
- 参考的 `weight_transfer.py` **对我们不可直接用**（是"他们自己的源 blend→mod"，依赖 DEFAULT_GLB/MOD=None 的自有工程）；走照搬得自己写转移。
- 双马尾低垂head主导868点；一环邻居同head不能证明整束连通。转头位移应按到旋转轴垂距；竖轴30°刚性估算p50=177.7/max245.6mm，非游戏实测。23664个head→neck差异中z<1.40为0，不能全标成长发。
- Slim/Stocky只有距离分位数一致，不能据此证明几何相同或排除变体偏差。
- 专家推荐六区+附件身份+同侧/同指三角面重心转移+置信度回退；区域交界留渐变，不凭骨更近判对。长发单独识别再显式混head/neck/躯干，首版legacy三档比较，不承诺碰撞。先腰腹局部、附件，再手部动作、尽早实机。简单2048最大余数half方案经121704行+10000随机验证；C#示例在§10.7，未编译接入，非SDK逐位等价。
- 探针：`_probe_weightaudit.py` / `_probe_halfclosure.py` / `_probe_refweights.py` / `_probe_refpalette.py` / `_probe_refalign.py` / `_probe_weightcompare.py` / `_probe_headdom.py` / `_probe_variants.py`。
- **度量口径**：判断"影响距离"要用**到骨头（关节枢轴）**的距离，**不能用骨段**（HD2 子节点侧向，child-direction deviation p95=91.45°，会把正确的骨报成150mm）。到关节 p50 238.6mm 这类数字**不是缺陷**（LBS 本就允许远骨，如整腿挂 hips）。
- **专家路线 D（§10）**：六区域 `body_region` + 附件身份 `attachment_class` 双层软掩码；转移用**最近三角面+重心插值**（非最近顶点）、置信度回退 `W=(1-c)*Wbase+c*Wref`、同骨先合并再top4；不做全局ICP。**拒绝全量最近点照搬**。顺序：冻结hash→**编码合法性**→分区预览→腰腹局部→长发三档(a_tip 1.0/0.7/0.4)+袖子→手部动作→实机；**不要等完美系统才第一次进游戏**。
- **专家纠正**：距离分位数**不是**误配率（要量到三角面）；共享骨名≠平滑（按骨身份合并后边L1 p99=.6150/max1.8546，974条>1 只是复查线索）；**`head→neck` 23664 点里 z<1.40 的数目=0**（我"全是双马尾"错了）；`out_idle` 不是真实待机帧（hd2_style.py:1878 用 `rest_hd2@inv(rest_shipped)`）；长发摆动看到旋转轴**垂距**（868点转头30°刚性中位177.7/max245.6mm）；半精度闭合是**工具契约**不是已证实的引擎拒收条件；相同p50/p90不能证明Slim/Stocky几何相同；一环邻居不证明发束连通。
- **🔴 打包路径**：= **Blender + HD2SDK 插件** `docs/HD2SDK-CommunityEdition-3.8.0`（`helldiver2.export_patch`）。BatchTool 无导出器。SDK `stingray/unit.py::GetMeshData` 调 `vertex_group_normalize_all`+`limit_total(4)` 后原样写 weight，**无 half4 闭合**；且要求 **材质名必须是整数**（`int(material.name)`，否则抛错）。**`zoey.blend` 是诊断场景不是可导出场景**（12 个材质全非数字名、无UV/顶点色/贴图、无 `Z_ObjectID`/`MeshInfoIndex`/`BoneInfoIndex`/`matslot<N>`）。lab 管线**只带几何+权重，不带 UV/材质/贴图**。
- **🔴 权重编码已修好（第57轮）**：`weight_half4.py`（`merge_duplicate_bones`+`quantize_half4` 1/2048 最大余数+`verify_half_closed`）接进 `dump_blender.py`；`blender_build.py` 去掉 `np.round(w,3)`。**关键**：k/2048 在 half 里精确可表示 ⇒ float32 和精确=1 ⇒ SDK 的 `vertex_group_normalize_all` 变空操作 ⇒ half 和精确=1，**不必改 SDK 插件**。实测：合并重复骨 29635（24.35%，其中 **5761 顶点还带另一根骨 ⇒ 原 REPLACE 静默改比例**）、最坏单权重改动 0.0003252174、新增影响 0；.blend 端到端 **121704/121704 half 和精确=1**、重复骨 0、非格点 0。几何逐位未变（hash `05e8f5f876a2c32e`）。`meta.json` 新增 `fingerprint`（geometry/weights/rest/scripts 的 sha256）。
- **探针新增**：`weight_half4.py` / `_probe_exportchain.py` / `_verify_blendweights.py`（Blender 里读 .blend 顶点组）/ `_inspect_blend.py`。

## 🔴 Unit 资产契约（第59轮确立，权威，勿再猜）
- **`TransformInfo` 是一个 unit 自带的骨架副本**，权威算法 = SDK `stingray/unit.py::AddBoneData` 行1796-1934：
  逐骨遍历 armature；`NameHashes.index(hash)` **已存在就复用、否则追加**；`matrices[i]=bone.matrix`（世界，行主序16 float，平移flat12..14）；
  **`locals[i]=decompose(parent.matrix⁻¹@bone.matrix)`（父坐标系下的局部变换，根骨全0）**；`entries[i]=(Incriment, 父骨在本表的下标)`。
  管理器 `CanonicalTransformInfoExpander` 多一条：同名节点**必须覆写**（locals+matrices+parent 全取 avatar），且 `NameHashes` 不许重复。
- **`MeshInfo.TransformIndex` 决定网格摆放**：`origin=matrices[TransformIndex]⁻¹`，`Bones[i]=(origin@M_bone).inverted().transposed()`。
  ⇒ **必须指向单位阵节点**（模板 torso 用 lod3/2/1/0 → 5/6/7/8，全单位阵）；指到真实骨骼会把整个网格平移过去。
  不变量：**`MeshID == NameHashes[TransformIndex]`**（模板 6/6 成立）；所有 LOD 共享同一网格变换矩阵（SDK AutoLods）。
- **反导入验证**：SDK `CreateModel` 行2375-2483 用 `bone.matrix = TransformMatrices[i]`（世界）+ `TransformEntries` 的父链 ⇒ 填错会看到"骨骼堆在原点"。
- 模板捐赠者 = 管理器 `Torso/Stocky` unit `0xd841b16544c8fbae`（23 节点/4 流/6 mesh），其 TOC 与 Chiffon 源 patch **逐字节相同**。
  正确合并结果 = **115 节点（23 donor + 92 追加，覆写 15，donor 独有保留 8）**；donor 独有 8 个是 LOD 网格节点5-8 + culling 17/22 + 节点2/4。
- lodgroup 192B：record 的 `min/max` = **该 culling mesh 自身局部 bbox**，`radius` = **该 mesh 顶点范数**（模板 stream3 恒0.457118 / stream2 恒0.243766）。
  offset 表 `offs[i]+8` 只对 record0/2 成立 ⇒ **按 (min,max) 内容定位 record**。
- 工具：`_probe_unitdiag.py`（字段级诊断）/ `_verify_patch.py`（45 项回读校验）/ `rewrite_lod_group_list()` / `merge_transform_info()` / `check_unit()`。

## 坐标、参照与数据
- HD2 +X角色右、+Y前、+Z上；左肢在-X。单位米。原生踝不在脚心。
- 参照 E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310；9ba626afa44a3aa3.patch_0 + gpu_resources，无manifest。E为测试库，D Steam为在用。
- out/patchdump：左右臂unit 91eb7be84016ceab(LeftArm_Undergarment_Slim_lod3) / 7336c6b662522f0e(RightArm_..._Slim)；上衣cae35ee15168145f(Torso_..._Slim)、Hip17da843a4908511e(Hip_..._Stocky)、af431352d9b95e83(**Torso_Undergarment_Slim_lod3**，z0.911..2.059 质心1.769，是上半身/头发外壳，**不是头盔**；仅2骨 neck83.9%+chest16.1%，不含head)。完整高约2.113m。
- Unit矩阵行向量（平移flat12..14），read_hd2_rig已转置；前9骨为场景节点，空骨名按hash解码。骨轴Source/HD2局部+X，Blender+Y；末骨无子节点不能用下一骨头推轴。
- tmp/rig-lab/ 一次性旧脚本已归档 _archive/；找旧探针先查此处。详细历史留每日日志和 docs/hd2-batchtool-sdk-findings.md 等，不把旧猜测当现状。

## 其他未决
- 脚底Y锚点前移、Nick过冲等历史问题不随手指任务扩展处理。
- 信息中心 RequestAsync<T> 唯一入口，ModInformationFactRegistry 25类；缓存不可替代真实性，“读不到”不等于“没有覆盖”。护甲持久化归属曾引发崩溃；ModLibraryResidualCleanupService递归清空库目录的回归仍待处理。
