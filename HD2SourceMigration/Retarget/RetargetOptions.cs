using HD2SourceModel;

namespace HD2SourceMigration.Retarget;

/// <summary>对齐旋转的求解方式。</summary>
public enum RetargetAlignment
{
	/// <summary>自由的 3 自由度刚体拟合。<b>不要用</b>：两套骨架的关节高度不同，拟合会带上约 2.8° 倾斜。</summary>
	Kabsch,

	/// <summary>只绕 +Z 的 1 自由度拟合。生产配置。</summary>
	Yaw,

	/// <summary>精确的绕 +Z 180°。</summary>
	RotZ180,

	Identity,
}

/// <summary>源脊椎骨映射到哪几根 HD2 骨。</summary>
public enum RetargetSpineMap
{
	/// <summary><c>Spine→spine1, Spine2→spine2, Spine4→chest</c>。</summary>
	Legacy,

	/// <summary><c>Spine→spine2, Spine2→chest, Spine4→chest</c>。生产配置：源关节坐在它所驱动区域的<b>底部</b>，
	/// HD2 的坐在<b>中心</b>，所以按网格质心对才是对的。</summary>
	Mesh,
}

/// <summary>发货哪一套骨架。</summary>
public enum RetargetSkeleton
{
	/// <summary>HD2 原生 rest，逐字节。运行时的 idle 变形 <c>rest_hd2 · inv(S_bind)</c> 因此是恒等，
	/// 玩家看到的就是这里摆出来的网格。生产配置。</summary>
	Native,

	/// <summary>snap 后的 rest。idle 帧会拖拽网格（缺脚跟、脚踝撕裂、长脖子、裙子裂开）。仅供 A/B。</summary>
	Snap,
}

/// <summary>源骨没有对应骨时怎么绑定。</summary>
public enum RetargetFallback
{
	/// <summary>沿源链上溯到最近的<b>已映射祖先</b>。</summary>
	Parent,

	/// <summary>绑到关节离它<b>最近</b>的已映射骨。</summary>
	Distance,

	/// <summary><see cref="Distance"/> 但限制在与该祖先<b>同一肢体</b>内。生产配置。</summary>
	Region,
}

/// <summary>手掌转正（4g）的取值方式。</summary>
public enum HandReorientMode
{
	Off,

	/// <summary>解出使"我们的手指肉"到"HD2 自己的指骨段"平均距离最小的刚体转角。生产配置。</summary>
	Solve,

	/// <summary>强制一个角度。</summary>
	Forced,
}

/// <summary>
/// 重定向选项。默认值就是<b>发货配置</b>——即 <c>dump_blender.py</c> 钉死的那一组，
/// 而不是 <c>hd2_style.py:main()</c> 的默认值。两者不同，照后者移植会得到另一个结果。
/// <para>
/// 刻意<b>没有</b>移植的实验分支（生产路径永远不会走到，移植它们只会引入无人验证的代码）：
/// <c>skeleton='snap'</c>、<c>align</c> 的 kabsch/rotz180、<c>spine_map='legacy'</c>、
/// <c>fallback</c> 的 parent/distance、<c>arm_rot_mode</c> 的 axis/seq/blend、
/// <c>arm_scratch_upper_gain/shift</c>（现版本已被"锁定点"模式取代，只打日志）、
/// 以及 <c>HD2_ARM_*</c> 环境变量开关。它们对应的枚举值保留，但语义是"照生产配置执行"。
/// </para>
/// <para>
/// Defaults are the <b>shipping configuration</b>, i.e. the exact set <c>dump_blender.py</c>
/// pins, not <c>hd2_style.py:main()</c>'s defaults.  The two differ, and porting the latter
/// produces a different mesh.
/// </para>
/// </summary>
public sealed record RetargetOptions
{
	// ---- 1a / 1b: 对齐与统一缩放 -------------------------------------------
	public RetargetAlignment Alignment { get; init; } = RetargetAlignment.Yaw;

	public RetargetSpineMap SpineMap { get; init; } = RetargetSpineMap.Mesh;

	/// <summary>统一缩放；<c>0</c> = 由"脚底 → 头关节"推出。</summary>
	public double Scale { get; init; }

	/// <summary>头关节相对 HD2 的垂直偏移（米）。</summary>
	public double HeadOffset { get; init; } = -0.015;

