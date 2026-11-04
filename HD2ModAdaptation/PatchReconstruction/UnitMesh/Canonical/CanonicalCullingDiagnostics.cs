using System.Globalization;
using System.Text;

namespace HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;

/// <summary>
/// 包围盒重算的按需诊断（<c>HD2_CANONICAL_CULLING_DIAGNOSTICS=1</c>）。
/// <para>
/// <b>为什么它值得单独存在。</b>包围盒算错的唯一表现是"游戏里某块几何不见了"，而那时
/// 已经没有现场了。所以要在<b>重建那一刻</b>就把三个数记下来：
/// 原盒、算出来的盒、以及"有多少顶点落在盒外"。
/// 最后那个数是契约检查（<c>review_unit_contract.py</c> 断言 <c>vertices_outside == 0</c>）的同款判据。
/// </para>
/// <para>
/// 与 <see cref="CanonicalPositionDiagnostics"/> 同样的纪律：<b>诊断绝不影响重建结果</b>，
/// 任何异常都被吞掉。
/// </para>
/// </summary>
public static class CanonicalCullingDiagnostics
{
	private const string EnabledVariable = "HD2_CANONICAL_CULLING_DIAGNOSTICS";
	private const string FileName = "canonical-culling-diagnostics.log";
	private static readonly object Sync = new();

	public static bool IsEnabled
		=> string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal)
		|| string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "true", StringComparison.OrdinalIgnoreCase);

	/// <summary>每次重算记一行：每个网格的原盒、新盒、重算前后的"盒外顶点数"。</summary>
	public static void Record(
		UnitMeshModel model,
		IReadOnlyList<UnitRawMeshData> prepared,
		CanonicalCullingBoundsMode mode,
		IReadOnlyDictionary<int, float[]> computedByMesh)
	{
		if (!IsEnabled)
			return;
		foreach (var raw in prepared)
		{
			var mesh = model.Meshes.FirstOrDefault(candidate => candidate.Index == raw.MeshInfoIndex);
			var before = mesh?.CullingBounds.Values ?? [];
			var (outsideBefore, total) = CanonicalCullingBounds.CountOutside(raw, before);
			var computed = computedByMesh.TryGetValue(raw.MeshInfoIndex, out var values) ? values : null;
			var after = computed is null
				? before
				: mode == CanonicalCullingBoundsMode.Computed
					? computed
					: CanonicalCullingBounds.Union(before, computed);
			var (outsideAfter, _) = CanonicalCullingBounds.CountOutside(raw, after);
			Write(string.Format(
				CultureInfo.InvariantCulture,
				"mode={0} mesh={1} lod={2} vertices={3} outsideBefore={4} outsideAfter={5} before[{6}] computed[{7}] after[{8}]",
				mode,
				raw.MeshInfoIndex,
				raw.LodIndex,
				total,
				outsideBefore,
				outsideAfter,
				CanonicalCullingBounds.Describe(before),
				computed is null ? "none" : CanonicalCullingBounds.Describe(computed),
				CanonicalCullingBounds.Describe(after)));
		}
	}

	private static void Write(string message)
	{
		try
		{
			var directory = Path.Combine(AppContext.BaseDirectory, "logs");
			Directory.CreateDirectory(directory);
			var line = $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}";
			lock (Sync)
				File.AppendAllText(Path.Combine(directory, FileName), line, Encoding.UTF8);
		}
		catch
		{
			// 诊断绝不影响重建。
		}
	}
}
