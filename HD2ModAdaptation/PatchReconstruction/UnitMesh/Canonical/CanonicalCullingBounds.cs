using System.Globalization;
using System.Numerics;

namespace HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;

/// <summary>
/// 追加几何之后要不要重算 <c>MeshInfo</c> 里那 7 个 float（包围盒 + 半径）。
/// </summary>
public enum CanonicalCullingBoundsMode
{
	/// <summary>
	/// <b>现行行为</b>：原样保留外壳的包围盒。默认值，三条在役路径（装饰合并 / 头盔合并 /
	/// 多源合并）都走这个，所以引入本功能不改变任何既有产物。
	/// </summary>
	Preserve = 0,

	/// <summary>
	/// <b>保守重算</b>：原范围 ∪ 按最终顶点算出来的范围。
	/// 只会变大、不会变小，所以不会让原本能画的东西被剔掉；代价是可能少剔一点（多画几块）。
	/// </summary>
	UnionWithComputed = 1,

	/// <summary>
	/// <b>紧盒</b>：只用算出来的范围。会缩小，因此可能剔掉原本为动画预留的空间。
	/// 只给实验路径用，必须实机验证过再考虑。
	/// </summary>
	Computed = 2,
}

/// <summary>
/// 重算网格包围盒。
/// <para>
/// <b>为什么必须有这一步。</b><c>CanonicalUnitRebuilder</c> 不动 <c>MeshInfo</c> 里那 7 个 float，
/// 所以追加几何之后留下的是<b>外壳的</b>包围盒。顶点跑到盒外，游戏的剔除就会把整块网格丢掉 ——
/// 表现为"几何整块消失但游戏不崩"。`UnitAuthor.cs` 的注释与 Python 契约检查
/// （断言 <c>vertices_outside == 0</c>）都写明了这一点。
/// </para>
/// <para>
/// <b>为什么默认关。</b>重算会改变三条在役路径的产物字节，而"盒变大"本身也有代价
/// （提交与着色工作增加，虽然不等于显存增加）。先做成能力 + 诊断，由实验/拆件路径显式打开。
/// </para>
/// </summary>
public static class CanonicalCullingBounds
{
	/// <summary>包围盒里那 7 个 float 的个数：min xyz / max xyz / 半径。</summary>
	public const int ValueCount = 7;

	/// <summary>
	/// 按 <paramref name="mode"/> 更新 <paramref name="model"/> 的每个网格包围盒。
	/// <paramref name="prepared"/> 是<b>最终顶点</b>（追加、材质重排、骨骼重编之后的那一份）。
	/// </summary>
	public static UnitMeshModel Apply(
		UnitMeshModel model,
		IReadOnlyList<UnitRawMeshData> prepared,
		CanonicalCullingBoundsMode mode)
	{
		ArgumentNullException.ThrowIfNull(model);
		ArgumentNullException.ThrowIfNull(prepared);
		if (mode == CanonicalCullingBoundsMode.Preserve || prepared.Count == 0)
			return model;

		var computedByMesh = new Dictionary<int, float[]>();
		foreach (var raw in prepared)
		{
			if (TryCompute(raw) is { } computed)
				computedByMesh[raw.MeshInfoIndex] = computed;
		}

		if (computedByMesh.Count == 0)
			return model;

		var meshes = model.Meshes
			.Select(mesh =>
			{
				if (!computedByMesh.TryGetValue(mesh.Index, out var computed))
					return mesh;
				var values = mode == CanonicalCullingBoundsMode.Computed
					? computed
					: Union(mesh.CullingBounds.Values, computed);
				return mesh with { CullingBounds = new UnitMeshCullingBounds(values) };
			})
			.ToArray();

		CanonicalCullingDiagnostics.Record(model, prepared, mode, computedByMesh);
		return model with { Meshes = meshes };
	}

