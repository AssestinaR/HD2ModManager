using Xunit;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;

namespace HD2ModAdaptation.Tests;

/// <summary>
/// 段来源账的翻译：把"追加前的临时段下标"换成"最终网格里的段下标"。
/// <para>
/// 必须按<b>材质槽 id</b>对齐，不能按段数：材质布局会丢掉 0 三角形段（legacy unit 的第 0 段就是
/// 这种），按槽合并又会把多段并成一段。按段数判"账可不可信"的旧做法实测让拆件那条路
/// 11 个 target 全部留空——变体组因此没有任何成员，用户点了没反应。
/// </para>
/// </summary>
public sealed class CanonicalAppendOriginTests
{
	private const int Mesh = 2;

	[Fact]
	public void ResolveOriginsBySlot_MapsBySlotAndSkipsWhatDidNotSurvive()
	{
		// 临时段 0..3：0 是宿主自己的段，1..3 是追加段（各自唯一槽）。
		var slots = new Dictionary<int, uint> { [0] = 100, [1] = 201, [2] = 202, [3] = 203 };
		// 最终网格：宿主那段还在（槽 100），201 被丢掉（0 三角形，没有材质），202 / 203 还在。
		var final = new[] { Section(100), Section(202), Section(203) };
		var origins = new[]
		{
			new CanonicalAppendSectionOrigin(0, -1, 0),
			new CanonicalAppendSectionOrigin(1, 0, 0),
			new CanonicalAppendSectionOrigin(2, 0, 1),
			new CanonicalAppendSectionOrigin(3, 1, 0),
		};

		var resolved = CanonicalDecorationUnitAppender.ResolveOriginsBySlot(Mesh, origins, slots, final);

		Assert.Equal(
			[(0, -1, 0), (1, 0, 1), (2, 1, 0)],
			resolved.Select(item => (item.FinalSectionIndex, item.InputIndex, item.SourceSectionIndex)));
		Assert.All(resolved, item => Assert.Equal(Mesh, item.MeshInfoIndex));
	}

	[Fact]
	public void ResolveOriginsBySlot_KeepsTheFirstSectionWhenSeveralCollapseIntoOneSlot()
	{
		// 宿主自己的两段共用同一个槽 ⇒ 最终合成一段。此时只能记一条：
		// 记两条会让下游以为"两块几何各自能开关"，而它们其实同生共死。
		var slots = new Dictionary<int, uint> { [0] = 100, [1] = 100 };
		var final = new[] { Section(100) };
		var origins = new[]
		{
			new CanonicalAppendSectionOrigin(0, -1, 0),
			new CanonicalAppendSectionOrigin(1, -1, 1),
		};

		var resolved = CanonicalDecorationUnitAppender.ResolveOriginsBySlot(Mesh, origins, slots, final);

		var single = Assert.Single(resolved);
		Assert.Equal(0, single.FinalSectionIndex);
		Assert.Equal(0, single.SourceSectionIndex);
	}

	[Fact]
	public void ResolveOriginsBySlot_EmitsNothingWhenNoSlotSurvives()
	{
		var slots = new Dictionary<int, uint> { [0] = 100, [1] = 201 };
		var origins = new[]
		{
			new CanonicalAppendSectionOrigin(0, -1, 0),
			new CanonicalAppendSectionOrigin(1, 0, 0),
		};

		Assert.Empty(CanonicalDecorationUnitAppender.ResolveOriginsBySlot(Mesh, origins, slots, []));
	}

	private static UnitRawMeshSectionData Section(uint slotId)
		=> new(0, slotId, []);
}
