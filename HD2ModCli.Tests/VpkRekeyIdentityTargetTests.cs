using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using Xunit;
using static HD2ModCli.CliVpkRekey;

namespace HD2ModCli.Tests;

/// <summary>
/// "目标就是这个 Mod 自己的素体"的判定。
/// <para>
/// 判对了，换甲退化成"把来源 patch 复制进 Overwrite"（用户点"转回自定义素体"要的就是这个）；
/// 判错了，跨护甲链路会去把 Unit 搬到它自己身上——那会把本来能用的 Mod 弄坏。
/// 所以判据只看 <b>Unit 键集合</b>，不看名字也不看键：自定义素体的键是转换时生成的，
/// 名字可以被用户改，键也可以被复制。
/// </para>
/// </summary>
public sealed class VpkRekeyIdentityTargetTests
{
	private static EquipmentUnitPart Part(string unit, UnitMeshPartKind kind)
		=> new(
			new AssetKey(PatchUnitMeshReader.UnitTypeId, ulong.Parse(unit, System.Globalization.NumberStyles.HexNumber)),
			MeshInfoIndex: 0,
			MeshId: 0,
			kind,
			UnitMeshPartLayer.Undergarment,
			UnitMeshBodyVariant.Any,
			SemanticName: kind.ToString(),
			Confidence: 100,
			SharedArchiveIds: []);

	private static EquipmentUnitCatalogEntry Entry(string archiveId, params EquipmentUnitPart[] parts)
		=> new(archiveId, "CustomModel", archiveId, parts);

	[Fact]
	public void SameUnitSetIsAnIdentityTarget()
	{
		var source = Entry("vpk-mod:abc",
			Part("18054b9f5a540001", UnitMeshPartKind.Torso),
			Part("18054b9f5a540002", UnitMeshPartKind.Hips));
		var target = Entry("custom-model:custom-abc",
			Part("18054b9f5a540001", UnitMeshPartKind.Torso),
			Part("18054b9f5a540002", UnitMeshPartKind.Hips));

		Assert.True(IsIdentityTarget(source, [target]));
	}

	[Fact]
	public void DifferentUnitsAreNotAnIdentityTarget()
	{
		var source = Entry("vpk-mod:abc", Part("18054b9f5a540001", UnitMeshPartKind.Torso));
		var target = Entry("custom-model:other", Part("3c09e08493ee44b8", UnitMeshPartKind.Torso));

		Assert.False(IsIdentityTarget(source, [target]));
	}

	/// <summary>多个目标永远不是恒等：哪怕其中一个恰好是自己的素体，也得真的搬一次。</summary>
	[Fact]
	public void MoreThanOneTargetIsNeverAnIdentityTarget()
	{
		var source = Entry("vpk-mod:abc", Part("18054b9f5a540001", UnitMeshPartKind.Torso));
		var same = Entry("custom-model:custom-abc", Part("18054b9f5a540001", UnitMeshPartKind.Torso));
		var other = Entry("custom-model:other", Part("3c09e08493ee44b8", UnitMeshPartKind.Torso));

		Assert.False(IsIdentityTarget(source, [same, other]));
	}

	/// <summary>目标没有件（注册表里被清空）时不是恒等——那种目标根本换不了。</summary>
	[Fact]
	public void AnEmptyTargetIsNotAnIdentityTarget()
	{
		var source = Entry("vpk-mod:abc", Part("18054b9f5a540001", UnitMeshPartKind.Torso));
		var empty = Entry("custom-model:empty");

		Assert.False(IsIdentityTarget(source, [empty]));
	}
}
