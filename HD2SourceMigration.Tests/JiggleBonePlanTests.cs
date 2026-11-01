using HD2SourceMigration.Retarget;
using HD2SourceModel;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 合成骨规划：给 jiggle 骨各分一根节点，且**不动原生下标**。
/// 这一段错了不会报错，只会让一部分头发/裙摆静默不动或跟着错误的骨走，所以单独钉死。
/// </summary>
public sealed class JiggleBonePlanTests
{
	private static SourceBone JiggleBone(int index, int parent, double z = 0) =>
		TestRig.UnnamedSourceBone(index) with
		{
			// 名字必须是 null：DisplayName 才会给出 source_extra_{index}，
			// 这正是运行时按名字找节点用的那个称呼。
			// 最小可用的参数块：值本身不参与规划，只要存在即视为 jiggle 骨。
			Jiggle = new SourceJiggleBone(
				0x31, 30f, 200f, 50f, 7f, 50f, 7f, 0f, 0f, 0.611f,
				0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,
				0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f),
			ParentIndex = parent,
			WorldBind = TestRig.TranslationAt(0, 0, z),
		};

	[Fact]
	public void Plan_GivesEachJiggleBoneItsOwnNodeAfterTheNativeOnes()
	{
		var bones = new List<SourceBone>
		{
			TestRig.SourceBone(0, "Head1"),
			JiggleBone(1, 0),
			JiggleBone(2, 0),
		};
		var resolved = new Dictionary<int, int> { [0] = 10 };

		var extras = Retargeter.PlanJiggleBones(bones, resolved, 100, _ => Vec3.Zero);

		Assert.Equal(2, extras.Count);
		Assert.All(extras, extra => Assert.Equal(10, extra.ParentIndex));    // 都挂在 head 上
		Assert.Equal("source_extra_1", extras[0].Name);
		Assert.Equal(100, resolved[1]);                                       // 接在原生骨之后
		Assert.Equal(101, resolved[2]);
		Assert.Equal(10, resolved[0]);                                        // 原生映射没被动过
	}

	[Fact]
	public void Plan_KeepsChainsTogetherEvenWhenTheChildHasTheLowerIndex()
	{
		// 故意让"子"的下标比"父"小：规划按下标排序时会先看到子，
		// 必须靠"反复扫到不动为止"的拓扑循环把它留到父分配之后再处理。
		var bones = new List<SourceBone>
		{
			TestRig.SourceBone(0, "Head1"),
			JiggleBone(1, 2),   // 子（下标更小）
			JiggleBone(2, 0),   // 父
		};
		var resolved = new Dictionary<int, int> { [0] = 10 };

		var extras = Retargeter.PlanJiggleBones(bones, resolved, 100, _ => Vec3.Zero);

		Assert.Equal(2, extras.Count);
		Assert.Equal("source_extra_2", extras[0].Name);   // 父先分配
		Assert.Equal(10, extras[0].ParentIndex);
		Assert.Equal("source_extra_1", extras[1].Name);
		Assert.Equal(100, extras[1].ParentIndex);         // 子挂在合成骨上
		Assert.Equal(101, resolved[1]);
	}

	[Fact]
	public void Plan_SkipsJiggleBonesWhoseParentIsNeitherMappedNorJiggle()
	{
		var bones = new List<SourceBone>
		{
			TestRig.SourceBone(0, "Head1"),
			TestRig.UnnamedSourceBone(5),   // 普通无名骨，且未映射
			JiggleBone(6, 5),
		};
		var resolved = new Dictionary<int, int> { [0] = 10 };

		var extras = Retargeter.PlanJiggleBones(bones, resolved, 100, _ => Vec3.Zero);

		Assert.Empty(extras);
		Assert.False(resolved.ContainsKey(6));
	}

	[Fact]
	public void Plan_EmitsNothingWhenThereAreNoJiggleBones()
	{
		var bones = new List<SourceBone> { TestRig.SourceBone(0, "Head1") };
		var resolved = new Dictionary<int, int> { [0] = 10 };

		var extras = Retargeter.PlanJiggleBones(bones, resolved, 100, _ => Vec3.Zero);

		Assert.Empty(extras);
		Assert.Single(resolved);
	}

	[Fact]
	public void Plan_UsesTheJointAccessorForTheWorldOrigin()
	{
		var bones = new List<SourceBone> { TestRig.SourceBone(0, "Head1"), JiggleBone(1, 0) };
		var resolved = new Dictionary<int, int> { [0] = 10 };

		var extras = Retargeter.PlanJiggleBones(
			bones, resolved, 100, index => new Vec3(index, index * 2, index * 3));

		Assert.Equal(new Vec3(1, 2, 3), Assert.Single(extras).WorldOrigin);
	}
}
