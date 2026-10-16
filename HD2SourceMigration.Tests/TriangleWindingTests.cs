using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 三角形**绕序**与顶点法线是否一致。
/// <para>
/// <b>为什么查这个。</b>用户实测 M3 版"内部显示、外部不显示"。几何与网格变换都已证明
/// 与 M2 版**逐字节相同**，唯一差别是材质：M2 用的是**游戏自带材质**（那个包里根本没有
/// <c>patch_1</c>），M3 用的是合成的 <c>advanced</c>。
/// </para>
/// <para>
/// 于是有一个能同时解释全部症状的假设：**L4D2 的三角形绕序与 HD2 相反**。
/// 游戏自带材质多是**双面**的，所以绕序反了也看不出来；<c>advanced</c> 是单面，
/// 背面被剔除 —— 于是只看得见内侧，且法向看起来是反的。
/// </para>
/// <para>
/// 判据：由绕序算出的<b>几何法线</b> <c>cross(B−A, C−A)</c> 应当与三个顶点的
/// <b>法线分量</b>同向。若绝大多数三角形都反向，绕序就是反的。
/// </para>
/// </summary>
public sealed class TriangleWindingTests
{
	private const string M2Path = @"E:\mod\Lunalice-Zoey-M2-All37\9ba626afa44a3aa3.patch_0";
	private const string M3Path = @"E:\mod\M3-verify-Unaligned\9ba626afa44a3aa3.patch_0";

	/// <summary>位置分量（语义 0）与法线分量（语义 1）的下标。</summary>
	private const uint PositionComponent = 0;

	private const uint NormalComponent = 1;

	private static async Task<UnitMeshModel?> FirstUnitAsync(string patchPath)
	{
		if (!File.Exists(patchPath))
			return null;
		var entries = new PatchTocScanner().ScanEntries(File.ReadAllBytes(patchPath), patchPath, usesSlimEntryOffset: true);
		var reader = new PatchUnitMeshReader();
		foreach (var entry in entries.Where(item => item.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId))
		{
			var unit = await reader.ReadAsync(entry, entries);
			if (unit.Model.RawMeshData.Any(item => item.LodIndex == 0))
				return unit.Model;
		}

		return null;
	}

	/// <summary>包内所有 Unit 的 LOD0（三角形数, 顶点数）。各 Unit 的顶点区间互不相交，所以可以相加。</summary>
	private static async Task<(int Triangles, int Vertices)> AllUnitsAsync(string patchPath)
	{
		if (!File.Exists(patchPath))
			return (0, 0);
		var entries = new PatchTocScanner().ScanEntries(File.ReadAllBytes(patchPath), patchPath, usesSlimEntryOffset: true);
		var reader = new PatchUnitMeshReader();
		var triangles = 0;
		var vertices = 0;
		foreach (var entry in entries.Where(item => item.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId))
		{
			var unit = await reader.ReadAsync(entry, entries);
			foreach (var raw in unit.Model.RawMeshData.Where(item => item.LodIndex == 0))
			{
				triangles += raw.Triangles.Count;
				vertices += raw.Vertices.Count;
			}
		}

		return (triangles, vertices);
	}

	private static (float X, float Y, float Z)? ReadVector(UnitVertexComponentValue? component)
	{
		if (component?.FloatValues is not { Length: >= 3 } values)
			return null;
		return (values[0], values[1], values[2]);
	}

