using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// Purpose: Inspects multiple Mod/Patch groups, groups identical AssetKey payloads,
// reports real conflicts, and writes an approved subset as a standalone Patch.
public interface IPatchResourceRepackagingService
{
    IReadOnlyList<PatchResourcePatchGroup> DiscoverPatchGroups(
        IReadOnlyCollection<ModNode> nodes,
        string modsRootDirectory);

    ValueTask<PatchResourceRepackagingInspection> InspectAsync(
        IReadOnlyCollection<PatchResourcePatchGroup> patchGroups,
        CancellationToken cancellationToken = default);

    ValueTask<string> WriteAsync(
        IReadOnlyCollection<PatchResourceRepackagingSelection> selections,
        string outputDirectory,
        CancellationToken cancellationToken = default);
}
