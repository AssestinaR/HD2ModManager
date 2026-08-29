using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// Purpose: Prepares configuration-scoped Lua merge output and external component mappings.
// It owns content composition; ApplyPlanner and ApplyExecutor only consume its result.
public interface ILuaDeploymentService
{
	ValueTask<LuaDeploymentPreparation> PrepareAsync(
		Profile profile,
		LibrarySnapshot snapshot,
		PatchFileIndex patchIndex,
		string modsRootDirectory,
		string gameDataDirectory,
		IReadOnlyDictionary<ModNodeId, ModContentFacts> contentFacts,
		CancellationToken cancellationToken = default);
}

public sealed record LuaDeploymentPreparation(
	PatchFileIndex PatchIndex,
	IReadOnlyList<GeneratedPatchGroup> GeneratedPatchGroups,
	IReadOnlyList<ExternalFileMapping> ExternalFiles,
	IReadOnlyList<CoreIssue> Issues)
{
	public bool HasErrors => Issues.Any(issue => issue.Severity == CoreIssueSeverity.Error);
}
