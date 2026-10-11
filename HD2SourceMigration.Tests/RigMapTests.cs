using HD2SourceModel;

namespace HD2SourceMigration.Tests;

public sealed class RigMapTests
{
	[Fact]
	public void Correspondence_IsTwentyBodyBonesPlusThirtyFingerBones()
	{
		var table = SourceToHd2RigMap.Correspondence;

		Assert.Equal(50, table.Count);

		// 重复会让"按短名索引"这一步静默丢掉一根骨，症状是映射覆盖率莫名变成 49/50。
		Assert.Equal(table.Count, table.Select(item => item.SourceBone).Distinct(StringComparer.Ordinal).Count());
		Assert.Equal(table.Count, table.Select(item => item.Hd2Node).Distinct(StringComparer.Ordinal).Count());

		Assert.Equal(30, table.Count(item => item.SourceBone.Contains("Finger", StringComparison.Ordinal)));
	}

	[Fact]
	public void Build_MapsEveryBoneOfTheCorrespondenceTable()
	{
		var (source, hd2) = TestRig.MatchingRigs();

		var map = SourceToHd2RigMap.Build(source, hd2);

		Assert.Empty(map.Missing);
		Assert.Equal(50, map.Entries.Count);
	}

	[Fact]
	public void Build_ReportsMissingWhenTheSourceBoneIsAbsent()
	{
		var (source, hd2) = TestRig.MatchingRigs();
		var withoutPelvis = TestRig.SourceModel(
			source.Bones.Where(bone => !bone.Name!.EndsWith("Pelvis", StringComparison.Ordinal)).ToArray());

		var map = SourceToHd2RigMap.Build(withoutPelvis, hd2);

		var missing = Assert.Single(map.Missing);
		Assert.Equal("Pelvis", missing.SourceBone);
		Assert.Equal("hips", missing.Hd2Node);
		Assert.Equal(49, map.Entries.Count);
	}

	[Fact]
	public void Build_ReportsMissingWhenTheHd2NodeIsAbsent()
	{
		var (source, hd2) = TestRig.MatchingRigs();
		var withoutChest = new Hd2Rig(hd2.Bones.Where(bone => bone.Name != "chest").ToArray());

		var map = SourceToHd2RigMap.Build(source, withoutChest);

		var missing = Assert.Single(map.Missing);
		Assert.Equal("Spine4", missing.SourceBone);
		Assert.Equal("chest", missing.Hd2Node);
	}

	[Fact]
	public void Build_LeavesEveryMappedOriginExactlyOnItsHd2Node()
	{
		// 这是逐骨相似变换的全部意义：源骨原点必须精确落到 HD2 关节上，
		// 映射后的骨架才可能拿到 HD2 的骨长。差一点点，骨长就对不上。
		var (source, hd2) = TestRig.MatchingRigs();

		var map = SourceToHd2RigMap.Build(source, hd2);

		var worst = map.Entries
			.Select(entry => Vec3.Distance(entry.Transform.TransformPoint(entry.SourceOriginMetres), entry.Hd2Origin))
			.Max();

		Assert.True(worst < 1e-9, $"worst origin error was {worst} m");
	}

	[Fact]
	public void Build_ReportsTheStretchOfTheReportedSegments()
	{
		// 源侧 Pelvis→Spine4 是 1 英寸（0.0254 m），HD2 侧 hips→chest 是 2 英寸，
		// 于是那一段会被拉伸 2 倍——这正是"映射后比例失调"的量化形式。
		var source = TestRig.SourceModel(
		[
			TestRig.SourceBone(0, "Pelvis"),
			TestRig.SourceBone(1, "Spine4", x: 1),
		]);
		var hd2 = new Hd2Rig(
		[
			TestRig.Hd2Bone(0, "hips"),
			TestRig.Hd2Bone(1, "chest", x: 2 * SourceToHd2RigMap.SourceInchesToMetres),
		]);

		var map = SourceToHd2RigMap.Build(source, hd2);

		var stretch = Assert.Single(map.Stretches);
		Assert.Equal("Pelvis->Spine4", stretch.Segment);
		Assert.Equal(0.0254, stretch.SourceLength, 9);
		Assert.Equal(2.0, stretch.Stretch, 9);
	}

	[Fact]
	public void Build_IgnoresSourceBonesWhoseNameCouldNotBeRead()
	{
		// 名字读不出来的骨（头发/裙摆/大袖子的 helper）本来就是"追加骨 + Lua 驱动"要处理的对象，
		// 不该混进这张对应表——它们混进来的唯一后果是让覆盖率看起来更好。
		var source = TestRig.SourceModel(
		[
			TestRig.SourceBone(0, "Pelvis"),
			TestRig.SourceBone(1, "Spine4"),
			TestRig.UnnamedSourceBone(2),
		]);
		var hd2 = new Hd2Rig([TestRig.Hd2Bone(0, "hips"), TestRig.Hd2Bone(1, "chest")]);

		var map = SourceToHd2RigMap.Build(source, hd2);

		Assert.Equal(2, map.Entries.Count);
		Assert.DoesNotContain(map.Missing, item => item.SourceBone.StartsWith("source_extra", StringComparison.Ordinal));
	}
}
