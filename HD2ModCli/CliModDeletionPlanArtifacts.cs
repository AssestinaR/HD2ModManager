using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// A persisted, reviewed deletion request. The embedded Core plan is for
/// human/Agent review only; validate and apply always rebuild it from the
/// frozen node IDs and current library state.
/// </summary>
public sealed record CliModDeletionPlanDocument(
	string Schema,
	int Version,
	DateTimeOffset CreatedUtc,
	string InputFingerprint,
	CliModDeletionRequest Request,
	CliModDeletionTarget Target,
	SafeModDeletionPlan Deletion,
	CliModDeletionInputState Inputs,
	IReadOnlyList<CliModDeletionDiagnostic> Diagnostics)
{
	public const string CurrentSchema = "hd2mod.mod-deletion-plan";
	public const int CurrentVersion = 1;

	public bool IsReady => !Deletion.IsBlocked && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

public sealed record CliModDeletionRequest(
	IReadOnlyList<string> NodeIds,
	SafeModDeletionScope Scope);

public sealed record CliModDeletionTarget(
	string ManagerRoot,
	string LibraryRoot);

public sealed record CliModDeletionInputState(
	CliModDeletionFileSnapshot LibrarySnapshot,
	CliModDeletionFileSnapshot Profiles,
	CliModDeletionFileSnapshot OptionActivations,
	CliModDeletionFileSnapshot DecorationActivations,
	IReadOnlyList<CliModDeletionStorageSnapshot> Storage);

public sealed record CliModDeletionFileSnapshot(
	string FileName,
	bool Exists,
	long Length,
	string? Sha256);

public sealed record CliModDeletionStorageSnapshot(
	string RelativePath,
	bool Exists,
	bool IsDirectory,
	int FileCount,
	long TotalBytes,
	string? Sha256);

public sealed record CliModDeletionDiagnostic(
	string Code,
	string Severity,
	bool Blocking,
	string Message,
	string? Path = null,
	string? Details = null);

internal sealed record CliModDeletionBuildResult(
	CliModDeletionPlanDocument Document,
	CliLibraryState State);

internal sealed record CliModDeletionValidationResult(
	CliModDeletionPlanDocument Document,
	CliModDeletionBuildResult? Current,
	IReadOnlyList<CliModDeletionDiagnostic> Diagnostics)
{
	public bool IsValid => Current is not null && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

internal static class CliModDeletionPlanArtifacts
{
	public static async ValueTask<CliModDeletionBuildResult> BuildAsync(
		CliWorkspace workspace,
		IReadOnlyList<string> nodeSelectors,
		SafeModDeletionScope scope,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(nodeSelectors);
		if (nodeSelectors.Count == 0)
			throw new CliUserException("ModDeletionSelectionRequired", "mod delete plan 至少需要一个 Mod GUID 或唯一 GUID 前缀。 ");
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot))
			throw new CliUserException("ModDeletionManagerRootRequired", "mod delete plan 需要 --manager-root <管理器目录>，以固定可写入的 Mod 库目标。 ");
		if (string.IsNullOrWhiteSpace(workspace.LibraryRoot))
			throw new CliUserException("LibraryNotConfigured", "mod delete plan 需要明确的 Mod 库。 ");

		var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
		var requestedIds = nodeSelectors
			.Select(selector => CliProjections.ResolveMod(state.Snapshot.Nodes, selector).Id)
			.Distinct()
			.ToArray();
		var frozenSelectors = requestedIds.Select(CliProjections.FormatId).ToArray();
		var plan = CliLibraryMutationOperations.CreateDeletionPlan(state, frozenSelectors, scope);
		var paths = new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot);
		var diagnostics = plan.Blockers
			.Select(blocker => new CliModDeletionDiagnostic(
				blocker.Code,
				blocker.Severity.ToString().ToLowerInvariant(),
				blocker.Severity == SafeModDeletionBlockerSeverity.Blocking,
				blocker.Message,
				blocker.RelativePaths.FirstOrDefault(),
				blocker.RelatedNodeIds.Count == 0 ? null : string.Join(", ", blocker.RelatedNodeIds.Select(CliProjections.FormatId))))
			.ToList();
		var storage = await CaptureStorageAsync(paths, plan.PhysicalRelativePaths, diagnostics, cancellationToken).ConfigureAwait(false);
		var inputs = new CliModDeletionInputState(
			await CaptureFileAsync(paths.LibraryPath, cancellationToken).ConfigureAwait(false),
			await CaptureFileAsync(paths.ProfilesPath, cancellationToken).ConfigureAwait(false),
			await CaptureFileAsync(Path.Combine(paths.ModsDirectory, "option-activations.json"), cancellationToken).ConfigureAwait(false),
			await CaptureFileAsync(paths.DecorationActivationsPath, cancellationToken).ConfigureAwait(false),
			storage);
		var request = new CliModDeletionRequest(frozenSelectors, scope);
		var target = new CliModDeletionTarget(Path.GetFullPath(workspace.ManagerRoot), Path.GetFullPath(workspace.LibraryRoot));
		var document = new CliModDeletionPlanDocument(
			CliModDeletionPlanDocument.CurrentSchema,
			CliModDeletionPlanDocument.CurrentVersion,
			DateTimeOffset.UtcNow,
			ComputeInputFingerprint(request, target, plan, inputs),
			request,
			target,
			plan,
			inputs,
			DistinctDiagnostics(diagnostics));
		return new CliModDeletionBuildResult(document, state);
	}

	public static async ValueTask<CliModDeletionPlanDocument> ReadDocumentAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("ModDeletionPlanRequired", "请通过 --plan <删除计划.json> 提供已导出的删除计划。 ");
		var fullPath = CliOutput.NormalizeDirectoryPath(path, "ModDeletionPlanPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("ModDeletionPlanNotFound", $"删除计划不存在：{fullPath}");
		try
		{
			var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
			var document = JsonSerializer.Deserialize<CliModDeletionPlanDocument>(json, CliOutput.JsonOptions);
			if (document is null)
				throw new CliUserException("ModDeletionPlanUnreadable", "删除计划为空或无法解析。 ");
			if (!string.Equals(document.Schema, CliModDeletionPlanDocument.CurrentSchema, StringComparison.Ordinal)
				|| document.Version != CliModDeletionPlanDocument.CurrentVersion)
			{
				throw new CliUserException(
					"ModDeletionPlanVersionUnsupported",
					$"删除计划版本不受支持。需要 {CliModDeletionPlanDocument.CurrentSchema} v{CliModDeletionPlanDocument.CurrentVersion}。 ");
			}
			if (string.IsNullOrWhiteSpace(document.InputFingerprint)
				|| document.Request?.NodeIds is null
				|| document.Request.NodeIds.Count == 0
				|| document.Target is null
				|| string.IsNullOrWhiteSpace(document.Target.ManagerRoot)
				|| string.IsNullOrWhiteSpace(document.Target.LibraryRoot)
				|| document.Deletion is null
				|| document.Inputs is null
				|| document.Diagnostics is null)
			{
				throw new CliUserException("ModDeletionPlanUnreadable", "删除计划缺少目标、请求、输入指纹或删除预览。 ");
			}
			if (!Enum.IsDefined(document.Request.Scope))
				throw new CliUserException("ModDeletionPlanUnreadable", "删除计划包含不支持的删除范围。 ");
			foreach (var nodeId in document.Request.NodeIds)
				if (!Guid.TryParse(nodeId, out _))
					throw new CliUserException("ModDeletionPlanUnreadable", $"删除计划包含无效 Mod ID：{nodeId}");
			return document;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("ModDeletionPlanUnreadable", $"无法读取删除计划：{exception.Message}");
		}
	}

	public static async ValueTask<CliModDeletionValidationResult> ValidateAsync(
		CliModDeletionPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);
		var diagnostics = new List<CliModDeletionDiagnostic>();
		CliModDeletionBuildResult? current = null;
		try
		{
			current = await BuildAsync(workspace, document.Request.NodeIds, document.Request.Scope, cancellationToken).ConfigureAwait(false);
			if (!PathsEqual(document.Target.ManagerRoot, current.Document.Target.ManagerRoot)
				|| !PathsEqual(document.Target.LibraryRoot, current.Document.Target.LibraryRoot))
			{
				diagnostics.Add(new CliModDeletionDiagnostic(
					"ModDeletionTargetChanged", "error", true,
					"当前管理器或 Mod 库目标与删除计划不同；请重新生成计划。"));
			}
			if (!string.Equals(document.InputFingerprint, current.Document.InputFingerprint, StringComparison.OrdinalIgnoreCase))
			{
				diagnostics.Add(new CliModDeletionDiagnostic(
					"ModDeletionInputChanged", "error", true,
					"库记录、Profile、激活状态、待删除存储内容或删除关系已变化；请重新生成计划并重新确认。"));
			}
			diagnostics.AddRange(current.Document.Diagnostics);
		}
		catch (CliUserException exception)
		{
			diagnostics.Add(new CliModDeletionDiagnostic(exception.Code, "error", true, exception.Message));
		}
		return new CliModDeletionValidationResult(document, current, DistinctDiagnostics(diagnostics));
	}

	private static async ValueTask<IReadOnlyList<CliModDeletionStorageSnapshot>> CaptureStorageAsync(
		StoragePaths paths,
		IReadOnlyList<SafeModDeletionPhysicalPath> physicalPaths,
		ICollection<CliModDeletionDiagnostic> diagnostics,
		CancellationToken cancellationToken)
	{
		var snapshots = new List<CliModDeletionStorageSnapshot>();
		foreach (var physical in physicalPaths.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!physical.IsSafeRelativePath)
			{
				snapshots.Add(new CliModDeletionStorageSnapshot(physical.RelativePath, false, false, 0, 0, null));
				continue;
			}
			var absolute = Path.GetFullPath(Path.Combine(paths.ModsDirectory, physical.RelativePath));
			if (!IsWithin(paths.ModsDirectory, absolute))
			{
				diagnostics.Add(new CliModDeletionDiagnostic(
					"DeletionStorageOutsideLibrary", "error", true,
					"删除计划的物理路径逃逸出了 Mod 库；已拒绝生成可执行计划。", physical.RelativePath));
				snapshots.Add(new CliModDeletionStorageSnapshot(physical.RelativePath, false, false, 0, 0, null));
				continue;
			}
			if (!Directory.Exists(absolute))
			{
				diagnostics.Add(new CliModDeletionDiagnostic(
					"DeletionStorageMissing", "error", true,
					"待删除 Mod 的存储目录不存在或不是目录；为避免库记录与磁盘内容脱节，已拒绝执行。", absolute));
				snapshots.Add(new CliModDeletionStorageSnapshot(physical.RelativePath, false, false, 0, 0, null));
				continue;
			}
			if ((File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0)
			{
				diagnostics.Add(new CliModDeletionDiagnostic(
					"DeletionStorageReparsePoint", "error", true,
					"待删除 Mod 的根目录是重解析点/链接；为避免越过 Mod 库边界，CLI 已拒绝执行。", absolute));
				snapshots.Add(new CliModDeletionStorageSnapshot(physical.RelativePath, true, true, 0, 0, null));
				continue;
			}
			snapshots.Add(await CaptureDirectoryAsync(physical.RelativePath, absolute, cancellationToken).ConfigureAwait(false));
		}
		return snapshots;
	}

	private static async ValueTask<CliModDeletionStorageSnapshot> CaptureDirectoryAsync(
		string relativePath,
		string directory,
		CancellationToken cancellationToken)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		var fileCount = 0;
		long totalBytes = 0;
		foreach (var file in EnumerateFiles(directory, cancellationToken))
		{
			var info = new FileInfo(file);
			var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
			hash.AppendData(Encoding.UTF8.GetBytes(relative));
			hash.AppendData([0]);
			await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
			var content = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
			hash.AppendData(content);
			fileCount++;
			totalBytes = checked(totalBytes + info.Length);
		}
		return new CliModDeletionStorageSnapshot(relativePath, true, true, fileCount, totalBytes, Convert.ToHexString(hash.GetHashAndReset()));
	}

	private static async ValueTask<CliModDeletionFileSnapshot> CaptureFileAsync(string path, CancellationToken cancellationToken)
	{
		var fileName = Path.GetFileName(path);
		if (!File.Exists(path)) return new CliModDeletionFileSnapshot(fileName, false, 0, null);
		var info = new FileInfo(path);
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
		var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
		return new CliModDeletionFileSnapshot(fileName, true, info.Length, Convert.ToHexString(hash));
	}

	private static IEnumerable<string> EnumerateFiles(string root, CancellationToken cancellationToken)
	{
		var pending = new Stack<string>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var directory = pending.Pop();
			foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
				yield return file;
			foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
				.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
			{
				if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
					pending.Push(child);
			}
		}
	}

	private static string ComputeInputFingerprint(
		CliModDeletionRequest request,
		CliModDeletionTarget target,
		SafeModDeletionPlan deletion,
		CliModDeletionInputState inputs)
	{
		var canonical = CliOutput.ToJson(new { request, target, deletion, inputs });
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
	}

	private static bool PathsEqual(string left, string right)
	{
		try
		{
			return string.Equals(
				Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}

	private static bool IsWithin(string root, string candidate)
	{
		var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase)
			|| normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
			|| normalizedCandidate.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}

	private static IReadOnlyList<CliModDeletionDiagnostic> DistinctDiagnostics(IEnumerable<CliModDeletionDiagnostic> diagnostics)
		=> diagnostics
			.GroupBy(item => $"{item.Code}\u001f{item.Path}\u001f{item.Message}", StringComparer.Ordinal)
			.Select(group => group.OrderByDescending(item => item.Blocking).First())
			.OrderByDescending(item => item.Blocking)
			.ThenBy(item => item.Code, StringComparer.Ordinal)
			.ToArray();
}
