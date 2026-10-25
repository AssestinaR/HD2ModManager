using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;

namespace HD2ModAdaptation.Analysis;

// Purpose: Converts SDK-compatible mesh names and flags into conservative armor-part facts.
public sealed class UnitMeshPartClassifier
{
	public IReadOnlyList<UnitMeshPartFact> Classify(AssetKey unitAssetKey, UnitMeshModel model)
	{
		ArgumentNullException.ThrowIfNull(model);
		return model.Meshes.Select(mesh => Classify(unitAssetKey, mesh.SemanticInfo, mesh.MeshId)).ToArray();
	}

	public IReadOnlyList<UnitMeshPartFact> Classify(
		AssetKey unitAssetKey,
		UnitMeshModel model,
		IReadOnlyDictionary<uint, string> globalBoneNames)
	{
		ArgumentNullException.ThrowIfNull(model);
		ArgumentNullException.ThrowIfNull(globalBoneNames);
		return model.Meshes.Select(mesh =>
		{
			if (!globalBoneNames.TryGetValue(mesh.MeshId, out var name))
			{
				return Classify(unitAssetKey, mesh.SemanticInfo, mesh.MeshId);
			}

			var isCulling = name.Contains("_culling", StringComparison.OrdinalIgnoreCase);
			return Classify(unitAssetKey, mesh.SemanticInfo with
			{
				Name = name,
				IsCullingBody = isCulling || mesh.SemanticInfo.IsCullingBody,
				// SDK import uses MeshInfo.LodIndex. A name such as `*_lod0` is a
				// normal default-imported object; only explicit shadow naming remains
				// a conservative non-transfer classification when metadata is absent.
				IsLod = mesh.SemanticInfo.IsLod || name.Contains("_shadow", StringComparison.OrdinalIgnoreCase)
			}, mesh.MeshId);
		}).ToArray();
	}

	private static UnitMeshPartFact Classify(AssetKey unitAssetKey, UnitMeshSemanticInfo semantic, uint meshId)
	{
		var text = string.Join('_', new[] { semantic.Name, semantic.Slot, semantic.PieceType, semantic.BodyType })
			.ToLowerInvariant();
		var evidence = semantic.Name.Length > 0 && semantic.Name != $"{meshId}_lod{semantic.LodIndex}"
			? UnitMeshPartEvidenceKind.BoneName
			: semantic.HasValue ? UnitMeshPartEvidenceKind.CustomizationInfo : UnitMeshPartEvidenceKind.Unknown;
		var kind = ResolveKind(text);
		var bodyVariant = ResolveBodyVariant(semantic.BodyType, text);
		var layer = semantic.IsCullingBody ? UnitMeshPartLayer.Culling
			: semantic.IsStaticMesh ? UnitMeshPartLayer.Static
			: Contains(text, "undergarment", "inner", "body") ? UnitMeshPartLayer.Undergarment
			: Contains(text, "armor", "armour", "plate") ? UnitMeshPartLayer.Armor
			: kind is UnitMeshPartKind.LeftShoulder or UnitMeshPartKind.RightShoulder or UnitMeshPartKind.Accessory ? UnitMeshPartLayer.Accessory
			: kind is UnitMeshPartKind.Torso or UnitMeshPartKind.LeftArm or UnitMeshPartKind.RightArm or UnitMeshPartKind.Hips or UnitMeshPartKind.LeftLeg or UnitMeshPartKind.RightLeg ? UnitMeshPartLayer.Armor
			: UnitMeshPartLayer.Unknown;
		var confidence = evidence switch
		{
			UnitMeshPartEvidenceKind.BoneName when kind != UnitMeshPartKind.Unknown => 100,
			UnitMeshPartEvidenceKind.CustomizationInfo when kind != UnitMeshPartKind.Unknown => 85,
			_ when kind != UnitMeshPartKind.Unknown => 55,
			_ => 0
		};
		var reason = semantic.IsCullingBody ? "SDK culling-body mesh."
			: semantic.IsStaticMesh ? "SDK static mesh."
			: kind == UnitMeshPartKind.Unknown ? "No recognized armor-part token in SDK semantic name."
			: $"{evidence} classified '{semantic.Name}'.";
		return new UnitMeshPartFact(unitAssetKey, semantic.MeshInfoIndex, meshId, kind, layer, bodyVariant, semantic.Name, evidence, confidence, semantic.IsVisualMesh, semantic.IsLod, reason)
		{
			PieceType = semantic.PieceType
		};
	}

	private static UnitMeshBodyVariant ResolveBodyVariant(string bodyType, string semanticText)
	{
		switch (bodyType.Trim())
		{
			case var value when value.Equals("Slim", StringComparison.OrdinalIgnoreCase): return UnitMeshBodyVariant.Slim;
			case var value when value.Equals("Stocky", StringComparison.OrdinalIgnoreCase): return UnitMeshBodyVariant.Stocky;
			case var value when value.Equals("Any", StringComparison.OrdinalIgnoreCase): return UnitMeshBodyVariant.Any;
		}
		if (Contains(semanticText, "female", "slim")) return UnitMeshBodyVariant.Slim;
		if (Contains(semanticText, "male", "stocky")) return UnitMeshBodyVariant.Stocky;
		return bodyType.Trim().Length == 0 ? UnitMeshBodyVariant.Any : UnitMeshBodyVariant.Other;
	}

	private static UnitMeshPartKind ResolveKind(string text)
	{
		if (Contains(text, "leftshoulder", "l_shoulder", "shoulder_l", "shoulder_left")) return UnitMeshPartKind.LeftShoulder;
		if (Contains(text, "rightshoulder", "r_shoulder", "shoulder_r", "shoulder_right")) return UnitMeshPartKind.RightShoulder;
		if (Contains(text, "leftarm", "l_arm", "arm_l", "torso_arm_l", "arm_left", "lft_arm")) return UnitMeshPartKind.LeftArm;
		if (Contains(text, "rightarm", "r_arm", "arm_r", "torso_arm_r", "arm_right", "rgt_arm")) return UnitMeshPartKind.RightArm;
		if (Contains(text, "leftleg", "l_leg", "leg_l", "leg_left", "lft_leg", "g_leg_l_", "g_leg_undergarment_l")) return UnitMeshPartKind.LeftLeg;
		if (Contains(text, "rightleg", "r_leg", "leg_r", "leg_right", "rgt_leg", "g_leg_r_", "g_leg_undergarment_r")) return UnitMeshPartKind.RightLeg;
		if (Contains(text, "pelvis", "hip", "hips", "legs_hips")) return UnitMeshPartKind.Hips;
		if (Contains(text, "torso", "chest")) return UnitMeshPartKind.Torso;
		if (Contains(text, "head", "helmet")) return UnitMeshPartKind.Helmet;
		return Contains(text, "plate", "pad", "pouch", "accessory", "cape") ? UnitMeshPartKind.Accessory : UnitMeshPartKind.Unknown;
	}

	private static bool Contains(string value, params string[] tokens)
		=> tokens.Any(value.Contains);
}