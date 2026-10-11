using HD2SourceModel;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 存储约定是这套代码里<b>唯一</b>搞错了不会报错的地方，所以单独钉死。
/// </summary>
public sealed class Hd2RigReaderTests
{
	[Fact]
	public void FromHd2RowVector_PutsTheTranslationInTheFourthColumn()
	{
		// HD2 的行向量存储：旋转按行铺开，平移落在平坦下标 12/13/14。
		IReadOnlyList<float> hd2 =
		[
			1, 0, 0, 0,
			0, 1, 0, 0,
			0, 0, 1, 0,
			1, 2, 3, 1,
		];

		var matrix = Hd2RigReader.FromHd2RowVector(hd2);

		// 忘了转置的话这里会得到 (0,0,0)——一个"很整齐"的错答案，而不是异常。
		Assert.Equal(1.0, matrix.Origin.X, 9);
		Assert.Equal(2.0, matrix.Origin.Y, 9);
		Assert.Equal(3.0, matrix.Origin.Z, 9);
	}

	[Fact]
	public void FromHd2RowVector_AppliesTheRotationInTheColumnVectorDirection()
	{
		// 绕 Z 轴 90°：列向量约定下把 (1,0,0) 送到 (0,1,0)。
		// 行向量存储是它的转置，所以按行铺开就是下面这个形状。
		IReadOnlyList<float> hd2 =
		[
			0, 1, 0, 0,
			-1, 0, 0, 0,
			0, 0, 1, 0,
			0, 0, 0, 1,
		];

		var rotated = Hd2RigReader.FromHd2RowVector(hd2).TransformPoint(new Vec3(1, 0, 0));

		Assert.Equal(0.0, rotated.X, 9);
		Assert.Equal(1.0, rotated.Y, 9);
		Assert.Equal(0.0, rotated.Z, 9);
	}

	[Fact]
	public void FromHd2RowVector_RejectsAShortArray()
	{
		Assert.Throws<ArgumentException>(() => Hd2RigReader.FromHd2RowVector([1, 2, 3]));
	}

	[Fact]
	public void FromTransformInfo_KeepsGoingWhenTheNamesAreMissing()
	{
		var transforms = TestRig.TransformInfo(
			[TestRig.RowVectorAt(0, 0, 0), TestRig.RowVectorAt(1, 2, 3)],
			hashes: [11u, 22u]);

		var rig = Hd2RigReader.FromTransformInfo(transforms);

		// 名字读不到只是拿不到称呼，变换与父子关系必须完整。
		Assert.Equal(2, rig.Bones.Count);
		Assert.All(rig.Bones, bone => Assert.False(bone.HasName));
		Assert.Equal("hd2_extra_1", rig.Bones[1].DisplayName);
		Assert.Equal(3.0, rig.Bones[1].WorldOrigin.Z, 9);
	}

	[Fact]
	public void FromTransformInfo_ResolvesNamesByHash()
	{
		var transforms = TestRig.TransformInfo(
			[TestRig.RowVectorAt(0, 0, 0), TestRig.RowVectorAt(1, 0, 0)],
			hashes: [11u, 22u]);
		var names = new UnitBoneNames([11u, 22u], ["hips", "spine1"]);

		var rig = Hd2RigReader.FromTransformInfo(transforms, names);

		Assert.Equal("hips", rig.Find("hips")!.Name);
		Assert.Equal(1, rig.Find("spine1")!.Index);
		Assert.Null(rig.Find("chest"));
	}
}
