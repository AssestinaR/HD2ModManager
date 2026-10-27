namespace HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;

/// <summary>
/// 从一个 Unit 里取"某一族 LOD"或"某一级 LOD 的可见网格"，并包成
/// <see cref="CanonicalDecorationFragment"/> 供 <see cref="CanonicalDecorationUnitAppender"/> 使用。
/// </summary>
/// <remarks>
/// 这套逻辑原本在 <c>DecorationHostRebuildService</c>、<c>HelmetToArmorMergeService</c>、
/// <c>ArmorNormalizationService</c> 里各有一份私有副本。替换护甲的合并要用同一套，
/// 抽出来一份共享，避免第四份副本继续漂移。
///
/// 两个解析器的区别很重要：
/// <list type="bullet">
///   <item><description><see cref="ResolveVisibleLodFamily"/> 取**同一族**（同 Slot/PieceType/BodyType/Weight）的全部可见 LOD，
///   用于确定宿主 Unit 的 LOD 族。</description></item>
///   <item><description><see cref="ResolveVisibleMeshAtLod"/> 只按 LOD 序号取，**不**校验族。
///   合并不同部位时（比如把胯部并进躯干）两边的 SemanticInfo 本来就不一样，按族匹配会一个都匹配不到。</description></item>
/// </list>
/// </remarks>
public static class UnitLodFamilySource
{
	public static IReadOnlyList<(UnitRawMeshData Raw, UnitMeshInfo Mesh)> ResolveVisibleLodFamily(
		UnitMeshModel model,
		UnitMeshInfo anchorMesh)
	{
		ArgumentNullException.ThrowIfNull(model);
		ArgumentNullException.ThrowIfNull(anchorMesh);
		return model.RawMeshData
			.Select(raw => (Raw: raw, Mesh: model.Meshes.SingleOrDefault(mesh => mesh.Index == raw.MeshInfoIndex)))
			.Where(item => item.Mesh is not null
				&& item.Raw.LodIndex >= 0
				&& item.Mesh!.SemanticInfo.IsVisualMesh
				&& SameLodFamily(anchorMesh.SemanticInfo, item.Mesh.SemanticInfo))
			.OrderBy(item => item.Raw.LodIndex)
			.ThenBy(item => item.Mesh!.Index)
			.Select(item => (item.Raw, item.Mesh!))
			.ToArray();
	}

	/// <summary>取这个 Unit 在指定 LOD 上的第一个可见网格；不校验部位族。</summary>
	public static (UnitRawMeshData Raw, UnitMeshInfo Mesh)? ResolveVisibleMeshAtLod(UnitMeshModel model, int lodIndex)
	{
		ArgumentNullException.ThrowIfNull(model);
		var match = model.RawMeshData
			.Select(raw => (Raw: raw, Mesh: model.Meshes.SingleOrDefault(mesh => mesh.Index == raw.MeshInfoIndex)))
			.Where(item => item.Mesh is not null
				&& item.Raw.LodIndex == lodIndex
				&& item.Mesh!.SemanticInfo.IsVisualMesh)
			.OrderBy(item => item.Raw.MeshInfoIndex)
			.FirstOrDefault();
		return match.Mesh is null ? null : (match.Raw, match.Mesh);
	}

	/// <summary>这个 Unit 的 LOD0 可见网格，作为 LOD 族的锚点。</summary>
	public static (UnitRawMeshData Raw, UnitMeshInfo Mesh)? ResolveAnchor(UnitMeshModel model)
	{
		ArgumentNullException.ThrowIfNull(model);
		var match = model.RawMeshData
			.Select(raw => (Raw: raw, Mesh: model.Meshes.SingleOrDefault(mesh => mesh.Index == raw.MeshInfoIndex)))
			.Where(item => item.Mesh is not null
				&& item.Raw.LodIndex >= 0
				&& item.Mesh!.SemanticInfo.IsVisualMesh)
			.OrderBy(item => item.Raw.LodIndex)
			.ThenBy(item => item.Raw.MeshInfoIndex)
			.FirstOrDefault();
		return match.Mesh is null ? null : (match.Raw, match.Mesh);
	}

	public static CanonicalDecorationFragment CreateFragment(PatchUnitMesh unit, UnitMeshInfo mesh, UnitRawMeshData raw)
	{
		ArgumentNullException.ThrowIfNull(unit);
		ArgumentNullException.ThrowIfNull(mesh);
		ArgumentNullException.ThrowIfNull(raw);
		var stream = unit.Model.Streams.SingleOrDefault(item => item.Index == mesh.StreamIndex)
			?? throw new InvalidDataException($"来源 MeshInfo {mesh.Index} 没有对应 Stream。");
		return new CanonicalDecorationFragment(
			mesh,
			raw,
			stream,
			unit.Model.Materials.ToArray(),
			unit.Model.BoneInfos.ToArray(),
			unit.Model.TransformInfo,
			unit.Model.TransformNameHashes.ToArray());
	}

	private static bool SameLodFamily(UnitMeshSemanticInfo anchor, UnitMeshSemanticInfo candidate)
	{
		if (!anchor.HasValue || !candidate.HasValue) return true;
		return string.Equals(anchor.Slot, candidate.Slot, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(anchor.PieceType, candidate.PieceType, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(anchor.BodyType, candidate.BodyType, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(anchor.Weight, candidate.Weight, StringComparison.OrdinalIgnoreCase);
	}
}
