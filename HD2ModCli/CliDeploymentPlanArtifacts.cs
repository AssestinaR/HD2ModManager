using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// Persisted, auditable deployment intent. It is not an authority to write:
/// apply always rebuilds and revalidates this document under both leases.
/// </summary>
public sealed record CliDeploymentPlanDocument(
	string Schema,
	int Version,
	DateTimeOffset CreatedUtc,
	string PlanId,
	CliDeploymentTarget Target,
	CliDeploymentProfile Profile,
	CliDeploymentOwnership Ownership,
	string InputFingerprint,
	IReadOnlyList<CliDeploymentPatch> Patches,
	IReadOnlyList<CliDeploymentExternalFile> ExternalFiles,
	CliDeploymentLua Lua,
	IReadOnlyList<CliDeploymentDiagnostic> Diagnostics)
{
	public const string CurrentSchema = "hd2mod.deployment-plan";
	public const int CurrentVersion = 2;

	public bool IsReady => !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

public sealed record CliDeploymentTarget(
	string? ManagerRoot,
	string? LibraryRoot,
	string? GameDataDirectory,
	string? GameRootDirectory,
	string? GameBinDirectory);

public sealed record CliDeploymentProfile(
	string Id,
	string Name,
	long Revision,
	IReadOnlyList<string> LogicalEntryIds,
	IReadOnlyList<string> EffectiveEntryIds);

public sealed record CliDeploymentOwnership(
	string Status,
	string? PreviousProfileId,
	long? PreviousProfileRevision,
	int ActualPatchCount,
	int SafeDeletionTargetCount);

public sealed record CliDeploymentPatch(
	string? NodeId,
	string SourcePath,
	long SourceLength,
	string? SourceSha256,
	bool IsGenerated,
	string ArchiveHex16,
	int SourcePatchIndex,
	int TargetPatchIndex,
	PatchSidecarKind SidecarKind,
	string TargetPath);

public sealed record CliDeploymentExternalFile(
	string NodeId,
	string SourcePath,
	string TargetPath,
	ExternalInstallTargetRoot TargetRoot,
	string TargetRelativePath,
	long Length,
	string Sha256,
	ExternalInstallFileKind Kind = ExternalInstallFileKind.ReShadePlugin);

public sealed record CliDeploymentLua(
	bool RequiresMaterialization,
	int InputCount,
	string? CompositionFingerprint,
	IReadOnlyList<string> GeneratedTargetPaths);

public sealed record CliDeploymentDiagnostic(
	string Code,
	string Severity,
	bool Blocking,
	string Stage,
	string Message,
	string? Path = null,
	string? NodeId = null,
	string? Details = null);

internal sealed record CliDeploymentBuildResult(
	CliDeploymentPlanDocument Document,
	ApplyPlan? ApplyPlan,
	LibrarySnapshot? Snapshot,
	Profile? EffectiveProfile,
	PatchFileIndex? OriginalPatchIndex,
	PatchFileIndex? ActivePatchIndex,
	IReadOnlyDictionary<ModNodeId, ModContentFacts> ContentFacts,
	LuaDeploymentPlan? LuaPlan,
	DeploymentOwnershipInspection? OwnershipInspection);

internal sealed record CliDeploymentValidationResult(
	CliDeploymentPlanDocument Document,
	CliDeploymentBuildResult? Current,
	IReadOnlyList<CliDeploymentDiagnostic> Diagnostics)
{
	public bool IsValid => !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

internal sealed record CliDeploymentMaterializedPlan(
	ApplyPlan? ApplyPlan,
	IReadOnlyList<CliDeploymentPatch> Patches,
	IReadOnlyList<CliDeploymentExternalFile> ExternalFiles,
	CliDeploymentLua Lua,
	IReadOnlyList<CliDeploymentDiagnostic> Diagnostics)
{
	public bool IsReady => ApplyPlan is not null && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

internal static class CliDeploymentPlanArtifacts
{
	public static async ValueTask<CliDeploymentBuildResult> BuildAsync(
		CliWorkspace workspace,
		string? profileSelector,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
		return await BuildAsync(workspace, state, profileSelector, cancellationToken).ConfigureAwait(false);
	}

	public static async ValueTask<CliDeploymentBuildResult> BuildAsync(
		CliWorkspace workspace,
		CliLibraryState libraryState,
		string? profileSelector,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(libraryState);
		var diagnostics = new List<CliDeploymentDiagnostic>();
		var selector = string.Equals(profileSelector, "active", StringComparison.OrdinalIgnoreCase) ? null : profileSelector;
		var profile = CliProjections.ResolveProfile(libraryState.Snapshot.Profiles, libraryState.Snapshot.ActiveProfileId, selector);
		var effective = ProfileEffectiveProfileResolver.Resolve(profile, libraryState.Snapshot, libraryState.OptionActivations);
		var targetValidation = new GameDataTargetGuard().Validate(workspace.GameDataRoot, cancellationToken);
		AddCoreIssues(diagnostics, targetValidation.Issues, "game-data");

		var target = new CliDeploymentTarget(
			workspace.ManagerRoot,
			workspace.LibraryRoot,
			targetValidation.GameDataDirectory ?? workspace.GameDataRoot,
			targetValidation.GameRootDirectory,
			targetValidation.GameBinDirectory);
		var profileProjection = new CliDeploymentProfile(
			CliProjections.FormatId(profile.Id),
			profile.Name,
			profile.Revision,
			profile.Entries
				.OrderBy(entry => entry.LoadOrder)
				.ThenBy(entry => entry.AddedUtc)
				.ThenBy(entry => entry.NodeId.Value)
				.Select(entry => CliProjections.FormatId(entry.NodeId))
				.ToArray(),
			effective.Entries.Select(entry => CliProjections.FormatId(entry.NodeId)).ToArray());

		if (!targetValidation.IsValid || string.IsNullOrWhiteSpace(workspace.LibraryRoot))
		{
			if (string.IsNullOrWhiteSpace(workspace.LibraryRoot))
			{
				diagnostics.Add(new CliDeploymentDiagnostic(
					"LibraryNotConfigured",
					"error",
					true,
					"library",
					"未能确定 Mod 库；部署规划需要已登记的 Mod 库。"));
			}
			var incompleteOwnership = new CliDeploymentOwnership("blocked", null, null, 0, 0);
			return CreateIncompleteResult(target, profileProjection, incompleteOwnership, diagnostics);
		}

		var ownershipInspection = await CoreServices.CreateDeploymentOwnershipInspector()
			.InspectAsync(targetValidation.GameDataDirectory, cancellationToken)
			.ConfigureAwait(false);
		AddCoreIssues(diagnostics, ownershipInspection.Issues, "ownership");
		var ownership = new CliDeploymentOwnership(
			ToOwnershipStatus(ownershipInspection.Status),
			ownershipInspection.ActivationState?.ProfileId is { } previousProfile ? CliProjections.FormatId(previousProfile) : null,
			ownershipInspection.ActivationState?.ProfileRevision,
			ownershipInspection.ActualPatches.Count,
			ownershipInspection.SafeDeletionTargets.Count);

		var activeNodeIds = effective.Entries.Select(entry => entry.NodeId).ToHashSet();
		var originalIndex = await CoreServices.CreatePatchFileIndexBuilder()
			.BuildAsync(libraryState.Snapshot, workspace.LibraryRoot, cancellationToken)
			.ConfigureAwait(false);
		AddCoreIssues(diagnostics, originalIndex.Issues.Where(issue => issue.NodeId is null || activeNodeIds.Contains(issue.NodeId.Value)), "patch-index");
		AddDuplicatePhysicalSourceIssues(diagnostics, effective, libraryState.Snapshot);

		var contentFacts = await ReadActiveContentFactsAsync(
			effective,
			libraryState.Snapshot,
			workspace.LibraryRoot,
			cancellationToken).ConfigureAwait(false);
		foreach (var facts in contentFacts.Values)
			AddCoreIssues(diagnostics, facts.Issues, "content-facts");

		var filteredIndex = new PatchFileIndex(
			originalIndex.BuiltUtc,
			originalIndex.FilesByNode
				.Where(pair => activeNodeIds.Contains(pair.Key))
				.ToDictionary(pair => pair.Key, pair => pair.Value),
			originalIndex.Issues.Where(issue => issue.NodeId is null || activeNodeIds.Contains(issue.NodeId.Value)).ToArray());
		var deploymentPaths = new StoragePaths(workspace.ManagerRoot ?? Directory.GetCurrentDirectory(), workspace.LibraryRoot);
		var luaPlan = await CoreServices.CreateLuaDeploymentService(deploymentPaths)
			.PlanAsync(effective, libraryState.Snapshot, filteredIndex, workspace.LibraryRoot, targetValidation.GameDataDirectory!, contentFacts, cancellationToken)
			.ConfigureAwait(false);
		AddCoreIssues(diagnostics, luaPlan.Issues, "lua");

		var rawPlan = await CoreServices.CreateApplyPlanner()
			.BuildPlanAsync(
				effective,
				libraryState.Snapshot,
				luaPlan.PatchIndex,
				targetValidation.GameDataDirectory!,
				new ApplyPlanAugmentation(luaPlan.GeneratedPatchGroups),
				cancellationToken)
			.ConfigureAwait(false);
		AddCoreIssues(diagnostics, rawPlan.Issues, "patch-plan");

		var deploymentPlan = rawPlan with
		{
			Operations = rawPlan.Operations.Where(operation => operation.Kind == ApplyOperationKind.DeployPatch).ToArray(),
			ExternalFileMappings = luaPlan.ExternalFiles,
		};
		var generatedFiles = luaPlan.GeneratedPatchGroups
			.SelectMany(group => group.Files)
			.ToDictionary(file => Path.GetFullPath(file.FilePath), file => file, StringComparer.OrdinalIgnoreCase);
		var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var patches = await CreatePatchProjectionsAsync(deploymentPlan, generatedFiles, hashes, diagnostics, cancellationToken).ConfigureAwait(false);
		var inputFingerprint = await ComputeInputFingerprintAsync(
			effective,
			originalIndex,
			activeNodeIds,
			luaPlan.ExternalFiles,
			hashes,
			cancellationToken).ConfigureAwait(false);
		var externalFiles = luaPlan.ExternalFiles
			.OrderBy(mapping => mapping.TargetPath, StringComparer.OrdinalIgnoreCase)
			.Select(mapping => new CliDeploymentExternalFile(
				CliProjections.FormatId(mapping.NodeId),
				mapping.SourcePath,
				mapping.TargetPath,
				mapping.TargetRoot,
				mapping.TargetRelativePath,
				mapping.Length,
				mapping.Sha256,
				mapping.Kind))
			.ToArray();
		var lua = new CliDeploymentLua(
			luaPlan.RequiresMaterialization,
			luaPlan.InputCount,
			luaPlan.CompositionFingerprint,
			patches.Where(patch => patch.IsGenerated).Select(patch => patch.TargetPath).ToArray());
		var document = CreateDocument(target, profileProjection, ownership, inputFingerprint, patches, externalFiles, lua, diagnostics);
		return new CliDeploymentBuildResult(document, deploymentPlan, libraryState.Snapshot, effective, originalIndex, filteredIndex, contentFacts, luaPlan, ownershipInspection);
	}

	public static async ValueTask<CliDeploymentPlanDocument> ReadDocumentAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("DeploymentPlanRequired", "请通过 --plan <部署计划.json> 提供已导出的部署计划。");
		var fullPath = CliOutput.NormalizeDirectoryPath(path, "DeploymentPlanPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("DeploymentPlanNotFound", $"部署计划文件不存在：{fullPath}");
		try
		{
			var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
			var document = JsonSerializer.Deserialize<CliDeploymentPlanDocument>(json, CliOutput.JsonOptions);
			if (document is null)
				throw new CliUserException("DeploymentPlanUnreadable", "部署计划文件为空或无法解析。");
			if (!string.Equals(document.Schema, CliDeploymentPlanDocument.CurrentSchema, StringComparison.Ordinal)
				|| document.Version != CliDeploymentPlanDocument.CurrentVersion)
			{
				throw new CliUserException(
					"DeploymentPlanVersionUnsupported",
					$"部署计划版本不受支持。需要 {CliDeploymentPlanDocument.CurrentSchema} v{CliDeploymentPlanDocument.CurrentVersion}；请重新运行 deploy plan。");
			}
			if (string.IsNullOrWhiteSpace(document.PlanId)
				|| document.Target is null
				|| document.Profile is null
				|| string.IsNullOrWhiteSpace(document.Profile.Id)
				|| document.Ownership is null
				|| string.IsNullOrWhiteSpace(document.InputFingerprint)
				|| document.Patches is null
				|| document.ExternalFiles is null
				|| document.Lua is null
				|| document.Diagnostics is null)
			{
				throw new CliUserException("DeploymentPlanUnreadable", "部署计划缺少身份、配置、指纹或部署映射信息。");
			}
			return document;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("DeploymentPlanUnreadable", $"无法读取部署计划：{exception.Message}");
		}
	}

	public static async ValueTask<CliDeploymentValidationResult> ValidateAsync(
		CliDeploymentPlanDocument document,
		CliWorkspace workspace,
		bool includeProcessCheck,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);
		var diagnostics = new List<CliDeploymentDiagnostic>();
		CliDeploymentBuildResult? current = null;
		try
		{
			current = await BuildAsync(workspace, document.Profile.Id, cancellationToken).ConfigureAwait(false);
			diagnostics.AddRange(current.Document.Diagnostics);
			if (!PathsEqual(document.Target.ManagerRoot, current.Document.Target.ManagerRoot)
				|| !PathsEqual(document.Target.LibraryRoot, current.Document.Target.LibraryRoot)
				|| !PathsEqual(document.Target.GameDataDirectory, current.Document.Target.GameDataDirectory))
			{
				diagnostics.Add(new CliDeploymentDiagnostic(
					"DeploymentTargetChanged", "error", true, "validate",
					"当前管理器、Mod 库或 GameData 目标与导出部署计划时不同；请重新生成计划。"));
			}
			if (!string.Equals(document.PlanId, current.Document.PlanId, StringComparison.OrdinalIgnoreCase))
			{
				diagnostics.Add(new CliDeploymentDiagnostic(
					"DeploymentPlanDrift", "error", true, "validate",
					"配置、选项、来源 Patch、Lua 组合、外部文件或当前部署状态已变化；请重新生成部署计划。"));
			}
		}
		catch (CliUserException exception)
		{
			diagnostics.Add(new CliDeploymentDiagnostic(exception.Code, "error", true, "validate", exception.Message));
		}

		if (includeProcessCheck)
		{
			var processCheck = CliDeploymentProcessGuard.Inspect();
			if (!processCheck.IsReliable)
			{
				diagnostics.Add(new CliDeploymentDiagnostic(
					"GameProcessCheckFailed", "error", true, "process",
					"无法可靠枚举 helldivers2.exe；为避免写入运行中的游戏，部署已被阻断。", Details: processCheck.Error));
			}
			else if (processCheck.IsGameRunning)
			{
				diagnostics.Add(new CliDeploymentDiagnostic(
					"GameProcessRunning", "error", true, "process",
					$"检测到 Helldivers 2 正在运行（PID：{string.Join(", ", processCheck.ProcessIds)}）。请先退出游戏。"));
			}
		}

		return new CliDeploymentValidationResult(document, current, DistinctDiagnostics(diagnostics));
	}

	public static async ValueTask<CliDeploymentMaterializedPlan> MaterializeAsync(
		CliDeploymentBuildResult current,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(current);
		var diagnostics = new List<CliDeploymentDiagnostic>();
		if (current.Snapshot is null
			|| current.EffectiveProfile is null
			|| current.ActivePatchIndex is null
			|| current.LuaPlan is null
			|| string.IsNullOrWhiteSpace(current.Document.Target.LibraryRoot)
			|| string.IsNullOrWhiteSpace(current.Document.Target.GameDataDirectory))
		{
			diagnostics.Add(new CliDeploymentDiagnostic("DeploymentMaterializationUnavailable", "error", true, "apply", "当前部署计划缺少可物化的配置、来源或目标信息。"));
			return new CliDeploymentMaterializedPlan(null, [], [], new CliDeploymentLua(false, 0, null, []), diagnostics);
		}

		var deploymentPaths = new StoragePaths(
			current.Document.Target.ManagerRoot ?? Directory.GetCurrentDirectory(),
			current.Document.Target.LibraryRoot);
		var preparation = await CoreServices.CreateLuaDeploymentService(deploymentPaths)
			.MaterializeAsync(
				current.EffectiveProfile,
				current.Snapshot,
				current.ActivePatchIndex,
				current.Document.Target.LibraryRoot,
				current.Document.Target.GameDataDirectory,
				current.ContentFacts,
				LuaDeploymentValidationMode.CompileOnly,
				cancellationToken)
			.ConfigureAwait(false);
		AddCoreIssues(diagnostics, preparation.Issues, "lua-materialize");
		if (preparation.HasErrors)
			return new CliDeploymentMaterializedPlan(null, [], [], new CliDeploymentLua(false, 0, null, []), DistinctDiagnostics(diagnostics));

		var raw = await CoreServices.CreateApplyPlanner()
			.BuildPlanAsync(
				current.EffectiveProfile,
				current.Snapshot,
				preparation.PatchIndex,
				current.Document.Target.GameDataDirectory,
				new ApplyPlanAugmentation(preparation.GeneratedPatchGroups),
				cancellationToken)
			.ConfigureAwait(false);
		AddCoreIssues(diagnostics, raw.Issues, "patch-plan");
		var plan = raw with { ExternalFileMappings = preparation.ExternalFiles };
		var generatedFiles = preparation.GeneratedPatchGroups
			.SelectMany(group => group.Files)
			.ToDictionary(file => Path.GetFullPath(file.FilePath), file => file, StringComparer.OrdinalIgnoreCase);
		var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var patches = await CreatePatchProjectionsAsync(plan, generatedFiles, hashes, diagnostics, cancellationToken).ConfigureAwait(false);
		var externalFiles = preparation.ExternalFiles
			.OrderBy(mapping => mapping.TargetPath, StringComparer.OrdinalIgnoreCase)
			.Select(mapping => new CliDeploymentExternalFile(
				CliProjections.FormatId(mapping.NodeId),
				mapping.SourcePath,
				mapping.TargetPath,
				mapping.TargetRoot,
				mapping.TargetRelativePath,
				mapping.Length,
				mapping.Sha256,
				mapping.Kind))
			.ToArray();
		var lua = new CliDeploymentLua(
			current.LuaPlan.RequiresMaterialization,
			current.LuaPlan.InputCount,
			current.LuaPlan.CompositionFingerprint,
			patches.Where(patch => patch.IsGenerated).Select(patch => patch.TargetPath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());
		return new CliDeploymentMaterializedPlan(plan, patches, externalFiles, lua, DistinctDiagnostics(diagnostics));
	}

	public static bool MatchesMaterializedPlan(CliDeploymentPlanDocument document, CliDeploymentMaterializedPlan materialized)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(materialized);
		if (!materialized.IsReady) return false;
		if (document.Patches.Count != materialized.Patches.Count || document.ExternalFiles.Count != materialized.ExternalFiles.Count) return false;
		var expectedPatches = document.Patches.OrderBy(patch => patch.TargetPath, StringComparer.OrdinalIgnoreCase).ToArray();
		var actualPatches = materialized.Patches.OrderBy(patch => patch.TargetPath, StringComparer.OrdinalIgnoreCase).ToArray();
		for (var index = 0; index < expectedPatches.Length; index++)
		{
			if (!Equals(expectedPatches[index], actualPatches[index])) return false;
		}
		var expectedExternal = document.ExternalFiles.OrderBy(file => file.TargetPath, StringComparer.OrdinalIgnoreCase).ToArray();
		var actualExternal = materialized.ExternalFiles.OrderBy(file => file.TargetPath, StringComparer.OrdinalIgnoreCase).ToArray();
		for (var index = 0; index < expectedExternal.Length; index++)
		{
			if (!Equals(expectedExternal[index], actualExternal[index])) return false;
		}
		return document.Lua.RequiresMaterialization == materialized.Lua.RequiresMaterialization
			&& document.Lua.InputCount == materialized.Lua.InputCount
			&& string.Equals(document.Lua.CompositionFingerprint, materialized.Lua.CompositionFingerprint, StringComparison.OrdinalIgnoreCase)
			&& document.Lua.GeneratedTargetPaths
				.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
				.SequenceEqual(materialized.Lua.GeneratedTargetPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
	}

	private static CliDeploymentBuildResult CreateIncompleteResult(
		CliDeploymentTarget target,
		CliDeploymentProfile profile,
		CliDeploymentOwnership ownership,
		IReadOnlyList<CliDeploymentDiagnostic> diagnostics)
	{
		var document = CreateDocument(target, profile, ownership, "unavailable", [], [], new CliDeploymentLua(false, 0, null, []), diagnostics);
		return new CliDeploymentBuildResult(document, null, null, null, null, null, new Dictionary<ModNodeId, ModContentFacts>(), null, null);
	}

	private static async ValueTask<IReadOnlyDictionary<ModNodeId, ModContentFacts>> ReadActiveContentFactsAsync(
		Profile effectiveProfile,
		LibrarySnapshot snapshot,
		string modsRootDirectory,
		CancellationToken cancellationToken)
	{
		await using var reader = CoreServices.CreateModInformationReader();
		var producer = CoreServices.CreateAssetInventoryProducer(reader);
		var result = new Dictionary<ModNodeId, ModContentFacts>();
		foreach (var nodeId in effectiveProfile.Entries.Select(entry => entry.NodeId).Distinct())
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!snapshot.Nodes.TryGetValue(nodeId, out var node)) continue;
			result[nodeId] = await producer.GetNodeFactsAsync(node, modsRootDirectory, cancellationToken).ConfigureAwait(false);
		}
		return result;
	}

	private static async ValueTask<IReadOnlyList<CliDeploymentPatch>> CreatePatchProjectionsAsync(
		ApplyPlan plan,
		IReadOnlyDictionary<string, GeneratedPatchFile> generatedFiles,
		IDictionary<string, string> hashes,
		ICollection<CliDeploymentDiagnostic> diagnostics,
		CancellationToken cancellationToken)
	{
		var patches = new List<CliDeploymentPatch>();
		foreach (var operation in plan.Operations.Where(operation => operation.Kind == ApplyOperationKind.DeployPatch)
			.OrderBy(operation => operation.ArchiveHex16, StringComparer.OrdinalIgnoreCase)
			.ThenBy(operation => operation.TargetPatchIndex)
			.ThenBy(operation => operation.SidecarKind))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (string.IsNullOrWhiteSpace(operation.SourcePath)
				|| string.IsNullOrWhiteSpace(operation.ArchiveHex16)
				|| operation.SourcePatchIndex is null
				|| operation.TargetPatchIndex is null
				|| operation.SidecarKind is null)
			{
				diagnostics.Add(new CliDeploymentDiagnostic("DeploymentMappingIncomplete", "error", true, "patch-plan", "Patch 部署映射缺少必要的源或目标身份。", operation.TargetPath));
				continue;
			}
			var sourcePath = Path.GetFullPath(operation.SourcePath);
			var generated = generatedFiles.TryGetValue(sourcePath, out var generatedFile);
			long length;
			string? hash = null;
			if (generated)
			{
				length = generatedFile!.Length;
			}
			else
			{
				try
				{
					var info = new FileInfo(sourcePath);
					if (!info.Exists) throw new FileNotFoundException("部署来源 Patch 不存在。", sourcePath);
					length = info.Length;
					hash = await GetHashAsync(sourcePath, hashes, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileNotFoundException)
				{
					diagnostics.Add(new CliDeploymentDiagnostic("DeploymentSourceUnreadable", "error", true, "patch-source", exception.Message, sourcePath, operation.NodeId is { } node ? CliProjections.FormatId(node) : null, exception.ToString()));
					continue;
				}
			}
			patches.Add(new CliDeploymentPatch(
				operation.NodeId is { } nodeId ? CliProjections.FormatId(nodeId) : null,
				sourcePath,
				length,
				hash,
				generated,
				operation.ArchiveHex16.ToLowerInvariant(),
				operation.SourcePatchIndex.Value,
				operation.TargetPatchIndex.Value,
				operation.SidecarKind.Value,
				Path.GetFullPath(operation.TargetPath)));
		}
		return patches;
	}

	private static async ValueTask<string> ComputeInputFingerprintAsync(
		Profile effectiveProfile,
		PatchFileIndex index,
		IReadOnlySet<ModNodeId> activeNodeIds,
		IReadOnlyList<ExternalFileMapping> externalFiles,
		IDictionary<string, string> hashes,
		CancellationToken cancellationToken)
	{
		using var builder = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		AppendField(builder, "hd2mod-deployment-input-v1");
		foreach (var entry in effectiveProfile.Entries)
			AppendField(builder, entry.NodeId.Value.ToString("N"));
		foreach (var file in index.FilesByNode
			.Where(pair => activeNodeIds.Contains(pair.Key))
			.SelectMany(pair => pair.Value)
			.OrderBy(file => file.NodeId.Value)
			.ThenBy(file => file.FilePath, StringComparer.OrdinalIgnoreCase))
		{
			cancellationToken.ThrowIfCancellationRequested();
			AppendField(builder, file.NodeId.Value.ToString("N"));
			AppendField(builder, file.ArchiveHex16);
			AppendField(builder, file.SourcePatchIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
			AppendField(builder, ((int)file.SidecarKind).ToString(System.Globalization.CultureInfo.InvariantCulture));
			AppendField(builder, await GetHashAsync(file.FilePath, hashes, cancellationToken).ConfigureAwait(false));
		}
		foreach (var external in externalFiles.OrderBy(file => file.TargetPath, StringComparer.OrdinalIgnoreCase))
		{
			AppendField(builder, external.NodeId.Value.ToString("N"));
			AppendField(builder, external.TargetPath);
			AppendField(builder, external.Sha256);
		}
		return Convert.ToHexString(builder.GetHashAndReset()).ToLowerInvariant();
	}

	private static async ValueTask<string> GetHashAsync(string path, IDictionary<string, string> hashes, CancellationToken cancellationToken)
	{
		var fullPath = Path.GetFullPath(path);
		if (hashes.TryGetValue(fullPath, out var cached)) return cached;
		await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
		var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
		hashes[fullPath] = hash;
		return hash;
	}

	private static CliDeploymentPlanDocument CreateDocument(
		CliDeploymentTarget target,
		CliDeploymentProfile profile,
		CliDeploymentOwnership ownership,
		string inputFingerprint,
		IReadOnlyList<CliDeploymentPatch> patches,
		IReadOnlyList<CliDeploymentExternalFile> externalFiles,
		CliDeploymentLua lua,
		IReadOnlyList<CliDeploymentDiagnostic> diagnostics)
	{
		var stableDiagnostics = DistinctDiagnostics(diagnostics);
		var stablePatches = patches
			.OrderBy(patch => patch.ArchiveHex16, StringComparer.OrdinalIgnoreCase)
			.ThenBy(patch => patch.TargetPatchIndex)
			.ThenBy(patch => patch.SidecarKind)
			.ThenBy(patch => patch.TargetPath, StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var stableExternal = externalFiles.OrderBy(file => file.TargetPath, StringComparer.OrdinalIgnoreCase).ToArray();
		var stableLua = lua with { GeneratedTargetPaths = lua.GeneratedTargetPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray() };
		var body = new
		{
			target,
			profile,
			ownership,
			inputFingerprint,
			patches = stablePatches,
			externalFiles = stableExternal,
			lua = stableLua,
			diagnostics = stableDiagnostics,
		};
		var planId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CliOutput.ToJson(body)))).ToLowerInvariant();
		return new CliDeploymentPlanDocument(
			CliDeploymentPlanDocument.CurrentSchema,
			CliDeploymentPlanDocument.CurrentVersion,
			DateTimeOffset.UtcNow,
			planId,
			target,
			profile,
			ownership,
			inputFingerprint,
			stablePatches,
			stableExternal,
			stableLua,
			stableDiagnostics);
	}

	private static void AddDuplicatePhysicalSourceIssues(
		ICollection<CliDeploymentDiagnostic> diagnostics,
		Profile effectiveProfile,
		LibrarySnapshot snapshot)
	{
		foreach (var group in effectiveProfile.Entries
			.Select(entry => snapshot.Nodes.TryGetValue(entry.NodeId, out var node) ? node : null)
			.Where(node => node?.Metadata.Kind == ModNodeKind.Option
				&& !string.IsNullOrWhiteSpace(node.Metadata.SourcePackageGuid)
				&& !string.IsNullOrWhiteSpace(node.Metadata.SourcePackagePath)
				&& node.PatchGroups.Count > 0)
			.Cast<ModNode>()
			.GroupBy(node => node.Metadata.SourcePackageGuid + "\u001F" + node.Metadata.SourcePackagePath, StringComparer.OrdinalIgnoreCase)
			.Where(group => group.Select(node => node.Id).Distinct().Count() > 1))
		{
			var nodes = group.OrderBy(node => node.Metadata.Name, StringComparer.OrdinalIgnoreCase).ToArray();
			diagnostics.Add(new CliDeploymentDiagnostic(
				"DuplicatePhysicalOption",
				"error",
				true,
				"profile",
				$"同一真实 Patch 目录被重复启用：{nodes[0].Metadata.SourcePackagePath}（{string.Join("、", nodes.Select(node => node.Metadata.Name))}）。",
				nodes[0].Metadata.SourcePackagePath,
				CliProjections.FormatId(nodes[0].Id)));
		}
	}

	private static void AddCoreIssues(ICollection<CliDeploymentDiagnostic> destination, IEnumerable<CoreIssue> issues, string stage)
	{
		foreach (var issue in issues)
		{
			destination.Add(new CliDeploymentDiagnostic(
				issue.Code,
				issue.Severity.ToString().ToLowerInvariant(),
				issue.Severity == CoreIssueSeverity.Error,
				stage,
				issue.Message,
				issue.FilePath,
				issue.NodeId is { } nodeId ? CliProjections.FormatId(nodeId) : null,
				issue.ExceptionMessage));
		}
	}

	private static IReadOnlyList<CliDeploymentDiagnostic> DistinctDiagnostics(IEnumerable<CliDeploymentDiagnostic> diagnostics)
		=> diagnostics
			.GroupBy(diagnostic => string.Join("\u001F", diagnostic.Code, diagnostic.Severity, diagnostic.Blocking, diagnostic.Stage, diagnostic.Message, diagnostic.Path, diagnostic.NodeId, diagnostic.Details), StringComparer.Ordinal)
			.Select(group => group.First())
			.OrderBy(diagnostic => diagnostic.Blocking ? 0 : 1)
			.ThenBy(diagnostic => diagnostic.Stage, StringComparer.Ordinal)
			.ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
			.ToArray();

	private static void AppendField(IncrementalHash hash, string value)
	{
		hash.AppendData(Encoding.UTF8.GetBytes(value));
		hash.AppendData([0]);
	}

	private static string ToOwnershipStatus(DeploymentOwnershipStatus status)
		=> status switch
		{
			DeploymentOwnershipStatus.CleanBootstrap => "clean-bootstrap",
			DeploymentOwnershipStatus.Managed => "managed",
			_ => "blocked",
		};

	private static bool PathsEqual(string? left, string? right)
	{
		if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
			return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
		try
		{
			return string.Equals(
				Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
				Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
				StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}
}
