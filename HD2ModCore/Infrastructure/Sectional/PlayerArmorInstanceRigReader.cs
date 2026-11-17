using System.Globalization;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure.Sectional;

internal static class PlayerArmorInstanceRigReader
{
	internal static async Task<IReadOnlyDictionary<string, PlayerArmorInstanceRig>> ReadAsync(
		string modsDirectory,
		string gameDataDirectory,
		GameDataAuthoritySnapshot authority,
		IReadOnlyList<RuntimeArmorRegistryMapping> mappings,
		IReadOnlySet<ulong> deployedUnits,
		CancellationToken cancellationToken)
	{
		var rigs = new Dictionary<string, PlayerArmorInstanceRig>(StringComparer.Ordinal);
		var thumbnailArmorIds = authority.Kits.Where(kit => kit.Kind == GameDataEquipmentKitKind.Armor
			&& kit.Pieces.Any(piece => LowPollutionMappingPlanner.IsArmoryThumbnailShoulder(piece.UnitAssetKey.FileId)))
			.Select(kit => kit.ItemId).ToHashSet();
		var modelKeys = mappings.Where(mapping => mapping.IsModelMapping && thumbnailArmorIds.Contains(mapping.TargetItemId))
			.Select(mapping => mapping.ModelKey!).Distinct(StringComparer.Ordinal).ToArray();
		if (modelKeys.Length == 0) return rigs;

		var catalog = CustomModelCatalog.Collect(modsDirectory);
		var unitIds = new HashSet<ulong>();
		foreach (var modelKey in modelKeys)
		{
			if (!catalog.Entries.TryGetValue(modelKey, out var entry)
				|| !LowPollutionLayoutReader.TryRead(entry.ModDirectory, out var layout, out _)
				|| layout is not { HeadMerge: true })
			{
				continue;
			}

			foreach (var unit in layout.UnitsOf(LowPollutionRoles.BodyArms)
				.Concat(layout.UnitsOf(LowPollutionRoles.HeadBody)))
			{
				if (ulong.TryParse(unit, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)
					&& deployedUnits.Contains(id))
				{
					unitIds.Add(id);
				}
			}
		}
		if (unitIds.Count == 0) return rigs;

		UnitTransformInfo avatar;
		try
		{
			avatar = await new CanonicalAvatarRigReader(
				new HD2ModAdaptation.PatchReconstruction.GameDataPackageResolver(gameDataDirectory))
				.ReadTransformInfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or KeyNotFoundException)
		{
			return rigs;
		}
		var scanner = new PatchTocScanner();
		var reader = new PatchEntryPayloadReader();
		var parser = new PatchFileNameParser();
		var patches = Directory.EnumerateFiles(gameDataDirectory, "*.patch_*", SearchOption.TopDirectoryOnly)
			.Select(path => (Path: path, Info: parser.TryParse(Path.GetFileName(path), out var info) ? info : null))
			.Where(patch => patch.Info?.SidecarKind == PatchSidecarKind.Base)
			.OrderByDescending(patch => patch.Info!.PatchIndex)
			.ThenByDescending(patch => patch.Info!.ArchiveHex16, StringComparer.OrdinalIgnoreCase);
		foreach (var patch in patches)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var entries = await scanner.ScanEntriesAsync(patch.Path, cancellationToken).ConfigureAwait(false);
			foreach (var entry in entries)
			{
				if (entry.AssetKey.TypeId != PatchUnitMeshReader.UnitTypeId || !unitIds.Remove(entry.AssetKey.FileId))
					continue;

				// TransformInfo lives entirely in the TOC. Large geometry and texture sidecars are unnecessary.
				try
				{
					var data = await reader.ReadPayloadAsync(entry with { StreamSize = 0, GpuResourceSize = 0 }, cancellationToken)
						.ConfigureAwait(false);
					var model = UnitMeshReader.ReadTransformInfoFromUnitToc(data.TocData);
					if (CreateRig(avatar, model) is { } rig)
						rigs.Add(SectionalExclusivePlanner.Hex(entry.AssetKey.FileId), rig);
				}
				catch (Exception exception) when (exception is IOException or InvalidDataException)
				{
					// Keep the original unavailable result for this model.
				}
			}
			if (unitIds.Count == 0) break;
		}
		return rigs;
	}

	internal static PlayerArmorInstanceRig? CreateRig(UnitTransformInfo avatar, UnitTransformInfo model)
	{
		if (avatar.NameHashes.Count == 0 || model.NameHashes.Count < avatar.NameHashes.Count) return null;
		var avatarNodes = new Dictionary<uint, int>();
		for (var i = 0; i < avatar.NameHashes.Count; i++)
			if (!avatarNodes.TryAdd(avatar.NameHashes[i], i + 1)) return null;
		var boneLinks = new List<int>(2 * avatarNodes.Count);
		for (var i = 0; i < model.NameHashes.Count; i++)
		{
			if (!avatarNodes.Remove(model.NameHashes[i], out var avatarNode)) continue;
			boneLinks.Add(i + 1);
			boneLinks.Add(avatarNode);
		}
		return avatarNodes.Count == 0
			? new PlayerArmorInstanceRig(avatar.NameHashes.Count, model.NameHashes.Count, boneLinks)
			: null;
	}
}
