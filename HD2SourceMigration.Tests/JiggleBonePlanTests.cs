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

	// -----------------------------------------------------------------------
	// 链根锚点名（P1）
	//
	// 症状：一份裙子上 25 条链死 5 条，日志里是「缺锚点」。原因是容器里存的是**源侧**父骨名，
	// 而源骨架有一批骨在 HD2 侧不存在（无名骨 source_extra_90、非 Biped 命名的 UN_Skirt_R_02），
	// 运行时的映射表只认 ValveBiped，查不到就退化成"把源骨名当 HD2 骨名查"——必然查不到。
	// 这里给出的就是重定向当时已经算出来的答案：那根父骨回退到了哪根 HD2 骨。
	// -----------------------------------------------------------------------

	private static SourceBone NamedSourceBone(int index, string name, int parent) =>
		new(index, name, parent, Mat4.Identity, Mat4.Identity, null);

	/// <summary>
	/// 造一张<b>下标连续</b>的源骨表：真实模型里骨下标就是数组下标，稀疏表会让
	/// "父骨下标越界"这类分支被误触发，测试就会在错误的理由上通过。
	/// </summary>
	private static List<SourceBone> BoneTable(int count, params SourceBone[] placed)
	{
		var table = new List<SourceBone>(count);
		for (var i = 0; i < count; i++)
			table.Add(TestRig.UnnamedSourceBone(i));
		foreach (var bone in placed)
			table[bone.Index] = bone;
		return table;
	}

	[Fact]
	public void Anchors_MapAnUnnamedParentToTheHd2BoneItFellBackTo()
	{
		// 无名父骨（source_extra_5）在 HD2 侧不存在，但重定向把它回退到了 "spine1"。
		var bones = BoneTable(7, TestRig.SourceBone(0, "Pelvis"), JiggleBone(6, 5));
		var resolved = new Dictionary<int, int> { [0] = 0, [5] = 1 };
		var hd2 = new List<Hd2RigBone> { TestRig.Hd2Bone(0, "hips"), TestRig.Hd2Bone(1, "spine1") };

		var anchors = Retargeter.PlanJiggleAnchors(bones, resolved, hd2);

		Assert.Equal("spine1", Assert.Single(anchors).Value);
		Assert.Equal("source_extra_5", anchors.Keys.Single());
	}

	[Fact]
	public void Anchors_MapANonBipedNamedParentByItsOwnName()
	{
		// UN_Skirt_R_02 有名字但不是 ValveBiped 骨，映射表里查不到——同样需要换成 HD2 名。
		var bones = BoneTable(11,
			TestRig.SourceBone(0, "Pelvis"),
			NamedSourceBone(9, "UN_Skirt_R_02", 0),
			JiggleBone(10, 9));
		var resolved = new Dictionary<int, int> { [0] = 0, [9] = 1 };
		var hd2 = new List<Hd2RigBone> { TestRig.Hd2Bone(0, "hips"), TestRig.Hd2Bone(1, "l_thigh") };

		var anchors = Retargeter.PlanJiggleAnchors(bones, resolved, hd2);

		Assert.Equal("l_thigh", anchors["UN_Skirt_R_02"]);
	}

	[Fact]
	public void Anchors_SkipChainsWhoseParentIsAnotherJiggleBone()
	{
		// 链套链：运行时按父骨下标认链，不需要锚点。写进去反而会被当成"挂到别的骨上"。
		var bones = BoneTable(3,
			TestRig.SourceBone(0, "Head1"),
			JiggleBone(1, 0),
			JiggleBone(2, 1));
		var resolved = new Dictionary<int, int> { [0] = 0, [1] = 0, [2] = 0 };
		var hd2 = new List<Hd2RigBone> { TestRig.Hd2Bone(0, "head") };

		var anchors = Retargeter.PlanJiggleAnchors(bones, resolved, hd2);

		Assert.Equal("head", anchors["ValveBiped.Bip01_Head1"]);   // 只有"挂在头骨上"的那条
		Assert.False(anchors.ContainsKey("source_extra_1"));       // 父是 jiggle 骨 ⇒ 不写
	}

	[Fact]
	public void Anchors_SkipParentsThatNeverResolved()
	{
		var bones = BoneTable(7, JiggleBone(6, 5));
		var hd2 = new List<Hd2RigBone> { TestRig.Hd2Bone(0, "hips") };

		Assert.Empty(Retargeter.PlanJiggleAnchors(bones, new Dictionary<int, int>(), hd2));
	}

	[Fact]
	public void Anchors_SkipWhenTheHd2BoneItselfHasNoName()
	{
		// HD2 侧那根骨也没名字时，写进去运行时同样解析不了——不如不写，让它照原样失败并报出来。
		var bones = BoneTable(7, JiggleBone(6, 5));
		var resolved = new Dictionary<int, int> { [5] = 1 };
		var hd2 = new List<Hd2RigBone> { TestRig.Hd2Bone(0, "hips"), new(1, null, 0, Mat4.Identity) };

		Assert.Empty(Retargeter.PlanJiggleAnchors(bones, resolved, hd2));
	}
}