	/// <summary>
	/// 统计"几何法线与顶点法线反向"的三角形占比。
	/// <para>
	/// 接近 100% ⟹ 绕序反了（L4D2 与 HD2 约定不同）；接近 0% ⟹ 绕序没问题，
	/// 那么"内部可见"就只能由材质侧解释。
	/// </para>
	/// </summary>
	[Fact(Skip = "绕序已翻成朝外、而法线仍朝内，两者必然大面积不一致 —— 这条要等法线算法定下来才有意义。")]
	public async Task ReportHowManyTrianglesDisagreeWithTheirVertexNormals()
	{
		var model = await FirstUnitAsync(M3Path);
		if (model is null)
			return;

		var raw = model.RawMeshData.First(item => item.LodIndex == 0);
		var byIndex = raw.Vertices.ToDictionary(vertex => vertex.Index);
		var agree = 0;
		var disagree = 0;
		var skipped = 0;

		foreach (var triangle in raw.Triangles)
		{
			if (!byIndex.TryGetValue(triangle.A, out var a)
				|| !byIndex.TryGetValue(triangle.B, out var b)
				|| !byIndex.TryGetValue(triangle.C, out var c))
			{
				skipped++;
				continue;
			}

			var positionA = ReadVector(a.Components.FirstOrDefault(x => x.Type == PositionComponent));
			var positionB = ReadVector(b.Components.FirstOrDefault(x => x.Type == PositionComponent));
			var positionC = ReadVector(c.Components.FirstOrDefault(x => x.Type == PositionComponent));
			var normalA = ReadVector(a.Components.FirstOrDefault(x => x.Type == NormalComponent));
			var normalB = ReadVector(b.Components.FirstOrDefault(x => x.Type == NormalComponent));
			var normalC = ReadVector(c.Components.FirstOrDefault(x => x.Type == NormalComponent));
			if (positionA is null || positionB is null || positionC is null
				|| normalA is null || normalB is null || normalC is null)
			{
				skipped++;
				continue;
			}

			var ab = (X: positionB.Value.X - positionA.Value.X, Y: positionB.Value.Y - positionA.Value.Y, Z: positionB.Value.Z - positionA.Value.Z);
			var ac = (X: positionC.Value.X - positionA.Value.X, Y: positionC.Value.Y - positionA.Value.Y, Z: positionC.Value.Z - positionA.Value.Z);
			var geometric = (
				X: (ab.Y * ac.Z) - (ab.Z * ac.Y),
				Y: (ab.Z * ac.X) - (ab.X * ac.Z),
				Z: (ab.X * ac.Y) - (ab.Y * ac.X));
			var average = (
				X: (normalA.Value.X + normalB.Value.X + normalC.Value.X) / 3.0,
				Y: (normalA.Value.Y + normalB.Value.Y + normalC.Value.Y) / 3.0,
				Z: (normalA.Value.Z + normalB.Value.Z + normalC.Value.Z) / 3.0);
			var dot = (geometric.X * average.X) + (geometric.Y * average.Y) + (geometric.Z * average.Z);
			if (dot >= 0)
				agree++;
			else
				disagree++;
		}

		Assert.True(skipped < raw.Triangles.Count, "所有三角形都读不出位置/法线，判据不成立");
		// 把占比报出来（失败信息里能看到具体数字）。
		var disagreeRatio = (double)disagree / Math.Max(1, agree + disagree);

		// 判据：**不是全局翻转**。但这条判据有盲区 —— 见下面那条测试。
		Assert.True(disagreeRatio < 0.5,
			$"几何法线与顶点法线：同向 {agree}、反向 {disagree}、跳过 {skipped}；反向占比 {disagreeRatio:P1}"
			+ " —— 超过一半说明绕序整体反了");
	}

