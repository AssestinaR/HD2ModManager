using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// Controls what profile alignment does with the active-profile pointer. The
/// generated specification defaults to Preserve so an Agent must make any
/// activity change explicit and reviewable.
/// </summary>
public enum CliProfileAlignActiveMode
{
	Preserve,
	SetActive,
	ClearIfActive,
}

/// <summary>
/// Small, deliberately editable intent document. It is exported from a real
/// Profile, then an Agent may change only the final logical member order and
/// active-mode before turning it into a locked execution plan.
/// </summary>
public sealed record CliProfileAlignSpecDocument(
	string Schema,
	int Version,
	string ProfileId,
	string ExpectedProfileName,
	IReadOnlyList<string> EntryNodeIds,
	CliProfileAlignActiveMode ActiveMode)
{
	public const string CurrentSchema = "hd2mod.profile-align-spec";
	public const int CurrentVersion = 1;
}

public sealed record CliProfileAlignRequest(
	string ProfileId,
	string ExpectedProfileName,
	IReadOnlyList<string> EntryNodeIds,
	CliProfileAlignActiveMode ActiveMode);

public sealed record CliProfileAlignTarget(
	string ManagerRoot,
	string LibraryRoot);

/// <summary>
/// Semantic Profile facts. Timestamps are intentionally absent: they are not
/// desired-state semantics and would make an already-aligned no-op appear to
/// drift merely because a previous mutation created a new ModifiedUtc value.
/// </summary>
public sealed record CliProfileAlignProfileState(
	string ProfileId,
	string Name,
	long Revision,
	IReadOnlyList<string> EntryNodeIds,
	string? ActiveProfileId);

/// <summary>
/// Stable identity for every current or requested member participating in an
/// alignment. This freezes deployability without hashing unrelated library
/// nodes, so a harmless metadata change elsewhere does not invalidate a plan.
/// </summary>
public sealed record CliProfileAlignNodeIdentity(
	string NodeId,
	bool Exists,
	string? Name,
	string? Kind,
	string? RelativePath,
	IReadOnlyList<string> PatchGroups,
	bool IsDeployable);

public sealed record CliProfileAlignChangeSummary(
	bool EntriesChanged,
	bool ActiveProfileChanged,
	bool RequiresWrite,
	int CurrentEntryCount,
	int TargetEntryCount);

public sealed record CliProfileAlignDiagnostic(
	string Code,
	string Severity,
	bool Blocking,
	string Message,
	string? Details = null);

