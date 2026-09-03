using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Security.Cryptography;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Purpose: Preflights deployment sources, commits one complete patch set, verifies it and publishes activation state atomically.
public sealed class ApplyExecutor : IApplyExecutor
{
	private readonly IPatchStateScanner _stateScanner;
	private readonly IPatchFileNameParser _fileNameParser;
	private readonly IActivationStateStore _activationStateStore;

	public ApplyExecutor()
		: this(new PatchStateScanner(new PatchFileNameParser()), new PatchFileNameParser(), new JsonActivationStateStore())
	{
	}

	public ApplyExecutor(IPatchStateScanner stateScanner)
		: this(stateScanner, new PatchFileNameParser(), new JsonActivationStateStore())
	{
	}

	public ApplyExecutor(IPatchStateScanner stateScanner, IPatchFileNameParser fileNameParser, IActivationStateStore activationStateStore)
	{
		_stateScanner = stateScanner ?? throw new ArgumentNullException(nameof(stateScanner));
		_fileNameParser = fileNameParser ?? throw new ArgumentNullException(nameof(fileNameParser));
		_activationStateStore = activationStateStore ?? throw new ArgumentNullException(nameof(activationStateStore));
	}

	public async ValueTask<ApplyResult> ExecuteAsync(ApplyPlan plan, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(plan);
		Directory.CreateDirectory(plan.GameDataDirectory);
		var operationResults = new List<ApplyOperationResult>();
		var issues = new List<CoreIssue>(plan.Issues);
		var deployOperations = plan.Operations.Where(operation => operation.Kind == ApplyOperationKind.DeployPatch).ToList();
		var controlledPatchTargets = GetControlledPatchTargets(plan);
		var externalDeployMappings = plan.ExternalFileMappings ?? Array.Empty<ExternalFileMapping>();
		ActivationState? previousState = null;
		try
		{
			previousState = await _activationStateStore.TryLoadAsync(plan.GameDataDirectory, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ReadActivationStateFailed", exception.Message, Path.Combine(plan.GameDataDirectory, JsonActivationStateStore.StateFileName), ExceptionMessage: exception.ToString()));
			return new ApplyResult(false, operationResults, await _stateScanner.ScanAsync(plan.GameDataDirectory, false, CancellationToken.None).ConfigureAwait(false), issues);
		}
		var preflight = await PreflightAsync(plan.GameDataDirectory, deployOperations, externalDeployMappings, previousState?.ExternalFiles ?? Array.Empty<ExternalFileMapping>(), plan.UseFastLinkValidation, cancellationToken).ConfigureAwait(false);
		issues.AddRange(preflight.Issues);
		if (preflight.Issues.Any(issue => issue.Severity == CoreIssueSeverity.Error))
		{
			return new ApplyResult(false, operationResults, await _stateScanner.ScanAsync(plan.GameDataDirectory, false, CancellationToken.None).ConfigureAwait(false), issues);
		}

		// Last cancellable checkpoint. Commit must finish or deterministically clean every controlled patch.
		cancellationToken.ThrowIfCancellationRequested();
		var deployed = new List<ActivationStateFileEntry>();
		var deployedExternal = new List<ExternalFileMapping>();
		ExternalFileCommitResult? externalCommit = null;
		var commitFailed = false;
		foreach (var operation in plan.Operations.Where(operation => operation.Kind == ApplyOperationKind.DeletePatch))
		{
			var result = DeletePatch(operation);
			operationResults.Add(result);
			if (!result.Success)
			{
				commitFailed = true;
				issues.Add(ToIssue(result, operation));
			}
		}

		if (!commitFailed)
		{
			foreach (var operation in deployOperations)
			{
				var result = DeployPatch(operation, plan.DeploymentMethod);
				operationResults.Add(result);
				if (!result.Success || result.Method is null)
				{
					commitFailed = true;
					issues.Add(ToIssue(result, operation));
					continue;
				}

				var source = preflight.Sources[operation.SourcePath!];
				deployed.Add(new ActivationStateFileEntry(operation.TargetPath, operation.SourcePath!, result.Method.Value, operation.ArchiveHex16!, operation.SourcePatchIndex!.Value, operation.TargetPatchIndex!.Value, operation.SidecarKind!.Value, operation.NodeId, source.Length, source.Sha256));
			}

			if (!commitFailed)
			{
				externalCommit = await CommitExternalFilesAsync(
					plan.GameDataDirectory,
					externalDeployMappings,
					previousState?.ExternalFiles ?? Array.Empty<ExternalFileMapping>(),
					cancellationToken).ConfigureAwait(false);
				operationResults.AddRange(externalCommit.OperationResults);
				foreach (var result in externalCommit.OperationResults.Where(result => !result.Success))
					issues.Add(ToIssue(result, result.Operation));
				if (!externalCommit.Success)
					commitFailed = true;
				else
					deployedExternal.AddRange(externalCommit.DeployedMappings);
			}
		}

		if (commitFailed)
		{
			CleanupControlledFiles(plan.GameDataDirectory, controlledPatchTargets);
			if (externalCommit is not null)
				issues.AddRange(await externalCommit.RollbackAsync().ConfigureAwait(false));
			await FinalizeFailedApplyStateAsync(plan.GameDataDirectory, previousState, externalCommit, issues).ConfigureAwait(false);
			var failedReport = await _stateScanner.ScanAsync(plan.GameDataDirectory, false, CancellationToken.None).ConfigureAwait(false);
			return new ApplyResult(false, operationResults, failedReport, issues);
		}
		if (deployOperations.Count == 0 && externalDeployMappings.Count == 0)
		{
			await DeleteActivationStateBestEffortAsync(plan.GameDataDirectory).ConfigureAwait(false);
			return new ApplyResult(!issues.Any(issue => issue.Severity == CoreIssueSeverity.Error), operationResults, new PatchStateReport(plan.GameDataDirectory, DateTimeOffset.UtcNow, Array.Empty<PatchStateGroup>(), issues), issues);
		}

		var report = await _stateScanner.ScanAsync(plan.GameDataDirectory, recursive: false, CancellationToken.None).ConfigureAwait(false);
		issues.AddRange(report.Issues);
		issues.AddRange(await VerifyDeployedFilesAsync(deployed, plan.UseFastLinkValidation).ConfigureAwait(false));
		issues.AddRange(await VerifyExternalFilesAsync(deployedExternal).ConfigureAwait(false));
		if (issues.Any(issue => issue.Severity == CoreIssueSeverity.Error))
		{
			CleanupControlledFiles(plan.GameDataDirectory, controlledPatchTargets);
			if (externalCommit is not null)
				issues.AddRange(await externalCommit.RollbackAsync().ConfigureAwait(false));
			await FinalizeFailedApplyStateAsync(plan.GameDataDirectory, previousState, externalCommit, issues).ConfigureAwait(false);
			return new ApplyResult(false, operationResults, report, issues);
		}

		var statePath = Path.Combine(plan.GameDataDirectory, JsonActivationStateStore.StateFileName);
		var stateOperation = new ApplyOperation(ApplyOperationKind.WriteState, statePath, null, null, null, null, null, null);
		try
		{
			var state = new ActivationState(JsonActivationStateStore.CurrentVersion, plan.ProfileId, plan.ProfileRevision, DateTimeOffset.UtcNow, true, deployed, issues)
			{
				ExternalFiles = deployedExternal,
			};
			await _activationStateStore.SaveAsync(plan.GameDataDirectory, state, CancellationToken.None).ConfigureAwait(false);
			operationResults.Add(new ApplyOperationResult(stateOperation, true, DeploymentMethod.StateFile, null, null));
		}
		catch (Exception exception)
		{
			operationResults.Add(new ApplyOperationResult(stateOperation, false, DeploymentMethod.StateFile, "WriteStateFailed", exception.Message));
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "WriteStateFailed", exception.Message, statePath, ExceptionMessage: exception.ToString()));
			CleanupControlledFiles(plan.GameDataDirectory, controlledPatchTargets);
			if (externalCommit is not null)
				issues.AddRange(await externalCommit.RollbackAsync().ConfigureAwait(false));
			await FinalizeFailedApplyStateAsync(plan.GameDataDirectory, previousState, externalCommit, issues).ConfigureAwait(false);
			return new ApplyResult(false, operationResults, report, issues);
		}

