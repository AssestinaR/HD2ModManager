using HD2SourceModel;
using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 裙摆离体清空（<see cref="SkirtClearance"/>）的判据。
/// <para>
/// 为什么值得单测：这段只在 <b>转换那一刻</b> 跑一次，效果要到游戏里才看得见；
/// 而它改的是几何——偏错方向（往里偏）、偏到整条裙子上（下摆也一起飞）、
/// 或者顺手动了法线/权重，都会变成很难回查的观感事故。所以把四条判据钉在这里：
/// 只动裙类材质、只动水平径向、往下渐隐、其余字段逐字节保留。
/// </para>
/// </summary>
public sealed class SkirtClearanceTests
{
	private static readonly Vec3 Pelvis = new(0, 0, 1.0);

	private static RetargetedVertex Vertex(double x, double y, double z, uint boneHash = 0xAABBCCDD)
		=> new(new Vec3(x, y, z), new Vec3(0, 0, 1), 0.25f, 0.75f, [new RetargetedInfluence(boneHash, 1f)]);

	[Fact]
	public void ZeroAmountReturnsTheSameInstance()
	{
		var mesh = new RetargetedMesh(
			[Vertex(0.2, 0, 1.0)],
			[new RetargetedSection(0, [(0, 0, 0)])]);

		var result = SkirtClearance.Apply(mesh, ["materials/mat_ornament_metal_skirt.vmt"], Pelvis, 0);

		Assert.Same(mesh, result);
	}

	[Fact]
	public void MeshWithoutSkirtMaterialIsUntouched()
	{
		var mesh = new RetargetedMesh(
			[Vertex(0.2, 0, 1.0)],
			[new RetargetedSection(0, [(0, 0, 0)])]);

		var result = SkirtClearance.Apply(mesh, ["materials/body.vmt"], Pelvis, 0.02);

		Assert.Same(mesh, result);
	}

	[Fact]
	public void OnlySkirtSectionVerticesMove()
	{
		// 顶点 0 属于裙类段，顶点 1 属于身体段；两段共用同一份顶点表。
		var mesh = new RetargetedMesh(
			[Vertex(0.2, 0, 1.0), Vertex(-0.3, 0, 1.0)],
			[
				new RetargetedSection(0, [(0, 0, 0)]),
				new RetargetedSection(1, [(1, 1, 1)]),
			]);

		var result = SkirtClearance.Apply(
			mesh, ["materials/mat_ornament_metal_skirt.vmt", "materials/body.vmt"], Pelvis, 0.02);

		Assert.Equal(0.22, result.Vertices[0].Position.X, 6);
		Assert.Equal(-0.3, result.Vertices[1].Position.X, 6);   // 身体段一个字节都不动
	}

	[Fact]
	public void OffsetIsHorizontalRadialAndKeepsHeight()
	{
		var mesh = new RetargetedMesh(
			[Vertex(0, 0.5, 1.1)],
			[new RetargetedSection(0, [(0, 0, 0)])]);

		var result = SkirtClearance.Apply(mesh, ["x/skirt.vmt"], Pelvis, 0.02);
		var position = result.Vertices[0].Position;

		Assert.Equal(0, position.X, 9);        // 纯 +Y 方向上的点，X 不该被挪
		Assert.Equal(0.52, position.Y, 6);
		Assert.Equal(1.1, position.Z, 9);      // 高度不变（否则裙子会变长）
	}

	[Fact]
	public void OffsetFadesOutBelowTheHip()
	{
		var mesh = new RetargetedMesh(
			[Vertex(0.2, 0, 1.0), Vertex(0.2, 0, 0.875), Vertex(0.2, 0, 0.7)],
			[new RetargetedSection(0, [(0, 1, 2)])]);

		var result = SkirtClearance.Apply(
			mesh, ["x/skirt.vmt"], Pelvis, 0.02, falloffHeight: 0.25);

		Assert.Equal(0.22, result.Vertices[0].Position.X, 6);   // 腰线：满额
		Assert.Equal(0.21, result.Vertices[1].Position.X, 6);   // 半程：一半
		Assert.Equal(0.2, result.Vertices[2].Position.X, 6);    // 下摆：不动
	}

	[Fact]
	public void NormalsUvsAndInfluencesArePreserved()
	{
		var mesh = new RetargetedMesh(
			[Vertex(0.2, 0, 1.0, boneHash: 0x12345678)],
			[new RetargetedSection(0, [(0, 0, 0)])]);

		var result = SkirtClearance.Apply(mesh, ["x/skirt.vmt"], Pelvis, 0.02);
		var before = mesh.Vertices[0];
		var after = result.Vertices[0];

		Assert.Equal(before.Normal, after.Normal);
		Assert.Equal(before.U, after.U);
		Assert.Equal(before.V, after.V);
		Assert.Equal(before.Influences, after.Influences);
	}
}
