namespace HD2ModCore.Domain;

/// <summary>Projects source structure with the runtime's follow/manual override ownership.</summary>
public static class RuntimeArmorComponentProjection
{
	public static IReadOnlyList<RuntimeArmorRegistryModelPiece> Project(
		IReadOnlyList<RuntimeArmorRegistryModelPiece> sourcePieces,
		uint? sourceOverrideOwner,
		uint? target,
		bool followSource,
		IReadOnlyList<RuntimeArmorRegistryPieceOverride> overrides)
	{
		var owner = target is { } id && !followSource ? id : sourceOverrideOwner;
		return sourcePieces.Select(piece =>
		{
			var enabled = overrides.FirstOrDefault(item =>
				item.OwnerItemId == owner && item.Body == piece.Body
				&& item.Slot == piece.Slot && item.PieceType == piece.PieceType
				&& string.Equals(item.Unit, piece.Unit, StringComparison.OrdinalIgnoreCase))?.Enabled ?? true;
			// A disabled custom-model definition is filtered by Lua before target overrides.
			return piece with { Enabled = piece.Enabled && enabled };
		}).ToArray();
	}
}
