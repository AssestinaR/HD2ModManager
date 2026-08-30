using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

public enum CliHostDerivedOperation
{
	Decoration,
	Helmet,
	Rebuild,
}

public enum CliHostDerivedAction
{
	Enable,
	Disable,
	Rebuild,
}

/// <summary>
/// A single-state host-derived request. Decoration activation and temporary
/// helmet merge intentionally use separate plans, although both preserve the
/// other current layer while rebuilding one managed Overwrite.
/// </summary>
public sealed record CliHostDerivedRequest(
	CliHostDerivedOperation Operation,
	CliHostDerivedAction Action,
	IReadOnlyList<string> HostNodeIds,
	IReadOnlyList<string> DecorationNodeIds,
	bool AllDeclaredDecorationHosts,
	string GameDataDirectory);

public sealed record CliHostDerivedTarget(
	string ManagerRoot,
	string LibraryRoot,
	string GameDataDirectory);

public sealed record CliHostDerivedPlanDocument(
	string Schema,
	int Version,
	DateTimeOffset CreatedUtc,
	string InputFingerprint,
	CliHostDerivedRequest Request,
	CliHostDerivedTarget Target,
	IReadOnlyList<CliHostDerivedHost> Hosts,
	CliHostDerivedInputState Inputs,
	IReadOnlyList<CliHostDerivedDiagnostic> Diagnostics)
{
	public const string CurrentSchema = "hd2mod.host-derived-overwrite-plan";
	public const int CurrentVersion = 1;

	// A reviewed request that happens to be a no-op is still valid.  Treating it
	// as blocked makes an already-enabled decoration/helmet state look like an
	// error to an Agent, and prevents a harmless idempotent apply.
	public bool IsReady => !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

public sealed record CliHostDerivedHost(
	string NodeId,
	string Name,
	string Kind,
	string RelativePath,
	int PatchGroupCount,
	bool CurrentHelmetToArmorMerged,
	bool PlannedHelmetToArmorMerged,
	IReadOnlyList<CliHostDerivedDecorationAttachment> CurrentDecorations,
	IReadOnlyList<CliHostDerivedDecorationAttachment> PlannedDecorations,
	CliHostDerivedOverwriteState Overwrite);

public sealed record CliHostDerivedDecorationAttachment(
	string DecorationNodeId,
	string Name,
	string RelativePath,
	string SourceStorageMode,
	IReadOnlyList<string> DeclaredHostNodeIds,
	string PlanSha256);

public sealed record CliHostDerivedOverwriteState(
	bool Exists,
	bool IsManaged,
	bool HasDecorationMarker,
	bool HasHelmetMarker,
	int FileCount,
	long TotalBytes,
	string? Sha256);

public sealed record CliHostDerivedInputState(
	CliHostDerivedFileSnapshot LibrarySnapshot,
	CliHostDerivedFileSnapshot DecorationActivations,
	CliHostDerivedGameDataSnapshot GameData,
	IReadOnlyList<CliHostDerivedDirectorySnapshot> SourceDirectories,
	IReadOnlyList<CliHostDerivedDirectorySnapshot> OverwriteDirectories);

public sealed record CliHostDerivedFileSnapshot(
	string FileName,
	bool Exists,
	long Length,
	string? Sha256);

public sealed record CliHostDerivedDirectorySnapshot(
	string OwnerNodeId,
	string Role,
	string RelativePath,
	bool Exists,
	bool IsManaged,
	int FileCount,
	long TotalBytes,
	string? Sha256);

public sealed record CliHostDerivedGameDataSnapshot(
	string Directory,
	string IndexState,
	bool IsIndexCurrent,
	string CurrentSourceFingerprint,
	string? IndexedSourceFingerprint);

public sealed record CliHostDerivedDiagnostic(
	string Code,
	string Severity,
	bool Blocking,
	string Message,
	string? Path = null,
	string? NodeId = null,
	string? Details = null);

internal sealed record CliHostDerivedHostBuild(
	ModNode Host,
	IReadOnlyList<DecorationHostRebuildAttachment> CurrentAttachments,
	IReadOnlyList<DecorationHostRebuildAttachment> PlannedAttachments,
	bool CurrentHelmetMerged,
	bool PlannedHelmetMerged);

internal sealed record CliHostDerivedBuildResult(
	CliHostDerivedPlanDocument Document,
	StoragePaths Paths,
	CliLibraryState State,
	IReadOnlyDictionary<ModNodeId, CliHostDerivedHostBuild> HostsById,
	IReadOnlyDictionary<string, IReadOnlyCollection<string>> DecorationActivationUpdates);

internal sealed record CliHostDerivedValidationResult(
	CliHostDerivedPlanDocument Document,
	CliHostDerivedBuildResult? Current,
	IReadOnlyList<CliHostDerivedDiagnostic> Diagnostics)
{
	public bool IsValid => Current is not null && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

internal static class CliHostDerivedOverwritePlanArtifacts
{
	// These are the Core-owned markers.  The CLI must use the same names as the
	// WPF service or it would falsely classify a manager-generated Overwrite as
	// user-owned and refuse an otherwise safe rebuild.
	private const string DecorationMarkerName = DecorationHostRebuildService.DecorationOverwriteMarkerName;
	private const string HelmetMarkerName = DecorationHostRebuildService.HelmetMergeOverwriteMarkerName;

	public static async ValueTask<CliHostDerivedBuildResult> BuildAsync(
		CliWorkspace workspace,
		CliHostDerivedRequest requested,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(requested);
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot))
			throw new CliUserException("HostDerivedManagerRootRequired", "host-derived 需要 --manager-root <管理器目录>。 ");
		if (string.IsNullOrWhiteSpace(workspace.LibraryRoot))
			throw new CliUserException("LibraryNotConfigured", "host-derived 需要明确的 Mod 库。 ");
		if (string.IsNullOrWhiteSpace(workspace.GameDataRoot))
			throw new CliUserException("HostDerivedGameDataRequired", "host-derived 需要 --game-data <游戏 data 目录>，或可读取的管理器 GameDataFolder 设置。 ");
		if (!Enum.IsDefined(requested.Operation) || !Enum.IsDefined(requested.Action))
			throw new CliUserException("HostDerivedRequestInvalid", "host-derived 请求包含不支持的操作或状态。 ");

		var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
		var paths = new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot);
		var gameData = Path.GetFullPath(workspace.GameDataRoot);
		var diagnostics = new List<CliHostDerivedDiagnostic>();
		var gameDataSnapshot = await CaptureGameDataAsync(paths, gameData, diagnostics, cancellationToken).ConfigureAwait(false);
		var originalActivations = CloneActivations(state.DecorationActivations);
		var plannedActivations = CloneActivations(state.DecorationActivations);
		var decorationCache = new Dictionary<ModNodeId, CliHostDerivedDecorationBuild>();
		var requestedHosts = new HashSet<ModNodeId>();
		var activationUpdates = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);

		switch (requested.Operation)
		{
			case CliHostDerivedOperation.Decoration:
				if (requested.Action is not (CliHostDerivedAction.Enable or CliHostDerivedAction.Disable))
					throw new CliUserException("HostDerivedDecorationActionInvalid", "装饰操作仅支持 enable 或 disable。 ");
				await ApplyDecorationIntentAsync(
					state,
					paths,
					requested,
					originalActivations,
					plannedActivations,
					requestedHosts,
					decorationCache,
					diagnostics,
					cancellationToken).ConfigureAwait(false);
				foreach (var decorationId in requested.DecorationNodeIds
					.Select(selector => CliProjections.ResolveMod(state.Snapshot.Nodes, selector).Id.Value.ToString("N"))
					.Distinct(StringComparer.OrdinalIgnoreCase))
				{
					var before = originalActivations.TryGetValue(decorationId, out var original) ? original : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					var after = plannedActivations.TryGetValue(decorationId, out var planned) ? planned : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					if (!before.SetEquals(after)) activationUpdates[decorationId] = after.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
				}
				break;

			case CliHostDerivedOperation.Helmet:
				if (requested.Action is not (CliHostDerivedAction.Enable or CliHostDerivedAction.Disable))
					throw new CliUserException("HostDerivedHelmetActionInvalid", "临时头盔合并仅支持 enable 或 disable。 ");
				foreach (var selector in requested.HostNodeIds)
				{
					var host = ResolveHost(state.Snapshot, selector);
					if (host.Metadata.HelmetToArmorMerged != (requested.Action == CliHostDerivedAction.Enable))
						requestedHosts.Add(host.Id);
				}
				break;

			case CliHostDerivedOperation.Rebuild:
				if (requested.Action != CliHostDerivedAction.Rebuild)
					throw new CliUserException("HostDerivedRebuildActionInvalid", "重建操作只能使用 rebuild。 ");
				foreach (var selector in requested.HostNodeIds)
					requestedHosts.Add(ResolveHost(state.Snapshot, selector).Id);
				break;
		}

		var hostBuilds = new Dictionary<ModNodeId, CliHostDerivedHostBuild>();
		foreach (var hostId in requestedHosts.OrderBy(id => id.Value))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!state.Snapshot.Nodes.TryGetValue(hostId, out var host)) continue;
			var currentAttachments = await ReadAttachmentsAsync(
				state,
				paths,
				host,
				originalActivations,
				decorationCache,
				diagnostics,
				cancellationToken).ConfigureAwait(false);
			var plannedAttachments = await ReadAttachmentsAsync(
				state,
				paths,
				host,
				plannedActivations,
				decorationCache,
				diagnostics,
				cancellationToken).ConfigureAwait(false);
			var plannedHelmet = requested.Operation == CliHostDerivedOperation.Helmet
				? requested.Action == CliHostDerivedAction.Enable
				: host.Metadata.HelmetToArmorMerged;
			var overwrite = await CaptureOverwriteStateAsync(paths, host, diagnostics, cancellationToken).ConfigureAwait(false);
			if (overwrite.Exists && !overwrite.IsManaged)
			{
				diagnostics.Add(new CliHostDerivedDiagnostic(
					"HostDerivedOverwriteUnmanaged", "error", true,
					"主体已有非管理器生成的 Overwrite；CLI 不会覆盖或删除它。", overwrite.Path, CliProjections.FormatId(host.Id)));
			}
			hostBuilds[host.Id] = new CliHostDerivedHostBuild(
				host,
				currentAttachments.Select(item => item.ToCoreAttachment()).ToArray(),
				plannedAttachments.Select(item => item.ToCoreAttachment()).ToArray(),
				host.Metadata.HelmetToArmorMerged,
				plannedHelmet);
		}

		if (hostBuilds.Count == 0 && requested.Operation != CliHostDerivedOperation.Decoration)
		{
			diagnostics.Add(new CliHostDerivedDiagnostic(
				"HostDerivedNoChanges", "warning", false,
				"请求没有改变任何主体状态。"));
		}
		if (hostBuilds.Count == 0 && requested.Operation == CliHostDerivedOperation.Decoration)
		{
			diagnostics.Add(new CliHostDerivedDiagnostic(
				"HostDerivedNoChanges", "warning", false,
				"所选装饰在目标主体上已经处于请求状态；无需重建。"));
		}

		var request = NormalizeRequest(requested, state.Snapshot, gameData);
		var hostProjection = new List<CliHostDerivedHost>();
		foreach (var build in hostBuilds.Values.OrderBy(item => item.Host.Id.Value))
			hostProjection.Add(await CreateHostProjection(build, decorationCache, paths, cancellationToken).ConfigureAwait(false));
		var inputs = await CaptureInputsAsync(paths, hostBuilds.Values, decorationCache.Values, gameDataSnapshot, cancellationToken).ConfigureAwait(false);
		var target = new CliHostDerivedTarget(Path.GetFullPath(workspace.ManagerRoot), Path.GetFullPath(workspace.LibraryRoot), gameData);
		var normalizedDiagnostics = DistinctDiagnostics(diagnostics);
		var document = new CliHostDerivedPlanDocument(
			CliHostDerivedPlanDocument.CurrentSchema,
			CliHostDerivedPlanDocument.CurrentVersion,
			DateTimeOffset.UtcNow,
			ComputeFingerprint(request, target, hostProjection, inputs, normalizedDiagnostics),
			request,
			target,
			hostProjection,
			inputs,
			normalizedDiagnostics);
		return new CliHostDerivedBuildResult(document, paths, state, hostBuilds, activationUpdates);
	}

	public static async ValueTask<CliHostDerivedPlanDocument> ReadDocumentAsync(string path, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("HostDerivedPlanRequired", "请通过 --plan <主体派生计划.json> 提供计划文件。 ");
		var fullPath = CliOutput.NormalizeDirectoryPath(path, "HostDerivedPlanPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("HostDerivedPlanNotFound", $"主体派生计划不存在：{fullPath}");
		try
		{
			var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
			var document = JsonSerializer.Deserialize<CliHostDerivedPlanDocument>(json, CliOutput.JsonOptions);
			if (document is null)
				throw new CliUserException("HostDerivedPlanUnreadable", "主体派生计划为空或无法解析。 ");
			if (!string.Equals(document.Schema, CliHostDerivedPlanDocument.CurrentSchema, StringComparison.Ordinal)
				|| document.Version != CliHostDerivedPlanDocument.CurrentVersion)
			{
				throw new CliUserException("HostDerivedPlanVersionUnsupported", $"主体派生计划版本不受支持。需要 {CliHostDerivedPlanDocument.CurrentSchema} v{CliHostDerivedPlanDocument.CurrentVersion}。 ");
			}
			if (string.IsNullOrWhiteSpace(document.InputFingerprint)
				|| document.Request is null
				|| document.Target is null
				|| document.Hosts is null
				|| document.Inputs is null
				|| document.Diagnostics is null
				|| string.IsNullOrWhiteSpace(document.Request.GameDataDirectory))
			{
				throw new CliUserException("HostDerivedPlanUnreadable", "主体派生计划缺少请求、目标、主体或输入快照。 ");
			}
			if (!Enum.IsDefined(document.Request.Operation) || !Enum.IsDefined(document.Request.Action))
				throw new CliUserException("HostDerivedPlanUnreadable", "主体派生计划包含未知操作。 ");
			foreach (var host in document.Request.HostNodeIds)
				if (!Guid.TryParse(host, out _)) throw new CliUserException("HostDerivedPlanUnreadable", $"主体计划包含无效主体 ID：{host}");
			foreach (var decoration in document.Request.DecorationNodeIds)
				if (!Guid.TryParse(decoration, out _)) throw new CliUserException("HostDerivedPlanUnreadable", $"主体计划包含无效装饰 ID：{decoration}");
			var declared = ComputeFingerprint(document.Request, document.Target, document.Hosts, document.Inputs, document.Diagnostics);
			if (!string.Equals(declared, document.InputFingerprint, StringComparison.OrdinalIgnoreCase))
				throw new CliUserException("HostDerivedPlanIntegrityMismatch", "主体派生计划的审阅内容或输入快照在导出后被修改；请重新生成计划。 ");
			return document;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("HostDerivedPlanUnreadable", $"无法读取主体派生计划：{exception.Message}");
		}
	}

	public static async ValueTask<CliHostDerivedValidationResult> ValidateAsync(
		CliHostDerivedPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		var diagnostics = new List<CliHostDerivedDiagnostic>();
		CliHostDerivedBuildResult? current = null;
		try
		{
			if (string.IsNullOrWhiteSpace(workspace.GameDataRoot) || !PathsEqual(workspace.GameDataRoot, document.Request.GameDataDirectory))
			{
				diagnostics.Add(new CliHostDerivedDiagnostic("HostDerivedGameDataTargetChanged", "error", true, "当前 --game-data 与计划固定的目标不同；请重新生成计划。"));
			}
			current = await BuildAsync(workspace, document.Request, cancellationToken).ConfigureAwait(false);
			if (!PathsEqual(document.Target.ManagerRoot, current.Document.Target.ManagerRoot)
				|| !PathsEqual(document.Target.LibraryRoot, current.Document.Target.LibraryRoot)
				|| !PathsEqual(document.Target.GameDataDirectory, current.Document.Target.GameDataDirectory))
			{
				diagnostics.Add(new CliHostDerivedDiagnostic("HostDerivedTargetChanged", "error", true, "当前管理器、Mod 库或 GameData 目标与计划不同；请重新生成计划。"));
			}
			if (!string.Equals(document.InputFingerprint, current.Document.InputFingerprint, StringComparison.OrdinalIgnoreCase))
			{
				diagnostics.Add(new CliHostDerivedDiagnostic("HostDerivedInputChanged", "error", true, "主体、装饰、激活状态、Patch、Overwrite 或 GameData 索引输入已变化；请重新生成计划。"));
			}
			diagnostics.AddRange(current.Document.Diagnostics);
		}
		catch (CliUserException exception)
		{
			diagnostics.Add(new CliHostDerivedDiagnostic(exception.Code, "error", true, exception.Message));
		}
		return new CliHostDerivedValidationResult(document, current, DistinctDiagnostics(diagnostics));
	}

	private static async ValueTask ApplyDecorationIntentAsync(
		CliLibraryState state,
		StoragePaths paths,
		CliHostDerivedRequest requested,
		IReadOnlyDictionary<string, HashSet<string>> original,
		IDictionary<string, HashSet<string>> planned,
		ISet<ModNodeId> affectedHosts,
		IDictionary<ModNodeId, CliHostDerivedDecorationBuild> cache,
		ICollection<CliHostDerivedDiagnostic> diagnostics,
		CancellationToken cancellationToken)
	{
		if (requested.DecorationNodeIds.Count == 0)
			throw new CliUserException("HostDerivedDecorationRequired", "装饰操作至少需要一个装饰 Mod GUID 或唯一前缀。 ");
		if (requested.AllDeclaredDecorationHosts && requested.HostNodeIds.Count != 0)
			throw new CliUserException("HostDerivedDecorationTargetAmbiguous", "装饰 enable-all/disable-all 不能同时提供 --host。 ");
		if (!requested.AllDeclaredDecorationHosts && requested.HostNodeIds.Count != 1)
			throw new CliUserException("HostDerivedDecorationHostRequired", "装饰 enable/disable 必须显式提供一个 --host <主体 Mod GUID 或唯一前缀>。 ");

		var explicitHost = requested.AllDeclaredDecorationHosts ? null : ResolveHost(state.Snapshot, requested.HostNodeIds[0]);
		foreach (var selector in requested.DecorationNodeIds)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var decoration = CliProjections.ResolveMod(state.Snapshot.Nodes, selector);
			if (decoration.Metadata.Kind != ModNodeKind.Decoration)
				throw new CliUserException("HostDerivedDecorationKindInvalid", $"Mod “{decoration.Metadata.Name}”不是装饰 Mod。 ");
			var build = await ReadDecorationAsync(state, paths, decoration, cache, diagnostics, cancellationToken).ConfigureAwait(false);
			if (build is null) continue;
			IReadOnlyCollection<ModNodeId> targets = requested.AllDeclaredDecorationHosts
				? build.AvailableHostIds.ToArray()
				: [explicitHost!.Id];
			if (!requested.AllDeclaredDecorationHosts && !build.AvailableHostIds.Contains(explicitHost!.Id))
			{
				diagnostics.Add(new CliHostDerivedDiagnostic(
					"HostDerivedDecorationHostUnsupported", "error", true,
					$"装饰“{decoration.Metadata.Name}”没有声明主体“{explicitHost.Metadata.Name}”。",
					Path.Combine(paths.ModsDirectory, decoration.RelativePath, "decoration.json"),
					CliProjections.FormatId(decoration.Id)));
				continue;
			}
			foreach (var hostId in targets)
			{
				var decorationKey = decoration.Id.Value.ToString("N");
				if (!planned.TryGetValue(decorationKey, out var hosts)) planned[decorationKey] = hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				var before = original.TryGetValue(decorationKey, out var originalHosts) && originalHosts.Contains(hostId.Value.ToString("N"));
				var desired = requested.Action == CliHostDerivedAction.Enable;
				if (desired) hosts.Add(hostId.Value.ToString("N")); else hosts.Remove(hostId.Value.ToString("N"));
				if (before != desired) affectedHosts.Add(hostId);
			}
		}
	}

	private static async ValueTask<IReadOnlyList<CliHostDerivedAttachmentBuild>> ReadAttachmentsAsync(
		CliLibraryState state,
		StoragePaths paths,
		ModNode host,
		IReadOnlyDictionary<string, HashSet<string>> activations,
		IDictionary<ModNodeId, CliHostDerivedDecorationBuild> cache,
		ICollection<CliHostDerivedDiagnostic> diagnostics,
		CancellationToken cancellationToken)
	{
		var hostKey = host.Id.Value.ToString("N");
		var result = new List<CliHostDerivedAttachmentBuild>();
		foreach (var (rawDecorationId, hosts) in activations.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
		{
			if (!hosts.Contains(hostKey)) continue;
			if (!Guid.TryParse(rawDecorationId, out var rawId) || !state.Snapshot.Nodes.TryGetValue(new ModNodeId(rawId), out var decoration))
			{
				diagnostics.Add(new CliHostDerivedDiagnostic("HostDerivedStaleDecorationActivation", "warning", false, "发现指向不存在装饰的启用状态；重建会忽略该过期引用。", NodeId: rawDecorationId));
				continue;
			}
			if (decoration.Metadata.Kind != ModNodeKind.Decoration) continue;
			var build = await ReadDecorationAsync(state, paths, decoration, cache, diagnostics, cancellationToken).ConfigureAwait(false);
			if (build is null || !build.AvailableHostIds.Contains(host.Id))
			{
				diagnostics.Add(new CliHostDerivedDiagnostic("HostDerivedStaleDecorationHost", "warning", false, "发现装饰启用状态不再指向有效主体；重建会忽略该过期引用。", NodeId: CliProjections.FormatId(decoration.Id)));
				continue;
			}
			result.Add(new CliHostDerivedAttachmentBuild(build));
		}
		return result.OrderBy(item => item.Decoration.Node.Metadata.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Decoration.Node.Id.Value).ToArray();
	}

	private static async ValueTask<CliHostDerivedDecorationBuild?> ReadDecorationAsync(
		CliLibraryState state,
		StoragePaths paths,
		ModNode decoration,
		IDictionary<ModNodeId, CliHostDerivedDecorationBuild> cache,
		ICollection<CliHostDerivedDiagnostic> diagnostics,
		CancellationToken cancellationToken)
	{
		if (cache.TryGetValue(decoration.Id, out var known)) return known;
		var directory = ResolveNodeDirectory(paths, decoration);
		var planPath = Path.Combine(directory, "decoration.json");
		if (!File.Exists(planPath))
		{
			diagnostics.Add(new CliHostDerivedDiagnostic("HostDerivedDecorationPlanMissing", "error", true, "装饰 Mod 缺少 decoration.json，不能安全重建主体。", planPath, CliProjections.FormatId(decoration.Id)));
			return null;
		}
		DecorationPlanDocument plan;
		try
		{
			plan = JsonSerializer.Deserialize<DecorationPlanDocument>(await File.ReadAllTextAsync(planPath, cancellationToken).ConfigureAwait(false), CliOutput.JsonOptions)
				?? throw new InvalidDataException("decoration.json 为空或无法解析。");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
		{
			diagnostics.Add(new CliHostDerivedDiagnostic("HostDerivedDecorationPlanUnreadable", "error", true, $"无法读取 decoration.json：{exception.Message}", planPath, CliProjections.FormatId(decoration.Id), exception.ToString()));
			return null;
		}
		if (plan.Plan is null)
		{
			diagnostics.Add(new CliHostDerivedDiagnostic("HostDerivedDecorationAttachmentMissing", "error", true, "decoration.json 缺少主体附着策略。", planPath, CliProjections.FormatId(decoration.Id)));
			return null;
		}
		if (decoration.PatchGroups.Count == 0)
		{
			diagnostics.Add(new CliHostDerivedDiagnostic("HostDerivedDecorationPatchMissing", "error", true, "装饰 Mod 没有可用于重建的 Patch 组。", planPath, CliProjections.FormatId(decoration.Id)));
			return null;
		}
		var available = new HashSet<ModNodeId>();
		foreach (var rawHostId in plan.Plan.TargetModGuids ?? [])
		{
			if (!Guid.TryParse(rawHostId, out var parsed))
			{
				diagnostics.Add(new CliHostDerivedDiagnostic("HostDerivedDecorationDeclaredHostInvalid", "warning", false, $"装饰声明了无效主体 ID：{rawHostId}。", planPath, CliProjections.FormatId(decoration.Id)));
				continue;
			}
			var hostId = new ModNodeId(parsed);
			if (state.Snapshot.Nodes.TryGetValue(hostId, out var host) && IsHostNode(host)) available.Add(hostId);
		}
		if (available.Count == 0)
		{
			diagnostics.Add(new CliHostDerivedDiagnostic("HostDerivedDecorationHostsMissing", "error", true, "装饰没有任何当前可用的带 Patch 主体。", planPath, CliProjections.FormatId(decoration.Id)));
			return null;
		}
		var bytes = await File.ReadAllBytesAsync(planPath, cancellationToken).ConfigureAwait(false);
		var result = new CliHostDerivedDecorationBuild(decoration, plan, available, Convert.ToHexString(SHA256.HashData(bytes)));
		cache[decoration.Id] = result;
		return result;
	}

	private static async ValueTask<CliHostDerivedHost> CreateHostProjection(
		CliHostDerivedHostBuild build,
		IReadOnlyDictionary<ModNodeId, CliHostDerivedDecorationBuild> decorationCache,
		StoragePaths paths,
		CancellationToken cancellationToken)
	{
		var overwrite = await CaptureOverwriteStateAsync(paths, build.Host, diagnostics: null, cancellationToken).ConfigureAwait(false);
		return new CliHostDerivedHost(
			CliProjections.FormatId(build.Host.Id),
			build.Host.Metadata.Name,
			build.Host.Metadata.Kind.ToString().ToLowerInvariant(),
			build.Host.RelativePath,
			build.Host.PatchGroups.Count,
			build.CurrentHelmetMerged,
			build.PlannedHelmetMerged,
			build.CurrentAttachments.Select(attachment => CreateAttachmentProjection(attachment, decorationCache)).ToArray(),
			build.PlannedAttachments.Select(attachment => CreateAttachmentProjection(attachment, decorationCache)).ToArray(),
			overwrite.State);
	}

	private static CliHostDerivedDecorationAttachment CreateAttachmentProjection(
		DecorationHostRebuildAttachment attachment,
		IReadOnlyDictionary<ModNodeId, CliHostDerivedDecorationBuild> cache)
	{
		var match = cache.Values.First(item => string.Equals(item.Node.Id.Value.ToString("N"), attachment.DecorationId, StringComparison.OrdinalIgnoreCase));
		return new CliHostDerivedDecorationAttachment(
			CliProjections.FormatId(match.Node.Id),
			match.Node.Metadata.Name,
			match.Node.RelativePath,
			match.Plan.SourceStorageMode,
			match.AvailableHostIds.OrderBy(id => id.Value).Select(CliProjections.FormatId).ToArray(),
			match.PlanSha256);
	}

	private static async ValueTask<CliHostDerivedInputState> CaptureInputsAsync(
		StoragePaths paths,
		IEnumerable<CliHostDerivedHostBuild> hosts,
		IEnumerable<CliHostDerivedDecorationBuild> decorations,
		CliHostDerivedGameDataSnapshot gameData,
		CancellationToken cancellationToken)
	{
		var sourceDirectories = new List<CliHostDerivedDirectorySnapshot>();
		foreach (var host in hosts.Select(item => item.Host).OrderBy(item => item.Id.Value))
			sourceDirectories.Add(await CaptureDirectoryAsync(paths, host, "host-source", excludeOverwrite: true, cancellationToken).ConfigureAwait(false));
		foreach (var decoration in decorations.OrderBy(item => item.Node.Id.Value))
			sourceDirectories.Add(await CaptureDirectoryAsync(paths, decoration.Node, "decoration-source", excludeOverwrite: false, cancellationToken).ConfigureAwait(false));
		var overwriteDirectories = new List<CliHostDerivedDirectorySnapshot>();
		foreach (var host in hosts.Select(item => item.Host).OrderBy(item => item.Id.Value))
			overwriteDirectories.Add(await CaptureOverwriteDirectoryAsync(paths, host, cancellationToken).ConfigureAwait(false));
		return new CliHostDerivedInputState(
			await CaptureFileAsync(paths.LibraryPath, cancellationToken).ConfigureAwait(false),
			await CaptureFileAsync(paths.DecorationActivationsPath, cancellationToken).ConfigureAwait(false),
			gameData,
			sourceDirectories,
			overwriteDirectories);
	}

	private static async ValueTask<CliHostDerivedGameDataSnapshot> CaptureGameDataAsync(
		StoragePaths paths,
		string gameData,
		ICollection<CliHostDerivedDiagnostic> diagnostics,
		CancellationToken cancellationToken)
	{
		var target = new GameDataTargetGuard().Validate(gameData, cancellationToken);
		foreach (var issue in target.Issues)
			diagnostics.Add(CreateDiagnostic(issue, "game-data"));
		if (!target.IsValid || string.IsNullOrWhiteSpace(target.GameDataDirectory))
			return new CliHostDerivedGameDataSnapshot(gameData, "invalid", false, string.Empty, null);
		var archiveHashes = File.Exists(paths.ArchiveHashesPath)
			? await File.ReadAllTextAsync(paths.ArchiveHashesPath, cancellationToken).ConfigureAwait(false)
			: "{}";
		var status = await CoreServices.CreateAssetArchiveIndexService(paths).GetIndexStatusAsync(target.GameDataDirectory, archiveHashes, cancellationToken).ConfigureAwait(false);
		if (!status.IsCurrent)
		{
			diagnostics.Add(new CliHostDerivedDiagnostic("HostDerivedGameDataIndexNotCurrent", "error", true, "当前 GameData 资产索引不可用或已过期；请先在管理器设置页重建资产索引。", target.GameDataDirectory));
		}
		return new CliHostDerivedGameDataSnapshot(status.GameDataDirectory, status.State.ToString().ToLowerInvariant(), status.IsCurrent, status.CurrentSourceFingerprint, status.StoredFingerprint?.SourceFingerprint);
	}

	private static async ValueTask<(CliHostDerivedOverwriteState State, bool Exists, bool IsManaged, string? Path)> CaptureOverwriteStateAsync(
		StoragePaths paths,
		ModNode host,
		ICollection<CliHostDerivedDiagnostic>? diagnostics,
		CancellationToken cancellationToken)
	{
		var directory = Path.Combine(ResolveNodeDirectory(paths, host), "Overwrite");
		if (!Directory.Exists(directory))
		{
			return (new CliHostDerivedOverwriteState(false, false, false, false, 0, 0, null), false, false, directory);
		}
		var decorationMarker = File.Exists(Path.Combine(directory, DecorationMarkerName));
		var helmetMarker = File.Exists(Path.Combine(directory, HelmetMarkerName));
		var snapshot = await HashDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
		return (new CliHostDerivedOverwriteState(true, decorationMarker || helmetMarker, decorationMarker, helmetMarker, snapshot.FileCount, snapshot.TotalBytes, snapshot.Sha256), true, decorationMarker || helmetMarker, directory);
	}

	private static async ValueTask<CliHostDerivedDirectorySnapshot> CaptureDirectoryAsync(StoragePaths paths, ModNode node, string role, bool excludeOverwrite, CancellationToken cancellationToken)
	{
		var directory = ResolveNodeDirectory(paths, node);
		if (!Directory.Exists(directory)) return new CliHostDerivedDirectorySnapshot(CliProjections.FormatId(node.Id), role, node.RelativePath, false, false, 0, 0, null);
		IReadOnlySet<string>? excluded = excludeOverwrite
			? new HashSet<string>(["Overwrite"], StringComparer.OrdinalIgnoreCase)
			: null;
		var snapshot = await HashDirectoryAsync(directory, cancellationToken, excluded).ConfigureAwait(false);
		return new CliHostDerivedDirectorySnapshot(CliProjections.FormatId(node.Id), role, node.RelativePath, true, false, snapshot.FileCount, snapshot.TotalBytes, snapshot.Sha256);
	}

	private static async ValueTask<CliHostDerivedDirectorySnapshot> CaptureOverwriteDirectoryAsync(StoragePaths paths, ModNode host, CancellationToken cancellationToken)
	{
		var overwrite = await CaptureOverwriteStateAsync(paths, host, diagnostics: null, cancellationToken).ConfigureAwait(false);
		return new CliHostDerivedDirectorySnapshot(CliProjections.FormatId(host.Id), "overwrite", Path.Combine(host.RelativePath, "Overwrite"), overwrite.Exists, overwrite.IsManaged, overwrite.State.FileCount, overwrite.State.TotalBytes, overwrite.State.Sha256);
	}

	private static async ValueTask<CliHostDerivedFileSnapshot> CaptureFileAsync(string path, CancellationToken cancellationToken)
	{
		if (!File.Exists(path)) return new CliHostDerivedFileSnapshot(Path.GetFileName(path), false, 0, null);
		var info = new FileInfo(path);
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
		var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
		return new CliHostDerivedFileSnapshot(Path.GetFileName(path), true, info.Length, Convert.ToHexString(hash));
	}

	private static async ValueTask<(int FileCount, long TotalBytes, string Sha256)> HashDirectoryAsync(string root, CancellationToken cancellationToken, IReadOnlySet<string>? excludedTopDirectories = null)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		var files = 0;
		long total = 0;
		var pending = new Stack<string>();
		pending.Push(root);
		while (pending.Count != 0)
		{
			var directory = pending.Pop();
			foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
				hash.AppendData(Encoding.UTF8.GetBytes(relative));
				hash.AppendData([0]);
				var info = new FileInfo(path);
				await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
				hash.AppendData(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
				files++;
				total = checked(total + info.Length);
			}
			foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).OrderByDescending(item => item, StringComparer.OrdinalIgnoreCase))
			{
				var relative = Path.GetRelativePath(root, child).Replace('\\', '/');
				if (relative.IndexOf('/') < 0 && excludedTopDirectories?.Contains(Path.GetFileName(child)) == true) continue;
				if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
					throw new InvalidDataException($"目录包含重解析点，不能安全冻结：{child}");
				pending.Push(child);
			}
		}
		return (files, total, Convert.ToHexString(hash.GetHashAndReset()));
	}

	private static CliHostDerivedRequest NormalizeRequest(CliHostDerivedRequest requested, LibrarySnapshot snapshot, string gameData)
		=> requested with
		{
			HostNodeIds = requested.HostNodeIds.Select(selector => CliProjections.FormatId(ResolveHost(snapshot, selector).Id)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
			DecorationNodeIds = requested.DecorationNodeIds.Select(selector => CliProjections.FormatId(CliProjections.ResolveMod(snapshot.Nodes, selector).Id)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
			GameDataDirectory = Path.GetFullPath(gameData),
		};

	private static ModNode ResolveHost(LibrarySnapshot snapshot, string selector)
	{
		var host = CliProjections.ResolveMod(snapshot.Nodes, selector);
		if (!IsHostNode(host))
			throw new CliUserException("HostDerivedHostInvalid", $"Mod “{host.Metadata.Name}”不是带 Patch 的普通 Mod 或选项 Mod，不能承载派生 Overwrite。 ");
		return host;
	}

	private static bool IsHostNode(ModNode node)
		=> node.PatchGroups.Count != 0 && node.Metadata.Kind is ModNodeKind.Standard or ModNodeKind.Option;

	private static Dictionary<string, HashSet<string>> CloneActivations(IReadOnlyDictionary<string, IReadOnlySet<string>> source)
		=> source.ToDictionary(item => item.Key, item => new HashSet<string>(item.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

	private static string ResolveNodeDirectory(StoragePaths paths, ModNode node)
	{
		var root = Path.GetFullPath(paths.ModsDirectory);
		var directory = Path.GetFullPath(Path.Combine(root, node.RelativePath));
		if (!IsWithin(root, directory)) throw new InvalidDataException($"Mod 的库内相对路径无效：{node.RelativePath}");
		return directory;
	}

	private static CliHostDerivedDiagnostic CreateDiagnostic(CoreIssue issue, string stage)
		=> new(issue.Code, issue.Severity == CoreIssueSeverity.Error ? "error" : "warning", issue.Severity == CoreIssueSeverity.Error, issue.Message, issue.FilePath, issue.NodeId is { } nodeId ? CliProjections.FormatId(nodeId) : null, string.IsNullOrWhiteSpace(issue.ExceptionMessage) ? stage : issue.ExceptionMessage);

	private static string ComputeFingerprint(CliHostDerivedRequest request, CliHostDerivedTarget target, IReadOnlyList<CliHostDerivedHost> hosts, CliHostDerivedInputState inputs, IReadOnlyList<CliHostDerivedDiagnostic> diagnostics)
		=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CliOutput.ToJson(new { request, target, hosts, inputs, diagnostics }))));

	private static IReadOnlyList<CliHostDerivedDiagnostic> DistinctDiagnostics(IEnumerable<CliHostDerivedDiagnostic> diagnostics)
		=> diagnostics
			.GroupBy(item => $"{item.Code}\u001f{item.Path}\u001f{item.NodeId}\u001f{item.Message}", StringComparer.Ordinal)
			.Select(group => group.OrderByDescending(item => item.Blocking).First())
			.OrderByDescending(item => item.Blocking)
			.ThenBy(item => item.Code, StringComparer.Ordinal)
			.ToArray();

	private static bool PathsEqual(string left, string right)
	{
		try { return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
	}

	private static bool IsWithin(string root, string candidate)
	{
		var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase)
			|| normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
			|| normalizedCandidate.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}

	private sealed record CliHostDerivedDecorationBuild(ModNode Node, DecorationPlanDocument Plan, IReadOnlySet<ModNodeId> AvailableHostIds, string PlanSha256);

	private sealed record CliHostDerivedAttachmentBuild(CliHostDerivedDecorationBuild Decoration)
	{
		public DecorationHostRebuildAttachment ToCoreAttachment()
			=> new(Decoration.Node.Id.Value.ToString("N"), Decoration.Node.RelativePath, Decoration.Plan);
	}
}