		if (externalCommit is not null)
			externalCommit.FinalizeTransaction();
		return new ApplyResult(true, operationResults, report, issues);
	}

	public async ValueTask<ApplyResult> DeactivateAsync(string gameDataDirectory, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);
		Directory.CreateDirectory(gameDataDirectory);
		cancellationToken.ThrowIfCancellationRequested();
		var operations = new List<ApplyOperationResult>();
		var issues = new List<CoreIssue>();
		ActivationState? previousState = null;
		try { previousState = await _activationStateStore.TryLoadAsync(gameDataDirectory, cancellationToken).ConfigureAwait(false); }
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ReadActivationStateFailed", exception.Message, Path.Combine(gameDataDirectory, JsonActivationStateStore.StateFileName), ExceptionMessage: exception.ToString()));
		}
		if (previousState is null)
		{
			if (EnumerateControlledFiles(gameDataDirectory).Any())
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ActivationStateMissingForPatches", "GameData 中存在可解析 Patch，但没有可用的 activation-state.json；停用不会猜测性删除这些文件。", gameDataDirectory));
			var emptyReport = await _stateScanner.ScanAsync(gameDataDirectory, false, CancellationToken.None).ConfigureAwait(false);
			issues.AddRange(emptyReport.Issues);
			return new ApplyResult(!issues.Any(issue => issue.Severity == CoreIssueSeverity.Error), operations, emptyReport, issues);
		}
		foreach (var file in previousState.Files)
		{
			if (!IsSafePatchTarget(gameDataDirectory, file.TargetPath))
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ActivationStatePatchTargetRejected", "activation-state.json 中的 Patch 目标不位于当前 GameData 顶层或文件名无效；停用不会删除它。", file.TargetPath, file.NodeId));
				continue;
			}
			var operation = new ApplyOperation(ApplyOperationKind.DeletePatch, file.TargetPath, null, file.ArchiveHex16, file.SourcePatchIndex, file.TargetPatchIndex, file.SidecarKind, file.NodeId);
			var result = DeletePatch(operation);
			operations.Add(result);
			if (!result.Success) issues.Add(ToIssue(result, operation));
		}
		foreach (var mapping in previousState?.ExternalFiles ?? Array.Empty<ExternalFileMapping>())
		{
			if (!TryValidateExternalMapping(Path.GetFullPath(gameDataDirectory), mapping, issues, "DeactivateExternalMapping"))
			{
				var rejectedOperation = new ApplyOperation(ApplyOperationKind.DeleteExternalFile, mapping.TargetPath, null, null, null, null, null, mapping.NodeId);
				operations.Add(new ApplyOperationResult(rejectedOperation, false, null, "ExternalTargetPathRejected", "The external target path is not a safe path under the current game directory."));
				continue;
			}
			var safeMapping = mapping with { TargetPath = GetExpectedExternalTargetPath(gameDataDirectory, mapping) };
			var operation = new ApplyOperation(ApplyOperationKind.DeleteExternalFile, safeMapping.TargetPath, null, null, null, null, null, safeMapping.NodeId);
			var result = await DeleteExternalFileAsync(operation, safeMapping, cancellationToken).ConfigureAwait(false);
			operations.Add(result);
			if (!result.Success) issues.Add(ToIssue(result, operation));
		}

		var remainingPatchFiles = previousState?.Files
			.Where(file => PathExistsIncludingReparsePoint(file.TargetPath))
			.ToArray()
			?? Array.Empty<ActivationStateFileEntry>();
		var remainingExternalFiles = previousState?.ExternalFiles
			.Where(mapping => PathExistsIncludingReparsePoint(mapping.TargetPath))
			.ToArray()
			?? Array.Empty<ExternalFileMapping>();
		if (previousState is not null && (remainingPatchFiles.Length > 0 || remainingExternalFiles.Length > 0))
		{
			try
			{
				await _activationStateStore.SaveAsync(
					gameDataDirectory,
					previousState with
					{
						AppliedUtc = DateTimeOffset.UtcNow,
						Completed = false,
						Files = remainingPatchFiles,
						Issues = issues,
						ExternalFiles = remainingExternalFiles,
					},
					CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "SaveDeactivationStateFailed", exception.Message, Path.Combine(gameDataDirectory, JsonActivationStateStore.StateFileName), ExceptionMessage: exception.ToString()));
			}
		}
		else
		{
			try { await _activationStateStore.DeleteAsync(gameDataDirectory, CancellationToken.None).ConfigureAwait(false); }
			catch (Exception exception) { issues.Add(new CoreIssue(CoreIssueSeverity.Error, "DeleteActivationStateFailed", exception.Message, Path.Combine(gameDataDirectory, JsonActivationStateStore.StateFileName), ExceptionMessage: exception.ToString())); }
		}
		var report = await _stateScanner.ScanAsync(gameDataDirectory, false, CancellationToken.None).ConfigureAwait(false);
		issues.AddRange(report.Issues);
		return new ApplyResult(!issues.Any(issue => issue.Severity == CoreIssueSeverity.Error), operations, report, issues);
	}

	private async ValueTask<PreflightResult> PreflightAsync(
		string gameDataDirectory,
		IReadOnlyList<ApplyOperation> operations,
		IReadOnlyList<ExternalFileMapping> externalMappings,
		IReadOnlyList<ExternalFileMapping> previousExternalMappings,
		bool useFastLinkValidation,
		CancellationToken cancellationToken)
	{
		var issues = new List<CoreIssue>();
		var sources = new Dictionary<string, SourceFileFact>(StringComparer.OrdinalIgnoreCase);
		var fullGameDataDirectory = Path.GetFullPath(gameDataDirectory);
		foreach (var operation in operations)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (string.IsNullOrWhiteSpace(operation.SourcePath) || !File.Exists(operation.SourcePath))
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "SourceFileMissing", $"Source file does not exist: {operation.SourcePath}", operation.SourcePath, operation.NodeId));
				continue;
			}
			if (operation.ArchiveHex16 is null || operation.SourcePatchIndex is null || operation.TargetPatchIndex is null || operation.SidecarKind is null)
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "DeployOperationIncomplete", "Deploy operation lacks patch identity fields.", operation.SourcePath, operation.NodeId));
				continue;
			}
			try
			{
				var info = new FileInfo(operation.SourcePath);
				if (!info.Exists)
				{
					issues.Add(new CoreIssue(CoreIssueSeverity.Error, "SourceFileMissing", $"Source file does not exist: {operation.SourcePath}", operation.SourcePath, operation.NodeId));
					continue;
				}
				var sha256 = useFastLinkValidation
					? string.Empty
					: await ComputeSha256Async(operation.SourcePath, cancellationToken).ConfigureAwait(false);
				sources[operation.SourcePath] = new SourceFileFact(info.Length, sha256);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "SourceFileUnreadable", exception.Message, operation.SourcePath, operation.NodeId, exception.ToString()));
			}
		}

		var previousByTarget = new Dictionary<string, ExternalFileMapping>(StringComparer.OrdinalIgnoreCase);
		foreach (var mapping in previousExternalMappings)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!TryValidateExternalMapping(fullGameDataDirectory, mapping, issues, "PreviousExternalMapping"))
				continue;

			var normalizedTarget = Path.GetFullPath(mapping.TargetPath);
			if (!previousByTarget.TryAdd(normalizedTarget, mapping))
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Error,
					"ExternalInstallDuplicateStateTarget",
					"Activation state contains more than one external file mapping for the same target.",
					mapping.TargetPath,
					mapping.NodeId));
			}

			if (Directory.Exists(normalizedTarget))
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalTargetIsDirectory", "A managed external target is a directory and cannot be replaced.", normalizedTarget, mapping.NodeId));
				continue;
			}
			if (PathExistsIncludingReparsePoint(normalizedTarget) && IsReparsePoint(normalizedTarget))
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalTargetIsReparsePoint", "A managed external target is a symbolic link or another reparse point and cannot be managed safely.", normalizedTarget, mapping.NodeId));
				continue;
			}
			if (!File.Exists(normalizedTarget)) continue;
			try
			{
				var hash = await ComputeSha256Async(normalizedTarget, cancellationToken).ConfigureAwait(false);
				if (!string.Equals(hash, mapping.Sha256, StringComparison.OrdinalIgnoreCase))
					issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalTargetModified", "A previously managed external file was modified outside the manager.", normalizedTarget, mapping.NodeId));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalTargetUnreadable", exception.Message, normalizedTarget, mapping.NodeId, exception.ToString()));
			}
		}

		var desiredByTarget = new Dictionary<string, ExternalFileMapping>(StringComparer.OrdinalIgnoreCase);
		foreach (var mapping in externalMappings)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!TryValidateExternalMapping(fullGameDataDirectory, mapping, issues, "ExternalMapping"))
				continue;

			var normalizedTarget = Path.GetFullPath(mapping.TargetPath);
			if (!desiredByTarget.TryAdd(normalizedTarget, mapping))
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Error,
					"ExternalInstallDuplicateTarget",
					"More than one enabled external file mapping targets the same game path.",
					normalizedTarget,
					mapping.NodeId));
				continue;
			}

			if (Directory.Exists(mapping.SourcePath))
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalSourceIsDirectory", "An external install source must be a file.", mapping.SourcePath, mapping.NodeId));
				continue;
			}
			if (!File.Exists(mapping.SourcePath))
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalSourceMissing", $"External source file does not exist: {mapping.SourcePath}", mapping.SourcePath, mapping.NodeId));
				continue;
			}
			if (IsReparsePoint(mapping.SourcePath))
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalSourceIsReparsePoint", "An external install source cannot be a symbolic link or another reparse point.", mapping.SourcePath, mapping.NodeId));
				continue;
			}
			if (string.Equals(Path.GetFullPath(mapping.SourcePath), normalizedTarget, StringComparison.OrdinalIgnoreCase))
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalSourceEqualsTarget", "An external install source cannot be the same file as its game target.", normalizedTarget, mapping.NodeId));
				continue;
			}
			try
			{
				var info = new FileInfo(mapping.SourcePath);
				if (info.Length != mapping.Length)
				{
					issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalSourceChanged", "External source file length changed after planning.", mapping.SourcePath, mapping.NodeId));
					continue;
				}
				var hash = await ComputeSha256Async(mapping.SourcePath, cancellationToken).ConfigureAwait(false);
				if (!string.Equals(hash, mapping.Sha256, StringComparison.OrdinalIgnoreCase))
					issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalSourceChanged", "External source file content changed after planning.", mapping.SourcePath, mapping.NodeId));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalSourceUnreadable", exception.Message, mapping.SourcePath, mapping.NodeId, exception.ToString()));
			}
		}

		foreach (var mapping in desiredByTarget.Values)
		{
			var normalizedTarget = Path.GetFullPath(mapping.TargetPath);
			if (previousByTarget.ContainsKey(normalizedTarget)) continue;
			if (!PathExistsIncludingReparsePoint(normalizedTarget)) continue;
			if (Directory.Exists(normalizedTarget))
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalTargetIsDirectory", "An external target path is occupied by a directory.", normalizedTarget, mapping.NodeId));
				continue;
			}
			if (IsReparsePoint(normalizedTarget))
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalTargetIsReparsePoint", "An unmanaged symbolic link or another reparse point occupies the external target path.", normalizedTarget, mapping.NodeId));
				continue;
			}
			// Even an identical file is not silently adopted. Ownership comes only from
			// the previous activation state, never from a content hash coincidence.
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalTargetConflict", "An unmanaged file already occupies the external target path.", normalizedTarget, mapping.NodeId));
		}
		return new PreflightResult(sources, issues);
	}

	private static bool TryValidateExternalMapping(
		string gameDataDirectory,
		ExternalFileMapping mapping,
		ICollection<CoreIssue> issues,
		string issuePrefix)
	{
		if (!TryGetExpectedExternalTargetPath(gameDataDirectory, mapping, out var expectedTarget, out var error))
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, $"{issuePrefix}PathRejected", error, mapping.TargetPath, mapping.NodeId));
			return false;
		}

		string actualTarget;
		try
		{
			actualTarget = Path.GetFullPath(mapping.TargetPath);
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, $"{issuePrefix}PathRejected", exception.Message, mapping.TargetPath, mapping.NodeId, exception.ToString()));
			return false;
		}

		if (!string.Equals(actualTarget, expectedTarget, StringComparison.OrdinalIgnoreCase))
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, $"{issuePrefix}PathRejected", "The external target path does not match its declared game-root mapping.", mapping.TargetPath, mapping.NodeId));
			return false;
		}

		var binDirectory = Path.Combine(Directory.GetParent(Path.GetFullPath(gameDataDirectory))!.FullName, "bin");
		if (HasReparsePointInExistingPath(binDirectory, actualTarget))
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, $"{issuePrefix}PathRejected", "An external target path contains a symbolic link or another reparse point.", actualTarget, mapping.NodeId));
			return false;
		}

		return true;
	}

	private static bool TryGetExpectedExternalTargetPath(
		string gameDataDirectory,
		ExternalFileMapping mapping,
		out string expectedTarget,
		out string error)
	{
		expectedTarget = string.Empty;
		error = string.Empty;
		try
		{
			if (mapping.TargetRoot != ExternalInstallTargetRoot.GameBin)
			{
				error = "The external target root is not supported.";
				return false;
			}
			if (!ExternalInstallPathMapper.TryNormalizeRelativePath(mapping.TargetRelativePath, out var normalizedRelative))
			{
				error = "The external target relative path is not safe.";
				return false;
			}
			var gameRoot = Directory.GetParent(Path.GetFullPath(gameDataDirectory))?.FullName;
			if (string.IsNullOrWhiteSpace(gameRoot))
			{
				error = "The game root could not be resolved from the GameData directory.";
				return false;
			}
			expectedTarget = Path.GetFullPath(Path.Combine(gameRoot, "bin", normalizedRelative.Replace('/', Path.DirectorySeparatorChar)));
			var binDirectory = Path.GetFullPath(Path.Combine(gameRoot, "bin"));
			if (!IsWithinOrEqual(binDirectory, expectedTarget))
			{
				error = "The external target path escaped the game's bin directory.";
				return false;
			}
			return true;
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			error = exception.Message;
			return false;
		}
	}

	private static bool HasReparsePointInExistingPath(string rootDirectory, string path)
	{
		var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var fullPath = Path.GetFullPath(path);
		if (!IsWithinOrEqual(root, fullPath)) return true;
		if (PathExistsIncludingReparsePoint(root)
			&& (IsReparsePoint(root) || File.Exists(root))) return true;

		var current = root;
		var relative = Path.GetRelativePath(root, fullPath);
		if (relative == ".") return PathExistsIncludingReparsePoint(current) && IsReparsePoint(current);
		foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
		{
			current = Path.Combine(current, segment);
			if (!PathExistsIncludingReparsePoint(current)) break;
			if (IsReparsePoint(current)) return true;
			if (File.Exists(current) && !string.Equals(current, fullPath, StringComparison.OrdinalIgnoreCase)) return true;
		}
		return false;
	}

	private static bool PathExistsIncludingReparsePoint(string path)
	{
		try
		{
			_ = File.GetAttributes(path);
			return true;
		}
		catch (FileNotFoundException) { return false; }
		catch (DirectoryNotFoundException) { return false; }
		catch (UnauthorizedAccessException) { return true; }
		catch (IOException) { return true; }
	}

	private static bool IsReparsePoint(string path)
	{
		try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
		catch (FileNotFoundException) { return false; }
		catch (DirectoryNotFoundException) { return false; }
		catch (UnauthorizedAccessException) { return true; }
		catch (IOException) { return true; }
	}

	private static bool IsWithinOrEqual(string root, string path)
	{
		var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase)
			|| normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
			|| normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}

	private static async ValueTask<ExternalFileCommitResult> CommitExternalFilesAsync(
		string gameDataDirectory,
		IReadOnlyList<ExternalFileMapping> desiredMappings,
		IReadOnlyList<ExternalFileMapping> previousMappings,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var gameRoot = Directory.GetParent(Path.GetFullPath(gameDataDirectory))?.FullName
			?? throw new InvalidOperationException("The game root could not be resolved from the GameData directory.");
		if (desiredMappings.Count == 0 && previousMappings.Count == 0)
			return ExternalFileCommitResult.EmptySuccess();

		var transactionDirectory = Path.Combine(
			gameRoot,
			".hd2modmanager-external-transactions",
			Guid.NewGuid().ToString("N"));
		var result = new ExternalFileCommitResult(transactionDirectory);
		ApplyOperation? currentOperation = null;
		try
		{
			Directory.CreateDirectory(result.TransactionDirectory!);
			var stagingDirectory = Path.Combine(result.TransactionDirectory!, "staged");
			var backupDirectory = Path.Combine(result.TransactionDirectory!, "backup");
			Directory.CreateDirectory(stagingDirectory);
			Directory.CreateDirectory(backupDirectory);

			var desiredByTarget = desiredMappings
				.Select(mapping => mapping with { TargetPath = GetExpectedExternalTargetPath(gameDataDirectory, mapping) })
			.ToDictionary(mapping => mapping.TargetPath, StringComparer.OrdinalIgnoreCase);
			var previousByTarget = previousMappings
				.Select(mapping => mapping with { TargetPath = GetExpectedExternalTargetPath(gameDataDirectory, mapping) })
			.ToDictionary(mapping => mapping.TargetPath, StringComparer.OrdinalIgnoreCase);
			var backupIndex = 0;

			// Remove managed files which are no longer part of the active profile first.
			// They are moved to the transaction backup, never deleted in place.
			foreach (var previous in previousByTarget.Values)
			{
				if (desiredByTarget.ContainsKey(previous.TargetPath)) continue;
				currentOperation = CreateExternalOperation(ApplyOperationKind.DeleteExternalFile, previous);
				if (PathExistsIncludingReparsePoint(previous.TargetPath))
				{
					EnsureRegularExternalFile(previous.TargetPath);
					var backupPath = Path.Combine(backupDirectory, $"{backupIndex++:D6}.bak");
					ClearReadOnlyAttribute(previous.TargetPath);
					File.Move(previous.TargetPath, backupPath);
					result.RecordBackup(previous.TargetPath, backupPath);
				}
				result.AddOperationResult(new ApplyOperationResult(currentOperation, true, DeploymentMethod.Delete, null, null));
			}

			var mappingIndex = 0;
			foreach (var desired in desiredByTarget.Values.OrderBy(mapping => mapping.TargetPath, StringComparer.OrdinalIgnoreCase))
			{
				currentOperation = CreateExternalOperation(ApplyOperationKind.DeployExternalFile, desired);
				var stagedPath = Path.Combine(stagingDirectory, $"{mappingIndex++:D6}.bin");
				File.Copy(desired.SourcePath, stagedPath, overwrite: false);

				if (PathExistsIncludingReparsePoint(desired.TargetPath))
				{
					EnsureRegularExternalFile(desired.TargetPath);
					var backupPath = Path.Combine(backupDirectory, $"{backupIndex++:D6}.bak");
					ClearReadOnlyAttribute(desired.TargetPath);
					File.Move(desired.TargetPath, backupPath);
					result.RecordBackup(desired.TargetPath, backupPath);
				}

				var targetDirectory = Path.GetDirectoryName(desired.TargetPath);
				if (!string.IsNullOrWhiteSpace(targetDirectory)) Directory.CreateDirectory(targetDirectory);
				File.Move(stagedPath, desired.TargetPath);
				result.RecordCreatedTarget(desired.TargetPath);
				result.RecordDeployedMapping(desired);
				result.AddOperationResult(new ApplyOperationResult(currentOperation, true, DeploymentMethod.Copy, null, null));
			}

			result.MarkSucceeded();
			return result;
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			currentOperation ??= new ApplyOperation(
				ApplyOperationKind.DeployExternalFile,
				Path.Combine(gameRoot, "bin"),
				null,
				null,
				null,
				null,
				null,
				null);
			result.AddOperationResult(new ApplyOperationResult(currentOperation, false, null, "ExternalDeployFailed", exception.Message));
			result.MarkFailed();
			return result;
		}
	}

	private static string GetExpectedExternalTargetPath(string gameDataDirectory, ExternalFileMapping mapping)
	{
		if (!TryGetExpectedExternalTargetPath(gameDataDirectory, mapping, out var target, out var error))
			throw new InvalidDataException(error);
		return target;
	}

	private static ApplyOperation CreateExternalOperation(ApplyOperationKind kind, ExternalFileMapping mapping)
		=> new(kind, mapping.TargetPath, mapping.SourcePath, null, null, null, null, mapping.NodeId);

	private static void EnsureRegularExternalFile(string path)
	{
		if (Directory.Exists(path))
			throw new IOException("The external target path is occupied by a directory.");
		if (!PathExistsIncludingReparsePoint(path)) return;
		if (IsReparsePoint(path))
			throw new IOException("The external target path is a symbolic link or another reparse point.");
		if (!File.Exists(path))
			throw new IOException("The external target path exists but is not a regular file.");
	}

	private static async ValueTask<IReadOnlyList<CoreIssue>> VerifyDeployedFilesAsync(IReadOnlyList<ActivationStateFileEntry> files, bool useFastLinkValidation)
	{
		var issues = new List<CoreIssue>();
		foreach (var file in files)
		{
			if (!File.Exists(file.TargetPath)) { issues.Add(new CoreIssue(CoreIssueSeverity.Error, "DeployedTargetMissing", "Deployed target is missing.", file.TargetPath, file.NodeId)); continue; }
			if (file.Method == DeploymentMethod.SymbolicLink)
			{
				var resolved = File.ResolveLinkTarget(file.TargetPath, returnFinalTarget: true);
				if (resolved is null || !string.Equals(Path.GetFullPath(resolved.FullName), Path.GetFullPath(file.SourcePath), StringComparison.OrdinalIgnoreCase))
				{
					issues.Add(new CoreIssue(CoreIssueSeverity.Error, "DeployedLinkMismatch", "Symbolic link does not point to its source.", file.TargetPath, file.NodeId));
					continue;
				}
			}
			else if (new FileInfo(file.TargetPath).Length != file.Length)
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "DeployedLengthMismatch", "Deployed target length differs from its source.", file.TargetPath, file.NodeId));
				continue;
			}
			if (useFastLinkValidation) continue;
			if (string.IsNullOrWhiteSpace(file.ContentSha256))
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "DeployedHashMissing", "Deployed Patch has no recorded content hash.", file.TargetPath, file.NodeId));
				continue;
			}
			try
			{
				var hash = await ComputeSha256Async(file.TargetPath, CancellationToken.None).ConfigureAwait(false);
				if (!string.Equals(hash, file.ContentSha256, StringComparison.OrdinalIgnoreCase))
					issues.Add(new CoreIssue(CoreIssueSeverity.Error, "DeployedHashMismatch", "Deployed target content differs from its recorded source hash.", file.TargetPath, file.NodeId));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "DeployedTargetUnreadable", exception.Message, file.TargetPath, file.NodeId, exception.ToString()));
			}
		}
		return issues;
	}

	private static async ValueTask<IReadOnlyList<CoreIssue>> VerifyExternalFilesAsync(IReadOnlyList<ExternalFileMapping> files)
	{
		var issues = new List<CoreIssue>();
		foreach (var file in files)
		{
			if (!File.Exists(file.TargetPath))
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalTargetMissing", "Deployed external target is missing.", file.TargetPath, file.NodeId));
				continue;
			}
			try
			{
				var info = new FileInfo(file.TargetPath);
				if (info.Length != file.Length)
				{
					issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalTargetLengthMismatch", "Deployed external target length differs from its source.", file.TargetPath, file.NodeId));
					continue;
				}
				var hash = await ComputeSha256Async(file.TargetPath, CancellationToken.None).ConfigureAwait(false);
				if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
					issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalTargetHashMismatch", "Deployed external target content differs from its source.", file.TargetPath, file.NodeId));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalTargetUnreadable", exception.Message, file.TargetPath, file.NodeId, exception.ToString()));
			}
		}
		return issues;
	}

	private IEnumerable<string> EnumerateControlledFiles(string gameDataDirectory)
	{
		if (!Directory.Exists(gameDataDirectory)) yield break;
		foreach (var path in Directory.EnumerateFiles(gameDataDirectory, "*", SearchOption.TopDirectoryOnly))
		{
			if (_fileNameParser.TryParse(Path.GetFileName(path), out _)) yield return path;
		}
	}

	private IReadOnlyList<string> GetControlledPatchTargets(ApplyPlan plan)
	{
		var gameData = Path.GetFullPath(plan.GameDataDirectory);
		return plan.Operations
			.Where(operation => operation.Kind is ApplyOperationKind.DeletePatch or ApplyOperationKind.DeployPatch)
			.Select(operation => operation.TargetPath)
			.Where(path => IsSafePatchTarget(gameData, path))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private bool IsSafePatchTarget(string gameDataDirectory, string? candidate)
	{
		if (string.IsNullOrWhiteSpace(candidate)) return false;
		try
		{
			var gameData = Path.GetFullPath(gameDataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			var target = Path.GetFullPath(candidate);
			if (!target.StartsWith(gameData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
			if (!string.Equals(Path.GetDirectoryName(target), gameData, StringComparison.OrdinalIgnoreCase)) return false;
			return _fileNameParser.TryParse(Path.GetFileName(target), out _);
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return false;
		}
	}

	private void CleanupControlledFiles(string gameDataDirectory, IReadOnlyList<string> controlledTargets)
	{
		foreach (var path in controlledTargets)
		{
			if (!IsSafePatchTarget(gameDataDirectory, path)) continue;
			try
			{
				ClearReadOnlyAttribute(path);
				File.Delete(path);
			}
			catch { }
		}
	}

	private async ValueTask DeleteActivationStateBestEffortAsync(string gameDataDirectory)
	{
		try { await _activationStateStore.DeleteAsync(gameDataDirectory, CancellationToken.None).ConfigureAwait(false); } catch { }
	}

	private async ValueTask FinalizeFailedApplyStateAsync(
		string gameDataDirectory,
		ActivationState? previousState,
		ExternalFileCommitResult? externalCommit,
		IReadOnlyList<CoreIssue> issues)
	{
		var externalMappings = (previousState?.ExternalFiles ?? Array.Empty<ExternalFileMapping>())
			.Concat(externalCommit?.DeployedMappings ?? Array.Empty<ExternalFileMapping>())
			.GroupBy(mapping => mapping.TargetPath, StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First())
			.Where(mapping => PathExistsIncludingReparsePoint(mapping.TargetPath))
			.ToArray();
		var remainingPatchFiles = previousState?.Files
			.Where(file => PathExistsIncludingReparsePoint(file.TargetPath))
			.ToArray()
			?? Array.Empty<ActivationStateFileEntry>();

		if (externalMappings.Length == 0 && remainingPatchFiles.Length == 0)
		{
			await DeleteActivationStateBestEffortAsync(gameDataDirectory).ConfigureAwait(false);
			return;
		}

		try
		{
			var state = new ActivationState(
				JsonActivationStateStore.CurrentVersion,
				previousState?.ProfileId,
				previousState?.ProfileRevision ?? 0,
				DateTimeOffset.UtcNow,
				false,
				remainingPatchFiles,
				issues)
			{
				ExternalFiles = externalMappings,
			};
			await _activationStateStore.SaveAsync(gameDataDirectory, state, CancellationToken.None).ConfigureAwait(false);
		}
		catch
		{
			// There is no safe state to publish. Keep the original file rather than
			// deleting ownership metadata when rollback or state persistence failed.
		}
	}

	private static ApplyOperationResult DeletePatch(ApplyOperation operation)
	{
		try
		{
			if (File.Exists(operation.TargetPath))
			{
				File.Delete(operation.TargetPath);
			}
			else if (Directory.Exists(operation.TargetPath))
			{
				Directory.Delete(operation.TargetPath);
			}
			return new ApplyOperationResult(operation, true, DeploymentMethod.Delete, null, null);
		}
		catch (Exception exception) { return new ApplyOperationResult(operation, false, DeploymentMethod.Delete, "CannotDeleteExistingPatch", exception.Message); }
	}

	private static ApplyOperationResult DeployPatch(ApplyOperation operation, DeploymentMethod method)
	{
		try
		{
			var targetDirectory = Path.GetDirectoryName(operation.TargetPath);
			if (!string.IsNullOrWhiteSpace(targetDirectory)) Directory.CreateDirectory(targetDirectory);
			if (File.Exists(operation.TargetPath))
			{
				ClearReadOnlyAttribute(operation.TargetPath);
				File.Delete(operation.TargetPath);
			}
			else if (Directory.Exists(operation.TargetPath))
			{
				Directory.Delete(operation.TargetPath);
			}
		}
		catch (Exception exception) { return new ApplyOperationResult(operation, false, null, "CannotPrepareTarget", exception.Message); }
		if (method == DeploymentMethod.HardLink)
		{
			return TryCreateHardLink(operation.TargetPath, operation.SourcePath!, out var error)
				? new ApplyOperationResult(operation, true, DeploymentMethod.HardLink, null, null)
				: new ApplyOperationResult(operation, false, null, "HardLinkFailed", error);
		}
		if (method == DeploymentMethod.SymbolicLink)
		{
			return TryCreateSymbolicLink(operation.TargetPath, operation.SourcePath!, out var error)
				? new ApplyOperationResult(operation, true, DeploymentMethod.SymbolicLink, null, null)
				: new ApplyOperationResult(operation, false, null, "SymbolicLinkFailed", error);
		}
		return new ApplyOperationResult(operation, false, null, "UnsupportedDeploymentMethod", "Only hard-link and symbolic-link deployment are supported.");
	}

	private static async ValueTask<ApplyOperationResult> DeleteExternalFileAsync(
		ApplyOperation operation,
		ExternalFileMapping mapping,
		CancellationToken cancellationToken)
	{
		try
		{
			if (!PathExistsIncludingReparsePoint(mapping.TargetPath)) return new ApplyOperationResult(operation, true, DeploymentMethod.Delete, null, null);
			if (Directory.Exists(mapping.TargetPath)) return new ApplyOperationResult(operation, false, DeploymentMethod.Delete, "ExternalTargetIsDirectory", "External target path is a directory.");
			if (IsReparsePoint(mapping.TargetPath)) return new ApplyOperationResult(operation, false, DeploymentMethod.Delete, "ExternalTargetIsReparsePoint", "External target path is a symbolic link or another reparse point.");
			if (!File.Exists(mapping.TargetPath)) return new ApplyOperationResult(operation, false, DeploymentMethod.Delete, "ExternalTargetUnreadable", "External target path exists but is not a regular file.");
			var hash = await ComputeSha256Async(mapping.TargetPath, cancellationToken).ConfigureAwait(false);
			if (!string.Equals(hash, mapping.Sha256, StringComparison.OrdinalIgnoreCase))
				return new ApplyOperationResult(operation, false, DeploymentMethod.Delete, "ExternalTargetModified", "External target was modified outside the manager.");
			ClearReadOnlyAttribute(mapping.TargetPath);
			File.Delete(mapping.TargetPath);
			return new ApplyOperationResult(operation, true, DeploymentMethod.Delete, null, null);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return new ApplyOperationResult(operation, false, DeploymentMethod.Delete, "CannotDeleteExternalFile", exception.Message);
		}
	}

	private static async ValueTask<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
		return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
	}

	private static CoreIssue ToIssue(ApplyOperationResult result, ApplyOperation operation)
		=> new(CoreIssueSeverity.Error, result.ErrorCode ?? "DeployOperationFailed", result.Message ?? "Deployment operation failed.", operation.TargetPath, operation.NodeId);

	private static bool TryCreateSymbolicLink(string linkPath, string targetPath, out string? error)
	{
		try { File.CreateSymbolicLink(linkPath, targetPath); error = null; return true; }
		catch (Exception exception) { error = FormatDeploymentError(exception); return false; }
	}

	private static void ClearReadOnlyAttribute(string path)
	{
		var attributes = File.GetAttributes(path);
		if ((attributes & FileAttributes.ReadOnly) != 0)
		{
			File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
		}
	}

	private static bool TryCreateHardLink(string linkPath, string targetPath, out string? error)
	{
		if (!OperatingSystem.IsWindows()) { error = "Hardlink P/Invoke is only implemented for Windows."; return false; }
		if (CreateHardLinkW(linkPath, targetPath, IntPtr.Zero)) { error = null; return true; }
		var win32Error = Marshal.GetLastWin32Error();
		error = $"Win32 {win32Error}: {new Win32Exception(win32Error).Message}";
		return false;
	}

	private static string FormatDeploymentError(Exception exception)
		=> exception is Win32Exception win32
			? $"Win32 {win32.NativeErrorCode}: {win32.Message}"
			: exception.Message;

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

	private sealed class ExternalFileCommitResult
	{
		private readonly List<ApplyOperationResult> operationResults = new();
		private readonly List<ExternalFileMapping> deployedMappings = new();
		private readonly List<string> createdTargets = new();
		private readonly List<ExternalBackup> backups = new();
		private bool rolledBack;

		public ExternalFileCommitResult(string? transactionDirectory)
		{
			TransactionDirectory = transactionDirectory;
		}

		public string? TransactionDirectory { get; }
		public bool Success { get; private set; }
		public IReadOnlyList<ApplyOperationResult> OperationResults => operationResults;
		public IReadOnlyList<ExternalFileMapping> DeployedMappings => deployedMappings;

		public static ExternalFileCommitResult EmptySuccess()
		{
			var result = new ExternalFileCommitResult(null);
			result.MarkSucceeded();
			return result;
		}

		public void MarkSucceeded() => Success = true;
		public void MarkFailed() => Success = false;
		public void AddOperationResult(ApplyOperationResult result) => operationResults.Add(result);
		public void RecordDeployedMapping(ExternalFileMapping mapping) => deployedMappings.Add(mapping);
		public void RecordCreatedTarget(string path) => createdTargets.Add(path);
		public void RecordBackup(string targetPath, string backupPath) => backups.Add(new ExternalBackup(targetPath, backupPath));

		public ValueTask<IReadOnlyList<CoreIssue>> RollbackAsync()
		{
			if (rolledBack) return ValueTask.FromResult<IReadOnlyList<CoreIssue>>(Array.Empty<CoreIssue>());
			rolledBack = true;
			var issues = new List<CoreIssue>();

			foreach (var targetPath in createdTargets.AsEnumerable().Reverse())
			{
				try
				{
					if (Directory.Exists(targetPath))
					{
						issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalRollbackTargetIsDirectory", "Rollback found a directory at a file target.", targetPath));
						continue;
					}
					if (File.Exists(targetPath))
					{
						ClearReadOnlyAttribute(targetPath);
						File.Delete(targetPath);
					}
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalRollbackDeleteFailed", exception.Message, targetPath, ExceptionMessage: exception.ToString()));
				}
			}

			foreach (var backup in backups.AsEnumerable().Reverse())
			{
				try
				{
					if (Directory.Exists(backup.TargetPath))
					{
						issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalRollbackTargetIsDirectory", "Rollback cannot restore a file over a directory.", backup.TargetPath));
						continue;
					}
					if (File.Exists(backup.TargetPath))
					{
						ClearReadOnlyAttribute(backup.TargetPath);
						File.Delete(backup.TargetPath);
					}
					var parent = Path.GetDirectoryName(backup.TargetPath);
					if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
					if (!File.Exists(backup.BackupPath))
					{
						issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalRollbackBackupMissing", "Rollback backup file is missing.", backup.BackupPath));
						continue;
					}
					File.Move(backup.BackupPath, backup.TargetPath);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalRollbackRestoreFailed", exception.Message, backup.TargetPath, ExceptionMessage: exception.ToString()));
				}
			}

			DeleteTransactionDirectory(issues);
			return ValueTask.FromResult<IReadOnlyList<CoreIssue>>(issues);
		}

		public void FinalizeTransaction()
		{
			if (TransactionDirectory is null) return;
			try { if (Directory.Exists(TransactionDirectory)) Directory.Delete(TransactionDirectory, recursive: true); }
			catch { /* State is already committed; an orphan transaction is harmless and can be cleaned later. */ }
		}

		private void DeleteTransactionDirectory(ICollection<CoreIssue> issues)
		{
			if (TransactionDirectory is null) return;
			try { if (Directory.Exists(TransactionDirectory)) Directory.Delete(TransactionDirectory, recursive: true); }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "ExternalRollbackCleanupFailed", exception.Message, TransactionDirectory, ExceptionMessage: exception.ToString()));
			}
		}
	}

	private sealed record ExternalBackup(string TargetPath, string BackupPath);
	private sealed record SourceFileFact(long Length, string Sha256);
	private sealed record PreflightResult(IReadOnlyDictionary<string, SourceFileFact> Sources, IReadOnlyList<CoreIssue> Issues);
}
