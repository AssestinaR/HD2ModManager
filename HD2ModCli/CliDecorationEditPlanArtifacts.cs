using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// Editable, external request document for an existing PatchSnapshot decoration.
/// The candidate list is informational only: plan/validate always re-read the
/// decoration snapshot and never trust a copied candidate from this file.
/// </summary>
public sealed record CliDecorationEditSpecDocument(
	string Schema,
	int Version,
	DateTimeOffset ExportedUtc,
	CliDecorationEditSpecSource Source,
	IReadOnlyList<CliDecorationEditCandidate> Candidates,
	IReadOnlyList<CliDecorationEditSelection> Selections,
	CliDecorationEditAttachment Attachment,
	IReadOnlyList<CliDecorationEditDiagnostic> Diagnostics)
{
	public const string CurrentSchema = "hd2mod.decoration-edit-spec";
	public const int CurrentVersion = 1;
	public bool IsReady => Candidates.Count != 0 && !Diagnostics.Any(item => item.Blocking);
}

public sealed record CliDecorationEditSpecSource(
	string DecorationNodeId,
	string DecorationName,
	string RelativePath,
	string CurrentPlanSha256,
	string SourceStorageMode);

public sealed record CliDecorationEditRequest(
	string DecorationNodeId,
	IReadOnlyList<CliDecorationEditSelection> Selections,
	CliDecorationEditAttachment Attachment,
	string? ExpectedCurrentPlanSha256 = null);

public sealed record CliDecorationEditSelection(string SelectionId);

public sealed record CliDecorationEditAttachment(
	IReadOnlyList<string> TargetHostNodeIds,
	string TargetPart,
	string TargetBodyVariant,
	string DualVariantMode,
	bool ReplaceWhenSourcePartLayerMatches);

public sealed record CliDecorationEditCandidate(
	string SelectionId,
	string ArchiveId,
	string ArchiveCategory,
	string ArchiveDisplayName,
	string UnitAssetKey,
	int MeshInfoIndex,
	string Part,
	string Layer,
	string BodyVariant,
	string SemanticName,
	bool IsCulling,
	string GeometryQuality,
	long StoredBytes,
	int VertexCount,
	int TriangleCount,
	IReadOnlyList<string> SharedArchiveIds);

public sealed record CliDecorationEditResolvedSourceUnit(
	string SelectionId,
	string UnitAssetKey,
	int MeshInfoIndex,
	string BodyVariant,
	string Part,
	string Layer,
	bool IsCulling);

public sealed record CliDecorationEditProposedAttachment(
	IReadOnlyList<string> TargetHostNodeIds,
	string TargetPart,
	string TargetBodyVariant,
	string DualVariantMode,
	bool ReplaceWhenSourcePartLayerMatches,
	IReadOnlyList<string> SourcePartLayers);

public sealed record CliDecorationEditTarget(
	string ManagerRoot,
	string LibraryRoot,
	string GameDataDirectory);

public sealed record CliDecorationEditFileSnapshot(
	string Path,
	bool Exists,
	long Length,
	string? Sha256);

public sealed record CliDecorationEditInputState(
	CliDecorationEditFileSnapshot DecorationPlan,
	CliHostDerivedInputState HostDerived);

public sealed record CliDecorationEditActiveHost(
	string NodeId,
	string? Name,
	bool Exists,
	bool WasDeclaredBeforeEdit,
	bool WillBeDeclaredAfterEdit,
	bool WillBeRebuilt);

public sealed record CliDecorationEditPlanDocument(
	string Schema,
	int Version,
	DateTimeOffset CreatedUtc,
	string InputFingerprint,
	CliDecorationEditRequest Request,
	CliDecorationEditTarget Target,
	string CurrentPlanSha256,
	IReadOnlyList<CliDecorationEditCandidate> Candidates,
	IReadOnlyList<CliDecorationEditResolvedSourceUnit> ProposedSourceUnits,
	CliDecorationEditProposedAttachment ProposedAttachment,
	IReadOnlyList<CliDecorationEditActiveHost> ActiveHosts,
	CliDecorationEditInputState Inputs,
	IReadOnlyList<CliDecorationEditDiagnostic> Diagnostics)
{
	public const string CurrentSchema = "hd2mod.decoration-edit-plan";
	public const int CurrentVersion = 1;
	public bool IsReady => ProposedSourceUnits.Count != 0 && !Diagnostics.Any(item => item.Blocking);
}

public sealed record CliDecorationEditDiagnostic(
	string Code,
	string Severity,
	bool Blocking,
	string Message,
	string? Path = null,
	string? NodeId = null,
	string? Details = null);

internal sealed record CliDecorationEditBuildResult(
	CliDecorationEditPlanDocument Document,
	StoragePaths Paths,
	ModNode Decoration,
	string DecorationDirectory,
	string DecorationPlanPath,
	DecorationPlanDocument CurrentPlan,
	DecorationPlanDocument ProposedPlan,
	CliHostDerivedBuildResult BaselineHostDerived);

