using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using Xunit;

namespace HD2ModAdaptation.Tests;

public sealed class CanonicalUnitCapabilityTests
{
	[Fact]
	public void Analyze_RecognizesSupportedInlineUnitAsCanonicalEditable()
	{
		var model = Model(compositeRef: 0, streamInfoOffset: 128, semanticName: "npc_body");

		var capability = CanonicalUnitCapabilityDetector.Analyze(model);

		Assert.Equal(CanonicalUnitLayoutKind.Inline, capability.Layout);
		Assert.Equal(CanonicalUnitRoute.CanonicalRebuild, capability.PreferredRoute);
		Assert.True(capability.CanCanonicalRebuild);
		Assert.False(capability.IsLikelyPlayerEquipment);
	}

	[Fact]
	public void Analyze_RoutesCompositeBackedUnitToDirectReuse()
	{
		var model = Model(compositeRef: 0x22, streamInfoOffset: 0, semanticName: "npc_body");

		var capability = CanonicalUnitCapabilityDetector.Analyze(model);

		Assert.Equal(CanonicalUnitLayoutKind.CompositeBacked, capability.Layout);
		Assert.Equal(CanonicalUnitRoute.DirectReuse, capability.PreferredRoute);
		Assert.False(capability.CanCanonicalRebuild);
		Assert.True(capability.IsCompositeBacked);
	}

	[Fact]
	public void Analyze_RoutesUnknownStreamFormatToOpaqueRetention()
	{
		var model = Model(compositeRef: 0, streamInfoOffset: 128, semanticName: "npc_body") with
		{
			Streams = [new UnitStreamInfo(0, 128, 0, 1, 0, 1, 4, 0, 3, 0, 0, 0, 0, 0,
				[new UnitStreamComponentInfo(0, "position", 999, "unknown", 0, 0, 4)])]
		};

		var capability = CanonicalUnitCapabilityDetector.Analyze(model);

		Assert.Equal(CanonicalUnitRoute.OpaquePassThrough, capability.PreferredRoute);
		Assert.False(capability.HasSupportedStreamAbi);
		Assert.True(capability.CanBeRetained);
	}

	[Fact]
	public void Analyze_RoutesStructurallyAmbiguousAbiToOpaqueRetention()
	{
		var model = Model(compositeRef: 0, streamInfoOffset: 128, semanticName: "npc_body") with
		{
			StreamAbiResolution = new UnitStreamAbiResolution(
				UnitStreamAbiResolutionPath.Unresolved,
				UnitStreamAbiProfileKind.Unknown,
				10990002,
				["结构指纹存在歧义。"])
		};

		var capability = CanonicalUnitCapabilityDetector.Analyze(model);

		Assert.Equal(CanonicalUnitRoute.OpaquePassThrough, capability.PreferredRoute);
		Assert.False(capability.HasSupportedStreamAbi);
		Assert.Contains("结构指纹", capability.Reason, StringComparison.Ordinal);
	}

	// 回归：髋部下装这类 Unit 的网格名是 "Hip_Undergarment_*"，既不含 "hips" 也不含
	// 旧词表里的任何装备词，于是被当成非玩家装备 → 跳过 Avatar 骨架扩展 → 同键重建
	// 找不到 chest/spine2 而失败。自定义槽位枚举才是权威信号。
	[Fact]
	public void Analyze_TreatsCustomizationSlotAsPlayerEquipment()
	{
		var model = Model(compositeRef: 0, streamInfoOffset: 128, semanticName: "Hip_Undergarment_Any_lod0") with
		{
			CustomizationInfo = new UnitCustomizationInfo(
				"HelldiverCustomizationSlot_Hip",
				"HelldiverCustomizationPieceType_Undergarment",
				"HelldiverCustomizationBodyType_Any",
				"HelldiverCustomizationWeight_Heavy"),
			Streams =
			[
				new UnitStreamInfo(0, 128, 0, 1, 0, 4, 12, 0, 3, 0, 0, 0, 0, 0,
				[
					new UnitStreamComponentInfo(0, "position", 2, "vec3_float", 0, 0, 12),
					new UnitStreamComponentInfo(6, "bone_indices", 4, "vec4_uint8", 12, 0, 4),
					new UnitStreamComponentInfo(7, "bone_weights", 4, "vec4_uint8", 16, 0, 4)
				])
			]
		};

		var capability = CanonicalUnitCapabilityDetector.Analyze(model);

		Assert.True(capability.HasSkinning);
		Assert.True(capability.IsLikelyPlayerEquipment);
		Assert.True(capability.RequiresAvatarRigExpansion);
	}

	[Fact]
	public void Analyze_KeepsNonCustomizationUnitWithoutEquipmentTokensAsNpc()
	{
		var model = Model(compositeRef: 0, streamInfoOffset: 128, semanticName: "npc_body");

		var capability = CanonicalUnitCapabilityDetector.Analyze(model);

		Assert.False(capability.IsLikelyPlayerEquipment);
		Assert.False(capability.RequiresAvatarRigExpansion);
	}

	private static UnitMeshModel Model(ulong compositeRef, uint streamInfoOffset, string semanticName)
	{
		var mesh = new UnitMeshInfo(
			0, 64, 1, 0, 0, 0, 1, 0, 1, 0,
			new UnitMeshSemanticInfo(semanticName, string.Empty, string.Empty, string.Empty, string.Empty, 0, 0, false, false, false),
			[10], [new UnitMeshSectionInfo(128, 0, 10, 0, 4, 0, 3, 0)]);
		var stream = new UnitStreamInfo(
			0, 128, 0, 1, 0, 4, 12, 0, 3, 0, 0, 0, 0, 0,
			[new UnitStreamComponentInfo(0, "position", 2, "vec3_float", 0, 0, 12)]);
		var raw = new UnitRawMeshData(
			0, 1, 0, 0,
			[new UnitRawMeshSectionData(0, 10, [new UnitTriangleIndices(0, 1, 2)])],
			[new UnitTriangleIndices(0, 1, 2)],
			[
				new UnitRawVertexRecord(0, [], [new UnitVertexComponentValue(0, "position", 2, "vec3_float", 0, [0, 0, 0], [], [])]),
				new UnitRawVertexRecord(1, [], [new UnitVertexComponentValue(0, "position", 2, "vec3_float", 0, [1, 0, 0], [], [])]),
				new UnitRawVertexRecord(2, [], [new UnitVertexComponentValue(0, "position", 2, "vec3_float", 0, [0, 1, 0], [], [])]),
				new UnitRawVertexRecord(3, [], [new UnitVertexComponentValue(0, "position", 2, "vec3_float", 0, [0, 0, 1], [], [])])
			]);
		return new UnitMeshModel(
			1, 1, 0, compositeRef, 0, 0, streamInfoOffset, 64, 0, 512,
			UnitCustomizationInfo.Empty, [], [stream], [mesh], [], [new UnitRawMeshSummary(0, 1, 0, 0, 4, 3, 1, 1, true, true)], [raw]);
	}
}
