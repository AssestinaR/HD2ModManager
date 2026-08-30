using System.Security.Cryptography;
using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Purpose: Strictly proves ownership of top-level GameData patch files without writing, deleting, or creating anything.
public sealed class DeploymentOwnershipInspector : IDeploymentOwnershipInspector
{
	private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
		? StringComparer.OrdinalIgnoreCase
		: StringComparer.Ordinal;

	private readonly IActivationStateStore _activationStateStore;
	private readonly IPatchFileNameParser _fileNameParser;

	public DeploymentOwnershipInspector()
		: this(new JsonActivationStateStore(), new PatchFileNameParser())
	{
	}

	public DeploymentOwnershipInspector(
		IActivationStateStore activationStateStore,
		IPatchFileNameParser fileNameParser)
	{
		_activationStateStore = activationStateStore ?? throw new ArgumentNullException(nameof(activationStateStore));
		_fileNameParser = fileNameParser ?? throw new ArgumentNullException(nameof(fileNameParser));
	}

	public async ValueTask<DeploymentOwnershipInspection> InspectAsync(
		string? gameDataDirectory,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var issues = new List<CoreIssue>();
		if (!TryNormalizeGameDataDirectory(gameDataDirectory, issues, out var normalizedGameDataDirectory))
			return Blocked(normalizedGameDataDirectory, null, [], issues);

		if (File.Exists(normalizedGameDataDirectory))
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"GameDataTargetIsFile",
				"GameData 部署目标是文件，不是目录。",
				normalizedGameDataDirectory));
			return Blocked(normalizedGameDataDirectory, null, [], issues);
		}

		if (!Directory.Exists(normalizedGameDataDirectory))
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"GameDataDirectoryMissing",
				"GameData 部署目录不存在。",
				normalizedGameDataDirectory));
			return Blocked(normalizedGameDataDirectory, null, [], issues);
		}

		if (!TryEnsureNotReparsePoint(
				normalizedGameDataDirectory,
				"GameDataDirectoryReparsePoint",
				"GameData 部署目录不能是符号链接、联接或其他 reparse point。",
				issues,
				cancellationToken))
		{
			return Blocked(normalizedGameDataDirectory, null, [], issues);
		}

		var scan = ScanTopLevelPatches(normalizedGameDataDirectory, issues, cancellationToken);
		var statePath = Path.Combine(normalizedGameDataDirectory, JsonActivationStateStore.StateFileName);
		if (!TryGetOptionalFileAttributes(statePath, out var stateAttributes, out var stateAttributesException))
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ActivationStateAttributesUnreadable",
				"无法安全读取 activation-state.json 的文件系统属性。",
				statePath,
				ExceptionMessage: stateAttributesException!.ToString()));
			return Blocked(normalizedGameDataDirectory, null, scan.Patches, issues);
		}

		var statePathExists = stateAttributes is not null;
		if (!statePathExists)
		{
			if (scan.Patches.Count == 0 && !HasErrors(issues))
			{
				return new DeploymentOwnershipInspection(
					DeploymentOwnershipStatus.CleanBootstrap,
					normalizedGameDataDirectory,
					null,
					scan.Patches,
					[],
					issues);
			}

			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ActivationStateMissingForPatches",
				"GameData 中存在可解析 Patch，但没有 activation-state.json，不能安全接管或删除这些文件。",
				normalizedGameDataDirectory));
			return Blocked(normalizedGameDataDirectory, null, scan.Patches, issues);
		}

		if ((stateAttributes!.Value & FileAttributes.Directory) != 0)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ActivationStatePathIsDirectory",
				"activation-state.json 路径被目录占用，无法安全读取。",
				statePath));
			return Blocked(normalizedGameDataDirectory, null, scan.Patches, issues);
		}

		if ((stateAttributes.Value & FileAttributes.ReparsePoint) != 0)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ActivationStateReparsePoint",
				"activation-state.json 不能是符号链接、联接或其他 reparse point。",
				statePath));
			return Blocked(normalizedGameDataDirectory, null, scan.Patches, issues);
		}

		ActivationState? state;
		try
		{
			state = await _activationStateStore
				.TryLoadAsync(normalizedGameDataDirectory, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (InvalidDataException exception)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ActivationStateVersionUnsupported",
				"activation-state.json 的版本不受支持。",
				statePath,
				ExceptionMessage: exception.ToString()));
			return Blocked(normalizedGameDataDirectory, null, scan.Patches, issues);
		}
		catch (JsonException exception)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ActivationStateInvalid",
				"activation-state.json 无法解析。",
				statePath,
				ExceptionMessage: exception.ToString()));
			return Blocked(normalizedGameDataDirectory, null, scan.Patches, issues);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ActivationStateUnreadable",
				"activation-state.json 无法安全读取。",
				statePath,
				ExceptionMessage: exception.ToString()));
			return Blocked(normalizedGameDataDirectory, null, scan.Patches, issues);
		}

		if (state is null)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ActivationStateDisappeared",
				"读取 activation-state.json 时文件消失或状态发生变化。",
				statePath));
			return Blocked(normalizedGameDataDirectory, null, scan.Patches, issues);
		}

		if (state.Version != JsonActivationStateStore.CurrentVersion)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ActivationStateVersionUnsupported",
				$"activation-state.json 版本 {state.Version} 不受支持。",
				statePath));
		}

		if (!state.Completed)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ActivationStateIncomplete",
				"activation-state.json 标记为未完成部署，不能安全接管或删除 Patch。",
				statePath));
		}

		if (state.Files is null)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ActivationStateFilesMissing",
				"activation-state.json 缺少部署文件清单。",
				statePath));
			return Blocked(normalizedGameDataDirectory, state, scan.Patches, issues);
		}

		var actualByPath = scan.Patches.ToDictionary(patch => patch.Path, PathComparer);
		var declaredTargetPaths = new HashSet<string>(PathComparer);
		var declaredIdentityKeys = new HashSet<string>(StringComparer.Ordinal);

		foreach (var entry in state.Files)
		{
			cancellationToken.ThrowIfCancellationRequested();
			await InspectStateEntryAsync(
				entry,
				normalizedGameDataDirectory,
				actualByPath,
				scan.UnsafePaths,
				declaredTargetPaths,
				declaredIdentityKeys,
				issues,
				cancellationToken);
		}

		foreach (var patch in scan.Patches)
		{
			if (declaredTargetPaths.Contains(patch.Path)) continue;
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"UnmanagedPatchDetected",
				"GameData 中存在未由 activation-state.json 登记的 Patch，不能安全接管或删除。",
				patch.Path));
		}

		foreach (var declaredTargetPath in declaredTargetPaths)
		{
			if (actualByPath.ContainsKey(declaredTargetPath)) continue;
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ManagedPatchMissing",
				"activation-state.json 登记的 Patch 在 GameData 中不存在。",
				declaredTargetPath));
		}

		if (HasErrors(issues))
			return Blocked(normalizedGameDataDirectory, state, scan.Patches, issues);

		return new DeploymentOwnershipInspection(
			DeploymentOwnershipStatus.Managed,
			normalizedGameDataDirectory,
			state,
			scan.Patches,
			state.Files.ToArray(),
			issues);
	}

	private PatchScanResult ScanTopLevelPatches(
		string gameDataDirectory,
		List<CoreIssue> issues,
		CancellationToken cancellationToken)
	{
		var patches = new List<DeploymentOwnershipPatch>();
		var unreadableAttributePaths = new HashSet<string>(PathComparer);
		try
		{
			foreach (var entryPath in Directory.EnumerateFileSystemEntries(gameDataDirectory, "*", SearchOption.TopDirectoryOnly))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var fileName = Path.GetFileName(entryPath);
				if (!_fileNameParser.TryParse(fileName, out var identity) || identity is null) continue;

				var fullPath = Path.GetFullPath(entryPath);
				if (Directory.Exists(fullPath))
				{
					issues.Add(new CoreIssue(
						CoreIssueSeverity.Error,
						"PatchTargetIsDirectory",
						"可解析 Patch 文件名被目录占用，不能安全部署。",
						fullPath));
					continue;
				}

				if (!TryGetOptionalFileAttributes(fullPath, out var attributes, out var attributesException))
				{
					issues.Add(new CoreIssue(
						CoreIssueSeverity.Error,
						"ManagedPatchAttributesUnreadable",
						"无法安全读取可解析 Patch 的文件系统属性。",
						fullPath,
						ExceptionMessage: attributesException?.ToString()));
					unreadableAttributePaths.Add(fullPath);
				}
				patches.Add(new DeploymentOwnershipPatch(
					fullPath,
					identity.ArchiveHex16,
					identity.PatchIndex,
					identity.SidecarKind,
					attributes is { } value && (value & FileAttributes.ReparsePoint) != 0));
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"GameDataPatchEnumerationFailed",
				"无法安全枚举 GameData 顶层 Patch。",
				gameDataDirectory,
				ExceptionMessage: exception.ToString()));
		}

		foreach (var duplicate in patches
			.GroupBy(ToIdentityKey, StringComparer.Ordinal)
			.Where(group => group.Skip(1).Any()))
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"DuplicateActualPatchIdentity",
				"GameData 中存在具有相同 Patch 身份的多个文件。",
				duplicate.First().Path));
		}

		return new PatchScanResult(
			patches
				.OrderBy(patch => patch.Path, PathComparer)
				.ToArray(),
			unreadableAttributePaths);
	}

	private async ValueTask InspectStateEntryAsync(
		ActivationStateFileEntry? entry,
		string gameDataDirectory,
		IReadOnlyDictionary<string, DeploymentOwnershipPatch> actualByPath,
		IReadOnlySet<string> unsafeActualPaths,
		ISet<string> declaredTargetPaths,
		ISet<string> declaredIdentityKeys,
		List<CoreIssue> issues,
		CancellationToken cancellationToken)
	{
		if (entry is null)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ActivationStateEntryInvalid",
				"activation-state.json 包含空的部署文件条目。",
				Path.Combine(gameDataDirectory, JsonActivationStateStore.StateFileName)));
			return;
		}

		if (!TryNormalizeStateTargetPath(entry.TargetPath, gameDataDirectory, issues, out var targetPath))
			return;

		if (!declaredTargetPaths.Add(targetPath))
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"DuplicateActivationStateTarget",
				"activation-state.json 重复登记了同一个目标 Patch。",
				targetPath));
		}

		if (!Enum.IsDefined(entry.SidecarKind))
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ActivationStateTargetIdentityInvalid",
				"activation-state.json 包含未知的 Patch sidecar 类型。",
				targetPath));
			return;
		}

		var targetFileName = Path.GetFileName(targetPath);
		if (!_fileNameParser.TryParse(targetFileName, out var parsedTarget) || parsedTarget is null
			|| !string.Equals(parsedTarget.ArchiveHex16, entry.ArchiveHex16, StringComparison.OrdinalIgnoreCase)
			|| parsedTarget.PatchIndex != entry.TargetPatchIndex
			|| parsedTarget.SidecarKind != entry.SidecarKind)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ActivationStateTargetIdentityMismatch",
				"activation-state.json 的目标路径与登记的 Archive、Patch 序号或 sidecar 身份不一致。",
				targetPath));
			return;
		}

		if (!declaredIdentityKeys.Add(ToIdentityKey(parsedTarget)))
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"DuplicateActivationStatePatchIdentity",
				"activation-state.json 重复登记了同一个 Patch 身份。",
				targetPath));
		}

		if (Directory.Exists(targetPath))
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ManagedPatchIsDirectory",
				"activation-state.json 登记的 Patch 目标被目录占用。",
				targetPath));
			return;
		}

		var hasValidHash = true;
		if (string.IsNullOrWhiteSpace(entry.ContentSha256))
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ManagedPatchHashMissing",
				"activation-state.json 中的已部署 Patch 缺少内容 SHA-256。",
				targetPath,
				entry.NodeId));
			hasValidHash = false;
		}
		else if (!IsSha256(entry.ContentSha256))
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ManagedPatchHashInvalid",
				"activation-state.json 中的 Patch 内容 SHA-256 格式无效。",
				targetPath,
				entry.NodeId));
			hasValidHash = false;
		}

		if (!actualByPath.TryGetValue(targetPath, out var actualPatch))
			return;

		if (unsafeActualPaths.Contains(actualPatch.Path) || !hasValidHash)
			return;

		if (actualPatch.IsReparsePoint)
		{
			if (entry.Method != DeploymentMethod.SymbolicLink)
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Error,
					"ManagedPatchUnexpectedReparsePoint",
					"只有被明确登记为 SymbolicLink 的已管理 Patch 才允许是 reparse point。",
					targetPath,
					entry.NodeId));
				return;
			}
			if (!TryVerifySymbolicLinkTarget(actualPatch.Path, entry.SourcePath, issues, entry.NodeId))
				return;
		}

		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			var actualHash = await ComputeSha256Async(actualPatch.Path, cancellationToken).ConfigureAwait(false);
			if (!string.Equals(actualHash, entry.ContentSha256, StringComparison.OrdinalIgnoreCase))
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Error,
					"ManagedPatchHashMismatch",
					"已部署 Patch 的内容与 activation-state.json 记录的 SHA-256 不一致。",
					targetPath,
					entry.NodeId));
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ManagedPatchUnreadable",
				"无法安全读取已部署 Patch。",
				targetPath,
				entry.NodeId,
				exception.ToString()));
		}
	}

	private static async ValueTask<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 81920,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
		return Convert.ToHexString(hash).ToLowerInvariant();
	}

	private static bool TryNormalizeGameDataDirectory(
		string? gameDataDirectory,
		List<CoreIssue> issues,
		out string? normalizedGameDataDirectory)
	{
		normalizedGameDataDirectory = null;
		if (string.IsNullOrWhiteSpace(gameDataDirectory))
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"GameDataPathRequired",
				"必须指定 GameData 部署目录。"));
			return false;
		}

		try
		{
			normalizedGameDataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDataDirectory));
			return true;
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"GameDataPathInvalid",
				"GameData 部署目录路径无效。",
				gameDataDirectory,
				ExceptionMessage: exception.ToString()));
			return false;
		}
	}

	private static bool TryNormalizeStateTargetPath(
		string? targetPath,
		string gameDataDirectory,
		List<CoreIssue> issues,
		out string normalizedTargetPath)
	{
		normalizedTargetPath = string.Empty;
		if (string.IsNullOrWhiteSpace(targetPath))
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ActivationStateTargetPathRequired",
				"activation-state.json 包含没有目标路径的部署条目。",
				Path.Combine(gameDataDirectory, JsonActivationStateStore.StateFileName)));
			return false;
		}

		if (!Path.IsPathFullyQualified(targetPath))
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ActivationStateTargetPathNotAbsolute",
				"activation-state.json 中的 Patch 目标路径必须是绝对路径。",
				targetPath));
			return false;
		}

		try
		{
			normalizedTargetPath = Path.GetFullPath(targetPath);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ActivationStateTargetPathInvalid",
				"activation-state.json 中的 Patch 目标路径无效。",
				targetPath,
				ExceptionMessage: exception.ToString()));
			return false;
		}

		if (!PathComparer.Equals(Path.GetDirectoryName(normalizedTargetPath), gameDataDirectory))
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ActivationStateTargetNotTopLevel",
				"activation-state.json 中的 Patch 目标必须直接位于 GameData 顶层。",
				normalizedTargetPath));
			return false;
		}

		return true;
	}

	private static bool TryEnsureNotReparsePoint(
		string path,
		string issueCode,
		string issueMessage,
		List<CoreIssue> issues,
		CancellationToken cancellationToken)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) return true;
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, issueCode, issueMessage, path));
			return false;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				issueCode + "AttributesUnreadable",
				"无法安全读取文件系统属性。",
				path,
				ExceptionMessage: exception.ToString()));
			return false;
		}
	}

	private static bool TryVerifySymbolicLinkTarget(
		string targetPath,
		string? declaredSourcePath,
		ICollection<CoreIssue> issues,
		ModNodeId? nodeId)
	{
		if (string.IsNullOrWhiteSpace(declaredSourcePath) || !Path.IsPathFullyQualified(declaredSourcePath))
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ManagedSymbolicLinkSourceInvalid",
				"activation-state.json 中的 SymbolicLink 来源路径必须是绝对路径。",
				targetPath,
				nodeId));
			return false;
		}
		try
		{
			var resolved = File.ResolveLinkTarget(targetPath, returnFinalTarget: true);
			var expected = Path.GetFullPath(declaredSourcePath);
			if (resolved is null || !PathComparer.Equals(Path.GetFullPath(resolved.FullName), expected))
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Error,
					"ManagedSymbolicLinkTargetMismatch",
					"已管理 SymbolicLink 的实际目标与 activation-state.json 不一致。",
					targetPath,
					nodeId));
				return false;
			}
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Error,
				"ManagedSymbolicLinkUnreadable",
				"无法安全解析已管理 SymbolicLink 的目标。",
				targetPath,
				nodeId,
				exception.ToString()));
			return false;
		}
	}

	private static bool TryGetOptionalFileAttributes(
		string path,
		out FileAttributes? attributes,
		out Exception? exception)
	{
		try
		{
			attributes = File.GetAttributes(path);
			exception = null;
			return true;
		}
		catch (FileNotFoundException)
		{
			attributes = null;
			exception = null;
			return true;
		}
		catch (DirectoryNotFoundException)
		{
			attributes = null;
			exception = null;
			return true;
		}
		catch (Exception caught) when (caught is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			attributes = null;
			exception = caught;
			return false;
		}
	}

	private static bool IsSha256(string value)
		=> value.Length == 64 && value.All(Uri.IsHexDigit);

	private static string ToIdentityKey(DeploymentOwnershipPatch patch)
		=> ToIdentityKey(patch.ArchiveHex16, patch.PatchIndex, patch.SidecarKind);

	private static string ToIdentityKey(PatchFileNameInfo patch)
		=> ToIdentityKey(patch.ArchiveHex16, patch.PatchIndex, patch.SidecarKind);

	private static string ToIdentityKey(string archiveHex16, int patchIndex, PatchSidecarKind sidecarKind)
		=> $"{archiveHex16.ToLowerInvariant()}:{patchIndex}:{(int)sidecarKind}";

	private static bool HasErrors(IReadOnlyList<CoreIssue> issues)
		=> issues.Any(issue => issue.Severity == CoreIssueSeverity.Error);

	private static DeploymentOwnershipInspection Blocked(
		string? gameDataDirectory,
		ActivationState? state,
		IReadOnlyList<DeploymentOwnershipPatch> actualPatches,
		IReadOnlyList<CoreIssue> issues)
		=> new(
			DeploymentOwnershipStatus.Blocked,
			gameDataDirectory,
			state,
			actualPatches,
			[],
			issues);

	private sealed record PatchScanResult(
		IReadOnlyList<DeploymentOwnershipPatch> Patches,
		IReadOnlySet<string> UnsafePaths);
}
