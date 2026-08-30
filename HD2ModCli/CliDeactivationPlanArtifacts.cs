using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

public sealed record CliDeactivationPlanDocument(
	string Schema,
	int Version,
	DateTimeOffset CreatedUtc,
	string PlanId,
	CliDeploymentTarget Target,
	CliDeploymentOwnership Ownership,
	IReadOnlyList<CliDeactivationPatch> Patches,
	IReadOnlyList<CliDeactivationExternalFile> ExternalFiles,
	IReadOnlyList<CliDeploymentDiagnostic> Diagnostics)
{
	public const string CurrentSchema = "hd2mod.deactivation-plan";
	public const int CurrentVersion = 2;
	public bool IsReady => !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

public sealed record CliDeactivationPatch(
	string TargetPath,
	string ArchiveHex16,
	int TargetPatchIndex,
	PatchSidecarKind SidecarKind,
	string? NodeId,
	long Length,
	string ContentSha256);

public sealed record CliDeactivationExternalFile(
	string TargetPath,
	string TargetRelativePath,
	string? NodeId,
	long Length,
	string Sha256,
	ExternalInstallFileKind Kind = ExternalInstallFileKind.ReShadePlugin);

internal sealed record CliDeactivationBuildResult(
	CliDeactivationPlanDocument Document,
	DeploymentOwnershipInspection? OwnershipInspection);

internal sealed record CliDeactivationValidationResult(
	CliDeactivationPlanDocument Document,
	CliDeactivationBuildResult? Current,
	IReadOnlyList<CliDeploymentDiagnostic> Diagnostics)
{
	public bool IsValid => !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

internal static class CliDeactivationPlanArtifacts
{
	public static async ValueTask<CliDeactivationBuildResult> BuildAsync(
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		var diagnostics = new List<CliDeploymentDiagnostic>();
		var targetValidation = new GameDataTargetGuard().Validate(workspace.GameDataRoot, cancellationToken);
		AddCoreIssues(diagnostics, targetValidation.Issues, "game-data");
		var target = new CliDeploymentTarget(
			workspace.ManagerRoot,
			workspace.LibraryRoot,
			targetValidation.GameDataDirectory ?? workspace.GameDataRoot,
			targetValidation.GameRootDirectory,
			targetValidation.GameBinDirectory);
		if (!targetValidation.IsValid || string.IsNullOrWhiteSpace(targetValidation.GameDataDirectory))
			return new CliDeactivationBuildResult(CreateDocument(target, new CliDeploymentOwnership("blocked", null, null, 0, 0), [], [], diagnostics), null);

		var inspection = await CoreServices.CreateDeploymentOwnershipInspector()
			.InspectAsync(targetValidation.GameDataDirectory, cancellationToken)
			.ConfigureAwait(false);
		AddCoreIssues(diagnostics, inspection.Issues, "ownership");
		var ownership = new CliDeploymentOwnership(
			inspection.Status switch
			{
				DeploymentOwnershipStatus.CleanBootstrap => "clean-bootstrap",
				DeploymentOwnershipStatus.Managed => "managed",
				_ => "blocked",
			},
			inspection.ActivationState?.ProfileId is { } profileId ? CliProjections.FormatId(profileId) : null,
			inspection.ActivationState?.ProfileRevision,
			inspection.ActualPatches.Count,
			inspection.SafeDeletionTargets.Count);
		var patches = inspection.SafeDeletionTargets
			.OrderBy(file => file.TargetPath, StringComparer.OrdinalIgnoreCase)
			.Select(file => new CliDeactivationPatch(
				file.TargetPath,
				file.ArchiveHex16,
				file.TargetPatchIndex,
				file.SidecarKind,
				file.NodeId is { } nodeId ? CliProjections.FormatId(nodeId) : null,
				file.Length,
				file.ContentSha256))
			.ToArray();
		var externalFiles = (inspection.ActivationState?.ExternalFiles ?? Array.Empty<ExternalFileMapping>())
			.OrderBy(file => file.TargetPath, StringComparer.OrdinalIgnoreCase)
			.Select(file => new CliDeactivationExternalFile(
				file.TargetPath,
				file.TargetRelativePath,
				CliProjections.FormatId(file.NodeId),
				file.Length,
				file.Sha256,
				file.Kind))
			.ToArray();
		return new CliDeactivationBuildResult(CreateDocument(target, ownership, patches, externalFiles, diagnostics), inspection);
	}

	public static async ValueTask<CliDeactivationPlanDocument> ReadDocumentAsync(string path, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("DeactivationPlanRequired", "请通过 --plan <停用计划.json> 提供已导出的停用计划。");
		var fullPath = CliOutput.NormalizeDirectoryPath(path, "DeactivationPlanPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("DeactivationPlanNotFound", $"停用计划文件不存在：{fullPath}");
		try
		{
			var document = JsonSerializer.Deserialize<CliDeactivationPlanDocument>(await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false), CliOutput.JsonOptions);
			if (document is null || !string.Equals(document.Schema, CliDeactivationPlanDocument.CurrentSchema, StringComparison.Ordinal) || document.Version != CliDeactivationPlanDocument.CurrentVersion)
				throw new CliUserException("DeactivationPlanVersionUnsupported", "停用计划格式或版本不受支持；请重新运行 deploy deactivate plan。");
			if (string.IsNullOrWhiteSpace(document.PlanId) || document.Target is null || document.Ownership is null || document.Patches is null || document.ExternalFiles is null || document.Diagnostics is null)
				throw new CliUserException("DeactivationPlanUnreadable", "停用计划缺少必要的目标、归属或文件清单。 ");
			return document;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("DeactivationPlanUnreadable", $"无法读取停用计划：{exception.Message}");
		}
	}

	public static async ValueTask<CliDeactivationValidationResult> ValidateAsync(
		CliDeactivationPlanDocument document,
		CliWorkspace workspace,
		bool includeProcessCheck,
		CancellationToken cancellationToken = default)
	{
		var diagnostics = new List<CliDeploymentDiagnostic>();
		CliDeactivationBuildResult? current = null;
		try
		{
			current = await BuildAsync(workspace, cancellationToken).ConfigureAwait(false);
			diagnostics.AddRange(current.Document.Diagnostics);
			if (!PathsEqual(document.Target.ManagerRoot, current.Document.Target.ManagerRoot)
				|| !PathsEqual(document.Target.LibraryRoot, current.Document.Target.LibraryRoot)
				|| !PathsEqual(document.Target.GameDataDirectory, current.Document.Target.GameDataDirectory))
			{
				diagnostics.Add(new CliDeploymentDiagnostic("DeactivationTargetChanged", "error", true, "validate", "当前目标与导出停用计划时不同；请重新生成计划。"));
			}
			if (!string.Equals(document.PlanId, current.Document.PlanId, StringComparison.OrdinalIgnoreCase))
			{
				diagnostics.Add(new CliDeploymentDiagnostic("DeactivationPlanDrift", "error", true, "validate", "已部署 Patch、外部文件或 activation-state.json 已变化；请重新生成停用计划。"));
			}
		}
		catch (CliUserException exception)
		{
			diagnostics.Add(new CliDeploymentDiagnostic(exception.Code, "error", true, "validate", exception.Message));
		}
		if (includeProcessCheck)
		{
			var process = CliDeploymentProcessGuard.Inspect();
			if (!process.IsReliable)
				diagnostics.Add(new CliDeploymentDiagnostic("GameProcessCheckFailed", "error", true, "process", "无法可靠枚举 helldivers2.exe；已拒绝停用写入。", Details: process.Error));
			else if (process.IsGameRunning)
				diagnostics.Add(new CliDeploymentDiagnostic("GameProcessRunning", "error", true, "process", $"检测到 Helldivers 2 正在运行（PID：{string.Join(", ", process.ProcessIds)}）。请先退出游戏。"));
		}
		return new CliDeactivationValidationResult(document, current, DistinctDiagnostics(diagnostics));
	}

	public static ApplyPlan CreateApplyPlan(CliDeactivationBuildResult build)
	{
		ArgumentNullException.ThrowIfNull(build);
		var inspection = build.OwnershipInspection;
		if (inspection is null || string.IsNullOrWhiteSpace(inspection.GameDataDirectory))
			throw new CliUserException("DeactivationPlanUnavailable", "当前停用计划没有可执行的 GameData 归属快照。");
		var operations = inspection.SafeDeletionTargets
			.Select(file => new ApplyOperation(ApplyOperationKind.DeletePatch, file.TargetPath, null, file.ArchiveHex16, file.SourcePatchIndex, file.TargetPatchIndex, file.SidecarKind, file.NodeId))
			.ToArray();
		return new ApplyPlan(
			inspection.GameDataDirectory,
			inspection.ActivationState?.ProfileId,
			inspection.ActivationState?.ProfileRevision ?? 0,
			DateTimeOffset.UtcNow,
			operations,
			Array.Empty<CoreIssue>());
	}

	private static CliDeactivationPlanDocument CreateDocument(
		CliDeploymentTarget target,
		CliDeploymentOwnership ownership,
		IReadOnlyList<CliDeactivationPatch> patches,
		IReadOnlyList<CliDeactivationExternalFile> externalFiles,
		IReadOnlyList<CliDeploymentDiagnostic> diagnostics)
	{
		var stableDiagnostics = DistinctDiagnostics(diagnostics);
		var stablePatches = patches.OrderBy(file => file.TargetPath, StringComparer.OrdinalIgnoreCase).ToArray();
		var stableExternal = externalFiles.OrderBy(file => file.TargetPath, StringComparer.OrdinalIgnoreCase).ToArray();
		var body = new { target, ownership, patches = stablePatches, externalFiles = stableExternal, diagnostics = stableDiagnostics };
		var planId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CliOutput.ToJson(body)))).ToLowerInvariant();
		return new CliDeactivationPlanDocument(CliDeactivationPlanDocument.CurrentSchema, CliDeactivationPlanDocument.CurrentVersion, DateTimeOffset.UtcNow, planId, target, ownership, stablePatches, stableExternal, stableDiagnostics);
	}

	private static void AddCoreIssues(ICollection<CliDeploymentDiagnostic> destination, IEnumerable<CoreIssue> issues, string stage)
	{
		foreach (var issue in issues)
			destination.Add(new CliDeploymentDiagnostic(issue.Code, issue.Severity.ToString().ToLowerInvariant(), issue.Severity == CoreIssueSeverity.Error, stage, issue.Message, issue.FilePath, issue.NodeId is { } nodeId ? CliProjections.FormatId(nodeId) : null, issue.ExceptionMessage));
	}

	private static IReadOnlyList<CliDeploymentDiagnostic> DistinctDiagnostics(IEnumerable<CliDeploymentDiagnostic> diagnostics)
		=> diagnostics.GroupBy(diagnostic => string.Join("\u001F", diagnostic.Code, diagnostic.Severity, diagnostic.Blocking, diagnostic.Stage, diagnostic.Message, diagnostic.Path, diagnostic.NodeId, diagnostic.Details), StringComparer.Ordinal)
			.Select(group => group.First()).OrderBy(diagnostic => diagnostic.Blocking ? 0 : 1).ThenBy(diagnostic => diagnostic.Stage, StringComparer.Ordinal).ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal).ToArray();

	private static bool PathsEqual(string? left, string? right)
	{
		if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
		try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase); }
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
	}
}
