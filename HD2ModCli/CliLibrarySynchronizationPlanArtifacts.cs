using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// A reviewed, top-level filesystem reconciliation for one existing Mod
/// library. It only updates library node facts; it never imports packages,
/// deletes stored files, touches activation/profile state, or deploys.
/// </summary>
public sealed record CliLibrarySynchronizationPlanDocument(
	string Schema,
	int Version,
	DateTimeOffset CreatedUtc,
	string InputFingerprint,
	CliLibrarySynchronizationRequest Request,
	CliLibrarySynchronizationInspection Inspection)
{
	public const string CurrentSchema = "hd2mod.library-synchronization-plan";
	public const int CurrentVersion = 1;

	public bool IsReady => !Inspection.Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

public sealed record CliLibrarySynchronizationRequest(string ManagerRoot, string LibraryRoot);

public sealed record CliLibrarySynchronizationInspection(
	CliLibrarySynchronizationInputSnapshot Input,
	IReadOnlyList<CliLibrarySynchronizationAddedNode> Added,
	IReadOnlyList<CliLibrarySynchronizationChangedNode> Changed,
	IReadOnlyList<CliLibrarySynchronizationMissingNode> Missing,
	bool HasFilesystemChanges,
	bool HasSnapshotChanges,
	IReadOnlyList<CliLibrarySynchronizationDiagnostic> Diagnostics);

public sealed record CliLibrarySynchronizationInputSnapshot(
	string LibraryJsonSha256,
	string PendingDeletionSha256,
	string FilesystemFingerprint,
	int PatchFileCount,
	long PatchByteCount,
	IReadOnlyList<string> IgnoredRelativePaths);

public sealed record CliLibrarySynchronizationPatchGroup(string ArchiveHex16, int PatchIndex);

/// <summary>
/// A new node ID is assigned in the reviewed plan, not during apply. This is
/// required so an agent can safely carry a stable newly-registered identity
/// into subsequent commands.
/// </summary>
public sealed record CliLibrarySynchronizationAddedNode(
	string NodeId,
	string RelativePath,
	string Name,
	IReadOnlyList<CliLibrarySynchronizationPatchGroup> PatchGroups,
	string? ContentFingerprint);

public sealed record CliLibrarySynchronizationChangedNode(
	string NodeId,
	string Name,
	string RelativePath,
	IReadOnlyList<CliLibrarySynchronizationPatchGroup> BeforePatchGroups,
	IReadOnlyList<CliLibrarySynchronizationPatchGroup> AfterPatchGroups,
	string? BeforeContentFingerprint,
	string? AfterContentFingerprint);

public sealed record CliLibrarySynchronizationMissingNode(string NodeId, string Name, string RelativePath);

public sealed record CliLibrarySynchronizationDiagnostic(
	string Code,
	string Severity,
	bool Blocking,
	string Message,
	string? Path = null,
	string? NodeId = null,
	string? Details = null);

internal sealed record CliLibrarySynchronizationBuildResult(
	CliLibrarySynchronizationPlanDocument Document,
	ModLibrarySynchronizationResult Synchronization,
	StoragePaths Paths);

internal sealed record CliLibrarySynchronizationValidationResult(
	CliLibrarySynchronizationPlanDocument Document,
	CliLibrarySynchronizationBuildResult? Current,
	IReadOnlyList<CliLibrarySynchronizationDiagnostic> Diagnostics)
{
	public bool IsValid => Current is not null && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

internal static class CliLibrarySynchronizationPlanArtifacts
{
	public static async ValueTask<CliLibrarySynchronizationBuildResult> BuildAsync(
		CliWorkspace workspace,
		IReadOnlyDictionary<string, ModNodeId>? plannedAddedNodeIdsByRelativePath = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot))
			throw new CliUserException("LibrarySyncManagerRootRequired", "library sync 需要 --manager-root，以读取该管理器库的待删除排除项。 ");
		if (string.IsNullOrWhiteSpace(workspace.LibraryRoot))
			throw new CliUserException("LibraryNotConfigured", "library sync 需要明确的 Mod 库。 ");
		if (!Directory.Exists(workspace.LibraryRoot))
			throw new CliUserException("LibraryDirectoryNotFound", $"Mod 库目录不存在：{workspace.LibraryRoot}");

		var paths = new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot);
		var snapshot = await LoadSnapshotReadOnlyAsync(paths, cancellationToken).ConfigureAwait(false);
		try
		{
			var manager = CoreServices.CreateModLibraryManager(paths);
			var ignoredBefore = await manager.GetPendingStoredDeletionSynchronizationExclusionsAsync(cancellationToken).ConfigureAwait(false);
			var before = await ComputeInputSnapshotAsync(paths, ignoredBefore, cancellationToken).ConfigureAwait(false);
			var synchronizer = CoreServices.CreateModLibrarySynchronizer();
			var inspection = await synchronizer
				.InspectAsync(snapshot, paths.ModsDirectory, ignoredBefore, cancellationToken)
				.ConfigureAwait(false);
			var plannedIds = ResolvePlannedAddedNodeIds(snapshot, inspection, plannedAddedNodeIdsByRelativePath);
			var synchronization = synchronizer.ApplyInspection(snapshot, inspection, plannedIds);
			var ignoredAfter = await manager.GetPendingStoredDeletionSynchronizationExclusionsAsync(cancellationToken).ConfigureAwait(false);
			var after = await ComputeInputSnapshotAsync(paths, ignoredAfter, cancellationToken).ConfigureAwait(false);

			var diagnostics = new List<CliLibrarySynchronizationDiagnostic>();
			if (!InputSnapshotsEqual(before, after))
			{
				diagnostics.Add(new CliLibrarySynchronizationDiagnostic(
					"LibrarySyncInputChangedDuringInspection",
					"error",
					true,
					"library.json、待删除排除项或顶层 Patch 文件在库对账扫描期间发生变化；请等待外部文件操作完成后重新生成计划。",
					paths.ModsDirectory));
			}

			var added = synchronization.AddedNodeIds
				.Select(id => synchronization.Snapshot.Nodes[id])
				.Select(CreateAddedNode)
				.OrderBy(node => node.RelativePath, StringComparer.OrdinalIgnoreCase)
				.ToArray();
			var changed = synchronization.ChangedNodeIds
				.Select(id => CreateChangedNode(snapshot.Nodes[id], synchronization.Snapshot.Nodes[id]))
				.OrderBy(node => node.RelativePath, StringComparer.OrdinalIgnoreCase)
				.ToArray();
			var missing = synchronization.MissingNodeIds
				.Select(id => snapshot.Nodes[id])
				.Select(node => new CliLibrarySynchronizationMissingNode(CliProjections.FormatId(node.Id), node.Metadata.Name, node.RelativePath))
				.OrderBy(node => node.RelativePath, StringComparer.OrdinalIgnoreCase)
				.ToArray();

			if (added.Length != 0)
			{
				diagnostics.Add(new CliLibrarySynchronizationDiagnostic(
					"LibrarySyncAddsUnstructuredNodes",
					"warning",
					false,
					$"将登记 {added.Length} 个外部顶层 Patch 目录为普通 Mod。不会解析 manifest、Option 树、图像或备注；需要完整树结构的包请使用 import。"));
			}
			var emptied = changed.Where(node => node.BeforePatchGroups.Count != 0 && node.AfterPatchGroups.Count == 0).ToArray();
			if (emptied.Length != 0)
			{
				diagnostics.Add(new CliLibrarySynchronizationDiagnostic(
					"LibrarySyncPatchContentMissing",
					"warning",
					false,
					$"检测到 {emptied.Length} 个已登记 Mod 的目录仍存在、但可用 base Patch 已消失。apply 会仅将这些节点的 Patch 组标记为空；不会删除节点、配置、Option 或文件。"));
			}
			if (missing.Length != 0)
			{
				diagnostics.Add(new CliLibrarySynchronizationDiagnostic(
					"LibrarySyncRegisteredStorageMissing",
					"error",
					true,
					$"检测到 {missing.Length} 个已登记 Mod 的物理目录缺失。请先使用 mod delete preview/delete 进行关系感知的清理；library sync 不会擅自移除其关系、配置或激活状态。"));
			}

			var request = new CliLibrarySynchronizationRequest(
				Path.GetFullPath(workspace.ManagerRoot),
				Path.GetFullPath(workspace.LibraryRoot));
			var cliInspection = new CliLibrarySynchronizationInspection(
				after,
				added,
				changed,
				missing,
				synchronization.FilesystemChanged,
				synchronization.HasSnapshotChanges,
				diagnostics);
			var document = new CliLibrarySynchronizationPlanDocument(
				CliLibrarySynchronizationPlanDocument.CurrentSchema,
				CliLibrarySynchronizationPlanDocument.CurrentVersion,
				DateTimeOffset.UtcNow,
				ComputeInputFingerprint(request, cliInspection),
				request,
				cliInspection);
			return new CliLibrarySynchronizationBuildResult(document, synchronization, paths);
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or InvalidOperationException
			or DirectoryNotFoundException
			or FileNotFoundException)
		{
			throw new CliUserException("LibrarySyncInspectionFailed", $"无法完成 Mod 库对账检查：{exception.Message}");
		}
	}

	public static async ValueTask<CliLibrarySynchronizationPlanDocument> ReadDocumentAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("LibrarySyncPlanRequired", "请通过 --plan <库对账计划.json> 提供计划文件。 ");
		var fullPath = CliOutput.NormalizeDirectoryPath(path, "LibrarySyncPlanPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("LibrarySyncPlanNotFound", $"库对账计划不存在：{fullPath}");
		try
		{
			var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
			var document = JsonSerializer.Deserialize<CliLibrarySynchronizationPlanDocument>(json, CliOutput.JsonOptions);
			if (document is null)
				throw new CliUserException("LibrarySyncPlanUnreadable", "库对账计划为空或无法解析。 ");
			if (!string.Equals(document.Schema, CliLibrarySynchronizationPlanDocument.CurrentSchema, StringComparison.Ordinal)
				|| document.Version != CliLibrarySynchronizationPlanDocument.CurrentVersion)
			{
				throw new CliUserException("LibrarySyncPlanVersionUnsupported", $"库对账计划版本不受支持。需要 {CliLibrarySynchronizationPlanDocument.CurrentSchema} v{CliLibrarySynchronizationPlanDocument.CurrentVersion}。 ");
			}
			if (string.IsNullOrWhiteSpace(document.InputFingerprint)
				|| document.Request is null
				|| string.IsNullOrWhiteSpace(document.Request.ManagerRoot)
				|| string.IsNullOrWhiteSpace(document.Request.LibraryRoot)
				|| document.Inspection is null
				|| document.Inspection.Input is null
				|| document.Inspection.Added is null
				|| document.Inspection.Changed is null
				|| document.Inspection.Missing is null
				|| document.Inspection.Diagnostics is null)
			{
				throw new CliUserException("LibrarySyncPlanUnreadable", "库对账计划缺少目标、输入指纹或检查结果。 ");
			}
			_ = ExtractPlannedAddedNodeIds(document);
			return document;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("LibrarySyncPlanUnreadable", $"无法读取库对账计划：{exception.Message}");
		}
	}

	public static async ValueTask<CliLibrarySynchronizationValidationResult> ValidateAsync(
		CliLibrarySynchronizationPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);
		var diagnostics = new List<CliLibrarySynchronizationDiagnostic>();
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot)
			|| string.IsNullOrWhiteSpace(workspace.LibraryRoot)
			|| !PathsEqual(workspace.ManagerRoot, document.Request.ManagerRoot)
			|| !PathsEqual(workspace.LibraryRoot, document.Request.LibraryRoot))
		{
			diagnostics.Add(new CliLibrarySynchronizationDiagnostic(
				"LibrarySyncTargetChanged",
				"error",
				true,
				"当前 --manager-root 或 --library 与计划固定的库目标不一致；请在同一目标上重新生成计划。"));
			return new CliLibrarySynchronizationValidationResult(document, null, diagnostics);
		}

		CliLibrarySynchronizationBuildResult? current = null;
		try
		{
			current = await BuildAsync(workspace, ExtractPlannedAddedNodeIds(document), cancellationToken).ConfigureAwait(false);
		}
		catch (CliUserException exception)
		{
			diagnostics.Add(new CliLibrarySynchronizationDiagnostic(exception.Code, "error", true, exception.Message));
			return new CliLibrarySynchronizationValidationResult(document, null, diagnostics);
		}
		if (!string.Equals(document.InputFingerprint, current.Document.InputFingerprint, StringComparison.OrdinalIgnoreCase))
		{
			diagnostics.Add(new CliLibrarySynchronizationDiagnostic(
				"LibrarySyncInputChanged",
				"error",
				true,
				"library.json、待删除排除项、目录、Patch 文件或同步目标已变化；请重新生成库对账计划并审阅。"));
		}
		diagnostics.AddRange(current.Document.Inspection.Diagnostics.Where(diagnostic => diagnostic.Blocking));
		return new CliLibrarySynchronizationValidationResult(document, current, DistinctDiagnostics(diagnostics));
	}

	private static async ValueTask<LibrarySnapshot> LoadSnapshotReadOnlyAsync(StoragePaths paths, CancellationToken cancellationToken)
	{
		var snapshot = await new JsonModLibraryStore(paths, allowLegacyMigration: false)
			.TryLoadReadOnlyAsync(cancellationToken)
			.ConfigureAwait(false);
		return snapshot ?? throw new CliUserException(
			"LibrarySnapshotUnreadable",
			$"无法安全读取 Mod 库状态文件：{paths.LibraryPath}");
	}

	private static IReadOnlyDictionary<string, ModNodeId> ResolvePlannedAddedNodeIds(
		LibrarySnapshot snapshot,
		ModLibrarySynchronizationInspection inspection,
		IReadOnlyDictionary<string, ModNodeId>? supplied)
	{
		var knownPaths = snapshot.Nodes.Values
			.Select(node => NormalizeRelativePath(node.RelativePath))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var additions = inspection.Directories
			.Select(directory => NormalizeRelativePath(directory.RelativePath))
			.Where(path => !knownPaths.Contains(path))
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var normalized = new Dictionary<string, ModNodeId>(StringComparer.OrdinalIgnoreCase);
		if (supplied is not null)
		{
			foreach (var (rawPath, id) in supplied)
			{
				var path = NormalizeRelativePath(rawPath);
				if (string.IsNullOrWhiteSpace(path) || !normalized.TryAdd(path, id))
					throw new CliUserException("LibrarySyncAddedNodePlanInvalid", "计划中的新增 Mod 路径为空或重复。 ");
			}
		}
		if (normalized.Keys.Except(additions, StringComparer.OrdinalIgnoreCase).Any()
			|| additions.Any(path => !normalized.ContainsKey(path)))
		{
			if (supplied is not null)
				throw new CliUserException("LibrarySyncAddedNodePlanStale", "计划中的新增 Mod 路径与当前扫描结果不一致；请重新生成计划。 ");
			foreach (var path in additions) normalized[path] = ModNodeId.New();
		}
		if (normalized.Values.Distinct().Count() != normalized.Count)
			throw new CliUserException("LibrarySyncAddedNodePlanInvalid", "计划中的新增 Mod ID 不能重复。 ");
		if (normalized.Values.Any(snapshot.Nodes.ContainsKey))
			throw new CliUserException("LibrarySyncAddedNodePlanInvalid", "计划中的新增 Mod ID 已被当前 Mod 库使用。 ");
		return normalized;
	}

	private static IReadOnlyDictionary<string, ModNodeId> ExtractPlannedAddedNodeIds(CliLibrarySynchronizationPlanDocument document)
	{
		var result = new Dictionary<string, ModNodeId>(StringComparer.OrdinalIgnoreCase);
		foreach (var addition in document.Inspection.Added)
		{
			if (!Guid.TryParse(addition.NodeId, out var rawId))
				throw new CliUserException("LibrarySyncAddedNodePlanInvalid", $"计划中的新增 Mod ID 无效：{addition.NodeId}");
			var path = NormalizeRelativePath(addition.RelativePath);
			if (string.IsNullOrWhiteSpace(path) || !result.TryAdd(path, new ModNodeId(rawId)))
				throw new CliUserException("LibrarySyncAddedNodePlanInvalid", "计划中的新增 Mod 路径为空或重复。 ");
		}
		if (result.Values.Distinct().Count() != result.Count)
			throw new CliUserException("LibrarySyncAddedNodePlanInvalid", "计划中的新增 Mod ID 不能重复。 ");
		return result;
	}

	private static async ValueTask<CliLibrarySynchronizationInputSnapshot> ComputeInputSnapshotAsync(
		StoragePaths paths,
		IReadOnlySet<string> ignoredRelativePaths,
		CancellationToken cancellationToken)
	{
		var libraryJsonSha256 = await ComputeFileSha256Async(paths.LibraryPath, cancellationToken).ConfigureAwait(false);
		var pendingDeletionSha256 = File.Exists(paths.PendingDeletionPath)
			? await ComputeFileSha256Async(paths.PendingDeletionPath, cancellationToken).ConfigureAwait(false)
			: "missing";
		var normalizedIgnored = ignoredRelativePaths
			.Select(NormalizeRelativePath)
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var ignored = normalizedIgnored.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var parser = CoreServices.CreatePatchFileNameParser();
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		var patchFileCount = 0;
		long patchByteCount = 0;
		foreach (var directory in Directory.EnumerateDirectories(paths.ModsDirectory, "*", SearchOption.TopDirectoryOnly)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relativePath = NormalizeRelativePath(Path.GetRelativePath(paths.ModsDirectory, directory));
			if (string.Equals(relativePath, ".hd2modmanager-trash", StringComparison.OrdinalIgnoreCase)
				|| ignored.Contains(relativePath))
				continue;
			AppendField(hash, "directory");
			AppendField(hash, relativePath);
			foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
				.Where(path => parser.TryParse(Path.GetFileName(path), out _))
				.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var info = new FileInfo(path);
				AppendField(hash, "patch-file");
				AppendField(hash, Path.GetFileName(path));
				AppendField(hash, info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
				await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.SequentialScan);
				hash.AppendData(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
				patchFileCount++;
				patchByteCount = checked(patchByteCount + info.Length);
			}
		}
		return new CliLibrarySynchronizationInputSnapshot(
			libraryJsonSha256,
			pendingDeletionSha256,
			Convert.ToHexString(hash.GetHashAndReset()),
			patchFileCount,
			patchByteCount,
			normalizedIgnored);
	}

	private static async ValueTask<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.SequentialScan);
		return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
	}

	private static CliLibrarySynchronizationAddedNode CreateAddedNode(ModNode node)
		=> new(CliProjections.FormatId(node.Id), node.RelativePath, node.Metadata.Name, ProjectPatchGroups(node.PatchGroups), node.ContentFingerprint);

	private static CliLibrarySynchronizationChangedNode CreateChangedNode(ModNode before, ModNode after)
		=> new(
			CliProjections.FormatId(before.Id),
			before.Metadata.Name,
			before.RelativePath,
			ProjectPatchGroups(before.PatchGroups),
			ProjectPatchGroups(after.PatchGroups),
			before.ContentFingerprint,
			after.ContentFingerprint);

	private static IReadOnlyList<CliLibrarySynchronizationPatchGroup> ProjectPatchGroups(IReadOnlyList<PatchGroupKey> groups)
		=> groups.Select(group => new CliLibrarySynchronizationPatchGroup(group.ArchiveHex16, group.PatchIndex)).ToArray();

	private static string ComputeInputFingerprint(
		CliLibrarySynchronizationRequest request,
		CliLibrarySynchronizationInspection inspection)
	{
		var payload = CliOutput.ToJson(new
		{
			request.ManagerRoot,
			request.LibraryRoot,
			input = inspection.Input,
			added = inspection.Added.Select(node => new
			{
				node.NodeId,
				node.RelativePath,
				node.Name,
				node.ContentFingerprint,
				patchGroups = node.PatchGroups.Select(group => new { group.ArchiveHex16, group.PatchIndex }).ToArray(),
			}).OrderBy(node => node.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
			changed = inspection.Changed.Select(node => new
			{
				node.NodeId,
				node.RelativePath,
				node.BeforeContentFingerprint,
				node.AfterContentFingerprint,
				before = node.BeforePatchGroups.Select(group => new { group.ArchiveHex16, group.PatchIndex }).ToArray(),
				after = node.AfterPatchGroups.Select(group => new { group.ArchiveHex16, group.PatchIndex }).ToArray(),
			}).OrderBy(node => node.NodeId, StringComparer.Ordinal).ToArray(),
			missing = inspection.Missing.Select(node => new { node.NodeId, node.RelativePath }).OrderBy(node => node.NodeId, StringComparer.Ordinal).ToArray(),
		});
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
	}

	private static bool InputSnapshotsEqual(CliLibrarySynchronizationInputSnapshot left, CliLibrarySynchronizationInputSnapshot right)
		=> string.Equals(left.LibraryJsonSha256, right.LibraryJsonSha256, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(left.PendingDeletionSha256, right.PendingDeletionSha256, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(left.FilesystemFingerprint, right.FilesystemFingerprint, StringComparison.OrdinalIgnoreCase)
			&& left.PatchFileCount == right.PatchFileCount
			&& left.PatchByteCount == right.PatchByteCount
			&& left.IgnoredRelativePaths.SequenceEqual(right.IgnoredRelativePaths, StringComparer.OrdinalIgnoreCase);

	private static IReadOnlyList<CliLibrarySynchronizationDiagnostic> DistinctDiagnostics(
		IEnumerable<CliLibrarySynchronizationDiagnostic> diagnostics)
		=> diagnostics
			.GroupBy(diagnostic => new
			{
				diagnostic.Code,
				diagnostic.Severity,
				diagnostic.Message,
				diagnostic.Path,
				diagnostic.NodeId,
			})
			.Select(group => group.First())
			.ToArray();

	private static bool PathsEqual(string left, string right)
		=> string.Equals(
			Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			StringComparison.OrdinalIgnoreCase);

	private static string NormalizeRelativePath(string path)
		=> string.IsNullOrWhiteSpace(path) || path == "."
			? string.Empty
			: path.Replace(Path.DirectorySeparatorChar, '/').Trim('/');

	private static void AppendField(IncrementalHash hash, string value)
	{
		hash.AppendData(Encoding.UTF8.GetBytes(value));
		hash.AppendData([0]);
	}
}
