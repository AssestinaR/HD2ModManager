using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// Purpose: Prepares configuration-scoped Lua merge output and external component mappings.
// It owns content composition; ApplyPlanner and ApplyExecutor only consume its result.
public interface ILuaDeploymentService
{
	/// <summary>
	/// Builds the effective Lua/external deployment shape without writing the
	/// runtime cache, probing link capabilities, or executing Mod Lua. The
	/// generated Patch group paths are deterministic intents and may not exist
	/// until <see cref="PrepareAsync"/> materializes them during a guarded apply.
	/// </summary>
	ValueTask<LuaDeploymentPlan> PlanAsync(
		Profile profile,
		LibrarySnapshot snapshot,
		PatchFileIndex patchIndex,
		string modsRootDirectory,
		string gameDataDirectory,
		IReadOnlyDictionary<ModNodeId, ModContentFacts> contentFacts,
		CancellationToken cancellationToken = default,
		LuaDeploymentPermission permission = LuaDeploymentPermission.DenyOpaque);

	ValueTask<LuaDeploymentPreparation> PrepareAsync(
		Profile profile,
		LibrarySnapshot snapshot,
		PatchFileIndex patchIndex,
		string modsRootDirectory,
		string gameDataDirectory,
		IReadOnlyDictionary<ModNodeId, ModContentFacts> contentFacts,
		CancellationToken cancellationToken = default,
		LuaDeploymentPermission permission = LuaDeploymentPermission.DenyOpaque);

	/// <summary>
	/// Materializes a planned Lua merge after the caller has acquired its write
	/// leases. CompileOnly never executes Mod Lua in a standalone Lua runtime.
	/// </summary>
	ValueTask<LuaDeploymentPreparation> MaterializeAsync(
		Profile profile,
		LibrarySnapshot snapshot,
		PatchFileIndex patchIndex,
		string modsRootDirectory,
		string gameDataDirectory,
		IReadOnlyDictionary<ModNodeId, ModContentFacts> contentFacts,
		LuaDeploymentValidationMode validationMode,
		CancellationToken cancellationToken = default,
		LuaDeploymentPermission permission = LuaDeploymentPermission.DenyOpaque);
}

public enum LuaDeploymentPermission
{
	DenyOpaque,
	AllowOpaque,
}

public enum LuaDeploymentValidationMode
{
	CompileOnly,
	CompileAndSmoke,
}

public sealed record LuaDeploymentPreparation(
	PatchFileIndex PatchIndex,
	IReadOnlyList<GeneratedPatchGroup> GeneratedPatchGroups,
	IReadOnlyList<ExternalFileMapping> ExternalFiles,
	IReadOnlyList<CoreIssue> Issues)
{
	public bool HasErrors => Issues.Any(issue => issue.Severity == CoreIssueSeverity.Error);
}

// Purpose: Pure deployment planning result for Lua merge and external files.
// GeneratedPatchGroups contain deterministic output intents; they are only
// materialized after the deployment caller has acquired its write locks.
public sealed record LuaDeploymentPlan(
	PatchFileIndex PatchIndex,
	IReadOnlyList<GeneratedPatchGroup> GeneratedPatchGroups,
	IReadOnlyList<ExternalFileMapping> ExternalFiles,
	IReadOnlyList<CoreIssue> Issues,
	bool RequiresMaterialization,
	string? CompositionFingerprint,
	int InputCount)
{
	public bool HasErrors => Issues.Any(issue => issue.Severity == CoreIssueSeverity.Error);
}
