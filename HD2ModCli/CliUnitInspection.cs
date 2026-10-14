using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;

namespace HD2ModCli;

// 只读的 Unit 数据视图层。
//
// 这些能力在 C# 底座里**早已存在**（UnitMeshModel / UnitGeometryFactsBuilder /
// UnitBoneNamesReader），但此前没有任何命令把它们暴露出来，导致外部脚本只能用
// Python 重新实现一遍 Unit 解析（读 TocData+GpuData、解顶点分量、读骨骼表）。
// 本文件只做"包装 + 投影"，不新增解析逻辑；全部命令只读，不写任何文件。
//
// Read-only Unit data view. These capabilities already exist in the C# base layer
// (UnitMeshModel / UnitGeometryFactsBuilder / UnitBoneNamesReader) but had no command
// surface, which forced external scripts to re-implement Unit parsing in Python.
// This file only wraps and projects; every command is read-only and writes nothing.

internal sealed record CliUnitCustomizationSummary(
	string BodyType,
	string Slot,
	string Weight,
	string PieceType);

internal sealed record CliUnitStreamSummary(
	int Index,
	uint NumVertices,
	uint VertexStride,
	uint NumIndices,
	IReadOnlyList<string> Components);

internal sealed record CliUnitMeshSummary(
	int Index,
	uint MeshId,
	int LodIndex,
	uint StreamIndex,
	uint NumMaterials,
	uint NumSections,
	bool IsVisual,
	bool IsCulling,
	string SemanticName,
	string Slot,
	string PieceType,
	string BodyType);

internal sealed record CliUnitStructureSummary(
	string UnitKey,
	string Kind,
	uint Version,
	string NameHash,
	string BonesRef,
	string CompositeRef,
	bool BonesResolvedFromPatch,
	CliUnitCustomizationSummary Customization,
	int BoneGroupCount,
	int TotalBones,
	IReadOnlyList<CliUnitStreamSummary> Streams,
	IReadOnlyList<CliUnitMeshSummary> Meshes,
	int MaterialBindingCount);

internal sealed record CliUnitMeshGeometrySummary(
	int MeshInfoIndex,
	int LodIndex,
	int VertexCount,
	int TriangleCount,
	bool IsVisualMesh,
	bool IsCullingMesh,
	string Quality);

internal sealed record CliUnitGeometrySummary(
	string UnitKey,
	int MeshCount,
	int VisibleMeshCount,
	bool HasRenderableVisibleGeometry,
	bool IsFullyHidden,
	IReadOnlyList<CliUnitMeshGeometrySummary> Meshes);

internal sealed record CliUnitBoneGroupSummary(
	int Index,
	uint NumBones,
	IReadOnlyList<string> RealIndices,
	int RemapCount);

internal sealed record CliUnitBonesSummary(
	string UnitKey,
	int BoneGroupCount,
	int TotalBones,
	IReadOnlyList<CliUnitBoneGroupSummary> Groups,
	int BoneNameCount,
	IReadOnlyList<string> BoneNames);

internal static class CliUnitInspection
{
	internal const ulong UnitTypeId = 0xe0a48d0be9a7453f;
	internal const ulong BonesTypeId = 0x18dead01056b72e9;

	private static readonly PatchUnitMeshReader Reader = new();
	private static readonly PatchTocScanner Scanner = new();
	private static readonly PatchEntryPayloadReader PayloadReader = new();
	private static readonly UnitBoneNamesReader BoneNamesReader = new();

	// --- public surface -----------------------------------------------------

	public static async ValueTask<IReadOnlyList<CliUnitStructureSummary>> ShowAsync(
		string tocPath,
		IReadOnlyList<AdaptationAssetKey> keys,
		CancellationToken cancellationToken = default)
	{
		var results = new List<CliUnitStructureSummary>(keys.Count);
		foreach (var key in keys)
		{
			var mesh = await LoadAsync(tocPath, key, cancellationToken).ConfigureAwait(false);
			results.Add(ProjectStructure(key, mesh));
		}
		return results;
	}

	public static async ValueTask<IReadOnlyList<CliUnitGeometrySummary>> GeometryAsync(
		string tocPath,
		IReadOnlyList<AdaptationAssetKey> keys,
		CancellationToken cancellationToken = default)
	{
		var results = new List<CliUnitGeometrySummary>(keys.Count);
		foreach (var key in keys)
		{
			var mesh = await LoadAsync(tocPath, key, cancellationToken).ConfigureAwait(false);
			var facts = UnitGeometryFactsBuilder.Analyze(mesh.Model);
			results.Add(new CliUnitGeometrySummary(
				FormatKey(key),
				facts.Meshes.Count,
				facts.VisibleMeshes.Count,
				facts.HasRenderableVisibleGeometry,
				facts.IsFullyHidden,
				facts.Meshes.Select(fact => new CliUnitMeshGeometrySummary(
					fact.MeshInfoIndex,
					fact.LodIndex,
					fact.VertexCount,
					fact.TriangleCount,
					fact.IsVisualMesh,
					fact.IsCullingMesh,
					fact.Quality.ToString())).ToArray()));
		}
		return results;
	}