internal sealed record CliDecorationEditValidationResult(
	CliDecorationEditPlanDocument Document,
	CliDecorationEditBuildResult? Current,
	IReadOnlyList<CliDecorationEditDiagnostic> Diagnostics)
{
	public bool IsValid => Current is not null && Current.Document.IsReady && !Diagnostics.Any(item => item.Blocking);
}

internal static class CliDecorationEditPlanArtifacts
{
	// 接受的部位写法：ArmorStructure 的规范名（helmet / left_arm）、紧凑写法（leftarm）、
	// 中文显示名，外加旧规格文件里出现过的 Head / Pelvis / Hip。新写的规格请用规范名。
	private static readonly IReadOnlySet<string> SupportedTargetParts = new HashSet<string>(
		ArmorStructure.All
			.SelectMany(part => new[]
			{
				ArmorStructure.Name(part),
				ArmorStructure.Name(part).Replace("_", string.Empty, StringComparison.Ordinal),
				ArmorStructure.Display(part),
			})
			.Concat(["Head", "Pelvis", "Hip", "Accessory"]),
		StringComparer.OrdinalIgnoreCase);

	private static readonly IReadOnlySet<string> SupportedTargetBodyVariants = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"Dual", "Slim", "Stocky",
	};

	private static readonly IReadOnlySet<string> SupportedDualVariantModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"AutoAssign", "ApplyAllToBoth",
	};

	public static async ValueTask<CliDecorationEditSpecDocument> ExportSpecAsync(
		CliWorkspace workspace,
		string decorationSelector,
		CancellationToken cancellationToken = default)
	{
		var inspection = await InspectAsync(workspace, decorationSelector, cancellationToken).ConfigureAwait(false);
		var diagnostics = new List<CliDecorationEditDiagnostic>(inspection.Diagnostics);
		var request = CreateRequestFromCurrentPlan(inspection, diagnostics);
		return new CliDecorationEditSpecDocument(
			CliDecorationEditSpecDocument.CurrentSchema,
			CliDecorationEditSpecDocument.CurrentVersion,
			DateTimeOffset.UtcNow,
			new CliDecorationEditSpecSource(
				CliProjections.FormatId(inspection.Decoration.Id),
				inspection.Decoration.Metadata.Name,
				inspection.Decoration.RelativePath,
				inspection.CurrentPlanSha256,
				inspection.CurrentPlan.SourceStorageMode),
			inspection.Candidates,
			request.Selections,
			request.Attachment,
			DistinctDiagnostics(diagnostics));
	}

	public static async ValueTask<CliDecorationEditSpecDocument> ReadSpecAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("DecorationEditSpecRequired", "decoration edit plan 需要一个由 decoration edit export 生成并编辑后的规格文件。 ");
		var fullPath = CliOutput.NormalizeDirectoryPath(path, "DecorationEditSpecPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("DecorationEditSpecNotFound", $"装饰编辑规格文件不存在：{fullPath}");
		try
		{
			var spec = JsonSerializer.Deserialize<CliDecorationEditSpecDocument>(
				await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false),
				CliOutput.JsonOptions);
			if (spec is null
				|| !string.Equals(spec.Schema, CliDecorationEditSpecDocument.CurrentSchema, StringComparison.Ordinal)
				|| spec.Version != CliDecorationEditSpecDocument.CurrentVersion
				|| spec.Source is null
				|| string.IsNullOrWhiteSpace(spec.Source.DecorationNodeId)
				|| string.IsNullOrWhiteSpace(spec.Source.CurrentPlanSha256)
				|| spec.Candidates is null
				|| spec.Selections is null
				|| spec.Attachment is null
				|| spec.Diagnostics is null)
			{
				throw new CliUserException("DecorationEditSpecUnreadable", "装饰编辑规格缺少来源、选择或附着策略，或版本不受支持。 ");
			}
			return spec;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("DecorationEditSpecUnreadable", $"无法读取装饰编辑规格：{exception.Message}");
		}
	}

	public static async ValueTask<CliDecorationEditBuildResult> BuildAsync(
		CliWorkspace workspace,
		CliDecorationEditRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(request);
		if (string.IsNullOrWhiteSpace(request.DecorationNodeId))
			throw new CliUserException("DecorationEditDecorationRequired", "装饰编辑请求需要一个明确的装饰 Mod GUID 或唯一前缀。 ");
		if (request.Selections is null || request.Attachment is null)
			throw new CliUserException("DecorationEditRequestInvalid", "装饰编辑请求缺少 selections 或 attachment。 ");

		var inspection = await InspectAsync(workspace, request.DecorationNodeId, cancellationToken).ConfigureAwait(false);
		var diagnostics = new List<CliDecorationEditDiagnostic>(inspection.Diagnostics);
		if (!string.IsNullOrWhiteSpace(request.ExpectedCurrentPlanSha256)
			&& !string.Equals(request.ExpectedCurrentPlanSha256, inspection.CurrentPlanSha256, StringComparison.OrdinalIgnoreCase))
		{
			diagnostics.Add(new CliDecorationEditDiagnostic(
				"DecorationEditSourceChanged", "error", true,
				"编辑规格所依据的 decoration.json 已变化；请重新导出规格后再编辑。",
				inspection.DecorationPlanPath,
				CliProjections.FormatId(inspection.Decoration.Id)));
		}
		var selected = ResolveSelections(request.Selections, inspection.CandidatesById, diagnostics);
		var resolvedHosts = ResolveAttachmentHosts(
			inspection,
			request.Attachment,
			diagnostics);
		var proposedAttachment = CreateProposedAttachment(request.Attachment, resolvedHosts, selected, diagnostics);
		var proposedPlan = CreateProposedPlan(inspection.CurrentPlan, selected, proposedAttachment);
		var activeHosts = BuildActiveHosts(inspection, proposedAttachment, diagnostics);
		var rebuildRequest = new CliHostDerivedRequest(
			CliHostDerivedOperation.Rebuild,
			CliHostDerivedAction.Rebuild,
			activeHosts.Where(host => host.Exists).Select(host => host.NodeId).ToArray(),
			Array.Empty<string>(),
			false,
			Path.GetFullPath(workspace.GameDataRoot!));
		var baseline = await CliHostDerivedOverwritePlanArtifacts
			.BuildAsync(workspace, rebuildRequest, cancellationToken)
			.ConfigureAwait(false);
		diagnostics.AddRange(baseline.Document.Diagnostics.Select(ToEditDiagnostic));

		var target = new CliDecorationEditTarget(
			Path.GetFullPath(workspace.ManagerRoot!),
			Path.GetFullPath(workspace.LibraryRoot!),
			Path.GetFullPath(workspace.GameDataRoot!));
		var inputs = new CliDecorationEditInputState(
			new CliDecorationEditFileSnapshot(
				inspection.DecorationPlanPath,
				true,
				inspection.DecorationPlanLength,
				inspection.CurrentPlanSha256),
			baseline.Document.Inputs);
		var normalizedDiagnostics = DistinctDiagnostics(diagnostics);
		var document = new CliDecorationEditPlanDocument(
			CliDecorationEditPlanDocument.CurrentSchema,
			CliDecorationEditPlanDocument.CurrentVersion,
			DateTimeOffset.UtcNow,
			ComputeFingerprint(
				request,
				target,
				inspection.CurrentPlanSha256,
				inspection.Candidates,
				selected,
				proposedAttachment,
				activeHosts,
				inputs,
				normalizedDiagnostics),
			request,
			target,
			inspection.CurrentPlanSha256,
			inspection.Candidates,
			selected,
			proposedAttachment,
			activeHosts,
			inputs,
			normalizedDiagnostics);
		return new CliDecorationEditBuildResult(
			document,
			inspection.Paths,
			inspection.Decoration,
			inspection.DecorationDirectory,
			inspection.DecorationPlanPath,
			inspection.CurrentPlan,
			proposedPlan,
			baseline);
	}

	public static async ValueTask<CliDecorationEditPlanDocument> ReadDocumentAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("DecorationEditPlanRequired", "请通过 --plan <装饰编辑计划.json> 提供已导出的计划文件。 ");
		var fullPath = CliOutput.NormalizeDirectoryPath(path, "DecorationEditPlanPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("DecorationEditPlanNotFound", $"装饰编辑计划不存在：{fullPath}");
		try
		{
			var document = JsonSerializer.Deserialize<CliDecorationEditPlanDocument>(
				await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false),
				CliOutput.JsonOptions);
			if (document is null
				|| !string.Equals(document.Schema, CliDecorationEditPlanDocument.CurrentSchema, StringComparison.Ordinal)
				|| document.Version != CliDecorationEditPlanDocument.CurrentVersion
				|| string.IsNullOrWhiteSpace(document.InputFingerprint)
				|| document.Request is null
				|| document.Target is null
				|| document.Candidates is null
				|| document.ProposedSourceUnits is null
				|| document.ProposedAttachment is null
				|| document.ActiveHosts is null
				|| document.Inputs is null
				|| document.Diagnostics is null)
			{
				throw new CliUserException("DecorationEditPlanUnreadable", "装饰编辑计划缺少请求、目标、候选、输入快照或诊断。 ");
			}
			if (string.IsNullOrWhiteSpace(document.Request.DecorationNodeId)
				|| document.Request.Selections is null
				|| document.Request.Attachment is null)
			{
				throw new CliUserException("DecorationEditPlanUnreadable", "装饰编辑计划包含不完整的编辑请求。 ");
			}
			var declared = ComputeFingerprint(
				document.Request,
				document.Target,
				document.CurrentPlanSha256,
				document.Candidates,
				document.ProposedSourceUnits,
				document.ProposedAttachment,
				document.ActiveHosts,
				document.Inputs,
				document.Diagnostics);
			if (!string.Equals(document.InputFingerprint, declared, StringComparison.OrdinalIgnoreCase))
				throw new CliUserException("DecorationEditPlanIntegrityMismatch", "装饰编辑计划在导出后被修改；请重新由规格生成计划并审阅。 ");
			return document;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("DecorationEditPlanUnreadable", $"无法读取装饰编辑计划：{exception.Message}");
		}
	}

	public static async ValueTask<CliDecorationEditValidationResult> ValidateAsync(
		CliDecorationEditPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);
		var diagnostics = new List<CliDecorationEditDiagnostic>();
		CliDecorationEditBuildResult? current = null;
		try
		{
			if (string.IsNullOrWhiteSpace(workspace.GameDataRoot)
				|| !PathsEqual(workspace.GameDataRoot, document.Target.GameDataDirectory))
			{
				diagnostics.Add(new CliDecorationEditDiagnostic(
					"DecorationEditGameDataTargetChanged", "error", true,
					"当前 --game-data 与编辑计划固定的 GameData 目标不同；请重新生成计划。"));
			}
			current = await BuildAsync(workspace, document.Request, cancellationToken).ConfigureAwait(false);
			if (!PathsEqual(document.Target.ManagerRoot, current.Document.Target.ManagerRoot)
				|| !PathsEqual(document.Target.LibraryRoot, current.Document.Target.LibraryRoot)
				|| !PathsEqual(document.Target.GameDataDirectory, current.Document.Target.GameDataDirectory))
			{
				diagnostics.Add(new CliDecorationEditDiagnostic(
					"DecorationEditTargetChanged", "error", true,
					"当前管理器、Mod 库或 GameData 目标与编辑计划不同；请重新生成计划。"));
			}
			if (!string.Equals(document.InputFingerprint, current.Document.InputFingerprint, StringComparison.OrdinalIgnoreCase))
			{
				diagnostics.Add(new CliDecorationEditDiagnostic(
					"DecorationEditInputChanged", "error", true,
					"装饰计划、来源快照、候选、启用状态、主体 Overwrite 或 GameData 索引输入已变化；请重新生成编辑计划。"));
			}
			diagnostics.AddRange(current.Document.Diagnostics);
		}
		catch (CliUserException exception)
		{
			diagnostics.Add(new CliDecorationEditDiagnostic(exception.Code, "error", true, exception.Message));
		}
		return new CliDecorationEditValidationResult(document, current, DistinctDiagnostics(diagnostics));
	}

	private static async ValueTask<CliDecorationEditInspectionData> InspectAsync(
		CliWorkspace workspace,
		string decorationSelector,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot))
			throw new CliUserException("DecorationEditManagerRootRequired", "decoration edit 需要 --manager-root <管理器目录>。 ");
		if (string.IsNullOrWhiteSpace(workspace.LibraryRoot))
			throw new CliUserException("LibraryNotConfigured", "decoration edit 需要明确的 Mod 库。 ");
		if (string.IsNullOrWhiteSpace(workspace.GameDataRoot))
			throw new CliUserException("DecorationEditGameDataRequired", "decoration edit 需要 --game-data <游戏 data 目录>，或可读取的管理器 GameDataFolder 设置。 ");
		if (string.IsNullOrWhiteSpace(decorationSelector))
			throw new CliUserException("DecorationEditDecorationRequired", "decoration edit 需要一个装饰 Mod GUID 或唯一 GUID 前缀。 ");

		var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
		var decoration = CliProjections.ResolveMod(state.Snapshot.Nodes, decorationSelector);
		if (decoration.Metadata.Kind != ModNodeKind.Decoration)
			throw new CliUserException("DecorationEditKindInvalid", $"Mod “{decoration.Metadata.Name}”不是装饰 Mod，不能进入装饰编辑流程。 ");
		var paths = new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot);
		var directory = CliDecorationPackagePlanArtifacts.ResolveSourceDirectory(paths, decoration);
		var planPath = Path.Combine(directory, "decoration.json");
		if (!File.Exists(planPath))
			throw new CliUserException("DecorationEditPlanMissing", $"装饰 Mod 缺少 decoration.json：{planPath}");
		if ((File.GetAttributes(planPath) & FileAttributes.ReparsePoint) != 0)
			throw new CliUserException("DecorationEditPlanReparsePoint", $"decoration.json 不能是链接或其他 reparse point：{planPath}");

		var planBytes = await File.ReadAllBytesAsync(planPath, cancellationToken).ConfigureAwait(false);
		DecorationPlanDocument plan;
		try
		{
			plan = JsonSerializer.Deserialize<DecorationPlanDocument>(planBytes, CliOutput.JsonOptions)
				?? throw new InvalidDataException("decoration.json 为空或无法解析。");
		}
		catch (Exception exception) when (exception is InvalidDataException or JsonException)
		{
			throw new CliUserException("DecorationEditPlanUnreadable", $"无法读取 decoration.json：{exception.Message}");
		}
		if (plan.Plan is null)
			throw new CliUserException("DecorationEditAttachmentMissing", "decoration.json 缺少 Plan 附着策略，不能安全编辑。 ");
		if (!string.Equals(plan.SourceStorageMode, "PatchSnapshot", StringComparison.OrdinalIgnoreCase))
			throw new CliUserException("DecorationEditSourceStorageUnsupported", "当前 CLI 仅安全编辑 SourceStorageMode=PatchSnapshot 的装饰包；旧版 .bin 装饰请在桌面端迁移或重新生成。 ");

		var sourceInspection = await CliDecorationSourceInspectionArtifacts
			.BuildAsync(workspace, CliProjections.FormatId(decoration.Id), allowSnapshotDecoration: true, cancellationToken)
			.ConfigureAwait(false);
		var diagnostics = sourceInspection.Diagnostics.Select(ToEditDiagnostic).ToList();
		var candidates = sourceInspection.UnitCandidates
			.Select(ToCandidate)
			.OrderBy(candidate => candidate.ArchiveDisplayName, StringComparer.OrdinalIgnoreCase)
			.ThenBy(candidate => candidate.Part, StringComparer.Ordinal)
			.ThenBy(candidate => candidate.BodyVariant, StringComparer.Ordinal)
			.ThenBy(candidate => candidate.Layer, StringComparer.Ordinal)
			.ThenBy(candidate => candidate.UnitAssetKey, StringComparer.Ordinal)
			.ThenBy(candidate => candidate.MeshInfoIndex)
			.ToArray();
		if (candidates.Length == 0)
		{
			diagnostics.Add(new CliDecorationEditDiagnostic(
				"DecorationEditCandidatesMissing", "error", true,
				"装饰来源快照中没有可编辑的可见 Unit 候选。", planPath, CliProjections.FormatId(decoration.Id)));
		}
		return new CliDecorationEditInspectionData(
			state,
			paths,
			decoration,
			directory,
			planPath,
			plan,
			planBytes.LongLength,
			ComputeSha256(planBytes),
			candidates,
			candidates.ToDictionary(candidate => candidate.SelectionId, StringComparer.Ordinal),
			diagnostics);
	}

	private static CliDecorationEditRequest CreateRequestFromCurrentPlan(
		CliDecorationEditInspectionData inspection,
		ICollection<CliDecorationEditDiagnostic> diagnostics)
	{
		var selections = new List<CliDecorationEditSelection>();
		foreach (var unit in inspection.CurrentPlan.SourceUnits ?? [])
		{
			var matching = inspection.Candidates
				.Where(candidate => MatchesStoredUnit(candidate, unit))
				.OrderBy(candidate => candidate.SelectionId, StringComparer.Ordinal)
				.ToArray();
			if (matching.Length == 0)
			{
				diagnostics.Add(new CliDecorationEditDiagnostic(
					"DecorationEditStoredSourceMissing", "warning", false,
					$"当前 decoration.json 中的来源 Unit 不在可读快照候选中：0x{unit.TypeId:x16}/0x{unit.FileId:x16} Mesh {unit.MeshInfoIndex}。",
					inspection.DecorationPlanPath,
					CliProjections.FormatId(inspection.Decoration.Id)));
				continue;
			}
			selections.AddRange(matching.Select(candidate => new CliDecorationEditSelection(candidate.SelectionId)));
		}
		return new CliDecorationEditRequest(
			CliProjections.FormatId(inspection.Decoration.Id),
			selections.DistinctBy(item => item.SelectionId, StringComparer.Ordinal).ToArray(),
			new CliDecorationEditAttachment(
				(inspection.CurrentPlan.Plan.TargetModGuids ?? []).ToArray(),
				inspection.CurrentPlan.Plan.TargetPart,
				inspection.CurrentPlan.Plan.TargetBodyVariant,
				inspection.CurrentPlan.Plan.DualVariantMode,
				inspection.CurrentPlan.Plan.ReplaceWhenSourcePartLayerMatches),
			inspection.CurrentPlanSha256);
	}

	private static IReadOnlyList<CliDecorationEditResolvedSourceUnit> ResolveSelections(
		IReadOnlyList<CliDecorationEditSelection> selections,
		IReadOnlyDictionary<string, CliDecorationEditCandidate> candidates,
		ICollection<CliDecorationEditDiagnostic> diagnostics)
	{
		if (selections.Count == 0)
		{
			diagnostics.Add(new CliDecorationEditDiagnostic("DecorationEditSelectionRequired", "error", true, "编辑计划至少需要选择一个来源 Unit。"));
			return Array.Empty<CliDecorationEditResolvedSourceUnit>();
		}
		var result = new List<CliDecorationEditResolvedSourceUnit>();
		var seenSelections = new HashSet<string>(StringComparer.Ordinal);
		var seenUnits = new HashSet<string>(StringComparer.Ordinal);
		foreach (var selection in selections)
		{
			if (selection is null || string.IsNullOrWhiteSpace(selection.SelectionId) || !seenSelections.Add(selection.SelectionId))
			{
				diagnostics.Add(new CliDecorationEditDiagnostic("DecorationEditSelectionInvalid", "error", true, "编辑请求包含空白或重复的选择 ID。"));
				continue;
			}
			if (!candidates.TryGetValue(selection.SelectionId, out var candidate))
			{
				diagnostics.Add(new CliDecorationEditDiagnostic("DecorationEditSelectionUnavailable", "error", true, $"选择 ID 已不属于当前装饰来源快照：{selection.SelectionId}"));
				continue;
			}
			var unitIdentity = $"{candidate.UnitAssetKey}\u001f{candidate.MeshInfoIndex}\u001f{candidate.IsCulling}";
			if (!seenUnits.Add(unitIdentity)) continue;
			result.Add(new CliDecorationEditResolvedSourceUnit(
				candidate.SelectionId,
				candidate.UnitAssetKey,
				candidate.MeshInfoIndex,
				candidate.BodyVariant,
				candidate.Part,
				candidate.Layer,
				candidate.IsCulling));
		}
		if (result.Count == 0 && !diagnostics.Any(item => item.Code == "DecorationEditSelectionRequired"))
			diagnostics.Add(new CliDecorationEditDiagnostic("DecorationEditSelectionRequired", "error", true, "编辑请求没有留下任何可用来源 Unit。"));
		return result.OrderBy(item => item.Part, StringComparer.Ordinal).ThenBy(item => item.BodyVariant, StringComparer.Ordinal).ThenBy(item => item.Layer, StringComparer.Ordinal).ThenBy(item => item.UnitAssetKey, StringComparer.Ordinal).ThenBy(item => item.MeshInfoIndex).ToArray();
	}

	private static IReadOnlyList<ModNode> ResolveAttachmentHosts(
		CliDecorationEditInspectionData inspection,
		CliDecorationEditAttachment attachment,
		ICollection<CliDecorationEditDiagnostic> diagnostics)
	{
		if (attachment.TargetHostNodeIds is null || attachment.TargetHostNodeIds.Count == 0)
		{
			diagnostics.Add(new CliDecorationEditDiagnostic("DecorationEditHostRequired", "error", true, "编辑计划至少需要一个目标主体。"));
			return Array.Empty<ModNode>();
		}
		var hosts = new List<ModNode>();
		var seen = new HashSet<ModNodeId>();
		foreach (var raw in attachment.TargetHostNodeIds)
		{
			if (string.IsNullOrWhiteSpace(raw))
			{
				diagnostics.Add(new CliDecorationEditDiagnostic("DecorationEditHostInvalid", "error", true, "编辑计划包含空白主体 ID。"));
				continue;
			}
			ModNode node;
			try { node = CliProjections.ResolveMod(inspection.State.Snapshot.Nodes, raw); }
			catch (CliUserException exception)
			{
				diagnostics.Add(new CliDecorationEditDiagnostic(exception.Code, "error", true, exception.Message, NodeId: raw));
				continue;
			}
			if (!seen.Add(node.Id)) continue;
			if (node.Id == inspection.Decoration.Id)
			{
				diagnostics.Add(new CliDecorationEditDiagnostic("DecorationEditHostEqualsDecoration", "error", true, "装饰 Mod 不能作为自身的目标主体。", NodeId: CliProjections.FormatId(node.Id)));
				continue;
			}
			if (node.Metadata.Kind is not (ModNodeKind.Standard or ModNodeKind.Option) || node.PatchGroups.Count == 0)
			{
				diagnostics.Add(new CliDecorationEditDiagnostic("DecorationEditHostUnsupported", "error", true, $"主体“{node.Metadata.Name}”不是带 Patch 的普通 Mod 或选项 Mod。", NodeId: CliProjections.FormatId(node.Id)));
				continue;
			}
			hosts.Add(node);
		}
		return hosts.OrderBy(node => node.Metadata.Name, StringComparer.OrdinalIgnoreCase).ThenBy(node => node.Id.Value).ToArray();
	}

	private static CliDecorationEditProposedAttachment CreateProposedAttachment(
		CliDecorationEditAttachment attachment,
		IReadOnlyList<ModNode> hosts,
		IReadOnlyList<CliDecorationEditResolvedSourceUnit> selected,
		ICollection<CliDecorationEditDiagnostic> diagnostics)
	{
		var targetPart = attachment.TargetPart?.Trim() ?? string.Empty;
		var targetBodyVariant = attachment.TargetBodyVariant?.Trim() ?? string.Empty;
		var dualVariantMode = attachment.DualVariantMode?.Trim() ?? string.Empty;
		if (!SupportedTargetParts.Contains(targetPart))
			diagnostics.Add(new CliDecorationEditDiagnostic("DecorationEditTargetPartInvalid", "error", true, $"不支持的目标部位：{targetPart}。"));
		if (!SupportedTargetBodyVariants.Contains(targetBodyVariant))
			diagnostics.Add(new CliDecorationEditDiagnostic("DecorationEditTargetBodyVariantInvalid", "error", true, $"不支持的目标身形：{targetBodyVariant}。"));
		if (!SupportedDualVariantModes.Contains(dualVariantMode))
			diagnostics.Add(new CliDecorationEditDiagnostic("DecorationEditDualVariantModeInvalid", "error", true, $"不支持的双身形模式：{dualVariantMode}。"));
		var sourcePartLayers = selected
			.Select(item => ToPartLayerKey(item.Part, item.Layer, diagnostics))
			.Where(item => !string.IsNullOrWhiteSpace(item))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(item => item, StringComparer.Ordinal)
			.ToArray();
		return new CliDecorationEditProposedAttachment(
			hosts.Select(node => CliProjections.FormatId(node.Id)).ToArray(),
			targetPart,
			targetBodyVariant,
			dualVariantMode,
			attachment.ReplaceWhenSourcePartLayerMatches,
			sourcePartLayers!);
	}

	private static IReadOnlyList<CliDecorationEditActiveHost> BuildActiveHosts(
		CliDecorationEditInspectionData inspection,
		CliDecorationEditProposedAttachment attachment,
		ICollection<CliDecorationEditDiagnostic> diagnostics)
	{
		var decorationKey = inspection.Decoration.Id.Value.ToString("N");
		var enabled = inspection.State.DecorationActivations.TryGetValue(decorationKey, out var known)
			? known
			: new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var currentTargets = (inspection.CurrentPlan.Plan.TargetModGuids ?? [])
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var plannedTargets = attachment.TargetHostNodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var result = new List<CliDecorationEditActiveHost>();
		foreach (var rawHost in enabled.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
		{
			if (!Guid.TryParse(rawHost, out var parsed))
			{
				diagnostics.Add(new CliDecorationEditDiagnostic("DecorationEditStaleActivation", "warning", false, $"装饰启用状态包含无效主体 ID：{rawHost}。该状态不会参与本次回建。", NodeId: rawHost));
				result.Add(new CliDecorationEditActiveHost(rawHost, null, false, false, false, false));
				continue;
			}
			var id = new ModNodeId(parsed);
			if (!inspection.State.Snapshot.Nodes.TryGetValue(id, out var host))
			{
				diagnostics.Add(new CliDecorationEditDiagnostic("DecorationEditStaleActivation", "warning", false, $"装饰启用状态指向已删除主体：{CliProjections.FormatId(id)}。该状态不会参与本次回建。", NodeId: CliProjections.FormatId(id)));
				result.Add(new CliDecorationEditActiveHost(CliProjections.FormatId(id), null, false, false, false, false));
				continue;
			}
			var canonical = CliProjections.FormatId(id);
			var wasDeclared = ContainsGuid(currentTargets, id);
			var willBeDeclared = ContainsGuid(plannedTargets, id);
			if (wasDeclared && !willBeDeclared)
			{
				diagnostics.Add(new CliDecorationEditDiagnostic(
					"DecorationEditActiveHostWillBeDetached", "warning", false,
					$"当前已启用的主体“{host.Metadata.Name}”将从装饰目标列表移除；apply 会回建该主体并清理此装饰的启用关联。",
					NodeId: canonical));
			}
			result.Add(new CliDecorationEditActiveHost(
				canonical,
				host.Metadata.Name,
				true,
				wasDeclared,
				willBeDeclared,
				true));
		}
		return result;
	}

	private static DecorationPlanDocument CreateProposedPlan(
		DecorationPlanDocument current,
		IReadOnlyList<CliDecorationEditResolvedSourceUnit> selected,
		CliDecorationEditProposedAttachment attachment)
	{
		return new DecorationPlanDocument
		{
			Format = string.IsNullOrWhiteSpace(current.Format) ? "HD2ModManager.Decoration" : current.Format,
			Version = 3,
			Name = current.Name,
			SourceStorageMode = "PatchSnapshot",
			Payloads = [],
			SourceUnits = selected.Select(ToDecorationSourceUnit).ToList(),
			Plan = new DecorationAttachmentPlan
			{
				TargetModGuids = attachment.TargetHostNodeIds.ToList(),
				TargetPart = attachment.TargetPart,
				TargetBodyVariant = attachment.TargetBodyVariant,
				DualVariantMode = attachment.DualVariantMode,
				ReplaceWhenSourcePartLayerMatches = attachment.ReplaceWhenSourcePartLayerMatches,
				SourcePartLayers = attachment.SourcePartLayers.ToList(),
				CreatedUtc = current.Plan.CreatedUtc,
			},
		};
	}

	private static DecorationSourceUnit ToDecorationSourceUnit(CliDecorationEditResolvedSourceUnit unit)
	{
		if (!TryParseAssetKey(unit.UnitAssetKey, out var typeId, out var fileId))
			throw new CliUserException("DecorationEditSourceUnitInvalid", $"无法解析已选择的来源 AssetKey：{unit.UnitAssetKey}");
		return new DecorationSourceUnit
		{
			TypeId = typeId,
			FileId = fileId,
			MeshInfoIndex = unit.MeshInfoIndex,
			BodyVariant = unit.BodyVariant,
			Layer = unit.Layer,
			IsCulling = unit.IsCulling,
		};
	}

	private static string? ToPartLayerKey(string part, string layer, ICollection<CliDecorationEditDiagnostic> diagnostics)
	{
		if (!Enum.TryParse<UnitMeshPartKind>(part, ignoreCase: true, out var parsedPart)
			|| !Enum.TryParse<UnitMeshPartLayer>(layer, ignoreCase: true, out var parsedLayer))
		{
			diagnostics.Add(new CliDecorationEditDiagnostic("DecorationEditSourcePartLayerInvalid", "error", true, $"无法从来源候选推导部位/层级：{part}/{layer}。"));
			return null;
		}
		return DecorationPlanningDefaults.ToPartLayerKey(parsedPart, parsedLayer);
	}

	private static bool MatchesStoredUnit(CliDecorationEditCandidate candidate, DecorationSourceUnit unit)
		=> TryParseAssetKey(candidate.UnitAssetKey, out var typeId, out var fileId)
			&& typeId == unit.TypeId
			&& fileId == unit.FileId
			&& candidate.MeshInfoIndex == unit.MeshInfoIndex
			&& candidate.IsCulling == unit.IsCulling;

	private static bool TryParseAssetKey(string value, out ulong typeId, out ulong fileId)
	{
		typeId = 0;
		fileId = 0;
		var parts = value.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		return parts.Length == 2
			&& TryParseHex(parts[0], out typeId)
			&& TryParseHex(parts[1], out fileId);
	}

	private static bool TryParseHex(string value, out ulong result)
	{
		var raw = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
		return ulong.TryParse(raw, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out result);
	}

	private static bool ContainsGuid(IReadOnlySet<string> values, ModNodeId id)
		=> values.Contains(id.Value.ToString("N")) || values.Contains(id.Value.ToString("D"));

	private static CliDecorationEditCandidate ToCandidate(CliDecorationSourceUnitCandidate source)
		=> new(
			source.SelectionId,
			source.ArchiveId,
			source.ArchiveCategory,
			source.ArchiveDisplayName,
			source.UnitAssetKey,
			source.MeshInfoIndex,
			source.Part,
			source.Layer,
			source.BodyVariant,
			source.SemanticName,
			source.IsCulling,
			source.GeometryQuality,
			source.StoredBytes,
			source.VertexCount,
			source.TriangleCount,
			source.SharedArchiveIds);

	private static CliDecorationEditDiagnostic ToEditDiagnostic(CliDecorationSourceInspectionDiagnostic source)
		=> new(source.Code, source.Severity, source.Blocking, source.Message, source.Path, source.NodeId, source.Details);

	private static CliDecorationEditDiagnostic ToEditDiagnostic(CliHostDerivedDiagnostic source)
		=> new(source.Code, source.Severity, source.Blocking, source.Message, source.Path, source.NodeId, source.Details);

	private static string ComputeFingerprint(
		CliDecorationEditRequest request,
		CliDecorationEditTarget target,
		string currentPlanSha256,
		IReadOnlyList<CliDecorationEditCandidate> candidates,
		IReadOnlyList<CliDecorationEditResolvedSourceUnit> sourceUnits,
		CliDecorationEditProposedAttachment attachment,
		IReadOnlyList<CliDecorationEditActiveHost> activeHosts,
		CliDecorationEditInputState inputs,
		IReadOnlyList<CliDecorationEditDiagnostic> diagnostics)
	{
		var body = new
		{
			request,
			target,
			currentPlanSha256,
			candidates = candidates.OrderBy(item => item.SelectionId, StringComparer.Ordinal).ToArray(),
			sourceUnits = sourceUnits.OrderBy(item => item.SelectionId, StringComparer.Ordinal).ToArray(),
			attachment,
			activeHosts = activeHosts.OrderBy(item => item.NodeId, StringComparer.OrdinalIgnoreCase).ToArray(),
			inputs,
			diagnostics = diagnostics.OrderBy(item => item.Blocking ? 0 : 1).ThenBy(item => item.Code, StringComparer.Ordinal).ThenBy(item => item.NodeId, StringComparer.OrdinalIgnoreCase).ToArray(),
		};
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CliOutput.ToJson(body)))).ToLowerInvariant();
	}

	private static string ComputeSha256(byte[] bytes)
		=> Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

	private static IReadOnlyList<CliDecorationEditDiagnostic> DistinctDiagnostics(IEnumerable<CliDecorationEditDiagnostic> diagnostics)
		=> diagnostics
			.GroupBy(item => $"{item.Code}\u001f{item.Severity}\u001f{item.Message}\u001f{item.Path}\u001f{item.NodeId}", StringComparer.Ordinal)
			.Select(group => group.OrderByDescending(item => item.Blocking).First())
			.OrderByDescending(item => item.Blocking)
			.ThenBy(item => item.Code, StringComparer.Ordinal)
			.ThenBy(item => item.NodeId, StringComparer.OrdinalIgnoreCase)
			.ToArray();

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

	private sealed record CliDecorationEditInspectionData(
		CliLibraryState State,
		StoragePaths Paths,
		ModNode Decoration,
		string DecorationDirectory,
		string DecorationPlanPath,
		DecorationPlanDocument CurrentPlan,
		long DecorationPlanLength,
		string CurrentPlanSha256,
		IReadOnlyList<CliDecorationEditCandidate> Candidates,
		IReadOnlyDictionary<string, CliDecorationEditCandidate> CandidatesById,
		IReadOnlyList<CliDecorationEditDiagnostic> Diagnostics);
}
