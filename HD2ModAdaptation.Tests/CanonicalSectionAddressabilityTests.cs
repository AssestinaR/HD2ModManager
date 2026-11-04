using Xunit;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;

namespace HD2ModAdaptation.Tests;

/// <summary>
/// 可寻址契约的承重结论：<b>给追加段一个每段唯一的槽，段边界就保得住。</b>
/// <para>
/// 这组测试直接打材质槽分配器（<see cref="CanonicalUnitMaterialLayoutCompiler"/>），
/// 因为"两块几何会不会并成一段"完全由它分配出来的槽决定 ——
/// <c>MergeSectionsByMaterialSlot</c> 只是按槽分组的结果。
/// </para>
/// <para>
/// 三组对照对应 U0 实测里的三种情形：<b>现行行为</b>（共用目标材质 ⇒ 并段）、
/// <b>唯一槽</b>（保住）、以及 <see cref="CanonicalDecorationUnitAppender.UniqueSlotFor"/>
/// 本身的性质（稳定、不撞保留值）。
/// </para>
/// </summary>
public sealed class CanonicalSectionAddressabilityTests
{
	private const ulong MaterialId = 0x111UL;
	private const uint TargetSlot = 77u;

	private static UnitRawMeshData Mesh(params uint[] slots)
		=> new(
			0,
			0,
			0,
			0,
			[
				.. slots.Select((slot, index) => new UnitRawMeshSectionData(
					(uint)index,
					slot,
					[new UnitTriangleIndices(0, 1, 2)])),
			],
			[],
			[]);

	private static UnitMeshModel Target()
		=> new(
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			UnitCustomizationInfo.Empty,
			[],
			[],
			[
				new UnitMeshInfo(
					0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
					UnitMeshSemanticInfo.Empty(0, 0),
					[TargetSlot],
					[]),
			],
			[new UnitMaterialBinding(TargetSlot, MaterialId)],
			[],
			[]);

	[Fact]
	public void SharedTargetSlot_CollapsesTheAppendedSectionIntoTheTargetOne()
	{
		// 现行行为：UsesTargetUnitMaterialSlotLookup + PreferMeshLocalSlotReuse。
		// donor 的段与目标同材质 ⇒ occurrence=0 命中"复用目标槽" ⇒ 两段同槽 ⇒ 并段。
		var provenance = new[]
		{
			new CanonicalMaterialSectionProvenance(
				0, 1, 0, 5, TargetSlot, MaterialId,
				UsesTargetUnitMaterialSlotLookup: true,
				PreferMeshLocalSlotReuse: true),
		};

		var compiled = new CanonicalUnitMaterialLayoutCompiler().TryCompile(Target(), [Mesh(TargetSlot, TargetSlot)], provenance);

		Assert.True(compiled.IsValid);
		var slots = Assert.Single(compiled.Meshes).Sections.Select(section => section.MaterialSlotId).ToArray();
		Assert.Equal([TargetSlot, TargetSlot], slots);
	}

	[Fact]
	public void UniquePreferredSlot_KeepsTheAppendedSectionSeparate()
	{
		// 拆件路径：不走目标材质查找，且每段给一个唯一槽 ⇒ 两段不同槽 ⇒ 边界保住。
		var unique = CanonicalDecorationUnitAppender.UniqueSlotFor(1, MaterialId);
		var provenance = new[]
		{
			new CanonicalMaterialSectionProvenance(
				0, 1, 0, 5, unique, MaterialId,
				UsesTargetUnitMaterialSlotLookup: false,
				PreferMeshLocalSlotReuse: false),
		};

		var compiled = new CanonicalUnitMaterialLayoutCompiler().TryCompile(Target(), [Mesh(TargetSlot, TargetSlot)], provenance);

		Assert.True(compiled.IsValid);
		var slots = Assert.Single(compiled.Meshes).Sections.Select(section => section.MaterialSlotId).ToArray();
		Assert.Equal(2, slots.Length);
		Assert.NotEqual(slots[0], slots[1]);
		// 两个槽可以指向同一个材质——产品代码本来就会产出这种表（bindingsBySlot 是多对一）。
		Assert.All(compiled.Bindings, binding => Assert.Equal(MaterialId, binding.MaterialId));
	}

	[Fact]
	public void UniqueSlotFor_IsStableAndAvoidsReservedValues()
	{
		for (var section = 0; section < 64; section++)
		{
			var slot = CanonicalDecorationUnitAppender.UniqueSlotFor(section, MaterialId);
			Assert.Equal(slot, CanonicalDecorationUnitAppender.UniqueSlotFor(section, MaterialId));
			Assert.NotEqual(0u, slot);
			// 155175220 是分配器的默认材质槽（DefaultMaterialSlotId），撞上等于"这一段没有材质"。
			Assert.NotEqual(155175220u, slot);
		}
	}

	[Fact]
	public void UniqueSlotFor_GivesDifferentSectionsDifferentSlots()
	{
		var slots = Enumerable.Range(0, 32)
			.Select(section => CanonicalDecorationUnitAppender.UniqueSlotFor(section, MaterialId))
			.ToArray();

		Assert.Equal(slots.Length, slots.Distinct().Count());
	}
}
