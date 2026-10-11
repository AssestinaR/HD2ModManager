using static HD2SourceModel.Tests.SourceFormatFixtures;

namespace HD2SourceModel.Tests;

/// <summary>
/// 几何装配：MDL + VVD + VTX 三者的全局顶点号公式，以及结构不一致时的失败方式。
/// </summary>
public sealed class SourceGeometryBuilderTests
{
	/// <summary>8 个顶点，位置是 (i,0,0)，下标即全局顶点号。</summary>
	private static SourceVertex[] Vertices() =>
		[.. Enumerable.Range(0, 8).Select(index => new SourceVertex(
			new Vec3(index, 0, 0), new Vec3(0, 1, 0), 0, 0,
			new SourceVertexWeight(0, 1f),
			new SourceVertexWeight(0, 0f),
			new SourceVertexWeight(0, 0f)))];

	/// <summary>一个 bodypart / 一个 model / 一个 mesh / 一个 strip group。</summary>
	private static VtxBodyPartSpec OneModel(ushort[] originals, ushort[] indices) =>
		BodyPart(SingleMesh(Group(originals, indices)));

	private static SourceIndexBodyPart[] IndexParts(params VtxBodyPartSpec[] bodyParts)
	{
		var vtx = BuildVtx(6, 25, bodyParts);
		return [.. SourceIndexReader.Read(vtx, out _, out _)];
	}

	[Fact]
	public void Build_ResolvesTheGlobalVertexIndexFromModelOffsetMeshStartAndOriginal()
	{
		// 全局顶点号 = model.VertexOffset + mesh.VertexStart + 原始顶点号。
		// 这里三项都刻意非零（offset=2、start=1、orig=0/1/2），少加任何一项都会指向别的顶点。
		var mdl = BuildMdl(
			"x", 48,
			[new BoneSpec("root", -1)],
			[new BodyPartSpec("head", [new ModelSpec("m0", 6, 96, [new MeshSpec(0, 6, 1)])])]);

		var geometry = SourceGeometryBuilder.Build(
			SourceModelReader.Read(mdl),
			Vertices(),
			IndexParts(OneModel([0, 1, 2], [0, 1, 2])));

		Assert.Equal(3, geometry.Vertices.Count);
		Assert.Equal(new Vec3(3, 0, 0), geometry.Vertices[0].Position);
		Assert.Equal(new Vec3(4, 0, 0), geometry.Vertices[1].Position);
		Assert.Equal(new Vec3(5, 0, 0), geometry.Vertices[2].Position);
	}

	[Fact]
	public void Build_EmitsTrianglesThatPointIntoTheAssembledVertexArray()
	{
		var mdl = BuildMdl(
			"x", 48,
			[new BoneSpec("root", -1)],
			[new BodyPartSpec("head", [new ModelSpec("m0", 6, 96, [new MeshSpec(7, 6, 1)])])]);

		var geometry = SourceGeometryBuilder.Build(
			SourceModelReader.Read(mdl),
			Vertices(),
			IndexParts(OneModel([0, 1, 2], [0, 1, 2, 0, 2, 1])));

		Assert.Equal(2, geometry.Triangles.Count);
		Assert.Equal(new SourceTriangle(0, 1, 2, 7), geometry.Triangles[0]);
		Assert.Equal(new SourceTriangle(0, 2, 1, 7), geometry.Triangles[1]);
	}

	[Fact]
	public void Build_RecordsWhereEachBodyPartLanded()
	{
		var mdl = BuildMdl(
			"x", 48,
			[new BoneSpec("root", -1)],
			[
				new BodyPartSpec("Dress_Skirt", [new ModelSpec("m0", 6, 0, [new MeshSpec(0, 6, 0)])]),
				new BodyPartSpec("head", [new ModelSpec("m1", 6, 48, [new MeshSpec(0, 6, 0)])]),
			]);

		var geometry = SourceGeometryBuilder.Build(
			SourceModelReader.Read(mdl),
			Vertices(),
			IndexParts(
				OneModel([0, 1, 2], [0, 1, 2]),
				OneModel([3, 4, 5], [0, 1, 2])));

		Assert.Equal(2, geometry.Parts.Count);
		Assert.Equal("Dress_Skirt", geometry.Parts[0].BodyPart);
		Assert.Equal(0, geometry.Parts[0].VertexStart);
		Assert.Equal(3, geometry.Parts[0].VertexCount);
		Assert.Equal("head", geometry.Parts[1].BodyPart);
		Assert.Equal(3, geometry.Parts[1].VertexStart);
		Assert.Equal(3, geometry.Parts[1].VertexCount);
	}

	[Fact]
	public void Build_SkipsModelsThatCarryNoVertices()
	{
		var mdl = BuildMdl(
			"x", 48,
			[new BoneSpec("root", -1)],
			[
				new BodyPartSpec("head",
				[
					new ModelSpec("empty", 0, 0, []),
					new ModelSpec("m0", 6, 0, [new MeshSpec(0, 6, 0)]),
				]),
			]);

		var geometry = SourceGeometryBuilder.Build(
			SourceModelReader.Read(mdl),
			Vertices(),
			IndexParts(BodyPart(Model(), SingleMesh(Group([0, 1, 2], [0, 1, 2])))));

		var part = Assert.Single(geometry.Parts);
		Assert.Equal("m0", part.Model);
		Assert.Equal(3, geometry.Vertices.Count);
	}

	[Fact]
	public void Build_ThrowsWhenMdlAndVtxDisagreeOnBodyPartCount()
	{
		var mdl = BuildMdl(
			"x", 48,
			[new BoneSpec("root", -1)],
			[new BodyPartSpec("head", [new ModelSpec("m0", 6, 0, [new MeshSpec(0, 6, 0)])])]);

		var error = Assert.Throws<InvalidDataException>(() => SourceGeometryBuilder.Build(
			SourceModelReader.Read(mdl),
			Vertices(),
			IndexParts(
				OneModel([0, 1, 2], [0, 1, 2]),
				OneModel([3, 4, 5], [0, 1, 2]))));

		Assert.Contains("bodypart", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_ThrowsWhenAnOriginalVertexExceedsTheMeshVertexCount()
	{
		var mdl = BuildMdl(
			"x", 48,
			[new BoneSpec("root", -1)],
			[new BodyPartSpec("head", [new ModelSpec("m0", 3, 0, [new MeshSpec(0, 3, 0)])])]);

		var error = Assert.Throws<InvalidDataException>(() => SourceGeometryBuilder.Build(
			SourceModelReader.Read(mdl),
			Vertices(),
			IndexParts(OneModel([0, 1, 7], [0, 1, 2]))));

		Assert.Contains("超出 mesh 的顶点数", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_ThrowsWhenTheGlobalVertexIndexLeavesTheVvd()
	{
		var mdl = BuildMdl(
			"x", 48,
			[new BoneSpec("root", -1)],
			[new BodyPartSpec("head", [new ModelSpec("m0", 6, 96 * 100, [new MeshSpec(0, 6, 0)])])]);

		var error = Assert.Throws<InvalidDataException>(() => SourceGeometryBuilder.Build(
			SourceModelReader.Read(mdl),
			Vertices(),
			IndexParts(OneModel([0, 1, 2], [0, 1, 2]))));

		Assert.Contains("超出 VVD", error.Message, StringComparison.Ordinal);
	}
}
