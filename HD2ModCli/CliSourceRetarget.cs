using HD2SourceMigration;
using HD2SourceMigration.Retarget;

namespace HD2ModCli;

// 作用：`source retarget` 的投影与人类可读输出。把 JSON 形状从命令分派里拿出来，
// 这样格式化的每一行都能被单独审查，而命令体只剩"读—算—写"。
// Purpose: the projection and human-readable output for `source retarget`.
internal sealed record CliSourceRetargetSummary(
	string VpkPath,
	string MdlPath,
	string OutputDirectory,
	int Vertices,
	int Triangles,
	int SourceBones,
	int Hd2Bones,
	int ResolvedBones,
	double Scale,
	double HeightBefore,
	double HeightAfter,
	double FootSpreadSource,
	double FootSpreadOutput,
	IReadOnlyList<string> Missing,
	IReadOnlyDictionary<int, int> Hops,
	IReadOnlyDictionary<string, double> PalmGirth,
	IReadOnlyList<HandReorientRow> HandReorient,
	IReadOnlyList<FingerPoseRow> Fingers);

internal static class CliSourceRetarget
{
	internal static CliSourceRetargetSummary Project(
		RetargetInputs inputs, Hd2Rig rig, RetargetResult result)
	{
		ArgumentNullException.ThrowIfNull(inputs);
		ArgumentNullException.ThrowIfNull(rig);
		ArgumentNullException.ThrowIfNull(result);
		var metrics = result.Metrics;
		return new CliSourceRetargetSummary(
			Path.GetFullPath(inputs.VpkPath),
			inputs.MdlPath,
			string.Empty,
			result.Output.Count,
			inputs.Geometry.Triangles.Count,
			inputs.Model.Bones.Count,
			rig.Bones.Count,
			result.Resolved.Count,
			metrics.Scale,
			metrics.HeightBefore,
			metrics.HeightAfter,
			metrics.FootSpreadSource,
			metrics.FootSpreadOutput,
			metrics.MissingPairs,
			metrics.FallbackHops,
			result.PalmGirth,
			result.HandReorient,
			result.Fingers);
	}

	internal static string Format(CliSourceRetargetSummary summary)
	{
		ArgumentNullException.ThrowIfNull(summary);
		var lines = new List<string>
		{
			$"重定向：{summary.MdlPath}",
			$"  网格 {summary.Vertices} 顶点 / {summary.Triangles} 三角形；源骨 {summary.SourceBones} 根，HD2 骨 {summary.Hd2Bones} 根，已绑定 {summary.ResolvedBones} 根",
			$"  统一缩放 {summary.Scale:F6}；身高 {summary.HeightBefore:F4} → {summary.HeightAfter:F4} m",
			$"  脚距：源 {summary.FootSpreadSource:F4} → 出图 {summary.FootSpreadOutput:F4} m",
			$"  已写出：{Path.Combine(summary.OutputDirectory, "model.npz")}",
		};

		if (summary.Missing.Count > 0)
			lines.Add($"  未命中对应 {summary.Missing.Count} 对：{string.Join(", ", summary.Missing.Take(8))}");

		if (summary.Hops.Count > 0)
			lines.Add($"  回退跳数分布：{string.Join(", ", summary.Hops.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}→{pair.Value}"))}");

		foreach (var (side, girth) in summary.PalmGirth.OrderBy(pair => pair.Key, StringComparer.Ordinal))
			lines.Add($"  {side} 掌围 {girth * 1000:F2} mm（参考 {RetargetConstants.PalmReferenceGirth * 1000:F2} mm）");

		foreach (var row in summary.HandReorient)
		{
			lines.Add(row.Before is null
				? $"  {row.Side} 手掌转正 {row.Degrees:+0.00;-0.00}°（强制），影响 {row.InfluencedVertices} 顶点"
				: $"  {row.Side} 手掌转正 {row.Degrees:+0.00;-0.00}°（解出：手指肉到 HD2 指骨 {row.Before * 1000:F2} → {row.After * 1000:F2} mm，{row.InfluencedVertices} 顶点）");
		}

		foreach (var finger in summary.Fingers)
		{
			lines.Add($"  {finger.Side}_{finger.Stem} 施加 {string.Join("/", finger.AppliedDegrees.Select(value => $"{value:F2}"))}°，残差 {string.Join("/", finger.ResidualDegrees.Select(value => $"{value:F3}"))}°（源链弯折 {finger.SourceBendDegrees:F3}°）");
		}

		return string.Join(Environment.NewLine, lines);
	}
}
