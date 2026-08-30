using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// The write-side companion to <see cref="ModImportPlanner"/>. It deliberately
/// reuses <see cref="ModLibraryImporter"/> for all file-copy/tree semantics and
/// only owns the post-import bookkeeping shared by GUI and headless callers.
/// </summary>
public sealed class ModImportExecutionService : IModImportExecutionService
{
	private readonly StoragePaths _paths;
	private readonly IModLibraryStore _store;
	private readonly IObjectTreeImporter _folderImporter;
	private readonly IArchiveObjectTreeImporter _archiveImporter;
	private readonly OptionActivationStore _optionActivations;
	private readonly OptionActivationDefaultInitializer _optionDefaults;

	public ModImportExecutionService(
		StoragePaths paths,
		IModLibraryStore? store = null,
		IObjectTreeImporter? folderImporter = null,
		IArchiveObjectTreeImporter? archiveImporter = null,
		OptionActivationStore? optionActivations = null,
		OptionActivationDefaultInitializer? optionDefaults = null)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
		// A headless import must never hydrate a new external library from the
		// manager-root's legacy library. GUI startup keeps the default migration
		// behavior through its own store factory; this write workflow is strict.
		_store = store ?? new JsonModLibraryStore(paths, allowLegacyMigration: false);
		_folderImporter = folderImporter ?? CoreServices.CreateObjectTreeImporter();
		_archiveImporter = archiveImporter ?? CoreServices.CreateArchiveObjectTreeImporter();
		_optionActivations = optionActivations ?? CoreServices.CreateOptionActivationStore(paths);
		_optionDefaults = optionDefaults ?? new OptionActivationDefaultInitializer();
	}

	public ValueTask<ModImportExecutionResult> ImportFolderAsync(string folderPath, CancellationToken cancellationToken = default)
		=> ImportAsync(folderPath, archive: false, cancellationToken);

	public ValueTask<ModImportExecutionResult> ImportArchiveAsync(string archivePath, CancellationToken cancellationToken = default)
		=> ImportAsync(archivePath, archive: true, cancellationToken);

	private async ValueTask<ModImportExecutionResult> ImportAsync(string sourcePath, bool archive, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
		// The live OptionActivationStore intentionally keeps GUI startup resilient
		// by treating malformed legacy data as empty. A write workflow cannot do
		// that: validate the existing file strictly before any Mod bytes are copied.
		_ = await OptionActivationStore.TryLoadReadOnlySnapshotAsync(
			Path.Combine(_paths.ModsDirectory, "option-activations.json"),
			cancellationToken).ConfigureAwait(false);

		var before = await _store.TryLoadAsync(cancellationToken).ConfigureAwait(false);
		await using var reader = CoreServices.CreateModInformationReader();
		await using var informationCenter = CoreServices.CreateModInformationCenter(_paths, reader);
		var importer = new ModLibraryImporter(
			_paths,
			_folderImporter,
			_archiveImporter,
			_store,
			informationCenter: informationCenter,
			informationReader: reader);
		var imported = archive
			? await importer.ImportArchiveAsync(sourcePath, cancellationToken).ConfigureAwait(false)
			: await importer.ImportFolderAsync(sourcePath, cancellationToken).ConfigureAwait(false);

		var previousNodes = before?.Nodes ?? new Dictionary<ModNodeId, ModNode>();
		var added = imported.Snapshot.Nodes.Values
			.Where(node => !previousNodes.ContainsKey(node.Id))
			.Select(node => node.Id)
			.OrderBy(id => id.Value)
			.ToArray();
		var changed = imported.Snapshot.Nodes.Values
			.Where(node => previousNodes.TryGetValue(node.Id, out var previous) && !ModNodeSemanticComparer.AreEqual(previous, node))
			.Select(node => node.Id)
			.OrderBy(id => id.Value)
			.ToArray();
		var partial = new ModImportExecutionResult(imported.Snapshot, imported.SourceDisplayName, added, changed, 0, imported.HasOptionPathConflicts)
		{
			ImportedNodeIds = imported.ImportedNodeIds.Count != 0 ? imported.ImportedNodeIds : [imported.ImportedRootId],
			CacheInvalidationStatus = imported.CacheInvalidationStatus,
			CacheInvalidationFailedNodeIds = imported.CacheInvalidationFailedNodeIds,
		};

		try
		{
			var initialized = await _optionDefaults.InitializeMissingStatesAsync(
				imported.Snapshot,
				_optionActivations,
				partial.AffectedNodeIds,
				// Conflicted manifest parents carry their own local suppression marker.
				// Keep ordinary defaults for every unrelated node in the same import.
				enableDefaults: true,
				cancellationToken).ConfigureAwait(false);
			return partial with { InitializedOptionActivationCount = initialized };
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			throw new ModImportPostCommitException(partial, exception);
		}
	}
}