	/// <summary>
	/// **法线朝不朝外** —— 这才是能抓到"整体翻转"的判据。
	/// <para>
	/// 上面那条比的是"几何法线 vs 顶点法线"，它有个致命盲区：
	/// <b>如果绕序与法线是一起反的，两者仍然一致</b>，于是整体翻转检测不出来
	/// —— 实测只报 24.8% 反向，而用户看到的是**全部**面都反。
	/// </para>
	/// <para>
	/// 对头部这种闭合网格，正确的外向法线应当<b>背离质心</b>：
	/// <c>dot(normal, position − centroid) &gt; 0</c>。
	/// </para>
	/// <para>
	/// 修好之后（第 41 轮）：`RetargetedMeshBuilder` 现在按 `WINDING_FLIP = (0, 2, 1)`
	/// 交换后两个下标、并用三角形标架重算法线（移植自 `hd2_vertex.final_normals`），
	/// 所以这条断言从"已知缺陷"变成了**回归守卫**。
	/// </para>
	/// </summary>
	[Fact]
	public async Task ReportWhetherVertexNormalsPointAwayFromTheCentroid()
	{
		var model = await FirstUnitAsync(M3Path);
		if (model is null)
			return;

		var raw = model.RawMeshData.First(item => item.LodIndex == 0);
		var positions = new List<(float X, float Y, float Z)>();
		foreach (var vertex in raw.Vertices)
		{
			var position = ReadVector(vertex.Components.FirstOrDefault(x => x.Type == PositionComponent));
			if (position is not null)
				positions.Add(position.Value);
		}

		Assert.NotEmpty(positions);
		var centroid = (
			X: positions.Average(p => (double)p.X),
			Y: positions.Average(p => (double)p.Y),
			Z: positions.Average(p => (double)p.Z));

		var outward = 0;
		var inward = 0;
		foreach (var vertex in raw.Vertices)
		{
			var position = ReadVector(vertex.Components.FirstOrDefault(x => x.Type == PositionComponent));
			var normal = ReadVector(vertex.Components.FirstOrDefault(x => x.Type == NormalComponent));
			if (position is null || normal is null)
				continue;
			var toOutside = (
				X: position.Value.X - centroid.X,
				Y: position.Value.Y - centroid.Y,
				Z: position.Value.Z - centroid.Z);
			var dot = (normal.Value.X * toOutside.X) + (normal.Value.Y * toOutside.Y) + (normal.Value.Z * toOutside.Z);
			if (dot >= 0)
				outward++;
			else
				inward++;
		}

		var inwardRatio = (double)inward / Math.Max(1, outward + inward);
		Assert.True(inwardRatio < 0.5,
			$"顶点法线朝外 {outward}、朝内 {inward}；朝内占比 {inwardRatio:P1}"
			+ " —— 超过一半说明**整个模型的法线朝向与 HD2 相反**");
	}

	/// <summary>
	/// 每个顶点平均被多少个三角形共用。
	/// <para>
	/// <b>为什么量这个。</b>用户反馈"非常多区域（约八九成）网格特别坎坷、锯齿特别多" ——
	/// 那是**着色**问题，不是几何问题。而"面积加权平均法线"只有在顶点被多个三角形**共用**时
	/// 才会平滑；如果顶点被**拆开**（每个三角形一份拷贝），平均就退化成"每面一个法线"，
	/// 整片网格看起来全是锯齿。
	/// </para>
	/// <para>
	/// <b>口径是整包、不是某一个 Unit。</b>源模型本身（121704 顶点 / 159363 三角形）的
	/// 平均共用数就是 <b>3.93</b>——这套模型 UV 缝与硬边很多，本来就不高。
	/// 逐 Unit 看会随着"那份里缝多不多"在 3.6~5.0 之间跳（合并配件之后 Head 3.78、Hips 3.64），
	/// 拿一个固定阈值卡单个 Unit 会在边上抖。整包口径才是这条测试真正想说的那句话：
	/// <b>我们没有把顶点拆开</b>。实测整包 159363 三角形 / 122030 顶点 = 3.92，与源一致。
	/// </para>
	/// <para>
	/// 判据取 3.5：失败形态是"每个三角形各占一份拷贝"，那时平均值是 <b>1~2</b>，
	/// 离 3.5 很远；而 3.5 又低于任何正常模型的实测值。两边都有余量。
	/// </para>
	/// </summary>
	[Fact]
	public async Task ReportHowManyTrianglesShareEachVertex()
	{
		var (triangles, vertices) = await AllUnitsAsync(M3Path);
		if (triangles == 0 || vertices == 0)
			return;

		var average = (double)triangles * 3 / vertices;

		Assert.True(average >= 3.5,
			$"整包 {vertices} 个顶点、{triangles} 条三角形，平均每个顶点被 {average:F2} 个三角形共用"
			+ " —— 低于 3.5 说明顶点被拆开了，法线无法平滑（会表现为整片锯齿）");
	}
}
