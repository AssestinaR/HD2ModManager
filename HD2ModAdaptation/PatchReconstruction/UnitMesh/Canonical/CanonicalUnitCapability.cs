namespace HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;

// Purpose: Describes the structural and semantic boundary before a Unit enters
// Canonical editing. A Unit may be safely retained even when it is not editable.
public enum CanonicalUnitLayoutKind
{
	Unknown = 0,
	Inline = 1,
	CompositeBacked = 2,
	NoGeometry = 3
}

public enum CanonicalUnitRoute
{
	OpaquePassThrough = 0,
	DirectReuse = 1,
	CanonicalRebuild = 2,
	Unsupported = 3
}

public sealed record CanonicalUnitCapability(
	CanonicalUnitLayoutKind Layout,
	CanonicalUnitRoute PreferredRoute,
	bool HasRenderableGeometry,
	bool HasSkinning,
	bool HasSupportedStreamAbi,
	bool IsLikelyPlayerEquipment,
	bool RequiresAvatarRigExpansion,
	int MeshCount,
	int RenderableMeshCount,
	int StreamCount,
	string Reason)
{
	public bool IsCompositeBacked => Layout == CanonicalUnitLayoutKind.CompositeBacked;
	public bool CanCanonicalRebuild => PreferredRoute == CanonicalUnitRoute.CanonicalRebuild;
	public bool CanBeRetained => PreferredRoute != CanonicalUnitRoute.Unsupported;
}

// This detector intentionally does not inspect GameData or Mod metadata. It is
// a pure Unit-model capability probe that can be reused by same-key repair,
// decoration merge and future generic Patch planning.
public static class CanonicalUnitCapabilityDetector
{
	public static CanonicalUnitCapability Analyze(UnitMeshModel model)
	{
		ArgumentNullException.ThrowIfNull(model);

		var layout = model.Meshes.Count == 0 && model.RawMeshData.Count == 0
			? CanonicalUnitLayoutKind.NoGeometry
			: model.CompositeRef != 0 || model.StreamInfoOffset == 0
				? CanonicalUnitLayoutKind.CompositeBacked
				: CanonicalUnitLayoutKind.Inline;
		var geometry = UnitGeometryFactsBuilder.Analyze(model);
		var hasRenderable = geometry.HasRenderableVisibleGeometry;
		var renderableCount = geometry.VisibleMeshes.Count(mesh => mesh.HasRenderableGeometry);
		var hasSkinning = model.Streams.Any(stream =>
			stream.Components.Any(component => component.Type == 6)
			&& stream.Components.Any(component => component.Type == 7));
		var unsupported = model.Streams
			.SelectMany(stream => stream.Components)
			.Any(component => !IsSupportedComponent(component));
		var hasSupportedAbi = !unsupported
			&& model.Streams.All(stream => stream.VertexStride != 0 && stream.IndexBufferType is 0 or 1);
		var likelyPlayerEquipment = IsLikelyPlayerEquipment(model);
		var requiresAvatar = likelyPlayerEquipment && hasSkinning;

		if (layout == CanonicalUnitLayoutKind.NoGeometry)
			return Build(layout, CanonicalUnitRoute.OpaquePassThrough, model, hasRenderable, hasSkinning, hasSupportedAbi, likelyPlayerEquipment, false, renderableCount, "Unit 没有可写 MeshInfo，保留为不透明资源。");
		if (layout == CanonicalUnitLayoutKind.CompositeBacked)
			return Build(layout, CanonicalUnitRoute.DirectReuse, model, hasRenderable, hasSkinning, hasSupportedAbi, likelyPlayerEquipment, requiresAvatar, renderableCount, "Composite-backed Unit 暂使用依赖闭包复用，等待 Composite Canonical 编译器。");
		if (!hasSupportedAbi)
			return Build(layout, CanonicalUnitRoute.OpaquePassThrough, model, hasRenderable, hasSkinning, false, likelyPlayerEquipment, requiresAvatar, renderableCount, "Unit Stream ABI 含当前 Canonical 尚未覆盖的组件格式。");
		if (model.Meshes.Count == 0 || model.RawMeshData.Count != model.Meshes.Count || model.Streams.Count == 0)
			return Build(layout, CanonicalUnitRoute.OpaquePassThrough, model, hasRenderable, hasSkinning, hasSupportedAbi, likelyPlayerEquipment, requiresAvatar, renderableCount, "Unit MeshInfo 与 RawMesh 覆盖不完整，暂不进入语义重建。");

		return Build(layout, CanonicalUnitRoute.CanonicalRebuild, model, hasRenderable, hasSkinning, true, likelyPlayerEquipment, requiresAvatar, renderableCount,
			likelyPlayerEquipment ? "标准玩家装备 Unit，可使用 Avatar-aware Canonical 重建。" : "标准 inline Unit，可使用通用 Canonical 重建。" );
	}

	private static CanonicalUnitCapability Build(
		CanonicalUnitLayoutKind layout,
		CanonicalUnitRoute route,
		UnitMeshModel model,
		bool hasRenderable,
		bool hasSkinning,
		bool hasSupportedAbi,
		bool likelyPlayerEquipment,
		bool requiresAvatar,
		int renderableCount,
		string reason)
		=> new(layout, route, hasRenderable, hasSkinning, hasSupportedAbi, likelyPlayerEquipment, requiresAvatar,
			model.Meshes.Count, renderableCount, model.Streams.Count, reason);

	private static bool IsSupportedComponent(UnitStreamComponentInfo component)
		=> component.FormatName is "float" or "vec2_float" or "vec3_float" or "vec4_float"
			or "rgba_r8g8b8a8" or "vec4_uint32" or "vec4_uint8" or "vec4_1010102"
			or "unk_normal" or "vec2_half" or "vec4_half";

	private static bool IsLikelyPlayerEquipment(UnitMeshModel model)
	{
		var text = string.Join(' ', model.Meshes.SelectMany(mesh => new[]
		{
			mesh.SemanticInfo.Name,
			mesh.SemanticInfo.Slot,
			mesh.SemanticInfo.PieceType,
			mesh.SemanticInfo.BodyType,
			mesh.SemanticInfo.Weight
		})).ToLowerInvariant();
		if (Contains(text, "torso", "chest", "pelvis", "hips", "helmet", "armor", "armour", "shoulder", "leftarm", "rightarm", "leftleg", "rightleg"))
			return true;
		return Contains(model.CustomizationInfo.Slot, "armor", "helmet", "torso", "pelvis", "hips")
			|| Contains(model.CustomizationInfo.PieceType, "armor", "helmet", "torso", "pelvis");
	}

	private static bool Contains(string? value, params string[] tokens)
	{
		if (string.IsNullOrWhiteSpace(value)) return false;
		var normalized = value.ToLowerInvariant();
		return tokens.Any(normalized.Contains);
	}
}
