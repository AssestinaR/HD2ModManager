namespace HD2ModCore.Domain;

// Purpose: Compares ModNode values by their persisted meaning rather than by
// reference identity of collection-valued properties restored from JSON.
public static class ModNodeSemanticComparer
{
	public static bool AreEqual(ModNode? left, ModNode? right)
	{
		if (ReferenceEquals(left, right)) return true;
		if (left is null || right is null) return false;

		return left.Id == right.Id
			&& string.Equals(left.RelativePath, right.RelativePath, StringComparison.Ordinal)
			&& string.Equals(left.ContentFingerprint, right.ContentFingerprint, StringComparison.Ordinal)
			&& MetadataEquals(left.Metadata, right.Metadata)
			&& PatchGroupsEqual(left.PatchGroups, right.PatchGroups)
			&& SequenceEqual(left.Children, right.Children);
	}

	private static bool MetadataEquals(ModNodeMetadata left, ModNodeMetadata right)
		=> string.Equals(left.Name, right.Name, StringComparison.Ordinal)
			&& string.Equals(left.Notes, right.Notes, StringComparison.Ordinal)
			&& left.CreatedUtc == right.CreatedUtc
			&& left.ModifiedUtc == right.ModifiedUtc
			&& left.Kind == right.Kind
			&& SequenceEqual(left.HostModGuids, right.HostModGuids, StringComparer.OrdinalIgnoreCase)
			&& string.Equals(left.SourcePackageGuid, right.SourcePackageGuid, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(left.SourcePackagePath, right.SourcePackagePath, StringComparison.Ordinal)
			&& left.OptionOrder == right.OptionOrder
			&& left.HelmetToArmorMerged == right.HelmetToArmorMerged
			&& left.SuppressDefaultDirectOptionActivation == right.SuppressDefaultDirectOptionActivation
			&& left.IsLogicalContainer == right.IsLogicalContainer;

	private static bool PatchGroupsEqual(IReadOnlyList<PatchGroupKey>? left, IReadOnlyList<PatchGroupKey>? right)
	{
		if (ReferenceEquals(left, right)) return true;
		if (left is null || right is null || left.Count != right.Count) return false;

		for (var index = 0; index < left.Count; index++)
		{
			if (!string.Equals(left[index].ArchiveHex16, right[index].ArchiveHex16, StringComparison.OrdinalIgnoreCase)
				|| left[index].PatchIndex != right[index].PatchIndex)
				return false;
		}
		return true;
	}

	private static bool SequenceEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right, IEqualityComparer<T>? comparer = null)
	{
		if (ReferenceEquals(left, right)) return true;
		if (left is null || right is null || left.Count != right.Count) return false;

		comparer ??= EqualityComparer<T>.Default;
		for (var index = 0; index < left.Count; index++)
		{
			if (!comparer.Equals(left[index], right[index])) return false;
		}
		return true;
	}
}
