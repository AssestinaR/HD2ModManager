using HD2SourceModel;

namespace HD2SourceMigration.Retarget;

/// <summary>一根手指链的摆姿结果，用于日志与验收（残差是"这条链还差多少度"）。</summary>
/// <param name="Side"><c>l</c> / <c>r</c>。</param>
/// <param name="Stem"><c>thumb</c> / <c>index</c> / <c>middle</c> / <c>ring</c> / <c>pinky</c>。</param>
/// <param name="Hd2Bones">三节 HD2 指骨名。</param>
/// <param name="SourceBones">三节源指骨名。</param>
/// <param name="AppliedDegrees">每节实际施加的转角（度）。</param>
/// <param name="ResidualDegrees">摆完之后每节仍偏离目标方向的角度（度）。</param>
/// <param name="SourceBendDegrees">源链自身的弯折（度）。实测十个链都是 0.00；非零说明这个模型的手不是直的，
/// 而"末节沿中节延长"的假设就不再成立。</param>
public sealed record FingerPoseRow(
	string Side,
	string Stem,
	IReadOnlyList<string> Hd2Bones,
	IReadOnlyList<string> SourceBones,
	IReadOnlyList<double> AppliedDegrees,
	IReadOnlyList<double> ResidualDegrees,
	double SourceBendDegrees);

/// <summary>一只手的手掌转正结果。</summary>
/// <param name="Before">转正前"手指肉 → HD2 指骨段"的平均距离（米）；强制角度时为 <c>null</c>。</param>
/// <param name="After">转正后的同一指标。</param>
public sealed record HandReorientRow(
	string Side,
	double Degrees,
	double? Before,
	double? After,
	int InfluencedVertices);

/// <summary>管线指标。全部是"能证伪"的量，不是装饰。</summary>
public sealed record RetargetMetrics(
	double Scale,
	double SourceHeight,
	double HeightBefore,
	double HeightAfter,
	double FootSpreadSource,
	double FootSpreadSnapped,
	double FootSpreadOutput,
	int SnappedBoneCount,
	IReadOnlyList<string> MissingPairs,
	IReadOnlyDictionary<int, int> FallbackHops);

/// <summary>
/// 一次重定向的全部结果。坐标一律是<b>米</b>，HD2 世界空间。
/// <para>
/// <see cref="Output"/> 是发货的网格顶点；<see cref="IdleOutput"/> 是游戏 idle 帧真正显示的
/// 位置（把 <see cref="Output"/> 再经 <c>idle = rest_hd2 · inv(rest_shipped)</c> 蒙皮一次）。
/// 在 <see cref="RetargetSkeleton.Native"/> 下后者等于前者——这正是"离线摆成什么样、游戏里就是什么样"
/// 的判据，也是唯一能和参考包对比的渲染。
/// </para>
/// </summary>
public sealed record RetargetResult(
	IReadOnlyList<Vec3> SourcePoints,
	IReadOnlyList<Vec3> Aligned,
	IReadOnlyList<Vec3> Output,
	IReadOnlyList<Vec3> IdleOutput,
	IReadOnlyDictionary<int, Mat4> WorkingRest,
	IReadOnlyDictionary<int, Mat4> ShippedRest,
	IReadOnlyDictionary<int, Mat4> Deform,
	IReadOnlyDictionary<int, int> Resolved,
	IReadOnlyDictionary<int, int> Hops,
	IReadOnlyList<FingerPoseRow> Fingers,
	IReadOnlyList<HandReorientRow> HandReorient,
	IReadOnlyDictionary<string, double> PalmGirth,
	RetargetMetrics Metrics)
{
	/// <summary>把结果里的顶点位置摊平成 <c>[x, y, z]</c> 数组，供 npz 落盘与逐点对拍。</summary>
	public static double[,] ToArray(IReadOnlyList<Vec3> points)
	{
		var result = new double[points.Count, 3];
		for (var i = 0; i < points.Count; i++)
		{
			result[i, 0] = points[i].X;
			result[i, 1] = points[i].Y;
			result[i, 2] = points[i].Z;
		}

		return result;
	}
}