	// ---- 2b / 2c: 站姿 -----------------------------------------------------
	/// <summary>双腿绕脚踝前倾的角度（度），让脚踝落在髋部之后，与 HD2 的骨架一致。</summary>
	public double LegTilt { get; init; } = 5.7;

	/// <summary>正视图中双腿绕大腿根外张的角度（度），脚掌保持水平。</summary>
	public double LegSplay { get; init; } = 4.0;

	// ---- 3 / 4b: 骨架 ------------------------------------------------------
	public RetargetSkeleton Skeleton { get; init; } = RetargetSkeleton.Native;

	/// <summary>把轴对称 delta 烘进工作 rest。</summary>
	public bool AxisDeltas { get; init; } = true;

	/// <summary>发货前把轴对称轴转回原始轴（BatchTool 的最后一步）。</summary>
	public bool AxisRoundtrip { get; init; } = true;

	// ---- post: 仅作用于网格的后处理 ----------------------------------------
	public Vec3 MeshShift { get; init; } = new(0, -0.100, -0.090);

	public double UpperScale { get; init; } = 1.12;

	public double? UpperPivotZ { get; init; } = 1.15;

	public double UpperBlend { get; init; } = 0.45;

	public double HeadScale { get; init; } = 8.0 / 7.0;

	// ---- 4: 手臂 -----------------------------------------------------------
	/// <summary>用"绕源关节旋转源骨架"的方式摆手臂（<c>arm_scratch</c>）。生产配置为真。</summary>
	public bool ArmScratch { get; init; } = true;

	/// <summary>肘部瞄准"我们的肘 → HD2 的手腕"。</summary>
	public bool ArmAimWrist { get; init; } = true;

	/// <summary>在肘部平面内额外张开的角度（度）。正 = 更直。</summary>
	public double ArmElbowOpen { get; init; }

	/// <summary>手掌绕世界 +Y 的翘起角（度）。</summary>
	public double ArmHandTilt { get; init; }

	/// <summary>小臂长度补偿；<c>null</c> = 由瞄准解出。</summary>
	public double? ArmForearmGain { get; init; }

	/// <summary>
	/// 小臂长度补偿的径向管闸是否对手/指豁免。<b>必须为真</b>：闸是绕"肘→腕"线段半径
	/// <see cref="RetargetConstants.ForearmGateRadius"/> 的圆管，而拇指远端在腕后 0.0418–0.1437 m，
	/// 会被圆管拦腰切断并剪切——这就是"拇指反曲"。生产配置为真。
	/// </summary>
	public bool ForearmGateHandExempt { get; init; } = true;

	// ---- 4e: 仅网格的手臂重拟合 --------------------------------------------
	/// <summary>
	/// 是否允许重拟合在 <see cref="ArmScratch"/> 路径上也跑。默认关：这是第 77 轮的 A/B 开关，
	/// 打开会改变发货字节流。对应 <c>--arm-refit-scratch</c>。
	/// </summary>
	public bool ArmRefitScratch { get; init; }

	/// <summary>重拟合的总强度；<c>0</c> = 完全跳过。</summary>
	public double ArmFit { get; init; } = 1.0;

	/// <summary>残差里"竖直分量"的系数。这个分量是唯一能修正"网格小臂/手掌相对骨骼偏高几厘米"的项。</summary>
	public double ArmUp { get; init; } = 0.55;

	/// <summary>残差里"前后分量"的系数。<b>默认 0</b>：<see cref="MeshShift"/> 已经对着参考包拟合过，
	/// 这里再放一份就是双重计数——满强度时手臂会落到参考包之后 12 cm。</summary>
	public double ArmPerp { get; init; }

	/// <summary>拉普拉斯平滑轮数。LBS 权重在胸/锁骨边界变化很快，不平滑会把 1 cm 的边拉长约 11 倍。</summary>
	public int ArmSmooth { get; init; } = 8;

	/// <summary>沿本侧手臂轴的渐入区间（米）。原点在肩关节，<c>(-0.05, 0.22)</c> 让腋下/胸部
	/// 那团被源权重错误交给 <c>l_shoulder</c> 的几何保持焊接，只有手臂本身滑出去。</summary>
	public (double Start, double End)? ArmTaper { get; init; } = (-0.05, 0.22);

	// ---- 4f: 手指 ----------------------------------------------------------
	public bool FingerPose { get; init; } = true;

