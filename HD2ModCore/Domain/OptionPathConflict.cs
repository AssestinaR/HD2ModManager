namespace HD2ModCore.Domain;

/// <summary>
/// Describes one physical Patch directory whose strongest manifest Include
/// match belongs to more than one logical Option. The affected logical
/// Options keep their ordinary hierarchy; the shared payload is represented
/// once and attached to every listed host.
/// </summary>
public sealed record OptionPathConflict(
	string SourceRelativePath,
	IReadOnlyList<ModNodeId> AffectedParentOptionIds);
