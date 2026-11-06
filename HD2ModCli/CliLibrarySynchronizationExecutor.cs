using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliLibrarySynchronizationExecutionResult(
	bool Success,
	bool Changed,
	IReadOnlyList<string> AddedNodeIds,
	IReadOnlyList<string> ChangedNodeIds,
	IReadOnlyList<string> MissingNodeIds,
	IReadOnlyList<CliLibrarySynchronizationDiagnostic> Diagnostics);

/// <summary>
/// Saves only validated node facts under the ordinary CLI library lease. It
/// never rewrites profiles.json, activation files, physical Mod files, caches,
/// or GameData.
/// </summary>
internal static class CliLibrarySynchronizationExecutor
{
	public static async ValueTask<CliLibrarySynchronizationExecutionResult> ApplyAsync(
		CliLibrarySynchronizationPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		var validation = await CliLibrarySynchronizationPlanArtifacts
			.ValidateAsync(document, workspace, cancellationToken)
			.ConfigureAwait(false);
		if (!validation.IsValid || validation.Current is null)
			return Failure(validation.Diagnostics);

		await using var session = await CliWriteTargetGuard
			.AcquireSessionAsync(workspace, CliWriteTargetMode.Mutation, cancellationToken)
			.ConfigureAwait(false);
		// 基底目录改名（vpk2patch/ → sectional/）。放在租约内、第二次校验之前：
		// 改名会动磁盘，而下面那次扫描必须看到改名之后的布局。
		var migration = await CoreServices.CreateModContentLayoutMigration()
			.MigrateAsync(workspace.LibraryRoot!, validation.Current.Synchronization.Snapshot, cancellationToken)
			.ConfigureAwait(false);
		if (!migration.IsClean)
		{
			var diagnostics = migration.Conflicts
				.Select(name => new CliLibrarySynchronizationDiagnostic(
					"LibraryLayoutMigrationConflict", "warning", false,
					$"{name} 的新旧两个基底目录（sectional/ 与 vpk2patch/）同时存在；已跳过改名，需要人工确认哪个才是当前产物。",
					Path: name))
				.Concat(migration.Failures.Select(failure => new CliLibrarySynchronizationDiagnostic(
					"LibraryLayoutMigrationFailed", "warning", false,
					$"基底目录改名失败：{failure}")))
				.ToArray();
			if (diagnostics.Length != 0)
				return new CliLibrarySynchronizationExecutionResult(
					true, false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), diagnostics);
		}
		// Repeat validation inside the cross-process lease before writing the
		// library file; the plan never bypasses this second scan.
		var locked = await CliLibrarySynchronizationPlanArtifacts
			.ValidateAsync(document, workspace, cancellationToken)
			.ConfigureAwait(false);
		if (!locked.IsValid || locked.Current is null)
			return Failure(locked.Diagnostics);

		var synchronization = locked.Current.Synchronization;
		if (!synchronization.HasSnapshotChanges)
		{
			return new CliLibrarySynchronizationExecutionResult(
				true,
				false,
				Array.Empty<string>(),
				Array.Empty<string>(),
				synchronization.MissingNodeIds.OrderBy(id => id.Value).Select(CliProjections.FormatId).ToArray(),
				locked.Diagnostics);
		}

		_ = await session.SaveNodesOnlyAsync(synchronization.Snapshot, cancellationToken).ConfigureAwait(false);
		return new CliLibrarySynchronizationExecutionResult(
			true,
			true,
			synchronization.AddedNodeIds.OrderBy(id => id.Value).Select(CliProjections.FormatId).ToArray(),
			synchronization.ChangedNodeIds.OrderBy(id => id.Value).Select(CliProjections.FormatId).ToArray(),
			synchronization.MissingNodeIds.OrderBy(id => id.Value).Select(CliProjections.FormatId).ToArray(),
			locked.Diagnostics);
	}

	private static CliLibrarySynchronizationExecutionResult Failure(
		IReadOnlyList<CliLibrarySynchronizationDiagnostic> diagnostics)
		=> new(false, false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), diagnostics);
}
