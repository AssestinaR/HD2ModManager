using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Application;

namespace HD2ModManager.Services
{
    // 作用：将 UI 导入请求转交给 HD2ModCore，并补充 manager 层标签/图片元数据。
    public class ImportService
    {
        private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z" };
        private readonly ModLibraryService _library;
        private readonly Action<string>? _onInfo;
        private readonly Action<string>? _onError;
        private readonly StoragePaths _paths;
        private readonly HD2ModCore.Application.IModInformationCenter _informationCenter;

        // The most recent import's complete content-impact set (new and overwritten nodes).
        // Batch orchestration uses this to schedule derived data once after all file commits.
        public IReadOnlyList<ModNodeId> LastAffectedNodeIds { get; private set; } = Array.Empty<ModNodeId>();

        public ImportService(ModLibraryService library, Action<string>? onInfo = null, Action<string>? onError = null, IModInformationCenter? informationCenter = null)
        {
            _library = library;
            _onInfo = onInfo;
            _onError = onError;
            _paths = SettingsService.CreateStoragePaths();
            _informationCenter = informationCenter ?? throw new ArgumentNullException(nameof(informationCenter), "ImportService requires the shared IModInformationCenter.");
        }

        public Task EnqueueImportsAsync(IEnumerable<string> paths, CancellationToken ct = default)
        {
            return ImportSequentiallyAsync(paths, ct);
        }

        private async Task ImportSequentiallyAsync(IEnumerable<string> paths, CancellationToken ct)
        {
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                await ImportPathAsync(path, ct).ConfigureAwait(false);
            }
        }

        public async Task<List<string>> ImportPathAsync(
            string path,
            CancellationToken ct,
            bool notifyLibraryChanged = true,
            bool refreshDerivedData = true)
        {
            try
            {
				var importer = CoreServices.CreateModLibraryImporter(_paths, _informationCenter, _library.InformationReader);
                var before = _library.Snapshot.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value);
                ImportResult result;
                if (Directory.Exists(path))
                {
                    result = await importer.ImportFolderAsync(path, ct).ConfigureAwait(false);
                }
                else if (File.Exists(path) && ArchiveExtensions.Contains(Path.GetExtension(path)))
                {
                    result = await importer.ImportArchiveAsync(path, ct).ConfigureAwait(false);
                }
                else if (File.Exists(path))
                {
                    var dir = Path.GetDirectoryName(path) ?? throw new DirectoryNotFoundException(path);
                    result = await importer.ImportFolderAsync(dir, ct).ConfigureAwait(false);
                }
                else
                {
                    throw new FileNotFoundException("Import source not found.", path);
                }

                _library.ReplaceSnapshot(result.Snapshot, buildDerivedData: false);
                var importedGuids = _library.Snapshot.Nodes.Values
                    .Where(node => !before.ContainsKey(node.Id))
                    .Select(node => node.Id.Value.ToString("N"))
                    .ToList();
                var changedExistingNodeIds = _library.Snapshot.Nodes.Values
                    .Where(node => before.TryGetValue(node.Id, out var previous) && !Equals(previous, node))
                    .Select(node => node.Id)
                    .ToArray();
                await _library.EnableDefaultOptionsForEmptyHostsAsync(importedGuids, ct).ConfigureAwait(false);
                var affectedNodeIds = importedGuids
                    .Select(guid => Guid.TryParse(guid, out var value) ? new ModNodeId(value) : (ModNodeId?)null)
                    .Concat(changedExistingNodeIds.Select(id => (ModNodeId?)id))
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .Distinct()
                    .ToArray();
                LastAffectedNodeIds = affectedNodeIds;
                foreach (var nodeId in affectedNodeIds)
                {
                    await _library.InvalidateContentNodeAsync(nodeId, ct).ConfigureAwait(false);
                }
                if (refreshDerivedData)
                {
                    await _library.RefreshCommittedContentAsync(
                        affectedNodeIds,
                        ModContentChangeKind.Changed,
                        alreadyInvalidated: true,
                        cancellationToken: ct).ConfigureAwait(false);
                }
                if (notifyLibraryChanged) _library.NotifyImportCompleted();
                _onInfo?.Invoke($"Imported {result.SourceDisplayName}");
                return importedGuids.Where(g => _library.Get(g) != null).ToList();
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex.Message);
                throw;
            }
        }

    }
}