	public static async ValueTask<IReadOnlyList<CliUnitBonesSummary>> BonesAsync(
		string tocPath,
		IReadOnlyList<AdaptationAssetKey> keys,
		CancellationToken cancellationToken = default)
	{
		// 骨骼名字来自同 Patch 内的 Bones 资源，而不是 Unit 自身。
		// Bone names live in the sibling Bones resource, not in the Unit itself.
		var names = await ReadBoneNamesAsync(tocPath, cancellationToken).ConfigureAwait(false);
		var results = new List<CliUnitBonesSummary>(keys.Count);
		foreach (var key in keys)
		{
			var mesh = await LoadAsync(tocPath, key, cancellationToken).ConfigureAwait(false);
			var groups = mesh.Model.BoneInfos.Select(group => new CliUnitBoneGroupSummary(
				group.Index,
				group.NumBones,
				group.RealIndices.Select(index => index.ToString()).ToArray(),
				group.Remaps.Count)).ToArray();
			results.Add(new CliUnitBonesSummary(
				FormatKey(key),
				groups.Length,
				groups.Sum(group => (int)group.NumBones),
				groups,
				names.Names.Count,
				names.Names.ToArray()));
		}
		return results;
	}

	// --- helpers ------------------------------------------------------------

	private static async ValueTask<PatchUnitMesh> LoadAsync(
		string tocPath,
		AdaptationAssetKey key,
		CancellationToken cancellationToken)
	{
		if (key.TypeId != UnitTypeId)
		{
			throw new CliUserException(
				"UnitKeyTypeMismatch",
				$"unit 命令只接受 unit 类型（0x{UnitTypeId:x16}）；收到 0x{key.TypeId:x16}（{CliPatchPrimitives.DescribeKind(key.TypeId)}）。");
		}
		var entry = await CliPatchPrimitives.RequireEntryAsync(tocPath, key, cancellationToken).ConfigureAwait(false);
		return await Reader.ReadAsync(entry, cancellationToken).ConfigureAwait(false);
	}

	private static async ValueTask<UnitBoneNames> ReadBoneNamesAsync(string tocPath, CancellationToken cancellationToken)
	{
		var entries = await Scanner.ScanEntriesAsync(tocPath, cancellationToken).ConfigureAwait(false);
		foreach (var entry in entries)
		{
			if (entry.AssetKey.TypeId != BonesTypeId) continue;
			var payload = await PayloadReader.ReadPayloadAsync(entry, cancellationToken).ConfigureAwait(false);
			var names = BoneNamesReader.Read(payload.TocData);
			if (names.HasValue) return names;
		}
		return UnitBoneNames.Empty;
	}

	private static CliUnitStructureSummary ProjectStructure(AdaptationAssetKey key, PatchUnitMesh mesh)
	{
		var model = mesh.Model;
		var streams = model.Streams.Select(stream => new CliUnitStreamSummary(
			stream.Index,
			stream.NumVertices,
			stream.VertexStride,
			stream.NumIndices,
			stream.Components
				.Select(component => $"{component.TypeName}#{component.Index}:{component.FormatName}")
				.ToArray())).ToArray();
		var meshes = model.Meshes.Select(info => new CliUnitMeshSummary(
			info.Index,
			info.MeshId,
			info.LodIndex,
			info.StreamIndex,
			info.NumMaterials,
			info.NumSections,
			info.SemanticInfo.IsVisualMesh,
			info.SemanticInfo.IsCullingBody,
			info.SemanticInfo.Name,
			info.SemanticInfo.Slot,
			info.SemanticInfo.PieceType,
			info.SemanticInfo.BodyType)).ToArray();

		return new CliUnitStructureSummary(
			FormatKey(key),
			CliPatchPrimitives.DescribeKind(key.TypeId),
			model.Version,
			$"0x{model.NameHash:x16}",
			$"0x{model.BonesRef:x16}",
			$"0x{model.CompositeRef:x16}",
			mesh.Dependencies?.IsBoneResolvedFromPatch ?? false,
			new CliUnitCustomizationSummary(
				model.CustomizationInfo.BodyType,
				model.CustomizationInfo.Slot,
				model.CustomizationInfo.Weight,
				model.CustomizationInfo.PieceType),
			model.BoneInfos.Count,
			model.BoneInfos.Sum(group => (int)group.NumBones),
			streams,
			meshes,
			model.Materials.Count);
	}

	internal static string FormatKey(AdaptationAssetKey key) => $"0x{key.TypeId:x16}:0x{key.FileId:x16}";
}
