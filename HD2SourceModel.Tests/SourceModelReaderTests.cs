using static HD2SourceModel.Tests.SourceFormatFixtures;

namespace HD2SourceModel.Tests;

/// <summary>
/// MDL 解析：骨架（含 bind 矩阵求逆）、jigglebone 参数、bodypart/model/mesh 层级。
/// </summary>
public sealed class SourceModelReaderTests
{
	private static BodyPartSpec[] OneBodyPart(int vertexCount = 6, int vertexIndex = 0) =>
		[new("head", [new ModelSpec("m0", vertexCount, vertexIndex, [new MeshSpec(0, vertexCount, 0)])])];

	[Fact]
	public void Read_RejectsForeignMagic()
	{
		var mdl = BuildMdl("x", 48, [new BoneSpec("root", -1)], OneBodyPart());
		mdl[0] = (byte)'X';

		var error = Assert.Throws<InvalidDataException>(() => SourceModelReader.Read(mdl));
		Assert.Contains("IDST", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Read_RejectsVersionOutsideTheSupportedStrideRange()
	{
		// 骨骼结构体步长随版本变化；版本不在覆盖范围内必须拒绝而不是猜。
		var mdl = BuildMdl("x", 54, [new BoneSpec("root", -1)], OneBodyPart());

		Assert.Throws<NotSupportedException>(() => SourceModelReader.Read(mdl));
	}

	[Fact]
	public void Read_AcceptsEveryVersionSeenInRealL4D2Models()
	{
		// 实测求生之路 2 主模型的版本是 44 / 48 / 49 / 53，它们的骨骼步长都是 216。
		foreach (var version in new[] { 44, 48, 49, 53 })
		{
			var mdl = BuildMdl("x", version, [new BoneSpec("root", -1)], OneBodyPart());
			Assert.Equal(version, SourceModelReader.Read(mdl).Version);
		}
	}

	[Fact]
	public void Read_ReadsNameChecksumAndBoneHierarchy()
	{
		var mdl = BuildMdl(
			"survivor_teenangst",
			48,
			[
				new BoneSpec("ValveBiped.Bip01_Pelvis", -1),
				new BoneSpec("ValveBiped.Bip01_Spine", 0),
			],
			OneBodyPart());

		var document = SourceModelReader.Read(mdl);

		Assert.Equal(48, document.Version);
		Assert.Equal("survivor_teenangst", document.Name);
		Assert.Equal(2, document.Bones.Count);
		Assert.Equal("ValveBiped.Bip01_Pelvis", document.Bones[0].Name);
		Assert.Equal(-1, document.Bones[0].ParentIndex);
		Assert.Equal(0, document.Bones[1].ParentIndex);
		Assert.Empty(document.Diagnostics);
	}

	[Fact]
	public void Read_WorldBindIsTheInverseOfPoseToBone()
	{
		// 两个引擎的世界 bind 都等于 bind 矩阵的逆；搞错方向的后果是每段骨长都算成 0。
		var mdl = BuildMdl(
			"x",
			48,
			[
				new BoneSpec("root", -1),
				new BoneSpec("child", 0, Translation3x4(2, 4, 6)),
			],
			OneBodyPart());

		var document = SourceModelReader.Read(mdl);

		Assert.Equal(new Vec3(0, 0, 0), document.Bones[0].WorldBind.Origin);
		Assert.Equal(new Vec3(-2, -4, -6), document.Bones[1].WorldBind.Origin);
	}

	[Fact]
	public void Read_WorldBindInvertsARotationAndItsTranslationTogether()
	{
		// 绕 Z 转 90° 再平移 (1,0,0)：逆矩阵必须把 (1,0,0) 映回原点。
		float[] pose = [0, -1, 0, 1, 1, 0, 0, 0, 0, 0, 1, 0];
		var mdl = BuildMdl("x", 48, [new BoneSpec("root", -1, pose)], OneBodyPart());

		var document = SourceModelReader.Read(mdl);
		var inverse = document.Bones[0].WorldBind;

		var origin = inverse.TransformPoint(new Vec3(1, 0, 0));
		Assert.Equal(0, origin.X, 9);
		Assert.Equal(0, origin.Y, 9);
		Assert.Equal(0, origin.Z, 9);
	}

	[Fact]
	public void Read_ReadsEveryJiggleParameterOfAJiggleBone()
	{
		// 这是整条迁移链路里唯一"不需要猜"的输入：源模型自己写好的弹簧参数。
		const int flags = SourceJiggleBone.Flexible | SourceJiggleBone.YawConstrained;
		var mdl = BuildMdl(
			"x",
			48,
			[new BoneSpec("hair", -1, JiggleFlags: flags, JiggleValues: JiggleValues())],
			OneBodyPart());

		var document = SourceModelReader.Read(mdl);
		var jiggle = Assert.Single(document.JiggleBones).Jiggle!;

		Assert.Equal(flags, jiggle.Flags);
		Assert.True(jiggle.IsFlexible);
		Assert.False(jiggle.IsRigid);
		Assert.True(jiggle.HasYawConstraint);
		Assert.False(jiggle.HasBaseSpring);

		// 取值约定：第 i 个参数 = i + 0.5f，所以首尾两项足以钉住整块的字节布局。
		Assert.Equal(0.5f, jiggle.Length);
		Assert.Equal(1.5f, jiggle.TipMass);
		Assert.Equal(28.5f, jiggle.BaseForwardFriction);
	}

	[Fact]
	public void Read_LeavesOrdinaryBonesWithoutJiggleParameters()
	{
		var mdl = BuildMdl("x", 48, [new BoneSpec("root", -1)], OneBodyPart());

		var document = SourceModelReader.Read(mdl);

		Assert.Null(Assert.Single(document.Bones).Jiggle);
		Assert.Empty(document.JiggleBones);
	}

	[Fact]
	public void Read_UnnamedBoneFallsBackToAStablePlaceholderName()
	{
		// 骨名读不出来是常态（sznameindex 指向内嵌 SMD 文本），需要一个与下标一一对应的占位名。
		var mdl = BuildMdl(
			"x",
			48,
			[new BoneSpec("ValveBiped.Bip01_Pelvis", -1), new BoneSpec(null, 0)],
			OneBodyPart());

		var document = SourceModelReader.Read(mdl);

		Assert.True(document.Bones[0].HasName);
		Assert.Equal("ValveBiped.Bip01_Pelvis", document.Bones[0].DisplayName);
		Assert.False(document.Bones[1].HasName);
		Assert.Equal("source_extra_1", document.Bones[1].DisplayName);
	}

	[Fact]
	public void Read_ReadsBodyPartModelMeshTree()
	{
		// vertexindex 是字节偏移，必须除以 48 才是 VVD 的顶点下标；漏掉这一步网格会整体错位。
		var mdl = BuildMdl(
			"x",
			48,
			[new BoneSpec("root", -1)],
			[
				new BodyPartSpec("Dress_Skirt",
				[
					new ModelSpec("m0", 12, 96,
					[
						new MeshSpec(0, 8, 0),
						new MeshSpec(1, 4, 8),
					]),
				]),
			],
			["models/foo/bar.vmt"]);

		var document = SourceModelReader.Read(mdl);
		var part = Assert.Single(document.BodyParts);

		Assert.Equal("Dress_Skirt", part.Name);
		var model = Assert.Single(part.Models);
		Assert.Equal("m0", model.Name);
		Assert.Equal(12, model.VertexCount);
		Assert.Equal(2, model.VertexOffset);
		Assert.Equal(2, model.Meshes.Count);
		Assert.Equal(1, model.Meshes[1].MaterialIndex);
		Assert.Equal(4, model.Meshes[1].VertexCount);
		Assert.Equal(8, model.Meshes[1].VertexStart);
		Assert.Equal("models/foo/bar.vmt", Assert.Single(document.MaterialPaths));
	}

	[Fact]
	public void Read_DiagnosesAModelWhoseVertexIndexIsNotAMultipleOf48()
	{
		var mdl = BuildMdl(
			"x",
			48,
			[new BoneSpec("root", -1)],
			[new BodyPartSpec("head", [new ModelSpec("m0", 6, 100, [new MeshSpec(0, 6, 0)])])]);

		var document = SourceModelReader.Read(mdl);
		var model = Assert.Single(Assert.Single(document.BodyParts).Models);

		Assert.Contains(document.Diagnostics, item => item.Code == "ModelVertexIndexMisaligned");
		// 读不出顶点起点的 model 只能被跳过，否则产出的几何是错位的。
		Assert.Equal(0, model.VertexCount);
	}

	[Fact]
	public void Read_DiagnosesAnUnreadableMaterialTable()
	{
		var mdl = BuildMdl("x", 48, [new BoneSpec("root", -1)], OneBodyPart(), ["a.vmt"]);
		// 把材质表偏移指到文件外。
		System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(mdl.AsSpan(208), mdl.Length + 1024);

		var document = SourceModelReader.Read(mdl);

		Assert.Contains(document.Diagnostics, item => item.Code == "MaterialTableUnreadable");
		Assert.Empty(document.MaterialPaths);
	}
}
