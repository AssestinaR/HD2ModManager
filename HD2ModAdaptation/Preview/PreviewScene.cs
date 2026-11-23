using System.Numerics;

namespace HD2ModAdaptation.Preview;

// 作用：预览渲染器消费的几何中间表示。与 patch / unit 的二进制布局解耦，
//       渲染器只认这一层，于是"从哪里读网格"可以换（patch、游戏原版包、以后的内存缓存），
//       而"怎么画"不用动。
// Purpose: The geometry IR consumed by the preview renderer. It is decoupled from the
//          patch/unit binary layout, so the renderer never depends on where the mesh came from.

/// <summary>一个网格在预览里的分类。只有 <see cref="Visual"/> 参与渲染。</summary>
public enum PreviewMeshKind
{
	/// <summary>可见网格。</summary>
	Visual = 0,

	/// <summary>用于剔除的包围体（不可见）。</summary>
	CullingBody = 1,

	/// <summary>静态网格（不属于角色蒙皮）。</summary>
	StaticMesh = 2,
}

/// <summary>预览顶点。坐标与法线都在 unit 的本地空间；<see cref="Uv"/> 只给 v2 贴图预留。</summary>
public readonly record struct PreviewVertex(Vector3 Position, Vector3 Normal, Vector2 Uv);

/// <summary>预览三角形。索引是**网格内局部索引**（已减去 section 的 baseVertexOffset）。</summary>
public readonly record struct PreviewTriangle(int A, int B, int C, uint MaterialSlotId);

/// <summary>一个可渲染网格：来自某个 unit 的某个 mesh（某一个 LOD）。</summary>
public sealed record PreviewMesh(
	string UnitHex,
	int LodIndex,
	int MeshIndex,
	PreviewMeshKind Kind,
	IReadOnlyList<PreviewVertex> Vertices,
	IReadOnlyList<PreviewTriangle> Triangles)
{
	/// <summary>有没有画得出东西的三角形；空网格不占渲染预算。</summary>
	public bool HasGeometry => Triangles.Count > 0 && Vertices.Count >= 3;
}

/// <summary>场景包围球。相机取景与 pivot 归一化都以它为基准（规划 §6）。</summary>
public sealed record PreviewBoundingSphere(Vector3 Center, float Radius)
{
	/// <summary>包围盒下界（相机取景要判断"哪个轴是上"，光有包围球看不出来）。</summary>
	public Vector3 Min { get; init; }

	/// <summary>包围盒上界。</summary>
	public Vector3 Max { get; init; }

	/// <summary>各轴尺寸，日志与调试用。</summary>
	public Vector3 Size => Max - Min;

	/// <summary>退化包围球（没有几何或几何退化时）：半径取一个正数，避免除零。</summary>
	public static PreviewBoundingSphere Degenerate { get; } = new(Vector3.Zero, 1f);
}

/// <summary>
/// 一次场景构建的统计。刻意把"跳过了什么、失败了什么"都数出来：
/// 预览最大的风险是"看起来画出来了，其实少了一半"，光看三角形数看不出来。
/// </summary>
public sealed record PreviewSceneStats(
	int PatchCount,
	int UnitEntries,
	int UnitsRead,
	int UnitsFailed,
	int Meshes,
	int VisualMeshes,
	int Vertices,
	int Triangles,
	int SkippedCullingBodies,
	int SkippedStaticMeshes,
	int SkippedHigherLods,
	int SkippedFallbackLods,
	int SkippedPlaceholders,
	int DroppedTriangles)
{
	/// <summary>构建过程中值得人看的一句话（失败原因、被忽略的引用等），失败时一定非空。</summary>
	public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>构建结果。</summary>
public sealed record PreviewScene(
	IReadOnlyList<PreviewMesh> Meshes,
	PreviewBoundingSphere Bounds,
	PreviewSceneStats Stats)
{
	/// <summary>没有任何可渲染几何。</summary>
	public bool IsEmpty => Meshes.Count == 0;

	/// <summary>给日志/CLI 的一行摘要。</summary>
	public string Describe()
		=> $"units={Stats.UnitsRead}/{Stats.UnitEntries} meshes={Stats.VisualMeshes}/{Stats.Meshes} "
			+ $"vertices={Stats.Vertices} triangles={Stats.Triangles} "
			+ $"bounds=({Bounds.Center.X:0.###},{Bounds.Center.Y:0.###},{Bounds.Center.Z:0.###}) r={Bounds.Radius:0.###}";
}