	public double FingerPoseAmount { get; init; } = 1.0;

	/// <summary>整体蜷曲比例；<c>null</c> = 用 <see cref="FingerPoseAmount"/>。</summary>
	public double? FingerCurlAmount { get; init; }

	/// <summary>按指名覆盖蜷曲比例，可含 <c>"*"</c> 作兜底。例如 <c>{ ["thumb"] = 1.0 }</c>。</summary>
	public IReadOnlyDictionary<string, double>? FingerCurlMap { get; init; }

	/// <summary>只摆指名的链（茎名如 <c>middle</c>，或 <c>l_index</c>）。<c>null</c> = 全部。</summary>
	public IReadOnlyList<string>? FingerPoseOnly { get; init; }

	// ---- 4g / 4h / 4i: 手 --------------------------------------------------
	public HandReorientMode HandReorient { get; init; } = HandReorientMode.Solve;

	/// <summary><see cref="HandReorientMode.Forced"/> 时使用的角度（度）。</summary>
	public double HandReorientDegrees { get; init; }

	public double HandReorientRangeLow { get; init; } = -20.0;

	public double HandReorientRangeHigh { get; init; } = 20.0;

	/// <summary>腕部接缝的羽化半宽（米）。</summary>
	public double HandReorientFeather { get; init; } = 0.025;

	/// <summary>整手缩放的基础倍数。</summary>
	public double PalmScale { get; init; } = 1.0;

	/// <summary>按实测掌围归一化到 <see cref="RetargetConstants.PalmReferenceGirth"/>。</summary>
	public bool PalmScaleAuto { get; init; }

	/// <summary>绕掌法线转手的角度（度）。</summary>
	public double HandTurn { get; init; }

	// ---- 其他 --------------------------------------------------------------
	public RetargetFallback Fallback { get; init; } = RetargetFallback.Region;
}

/// <summary>
/// 管线里的具名常量。每一个都带"为什么是这个值"的出处——它们不是调参调出来的，
/// 而是量出来的，所以改动之前应当先重跑对应的测量。
/// </summary>
public static class RetargetConstants
{
	/// <summary>源骨名的统一前缀；剥掉它才能与 HD2 的短名对上。</summary>
	public const string SourceBonePrefix = "ValveBiped.Bip01_";

	/// <summary>Source 的长度单位是英寸。</summary>
	public const double InchesToMetres = 0.0254;

	/// <summary>
	/// 小臂长度补偿的径向管闸半径（米）。人体小臂半径约 40 mm，取 3 倍仍然放过所有
	/// 袖口/手套外壳，同时拒绝扫到几米外的披风（Lilium 的肘部加权披风就是这么被挡住的）。
	/// </summary>
	public const double ForearmGateRadius = 0.12;

	/// <summary>
	/// 自动手掌缩放的参考掌围（米）：掌轴 90 分位距离、排除拇指、T-pose，取 Lunalice
	/// ——基础缩放就是拿它调的。实测 MourningVeil 40.6 mm、Lunalice 40.9 mm、Lilium 37.4 mm。
	/// 这是缩放<b>唯一</b>允许依赖的常量，其余全部按模型实测。
	/// </summary>
	public const double PalmReferenceGirth = 0.0409;

	/// <summary>掌板取掌轴上 <c>[0.15, 0.95] × 掌长</c> 这一段；排除掌根与指根的重叠区。</summary>
	public const double PalmSlabStart = 0.15;

	public const double PalmSlabEnd = 0.95;

	/// <summary>手/指影响判定的权重阈值。</summary>
	public const double HandInfluenceWeight = 0.20;

	/// <summary>
	/// 沿源骨架上溯找"这根辅助骨其实挂在哪一段肢体上"时允许的最大跳数。
	/// <para>
	/// 取 64 是因为模型的辅助骨链最长只有几跳（<c>source_extra_78</c> 一跳就到 <c>L_Hand</c>），
	/// 而 64 远小于任何真实骨架深度，所以它只在源骨架的 parent 数组自环或损坏时兜底——
	/// 那种情况下宁可判成"不是手臂"（不动），也不能顺着环一直走下去。
	/// </para>
	/// </summary>
	public const int MaxSourceAncestorHops = 64;

	/// <summary>手掌转正的搜索步长（度）。</summary>
	public const double HandReorientStepDegrees = 0.5;
}
