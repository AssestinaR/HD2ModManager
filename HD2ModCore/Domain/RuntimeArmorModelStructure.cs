namespace HD2ModCore.Domain;

/// <summary>
/// Workspace-only structural state. Reserved IDs never become runtime slots.
/// <para>
/// 这里实现的是护甲结构里"Any 与 Stocky/Slim 互斥"那条规则：同一个
/// <c>(Slot, PieceType)</c> 要么一个 Any 件，要么一对 Stocky+Slim 件，两者不能同时生效。
/// 结构全貌见 <see cref="ArmorStructure"/>。
/// </para>
/// </summary>
public static class RuntimeArmorModelStructure
{
	public static IReadOnlyList<RuntimeArmorRegistryModelPiece> Normalize(IEnumerable<RuntimeArmorRegistryModelPiece> pieces)
	{
		var result = new List<RuntimeArmorRegistryModelPiece>();
		foreach (var row in pieces.GroupBy(p => (p.Slot, p.PieceType)))
		{
			var any = row.FirstOrDefault(p => p.Body == 3 && !p.IsReserved);
			// Migrate legacy split rows: a disabled Any alongside enabled concrete
			// variants was left behind by the previous editor.
			var legacySplit = any is { Enabled: false, OriginalBody: null }
				&& row.Any(p => p.Body is 0 or 1 && p.Enabled && !p.IsReserved);
			foreach (var p in row)
				result.Add(legacySplit && p == any ? p with { IsReserved = true }
					: !legacySplit && any is not null && p.Body is 0 or 1 ? p with { IsReserved = true }
					: p);
		}
		return result;
	}

	public static IReadOnlyList<RuntimeArmorRegistryModelPiece> Active(IEnumerable<RuntimeArmorRegistryModelPiece> pieces)
		=> Normalize(pieces).Where(p => !p.IsReserved).ToArray();

	public static IReadOnlyList<RuntimeArmorRegistryModelPiece> Merge(IEnumerable<RuntimeArmorRegistryModelPiece> pieces, uint slot, uint type)
	{
		var all = Normalize(pieces).ToArray();
		var row = all.Where(p => p.Slot == slot && p.PieceType == type).ToArray();
		if (row.Any(p => p.Body == 3 && !p.IsReserved)) return all;
		var selected = row.FirstOrDefault(p => p.Body == 0 && !p.IsReserved)
			?? row.FirstOrDefault(p => p.Body == 1 && !p.IsReserved);
		if (selected is null) return all;
		return all.Where(p => !(p.Slot == slot && p.PieceType == type && p.Body == 3))
			.Select(p => p == selected ? p with { Body = 3, OriginalBody = p.Body, IsReserved = false }
				: p.Slot == slot && p.PieceType == type ? p with { IsReserved = true } : p).ToArray();
	}

	public static IReadOnlyList<RuntimeArmorRegistryModelPiece> Split(IEnumerable<RuntimeArmorRegistryModelPiece> pieces, uint slot, uint type, Func<string> newId)
	{
		var all = Normalize(pieces).ToList();
		var any = all.FirstOrDefault(p => p.Slot == slot && p.PieceType == type && p.Body == 3 && !p.IsReserved);
		if (any is null) return all;
		var body = any.OriginalBody ?? (all.Any(p => p.Slot == slot && p.PieceType == type && p.Body == 0) ? 1u : 0u);
		all.Remove(any);
		// Restore the Any owner's original ID; discard only the obsolete duplicate
		// record left by older versions, never allocate a replacement for that ID.
		all.RemoveAll(p => p.Slot == slot && p.PieceType == type && p.Body == body);
		all.Add(any with { Body = body, OriginalBody = null, IsReserved = false });
		for (uint b = 0; b < 2; b++)
			if (!all.Any(p => p.Slot == slot && p.PieceType == type && p.Body == b))
				all.Add(new(b, newId(), slot, type, any.Tone, any.Enabled));
		return all.Where(p => !(p.Slot == slot && p.PieceType == type && p.Body == 3))
			.Select(p => p.Slot == slot && p.PieceType == type ? p with { IsReserved = false } : p).ToArray();
	}
}