	/// <summary>
	/// 按最终顶点算一份包围盒。<b>坐标空间必须与 MeshInfo 里那 7 个 float 一致</b> ——
	/// 也就是该 mesh 自己的局部空间（追加链路已经把顶点变换到目标 mesh 的局部空间）。
	/// 顶点里没有 position 分量时返回 <c>null</c>，此时调用方应当什么都不改。
	/// </summary>
	public static float[]? TryCompute(UnitRawMeshData raw)
	{
		ArgumentNullException.ThrowIfNull(raw);
		var minX = float.MaxValue;
		var minY = float.MaxValue;
		var minZ = float.MaxValue;
		var maxX = float.MinValue;
		var maxY = float.MinValue;
		var maxZ = float.MinValue;
		var radius = 0f;
		var seen = 0;
		foreach (var vertex in raw.Vertices)
		{
			foreach (var component in vertex.Components)
			{
				if (component.Type != 0 || component.Index != 0 || component.FloatValues.Length < 3)
					continue;
				var x = component.FloatValues[0];
				var y = component.FloatValues[1];
				var z = component.FloatValues[2];
				if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
					continue;
				minX = MathF.Min(minX, x);
				minY = MathF.Min(minY, y);
				minZ = MathF.Min(minZ, z);
				maxX = MathF.Max(maxX, x);
				maxY = MathF.Max(maxY, y);
				maxZ = MathF.Max(maxZ, z);
				radius = MathF.Max(radius, new Vector3(x, y, z).Length());
				seen++;
			}
		}

		if (seen == 0)
			return null;

		// 半径至少覆盖到盒子的最远角：只按顶点算半径会漏掉"盒角比任何顶点都远"的情况，
		// 而那个半径被引擎当作包围球用。
		var corner = MathF.Sqrt(
			MathF.Max(MathF.Abs(minX), MathF.Abs(maxX)) * MathF.Max(MathF.Abs(minX), MathF.Abs(maxX))
			+ MathF.Max(MathF.Abs(minY), MathF.Abs(maxY)) * MathF.Max(MathF.Abs(minY), MathF.Abs(maxY))
			+ MathF.Max(MathF.Abs(minZ), MathF.Abs(maxZ)) * MathF.Max(MathF.Abs(minZ), MathF.Abs(maxZ)));
		return [minX, minY, minZ, maxX, maxY, maxZ, MathF.Max(radius, corner)];
	}

	/// <summary>两份包围盒的并集：min 取小、max 取大、半径取大。只会变大。</summary>
	public static float[] Union(IReadOnlyList<float> existing, float[] computed)
	{
		ArgumentNullException.ThrowIfNull(computed);
		if (existing is null || existing.Count < ValueCount)
			return computed;
		return
		[
			MathF.Min(existing[0], computed[0]),
			MathF.Min(existing[1], computed[1]),
			MathF.Min(existing[2], computed[2]),
			MathF.Max(existing[3], computed[3]),
			MathF.Max(existing[4], computed[4]),
			MathF.Max(existing[5], computed[5]),
			MathF.Max(existing[6], computed[6]),
		];
	}

	/// <summary>
	/// 数一数有多少顶点落在给定的包围盒外。<b>这是"重算有没有必要"的判据</b>：
	/// 契约检查断言的就是这个数必须为 0。
	/// </summary>
	public static (int Outside, int Total) CountOutside(UnitRawMeshData raw, IReadOnlyList<float> bounds)
	{
		ArgumentNullException.ThrowIfNull(raw);
		ArgumentNullException.ThrowIfNull(bounds);
		if (bounds.Count < ValueCount)
			return (0, 0);
		var outside = 0;
		var total = 0;
		foreach (var vertex in raw.Vertices)
		{
			foreach (var component in vertex.Components)
			{
				if (component.Type != 0 || component.Index != 0 || component.FloatValues.Length < 3)
					continue;
				total++;
				var x = component.FloatValues[0];
				var y = component.FloatValues[1];
				var z = component.FloatValues[2];
				// 留一点容差：float32 累加误差不该被算成"跑出盒外"。
				const float Tolerance = 1e-4f;
				if (x < bounds[0] - Tolerance || x > bounds[3] + Tolerance
					|| y < bounds[1] - Tolerance || y > bounds[4] + Tolerance
					|| z < bounds[2] - Tolerance || z > bounds[5] + Tolerance)
				{
					outside++;
				}
			}
		}

		return (outside, total);
	}

	/// <summary>一句话摘要，给诊断日志用。</summary>
	public static string Describe(IReadOnlyList<float> bounds)
	{
		if (bounds is null || bounds.Count < ValueCount)
			return "none";
		return string.Format(
			CultureInfo.InvariantCulture,
			"min=({0:R},{1:R},{2:R}) max=({3:R},{4:R},{5:R}) r={6:R}",
			bounds[0], bounds[1], bounds[2], bounds[3], bounds[4], bounds[5], bounds[6]);
	}
}