/// <summary>
/// A frozen Profile-only mutation. It never authorizes a broad library write:
/// apply may modify profiles.json only after revalidating the exact target
/// Profile semantics and target Mod identities.
/// </summary>
public sealed record CliProfileAlignPlanDocument(
	string Schema,
	int Version,
	DateTimeOffset CreatedUtc,
	string DocumentFingerprint,
	CliProfileAlignRequest Request,
	CliProfileAlignTarget Target,
	string BeforeFingerprint,
	string AfterFingerprint,
	CliProfileAlignProfileState Before,
	CliProfileAlignProfileState After,
	IReadOnlyList<CliProfileAlignNodeIdentity> Nodes,
	CliProfileAlignChangeSummary Changes,
	IReadOnlyList<CliProfileAlignDiagnostic> Diagnostics)
{
	public const string CurrentSchema = "hd2mod.profile-align-plan";
	public const int CurrentVersion = 1;

	public bool IsReady => !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

internal sealed record CliProfileAlignPlanBuildResult(
	CliProfileAlignPlanDocument Document,
	CliLibraryState State,
	IReadOnlyList<ModNodeId> TargetEntryIds);

internal enum CliProfileAlignValidationStatus
{
	Ready,
	AlreadyAligned,
	Drifted,
}

internal sealed record CliProfileAlignValidationResult(
	CliProfileAlignPlanDocument Document,
	CliProfileAlignPlanBuildResult? Current,
	CliProfileAlignValidationStatus Status,
	IReadOnlyList<CliProfileAlignDiagnostic> Diagnostics)
{
	public bool IsValid => Status is CliProfileAlignValidationStatus.Ready or CliProfileAlignValidationStatus.AlreadyAligned;
	public bool RequiresWrite => Status == CliProfileAlignValidationStatus.Ready;
}

/// <summary>
/// Builds and checks only Profile alignment intent. The planning phase uses
/// Core's pure ProfileMutationService so the CLI and desktop application share
/// deployability and ordering semantics exactly.
/// </summary>
internal static class CliProfileAlignPlanArtifacts
{
	public static async ValueTask<CliProfileAlignSpecDocument> ExportSpecAsync(
		CliWorkspace workspace,
		string profileSelector,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		if (string.IsNullOrWhiteSpace(profileSelector))
			throw new CliUserException("ProfileAlignProfileRequired", "profile align export 需要一个 Profile GUID、唯一前缀、名称或 active。 ");

		var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
		var profile = CliProjections.ResolveProfile(state.Snapshot.Profiles, state.Snapshot.ActiveProfileId, profileSelector);
		return new CliProfileAlignSpecDocument(
			CliProfileAlignSpecDocument.CurrentSchema,
			CliProfileAlignSpecDocument.CurrentVersion,
			CliProjections.FormatId(profile.Id),
			profile.Name,
			OrderEntries(profile).Select(entry => CliProjections.FormatId(entry.NodeId)).ToArray(),
			CliProfileAlignActiveMode.Preserve);
	}

	public static async ValueTask<CliProfileAlignSpecDocument> ReadSpecAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		var fullPath = NormalizeFilePath(path, "ProfileAlignSpecPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("ProfileAlignSpecNotFound", $"Profile 对齐规格不存在：{fullPath}");
		try
		{
			var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
			var document = JsonSerializer.Deserialize<CliProfileAlignSpecDocument>(json, CliOutput.JsonOptions);
			if (document is null)
				throw new CliUserException("ProfileAlignSpecUnreadable", "Profile 对齐规格为空或无法解析。 ");
			return NormalizeSpec(document);
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("ProfileAlignSpecUnreadable", $"无法读取 Profile 对齐规格：{exception.Message}");
		}
	}

	public static ValueTask<CliProfileAlignPlanBuildResult> BuildAsync(
		CliWorkspace workspace,
		CliProfileAlignSpecDocument specification,
		CancellationToken cancellationToken = default)
		=> BuildAsync(workspace, CreateRequest(specification), cancellationToken);

	public static async ValueTask<CliProfileAlignPlanBuildResult> BuildAsync(
		CliWorkspace workspace,
		CliProfileAlignRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		var normalizedRequest = NormalizeRequest(request);
		EnsureWritableTargetCanBeFrozen(workspace);

		var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
		var profileId = ParseProfileId(normalizedRequest.ProfileId, "ProfileAlignProfileIdInvalid");
		var profile = state.Snapshot.Profiles.FirstOrDefault(item => item.Id == profileId)
			?? throw new CliUserException("ProfileAlignProfileNotFound", $"指定的 Profile 不存在：{normalizedRequest.ProfileId}");
		var targetEntryIds = normalizedRequest.EntryNodeIds
			.Select(raw => new ModNodeId(ParseGuid(raw, "ProfileAlignEntryIdInvalid", "Mod ID")))
			.ToArray();
		var before = CreateProfileState(state.Snapshot, profile.Id);
		var participatingIds = OrderEntries(profile)
			.Select(entry => entry.NodeId)
			.Concat(targetEntryIds)
			.Distinct()
			.ToArray();
		var nodes = CreateNodeIdentities(state.Snapshot, participatingIds);
		var diagnostics = new List<CliProfileAlignDiagnostic>();

		if (!string.Equals(profile.Name, normalizedRequest.ExpectedProfileName, StringComparison.Ordinal))
		{
			diagnostics.Add(new CliProfileAlignDiagnostic(
				"ProfileAlignExpectedNameChanged", "error", true,
				$"Profile 名称已从审阅值“{normalizedRequest.ExpectedProfileName}”变为“{profile.Name}”；请重新导出并审阅规格。"));
		}

		foreach (var identity in nodes.Where(item => !item.Exists))
		{
			var referencedByTarget = normalizedRequest.EntryNodeIds.Any(id => string.Equals(id, identity.NodeId, StringComparison.OrdinalIgnoreCase));
			diagnostics.Add(new CliProfileAlignDiagnostic(
				referencedByTarget ? "ProfileAlignTargetNodeMissing" : "ProfileAlignCurrentNodeMissing",
				"error", true,
				referencedByTarget
					? $"目标成员 Mod 已不存在：{identity.NodeId}。"
					: $"当前 Profile 成员 Mod 已不存在：{identity.NodeId}。"));
		}

		var service = CoreServices.CreateProfileMutationService();
		var entryMutation = service.ReplaceEntries(state.Snapshot, profile.Id, targetEntryIds);
		if (!entryMutation.Succeeded)
		{
			diagnostics.Add(new CliProfileAlignDiagnostic(
				"ProfileAlignEntriesRejected", "error", true,
				entryMutation.Message ?? $"Profile 成员替换被 Core 拒绝：{entryMutation.Failure?.ToString() ?? "未知原因"}。",
				entryMutation.Failure?.ToString()));
		}

		var finalSnapshot = entryMutation.Succeeded ? entryMutation.Snapshot : state.Snapshot;
		var activeMutation = entryMutation.Succeeded
			? service.SetActiveProfile(finalSnapshot, ResolveTargetActiveProfile(state.Snapshot, profile.Id, normalizedRequest.ActiveMode))
			: ProfileMutationResultNoChange(finalSnapshot);
		if (!activeMutation.Succeeded)
		{
			diagnostics.Add(new CliProfileAlignDiagnostic(
				"ProfileAlignActiveModeRejected", "error", true,
				activeMutation.Message ?? $"Profile 活动状态变更被 Core 拒绝：{activeMutation.Failure?.ToString() ?? "未知原因"}。",
				activeMutation.Failure?.ToString()));
		}
		if (activeMutation.Succeeded)
			finalSnapshot = activeMutation.Snapshot;

		var after = CreateProfileState(finalSnapshot, profile.Id);
		var changes = new CliProfileAlignChangeSummary(
			entryMutation.Changed,
			activeMutation.Changed,
			entryMutation.Changed || activeMutation.Changed,
			before.EntryNodeIds.Count,
			normalizedRequest.EntryNodeIds.Count);
		var target = new CliProfileAlignTarget(
			Path.GetFullPath(workspace.ManagerRoot!),
			Path.GetFullPath(workspace.LibraryRoot!));
		var beforeFingerprint = ComputeSemanticFingerprint(before, nodes);
		var afterFingerprint = ComputeSemanticFingerprint(after, nodes);
		var provisional = new CliProfileAlignPlanDocument(
			CliProfileAlignPlanDocument.CurrentSchema,
			CliProfileAlignPlanDocument.CurrentVersion,
			DateTimeOffset.UtcNow,
			string.Empty,
			normalizedRequest,
			target,
			beforeFingerprint,
			afterFingerprint,
			before,
			after,
			nodes,
			changes,
			DistinctDiagnostics(diagnostics));
		var document = provisional with { DocumentFingerprint = ComputeDocumentFingerprint(provisional) };
		return new CliProfileAlignPlanBuildResult(document, state, targetEntryIds);
	}

	public static async ValueTask<CliProfileAlignPlanDocument> ReadDocumentAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		var fullPath = NormalizeFilePath(path, "ProfileAlignPlanPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("ProfileAlignPlanNotFound", $"Profile 对齐计划不存在：{fullPath}");
		try
		{
			var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
			var document = JsonSerializer.Deserialize<CliProfileAlignPlanDocument>(json, CliOutput.JsonOptions);
			if (document is null)
				throw new CliUserException("ProfileAlignPlanUnreadable", "Profile 对齐计划为空或无法解析。 ");
			EnsureDocumentShape(document);
			var calculated = ComputeDocumentFingerprint(document);
			if (!string.Equals(document.DocumentFingerprint, calculated, StringComparison.OrdinalIgnoreCase))
			{
				throw new CliUserException(
					"ProfileAlignPlanIntegrityMismatch",
					"Profile 对齐计划的审阅内容在导出后被修改；请从规格重新生成计划并再次确认。 ");
			}
			if (!string.Equals(document.BeforeFingerprint, ComputeSemanticFingerprint(document.Before, document.Nodes), StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(document.AfterFingerprint, ComputeSemanticFingerprint(document.After, document.Nodes), StringComparison.OrdinalIgnoreCase))
			{
				throw new CliUserException(
					"ProfileAlignPlanIntegrityMismatch",
					"Profile 对齐计划的语义指纹与冻结状态不一致；请重新生成计划。 ");
			}
			return document;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("ProfileAlignPlanUnreadable", $"无法读取 Profile 对齐计划：{exception.Message}");
		}
	}

	public static async ValueTask<CliProfileAlignValidationResult> ValidateAsync(
		CliProfileAlignPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);
		var diagnostics = new List<CliProfileAlignDiagnostic>();
		CliProfileAlignPlanBuildResult? current = null;
		try
		{
			current = await BuildAsync(workspace, document.Request, cancellationToken).ConfigureAwait(false);
			if (!PathsEqual(document.Target.ManagerRoot, current.Document.Target.ManagerRoot)
				|| !PathsEqual(document.Target.LibraryRoot, current.Document.Target.LibraryRoot))
			{
				diagnostics.Add(new CliProfileAlignDiagnostic(
					"ProfileAlignTargetChanged", "error", true,
					"当前管理器或 Mod 库目标与 Profile 对齐计划不同；请重新生成计划。"));
			}

			if (current.Document.IsReady && IsAlreadyAligned(document, current.State.Snapshot))
			{
				return new CliProfileAlignValidationResult(
					document,
					current,
					CliProfileAlignValidationStatus.AlreadyAligned,
					DistinctDiagnostics(diagnostics));
			}

			if (!string.Equals(document.BeforeFingerprint, current.Document.BeforeFingerprint, StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(document.AfterFingerprint, current.Document.AfterFingerprint, StringComparison.OrdinalIgnoreCase))
			{
				diagnostics.Add(new CliProfileAlignDiagnostic(
					"ProfileAlignInputChanged", "error", true,
					"目标 Profile 的名称、版本、逻辑成员、活动状态或参与 Mod 的部署身份已变化；请重新生成计划并重新确认。"));
			}
			diagnostics.AddRange(current.Document.Diagnostics);
		}
		catch (CliUserException exception)
		{
			diagnostics.Add(new CliProfileAlignDiagnostic(exception.Code, "error", true, exception.Message));
		}

		var finalized = DistinctDiagnostics(diagnostics);
		return new CliProfileAlignValidationResult(
			document,
			current,
			finalized.Any(diagnostic => diagnostic.Blocking)
				? CliProfileAlignValidationStatus.Drifted
				: CliProfileAlignValidationStatus.Ready,
			finalized);
	}

	internal static CliProfileAlignProfileState CreateProfileState(LibrarySnapshot snapshot, ProfileId profileId)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		var profile = snapshot.Profiles.FirstOrDefault(item => item.Id == profileId)
			?? throw new CliUserException("ProfileAlignProfileNotFound", $"指定的 Profile 已不存在：{CliProjections.FormatId(profileId)}");
		return new CliProfileAlignProfileState(
			CliProjections.FormatId(profile.Id),
			profile.Name,
			profile.Revision,
			OrderEntries(profile).Select(entry => CliProjections.FormatId(entry.NodeId)).ToArray(),
			snapshot.ActiveProfileId is { } active ? CliProjections.FormatId(active) : null);
	}

	internal static IReadOnlyList<CliProfileAlignNodeIdentity> CreateNodeIdentities(
		LibrarySnapshot snapshot,
		IEnumerable<ModNodeId> ids)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(ids);
		return ids
			.Distinct()
			.OrderBy(id => id.Value)
			.Select(id => snapshot.Nodes.TryGetValue(id, out var node)
				? new CliProfileAlignNodeIdentity(
					CliProjections.FormatId(id),
					true,
					node.Metadata.Name,
					node.Metadata.Kind.ToString().ToLowerInvariant(),
					node.RelativePath,
					node.PatchGroups
						.OrderBy(group => group.ArchiveHex16, StringComparer.OrdinalIgnoreCase)
						.ThenBy(group => group.PatchIndex)
						.Select(group => $"{group.ArchiveHex16.ToLowerInvariant()}:patch_{group.PatchIndex}")
						.ToArray(),
					IsDeployable(node))
				: new CliProfileAlignNodeIdentity(
					CliProjections.FormatId(id),
					false,
					null,
					null,
					null,
					Array.Empty<string>(),
					false))
			.ToArray();
	}

	internal static bool MatchesExpectedAfter(
		CliProfileAlignPlanDocument document,
		LibrarySnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(snapshot);
		var profileId = ParseProfileId(document.Request.ProfileId, "ProfileAlignPlanUnreadable");
		var current = CreateProfileState(snapshot, profileId);
		var ids = document.Nodes
			.Select(identity => new ModNodeId(ParseGuid(identity.NodeId, "ProfileAlignPlanUnreadable", "Mod ID")))
			.ToArray();
		var nodes = CreateNodeIdentities(snapshot, ids);
		return ProfileStateEqualsIgnoringRevision(document.After, current)
			&& NodeIdentitiesEqual(document.Nodes, nodes);
	}

	private static CliProfileAlignSpecDocument NormalizeSpec(CliProfileAlignSpecDocument specification)
	{
		ArgumentNullException.ThrowIfNull(specification);
		if (!string.Equals(specification.Schema, CliProfileAlignSpecDocument.CurrentSchema, StringComparison.Ordinal)
			|| specification.Version != CliProfileAlignSpecDocument.CurrentVersion)
		{
			throw new CliUserException(
				"ProfileAlignSpecVersionUnsupported",
				$"Profile 对齐规格版本不受支持。需要 {CliProfileAlignSpecDocument.CurrentSchema} v{CliProfileAlignSpecDocument.CurrentVersion}。 ");
		}
		return new CliProfileAlignSpecDocument(
			CliProfileAlignSpecDocument.CurrentSchema,
			CliProfileAlignSpecDocument.CurrentVersion,
			CliProjections.FormatId(ParseProfileId(specification.ProfileId, "ProfileAlignProfileIdInvalid")),
			NormalizeExpectedName(specification.ExpectedProfileName),
			NormalizeNodeIds(specification.EntryNodeIds),
			ValidateActiveMode(specification.ActiveMode));
	}

	private static CliProfileAlignRequest CreateRequest(CliProfileAlignSpecDocument specification)
	{
		var normalized = NormalizeSpec(specification);
		return new CliProfileAlignRequest(
			normalized.ProfileId,
			normalized.ExpectedProfileName,
			normalized.EntryNodeIds,
			normalized.ActiveMode);
	}

	private static CliProfileAlignRequest NormalizeRequest(CliProfileAlignRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		return new CliProfileAlignRequest(
			CliProjections.FormatId(ParseProfileId(request.ProfileId, "ProfileAlignProfileIdInvalid")),
			NormalizeExpectedName(request.ExpectedProfileName),
			NormalizeNodeIds(request.EntryNodeIds),
			ValidateActiveMode(request.ActiveMode));
	}

	private static IReadOnlyList<string> NormalizeNodeIds(IReadOnlyList<string>? nodeIds)
	{
		if (nodeIds is null)
			throw new CliUserException("ProfileAlignEntriesMissing", "Profile 对齐规格缺少 entryNodeIds。空列表必须显式写为 []。 ");
		var normalized = nodeIds
			.Select(raw => CliProjections.FormatId(new ModNodeId(ParseGuid(raw, "ProfileAlignEntryIdInvalid", "Mod ID"))))
			.ToArray();
		var duplicate = normalized.GroupBy(id => id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
		if (duplicate is not null)
			throw new CliUserException("ProfileAlignDuplicateEntry", $"Profile 对齐规格重复包含 Mod ID：{duplicate.Key}");
		return normalized;
	}

	private static CliProfileAlignActiveMode ValidateActiveMode(CliProfileAlignActiveMode activeMode)
	{
		if (!Enum.IsDefined(activeMode))
			throw new CliUserException("ProfileAlignActiveModeInvalid", "activeMode 只能是 preserve、setActive 或 clearIfActive。 ");
		return activeMode;
	}

	private static string NormalizeExpectedName(string? value)
	{
		var normalized = value?.Trim() ?? string.Empty;
		if (string.IsNullOrWhiteSpace(normalized))
			throw new CliUserException("ProfileAlignExpectedNameInvalid", "Profile 对齐规格必须包含非空的 expectedProfileName。 ");
		return normalized;
	}

	private static void EnsureWritableTargetCanBeFrozen(CliWorkspace workspace)
	{
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot))
			throw new CliUserException("ProfileAlignManagerRootRequired", "profile align plan/inspect 需要 --manager-root <管理器目录>，以冻结后续可写入目标。 ");
		if (string.IsNullOrWhiteSpace(workspace.LibraryRoot))
			throw new CliUserException("LibraryNotConfigured", "profile align plan/inspect 需要明确的 Mod 库。 ");
	}

	private static ProfileId? ResolveTargetActiveProfile(
		LibrarySnapshot snapshot,
		ProfileId profileId,
		CliProfileAlignActiveMode activeMode)
		=> activeMode switch
		{
			CliProfileAlignActiveMode.Preserve => snapshot.ActiveProfileId,
			CliProfileAlignActiveMode.SetActive => profileId,
			CliProfileAlignActiveMode.ClearIfActive when snapshot.ActiveProfileId == profileId => null,
			CliProfileAlignActiveMode.ClearIfActive => snapshot.ActiveProfileId,
			_ => throw new CliUserException("ProfileAlignActiveModeInvalid", "activeMode 不受支持。"),
		};

	private static ProfileMutationResult ProfileMutationResultNoChange(LibrarySnapshot snapshot)
		=> new(
			ProfileMutationOperation.SetActiveProfile,
			ProfileMutationStatus.NoChange,
			snapshot,
			null,
			Array.Empty<ModNodeId>());

	private static bool IsDeployable(ModNode node)
		=> node.Metadata.Kind switch
		{
			ModNodeKind.Standard => node.PatchGroups.Count > 0,
			ModNodeKind.ReShade => true,
			_ => false,
		};

	private static void EnsureDocumentShape(CliProfileAlignPlanDocument document)
	{
		if (!string.Equals(document.Schema, CliProfileAlignPlanDocument.CurrentSchema, StringComparison.Ordinal)
			|| document.Version != CliProfileAlignPlanDocument.CurrentVersion)
		{
			throw new CliUserException(
				"ProfileAlignPlanVersionUnsupported",
				$"Profile 对齐计划版本不受支持。需要 {CliProfileAlignPlanDocument.CurrentSchema} v{CliProfileAlignPlanDocument.CurrentVersion}。 ");
		}
		if (string.IsNullOrWhiteSpace(document.DocumentFingerprint)
			|| document.Request is null
			|| document.Target is null
			|| string.IsNullOrWhiteSpace(document.Target.ManagerRoot)
			|| string.IsNullOrWhiteSpace(document.Target.LibraryRoot)
			|| string.IsNullOrWhiteSpace(document.BeforeFingerprint)
			|| string.IsNullOrWhiteSpace(document.AfterFingerprint)
			|| document.Before is null
			|| document.After is null
			|| document.Nodes is null
			|| document.Changes is null
			|| document.Diagnostics is null)
		{
			throw new CliUserException("ProfileAlignPlanUnreadable", "Profile 对齐计划缺少请求、目标、指纹或冻结状态。 ");
		}
		_ = NormalizeRequest(document.Request);
		_ = ParseProfileId(document.Before.ProfileId, "ProfileAlignPlanUnreadable");
		_ = ParseProfileId(document.After.ProfileId, "ProfileAlignPlanUnreadable");
		foreach (var node in document.Nodes)
		{
			if (node is null || !Guid.TryParse(node.NodeId, out _))
				throw new CliUserException("ProfileAlignPlanUnreadable", "Profile 对齐计划包含无效的参与 Mod 身份。 ");
		}
	}

	private static bool IsAlreadyAligned(CliProfileAlignPlanDocument planned, LibrarySnapshot current)
		=> MatchesExpectedAfter(planned, current);

	private static bool ProfileStateEqualsIgnoringRevision(
		CliProfileAlignProfileState expected,
		CliProfileAlignProfileState actual)
		=> string.Equals(expected.ProfileId, actual.ProfileId, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(expected.Name, actual.Name, StringComparison.Ordinal)
			&& string.Equals(expected.ActiveProfileId, actual.ActiveProfileId, StringComparison.OrdinalIgnoreCase)
			&& expected.EntryNodeIds.SequenceEqual(actual.EntryNodeIds, StringComparer.OrdinalIgnoreCase);

	private static bool NodeIdentitiesEqual(
		IReadOnlyList<CliProfileAlignNodeIdentity> expected,
		IReadOnlyList<CliProfileAlignNodeIdentity> actual)
	{
		if (expected.Count != actual.Count) return false;
		for (var index = 0; index < expected.Count; index++)
		{
			var left = expected[index];
			var right = actual[index];
			if (!string.Equals(left.NodeId, right.NodeId, StringComparison.OrdinalIgnoreCase)
				|| left.Exists != right.Exists
				|| !string.Equals(left.Name, right.Name, StringComparison.Ordinal)
				|| !string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
				|| !string.Equals(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase)
				|| left.IsDeployable != right.IsDeployable
				|| !left.PatchGroups.SequenceEqual(right.PatchGroups, StringComparer.OrdinalIgnoreCase))
			{
				return false;
			}
		}
		return true;
	}

	private static IReadOnlyList<ProfileEntry> OrderEntries(Profile profile)
		=> profile.Entries
			.OrderBy(entry => entry.LoadOrder)
			.ThenBy(entry => entry.AddedUtc)
			.ThenBy(entry => entry.NodeId.Value)
			.ToArray();

	private static string ComputeSemanticFingerprint(
		CliProfileAlignProfileState profile,
		IReadOnlyList<CliProfileAlignNodeIdentity> nodes)
		=> ComputeHash(new { profile, nodes });

	private static string ComputeDocumentFingerprint(CliProfileAlignPlanDocument document)
		=> ComputeHash(new
		{
			document.Schema,
			document.Version,
			document.Request,
			document.Target,
			document.BeforeFingerprint,
			document.AfterFingerprint,
			document.Before,
			document.After,
			document.Nodes,
			document.Changes,
			document.Diagnostics,
		});

	private static string ComputeHash(object value)
		=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CliOutput.ToJson(value))));

	private static IReadOnlyList<CliProfileAlignDiagnostic> DistinctDiagnostics(IEnumerable<CliProfileAlignDiagnostic> diagnostics)
		=> diagnostics
			.GroupBy(item => $"{item.Code}\u001f{item.Message}\u001f{item.Details}", StringComparer.Ordinal)
			.Select(group => group.OrderByDescending(item => item.Blocking).First())
			.OrderByDescending(item => item.Blocking)
			.ThenBy(item => item.Code, StringComparer.Ordinal)
			.ToArray();

	private static ProfileId ParseProfileId(string? raw, string code)
		=> new(ParseGuid(raw, code, "Profile ID"));

	private static Guid ParseGuid(string? raw, string code, string subject)
	{
		if (!Guid.TryParse(raw, out var value) || value == Guid.Empty)
			throw new CliUserException(code, $"{subject} 无效：{raw ?? "<null>"}");
		return value;
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

	private static string NormalizeFilePath(string path, string code)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException(code, "文件路径不能为空。 ");
		try { return Path.GetFullPath(path); }
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw new CliUserException(code, $"路径无效：{path}");
		}
	}
}
