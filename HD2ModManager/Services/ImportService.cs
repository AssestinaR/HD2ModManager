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
		public bool LastImportHasOptionPathConflicts { get; private set; }

        /// <summary>
        /// 这次导入如果是 VPK 来源，这里就是转换报告（每个 VPK 转了/跳过了/为什么）。
        /// <c>null</c> 表示这次导入与 VPK 无关。
        /// </summary>
        public VpkImportReport? LastVpkImportReport { get; private set; }

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
            bool refreshDerivedData = true,
            bool allowVpkConversion = true)
        {
            // VPK 来源走自己的路：一个 VPK = 一个 Mod 文件夹，文件夹里的 vpk2patch/ 放实际生效的 patch。
            // 判定失败（不是 VPK 来源）就原样往下走，行为与以前完全一致。
            //
            // 转换产出的目录必须传 allowVpkConversion: false：它根上有来源 VPK 原件，
            // 不挡住就会"产出目录 → 再当来源转换 → 又产出"，永远建不出 Mod。
            if (allowVpkConversion)
            {
                var vpkSource = await TryPrepareVpkSourceAsync(path, ct).ConfigureAwait(false);
                if (vpkSource is not null)
                    return await ImportVpkSourceAsync(vpkSource, ct, notifyLibraryChanged, refreshDerivedData).ConfigureAwait(false);
            }

            try
            {
				UiPerformanceDiagnostics.RecordBurst(
					"Import.Path",
					$"source={Path.GetFileName(path)}; kind={(Directory.Exists(path) ? "folder" : "file")}",
					threshold: 8);
				using var totalPerf = UiPerformanceDiagnostics.Measure(
					"Import.Path",
					thresholdMilliseconds: 100,
					details: () => $"source={Path.GetFileName(path)}; notify={notifyLibraryChanged}; refreshDerived={refreshDerivedData}");
				var importer = CoreServices.CreateModLibraryImporter(_paths, _informationCenter, _library.InformationReader);
                var before = _library.Snapshot.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value);
                ImportResult result;
                using (UiPerformanceDiagnostics.Measure(
					"Import.ScanAndStore",
					thresholdMilliseconds: 100,
					details: () => $"source={Path.GetFileName(path)}; archive={File.Exists(path) && ArchiveExtensions.Contains(Path.GetExtension(path))}"))
                {
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
                }

				List<string> importedGuids;
				ModNodeId[] affectedNodeIds;
				using (UiPerformanceDiagnostics.Measure(
					"Import.CommitSnapshot",
					thresholdMilliseconds: 50,
					details: () => $"source={Path.GetFileName(path)}; nodes={result.Snapshot.Nodes.Count}"))
				{
					_library.ReplaceSnapshot(result.Snapshot, buildDerivedData: false);
					importedGuids = _library.Snapshot.Nodes.Values
						.Where(node => !before.ContainsKey(node.Id))
						.Select(node => node.Id.Value.ToString("N"))
						.ToList();
					var changedExistingNodeIds = _library.Snapshot.Nodes.Values
						.Where(node => before.TryGetValue(node.Id, out var previous)
							&& !ModNodeSemanticComparer.AreEqual(previous, node))
						.Select(node => node.Id)
						.ToArray();
					affectedNodeIds = importedGuids
						.Select(guid => Guid.TryParse(guid, out var value) ? new ModNodeId(value) : (ModNodeId?)null)
						.Concat(changedExistingNodeIds.Select(id => (ModNodeId?)id))
						.Where(id => id.HasValue)
						.Select(id => id!.Value)
						.Distinct()
						.ToArray();
					await _library.InitializeDefaultOptionActivationsAsync(
						affectedNodeIds.Select(id => id.Value.ToString("N")),
						// The importer marks only conflicted logical parents. Do not
						// suppress defaults for every unrelated Mod in this batch.
						enableDefaults: true,
						ct).ConfigureAwait(false);
					LastAffectedNodeIds = affectedNodeIds;
					LastImportHasOptionPathConflicts = result.HasOptionPathConflicts;
					foreach (var nodeId in affectedNodeIds)
					{
						await _library.InvalidateContentNodeAsync(nodeId, ct).ConfigureAwait(false);
					}
				}
                if (refreshDerivedData)
                {
					using var derivedPerf = UiPerformanceDiagnostics.Measure(
						"Import.RefreshDerived",
						thresholdMilliseconds: 100,
						details: () => $"source={Path.GetFileName(path)}; affected={affectedNodeIds.Length}");
					await _library.RefreshCommittedContentAsync(
						affectedNodeIds,
						ModContentChangeKind.Changed,
						alreadyInvalidated: true,
						cancellationToken: ct).ConfigureAwait(false);
                }
                if (notifyLibraryChanged) _library.NotifyImportCompleted();
                var identityNote = result.ConflictRemappedNodeIds.Count == 0
                    ? string.Empty
                    : $"；检测到 {result.ConflictRemappedNodeIds.Count} 个 GUID 冲突，已生成新的库 GUID";
                _onInfo?.Invoke($"Imported {result.SourceDisplayName}{identityNote}");
                return importedGuids.Where(g => _library.Get(g) != null).ToList();
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex.Message);
                throw;
            }
        }

        /// <summary>
        /// 这个来源里有没有 VPK。有就摊开到临时目录并返回；没有返回 <c>null</c>。
        /// <para>
        /// 注意：<b>不能</b>只看扩展名。用户的包通常是"一个压缩包，里面 8 个模型 VPK + 一堆材质 VPK"，
        /// 所以压缩包要先看一眼目录区（<c>7z l</c>，不解压）。
        /// </para>
        /// </summary>
        private async Task<VpkImportSource?> TryPrepareVpkSourceAsync(string path, CancellationToken ct)
        {
            try
            {
                var service = new VpkImportService(_paths, SettingsService.GetGameDataFolder());
                return await service.TryPrepareAsync(path, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // 摊不开就当普通来源处理：原来的导入路径会给出更贴合的错误。
                _onError?.Invoke(exception.Message);
                return null;
            }
        }

        private async Task<List<string>> ImportVpkSourceAsync(
            VpkImportSource source,
            CancellationToken ct,
            bool notifyLibraryChanged,
            bool refreshDerivedData)
        {
            try
            {
                var service = new VpkImportService(_paths, SettingsService.GetGameDataFolder());
                var (report, produced) = await service
                    .ConvertAsync(source, stage => _onInfo?.Invoke(stage), ct)
                    .ConfigureAwait(false);
                LastVpkImportReport = report;
                _onInfo?.Invoke(report.ToSummary());
                LogService.Info(report.ToDetail());

                if (produced.Count == 0)
                {
                    if (notifyLibraryChanged) _library.NotifyImportCompleted();
                    return new List<string>();
                }

                // 每个 VPK 一个文件夹，逐个走常规导入。派生数据在整批之后再算一次，
                // 否则每个 VPK 都要重算一遍全库内容事实。
                var guids = new List<string>();
                var affected = new List<ModNodeId>();
                var conflicts = false;
                foreach (var directory in produced)
                {
                    ct.ThrowIfCancellationRequested();
                    var imported = await ImportPathAsync(
                        directory,
                        ct,
                        notifyLibraryChanged: false,
                        refreshDerivedData: false,
                        // 这是转换产出，不是待转换的来源：见 ImportPathAsync 里的说明。
                        allowVpkConversion: false).ConfigureAwait(false);
                    guids.AddRange(imported);
                    affected.AddRange(LastAffectedNodeIds);
                    conflicts |= LastImportHasOptionPathConflicts;

                    // 含 Unit 的转换产物**立刻**换到默认目标护甲（RE-2310）。
                    //
                    // 为什么默认就换：vpk2patch 产出的是"新 Unit id 的自定义素体"，它不替换
                    // 任何原版护甲——不换甲的话，用户装进游戏什么都不会变（实测反馈）。
                    // 默认目标写在 CLI 里（vpk rekey 的缺省值），这里不重复定义。
                    foreach (var guid in imported)
                    {
                        await TryAutoRekeyAsync(guid, ct).ConfigureAwait(false);
                    }
                }

                LastAffectedNodeIds = affected.Distinct().ToArray();
                LastImportHasOptionPathConflicts = conflicts;
                if (refreshDerivedData && LastAffectedNodeIds.Count != 0)
                {
                    await _library.RefreshCommittedContentAsync(
                        LastAffectedNodeIds,
                        ModContentChangeKind.Changed,
                        alreadyInvalidated: true,
                        cancellationToken: ct).ConfigureAwait(false);
                }

                if (notifyLibraryChanged) _library.NotifyImportCompleted();
                return guids.Where(guid => _library.Get(guid) != null).ToList();
            }
            finally
            {
                source.Dispose();
            }
        }

        /// <summary>
        /// 导入之后立刻把这个 Mod 换到默认目标护甲上。
        /// <para>
        /// <b>为什么是导入时而不是"用户点了才换"</b>：vpk2patch 的产物是<b>新 Unit id</b> 的
        /// 自定义素体，游戏里没有任何护甲引用它——不换甲就等于装了个看不见的 Mod。
        /// 用户要的是"装完就能用"，切换只是可选项。
        /// </para>
        /// <para>
        /// 失败**不算导入失败**：换甲依赖游戏装备表与资产索引，缺它们时用户仍应拿到
        /// 转换好的 Mod，只是它还没指向具体护甲——这一点写进日志与报告。
        /// </para>
        /// </summary>
        private async Task TryAutoRekeyAsync(string guid, CancellationToken ct)
        {
            try
            {
                if (!Guid.TryParse(guid, out var id)
                    || !_library.Snapshot.Nodes.TryGetValue(new ModNodeId(id), out var node))
                    return;
                var modDirectory = Path.Combine(_paths.ModsDirectory, node.RelativePath);
                if (!File.Exists(Path.Combine(modDirectory, ModNodePayloadLayout.VpkPatchDirectoryName, "sections.json")))
                    return;

                _onInfo?.Invoke($"正在把 {node.Metadata.Name} 换到默认目标护甲…");
                // 空目标 = 用 CLI 的缺省值（RE-2310）。默认只在一处定义，管理器不重复一遍。
                var outcome = await VpkRekeyService
                    .RunAsync(guid, modDirectory, Array.Empty<string>(), stage => _onInfo?.Invoke(stage), ct)
                    .ConfigureAwait(false);
                if (outcome.Ok)
                {
                    LogService.Info($"已换到默认目标护甲：{node.Metadata.Name}（{outcome.TargetNames.FirstOrDefault()}），"
                        + $"替换 {outcome.ReplacedUnits} 个 Unit、隐藏 {outcome.HiddenMeshes} 个网格。");
                    return;
                }

                LogService.Info($"换甲未完成：{node.Metadata.Name}：{outcome.Error}。"
                    + "这个 Mod 目前是自定义素体版，需要到详情页选择目标护甲。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException)
            {
                LogService.Info($"换甲未完成：{exception.Message}");
            }
        }

    }
}
