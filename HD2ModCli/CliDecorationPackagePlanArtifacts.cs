using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// A portable, externally-published decoration package request.  This is deliberately
/// narrower than the desktop decoration workflow: it snapshots selected source Units
/// and records their intended hosts, but never imports, activates, deploys or rebuilds
/// an Overwrite directory.
/// </summary>
public sealed record CliDecorationPackagePlanDocument(
	string Schema,
	int Version,
	DateTimeOffset CreatedUtc,
	string DisplayName,
	string LibraryFingerprint,
	string InputFingerprint,
	CliDecorationPackageSource Source,
	CliDecorationPackageAttachment Attachment,
	IReadOnlyList<CliDecorationPackageUnitCandidate> Candidates,
	IReadOnlyList<CliDecorationPackageUnitSelection> Selections,
	IReadOnlyList<CliDecorationPackageDiagnostic> Diagnostics)
{
	public const string CurrentSchema = "hd2mod.decoration-package-plan";
	public const int CurrentVersion = 1;
	public bool IsReady => Selections.Count != 0 && !Diagnostics.Any(item => item.Blocking);
}

public sealed record CliDecorationPackageSource(
	string NodeId,
	string ModName,
	string ModKind,
	string? ContentGeneration,
	IReadOnlyList<CliDecorationPackagePatchFamily> PatchFamilies);

public sealed record CliDecorationPackagePatchFamily(
	string PatchFileName,
	string Fingerprint,
	IReadOnlyList<CliDecorationPackagePatchFile> Files);

public sealed record CliDecorationPackagePatchFile(string FileName, bool Exists, long Length, string? Sha256);

public sealed record CliDecorationPackageAttachment(
	IReadOnlyList<string> TargetHostNodeIds,
	string TargetPart,
	string TargetBodyVariant,
	string DualVariantMode,
	bool ReplaceWhenSourcePartLayerMatches,
	IReadOnlyList<string> SourcePartLayers);

public sealed record CliDecorationPackageUnitCandidate(
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
	IReadOnlyList<string> SharedArchiveIds,
	bool IsDefaultSelected);

public sealed record CliDecorationPackageUnitSelection(string SelectionId);

public sealed record CliDecorationPackageDiagnostic(
	string Code,
	string Severity,
	bool Blocking,
	string Message,
	string? Path = null,
	string? NodeId = null,
	string? Details = null);

internal sealed record CliDecorationPackageBuildResult(
	CliDecorationPackagePlanDocument Document,
	StoragePaths Paths,
	ModNode SourceNode,
	IReadOnlyList<ModNode> HostNodes,
	IReadOnlyDictionary<string, CliDecorationPackageUnitCandidate> CandidatesById);

internal sealed record CliDecorationPackageValidationResult(
	CliDecorationPackagePlanDocument Document,
	CliDecorationPackageBuildResult? Current,
	IReadOnlyList<DecorationSourceUnit> ResolvedSelections,
	IReadOnlyList<CliDecorationPackageDiagnostic> Diagnostics)
{
	public bool IsValid => Current is not null && ResolvedSelections.Count != 0 && !Diagnostics.Any(item => item.Blocking);
}

/// <summary>
/// Freezes the small, safe data contract needed by an external decoration package.
/// Candidate facts are reconstructed on every validation; only the selection and
/// attachment policy are intentionally editable between plan and apply.
/// </summary>
internal static class CliDecorationPackagePlanArtifacts
{
	public static ValueTask<CliDecorationPackageBuildResult> BuildAsync(
		CliWorkspace workspace,
		string sourceSelector,
		string hostSelector,
		CancellationToken cancellationToken = default)
		=> BuildAsync(workspace, sourceSelector, [hostSelector], cancellationToken);

	public static async ValueTask<CliDecorationPackageBuildResult> BuildAsync(
		CliWorkspace workspace,
		string sourceSelector,
		IReadOnlyList<string> hostSelectors,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(hostSelectors);
		if (string.IsNullOrWhiteSpace(sourceSelector))
			throw new CliUserException("DecorationPackageSourceRequired", "decoration package plan 需要一个来源 Mod GUID 或唯一前缀。 ");
		if (hostSelectors.Count == 0 || hostSelectors.Any(string.IsNullOrWhiteSpace))
			throw new CliUserException("DecorationPackageHostRequired", "decoration package plan 至少需要一个目标主体 Mod GUID 或唯一前缀。 ");
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot))
			throw new CliUserException("DecorationPackageManagerRootRequired", "decoration package plan 需要 --manager-root，以读取当前装备目录索引。 ");
		if (string.IsNullOrWhiteSpace(workspace.LibraryRoot))
			throw new CliUserException("LibraryNotConfigured", "decoration package plan 需要明确的 Mod 库。 ");
		if (string.IsNullOrWhiteSpace(workspace.GameDataRoot))
			throw new CliUserException("DecorationPackageGameDataRequired", "decoration package plan 需要 --game-data <游戏 data 目录>，或可读取的管理器 GameDataFolder 设置。 ");

		try
		{
			var beforeState = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
			var source = CliProjections.ResolveMod(beforeState.Snapshot.Nodes, sourceSelector);
			var hosts = hostSelectors
				.Select(selector => CliProjections.ResolveMod(beforeState.Snapshot.Nodes, selector))
				.GroupBy(node => node.Id)
				.Select(group => group.First())
				.OrderBy(node => node.Metadata.Name, StringComparer.OrdinalIgnoreCase)
				.ThenBy(node => node.Id.Value)
				.ToArray();
			ValidateSourceNode(source);
			ValidateHostNodes(source, hosts);
			var paths = new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot);
			var sourceDirectory = ResolveSourceDirectory(paths, source);
			var beforeLibrary = await ComputeFileHashAsync(Path.Combine(workspace.LibraryRoot, "library.json"), cancellationToken).ConfigureAwait(false);
			var beforePatches = await ComputePatchFamiliesAsync(sourceDirectory, cancellationToken).ConfigureAwait(false);
			var inspection = await CliDecorationSourceInspectionArtifacts
				.BuildAsync(workspace, CliProjections.FormatId(source.Id), allowSnapshotDecoration: false, cancellationToken)
				.ConfigureAwait(false);
			var afterPatches = await ComputePatchFamiliesAsync(sourceDirectory, cancellationToken).ConfigureAwait(false);
			var afterLibrary = await ComputeFileHashAsync(Path.Combine(workspace.LibraryRoot, "library.json"), cancellationToken).ConfigureAwait(false);

			var diagnostics = inspection.Diagnostics.Select(ToDiagnostic).ToList();
			if (!PatchFamiliesEqual(beforePatches, afterPatches))
			{
				diagnostics.Add(new CliDecorationPackageDiagnostic(
					"DecorationPackageSourceChangedDuringInspection", "error", true,
					"来源 Patch 家族在装饰候选扫描期间发生变化；请等待来源写入完成后重新生成计划。", sourceDirectory, CliProjections.FormatId(source.Id)));
			}
			if (!string.Equals(beforeLibrary, afterLibrary, StringComparison.OrdinalIgnoreCase))
			{
				diagnostics.Add(new CliDecorationPackageDiagnostic(
					"DecorationPackageLibraryChangedDuringInspection", "error", true,
					"Mod 库记录在装饰候选扫描期间发生变化；请重新生成计划。", workspace.LibraryRoot));
			}

			var candidates = inspection.UnitCandidates
				.Select(ToCandidate)
				.OrderBy(item => item.ArchiveDisplayName, StringComparer.OrdinalIgnoreCase)
				.ThenBy(item => item.Part, StringComparer.Ordinal)
				.ThenBy(item => item.BodyVariant, StringComparer.Ordinal)
				.ThenBy(item => item.Layer, StringComparer.Ordinal)
				.ThenBy(item => item.UnitAssetKey, StringComparer.Ordinal)
				.ThenBy(item => item.MeshInfoIndex)
				.ToArray();
			if (candidates.Length == 0)
				diagnostics.Add(new CliDecorationPackageDiagnostic("DecorationPackageCandidatesMissing", "error", true, "没有可写入装饰包的来源 Unit 候选。", NodeId: CliProjections.FormatId(source.Id)));
			var selections = candidates.Where(item => item.IsDefaultSelected).Select(item => new CliDecorationPackageUnitSelection(item.SelectionId)).ToArray();
			if (selections.Length == 0 && candidates.Length != 0)
				diagnostics.Add(new CliDecorationPackageDiagnostic("DecorationPackageSelectionRequired", "warning", false, "没有安全的默认 Unit 选择；请在 selections 中明确保留至少一个 candidates.selectionId。"));
			var sourceLayers = SourcePartLayers(candidates.Where(item => item.IsDefaultSelected));
			var attachment = new CliDecorationPackageAttachment(
				hosts.Select(node => CliProjections.FormatId(node.Id)).ToArray(),
				inspection.SuggestedAttachment.TargetPart,
				NormalizeBodyVariant(inspection.SuggestedAttachment.TargetBodyVariant),
				inspection.SuggestedAttachment.DualVariantMode,
				inspection.SuggestedAttachment.ReplaceWhenSourcePartLayerMatches,
				sourceLayers);
			var sourceDocument = new CliDecorationPackageSource(
				CliProjections.FormatId(source.Id), source.Metadata.Name, source.Metadata.Kind.ToString().ToLowerInvariant(), inspection.ContentGeneration, afterPatches);
			var document = new CliDecorationPackagePlanDocument(
				CliDecorationPackagePlanDocument.CurrentSchema,
				CliDecorationPackagePlanDocument.CurrentVersion,
				DateTimeOffset.UtcNow,
				$"{source.Metadata.Name} - 装饰",
				afterLibrary,
				ComputeInputFingerprint(sourceDocument, hosts, candidates, workspace.GameDataRoot),
				sourceDocument,
				attachment,
				candidates,
				selections,
				diagnostics);
			return new CliDecorationPackageBuildResult(document, paths, source, hosts, candidates.ToDictionary(item => item.SelectionId, StringComparer.Ordinal));
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or DirectoryNotFoundException or FileNotFoundException)
		{
			throw new CliUserException("DecorationPackageInspectionFailed", $"无法完成装饰外部包检查：{exception.Message}");
		}
	}

	public static async ValueTask<CliDecorationPackagePlanDocument> ReadDocumentAsync(string path, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("DecorationPackagePlanRequired", "请通过 --plan <装饰外部包计划.json> 提供计划文件。 ");
		var fullPath = CliOutput.NormalizeDirectoryPath(path, "DecorationPackagePlanPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("DecorationPackagePlanNotFound", $"装饰外部包计划不存在：{fullPath}");
		try
		{
			var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
			var document = JsonSerializer.Deserialize<CliDecorationPackagePlanDocument>(json, CliOutput.JsonOptions);
			if (document is null)
				throw new CliUserException("DecorationPackagePlanUnreadable", "装饰外部包计划为空或无法解析。 ");
			if (!string.Equals(document.Schema, CliDecorationPackagePlanDocument.CurrentSchema, StringComparison.Ordinal) || document.Version != CliDecorationPackagePlanDocument.CurrentVersion)
				throw new CliUserException("DecorationPackagePlanVersionUnsupported", $"装饰外部包计划版本不受支持。需要 {CliDecorationPackagePlanDocument.CurrentSchema} v{CliDecorationPackagePlanDocument.CurrentVersion}。 ");
			if (string.IsNullOrWhiteSpace(document.DisplayName) || string.IsNullOrWhiteSpace(document.LibraryFingerprint) || string.IsNullOrWhiteSpace(document.InputFingerprint)
				|| document.Source is null || string.IsNullOrWhiteSpace(document.Source.NodeId) || document.Source.PatchFamilies is null
				|| document.Attachment is null || document.Attachment.TargetHostNodeIds is null || document.Candidates is null || document.Selections is null || document.Diagnostics is null)
				throw new CliUserException("DecorationPackagePlanUnreadable", "装饰外部包计划缺少来源、主体、候选或选择信息。 ");
			return document;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("DecorationPackagePlanUnreadable", $"无法读取装饰外部包计划：{exception.Message}");
		}
	}

	public static async ValueTask<CliDecorationPackageValidationResult> ValidateAsync(
		CliDecorationPackagePlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);
		var diagnostics = new List<CliDecorationPackageDiagnostic>();
		if (!ValidateDocumentShape(document, diagnostics))
			return new CliDecorationPackageValidationResult(document, null, Array.Empty<DecorationSourceUnit>(), diagnostics);

		CliDecorationPackageBuildResult? current;
		try
		{
			current = await BuildAsync(workspace, document.Source.NodeId, document.Attachment.TargetHostNodeIds, cancellationToken).ConfigureAwait(false);
		}
		catch (CliUserException exception)
		{
			diagnostics.Add(new CliDecorationPackageDiagnostic(exception.Code, "error", true, exception.Message));
			return new CliDecorationPackageValidationResult(document, null, Array.Empty<DecorationSourceUnit>(), diagnostics);
		}

		if (!string.Equals(document.LibraryFingerprint, current.Document.LibraryFingerprint, StringComparison.OrdinalIgnoreCase))
			diagnostics.Add(new CliDecorationPackageDiagnostic("DecorationPackageLibraryChanged", "error", true, "library.json 已变化；请重新生成并审阅装饰外部包计划。"));
		if (!string.Equals(document.InputFingerprint, current.Document.InputFingerprint, StringComparison.OrdinalIgnoreCase))
			diagnostics.Add(new CliDecorationPackageDiagnostic("DecorationPackageInputChanged", "error", true, "来源 Patch、目标主体、GameData 输入或候选结构已变化；请重新生成计划。"));
		if (!CandidatesEqual(document.Candidates, current.Document.Candidates))
			diagnostics.Add(new CliDecorationPackageDiagnostic("DecorationPackageCandidatesChanged", "error", true, "计划中的 Unit candidates 与当前重建结果不一致；候选不可手工伪造或沿用旧结构。"));
		if (!PatchFamiliesEqual(document.Source.PatchFamilies, current.Document.Source.PatchFamilies))
			diagnostics.Add(new CliDecorationPackageDiagnostic("DecorationPackageSourcePatchChanged", "error", true, "来源 Patch 或 sidecar 指纹已变化；请重新生成计划。"));
		diagnostics.AddRange(current.Document.Diagnostics.Where(item => item.Blocking));

		var resolved = ResolveSelections(document, current, diagnostics);
		ValidateAttachment(document.Attachment, document.Selections, current.CandidatesById, current.SourceNode, current.HostNodes, diagnostics);
		return new CliDecorationPackageValidationResult(document, current, resolved, DistinctDiagnostics(diagnostics));
	}

	private static IReadOnlyList<DecorationSourceUnit> ResolveSelections(CliDecorationPackagePlanDocument document, CliDecorationPackageBuildResult current, ICollection<CliDecorationPackageDiagnostic> diagnostics)
	{
		if (document.Selections.Count == 0)
		{
			diagnostics.Add(new CliDecorationPackageDiagnostic("DecorationPackageSelectionRequired", "error", true, "计划没有选择任何来源 Unit。"));
			return Array.Empty<DecorationSourceUnit>();
		}
		var selected = new List<DecorationSourceUnit>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var selection in document.Selections)
		{
			if (selection is null || string.IsNullOrWhiteSpace(selection.SelectionId) || !seen.Add(selection.SelectionId))
			{
				diagnostics.Add(new CliDecorationPackageDiagnostic("DecorationPackageSelectionInvalid", "error", true, "计划中存在空白或重复的 Unit 选择。"));
				continue;
			}
			if (!current.CandidatesById.TryGetValue(selection.SelectionId, out var candidate) || !TryToSourceUnit(candidate, out var sourceUnit))
			{
				diagnostics.Add(new CliDecorationPackageDiagnostic("DecorationPackageSelectionNotFound", "error", true, $"计划选择的 Unit 已不在当前来源中：{selection.SelectionId}。"));
				continue;
			}
			selected.Add(sourceUnit);
		}
		return selected;
	}

	private static bool ValidateDocumentShape(CliDecorationPackagePlanDocument document, ICollection<CliDecorationPackageDiagnostic> diagnostics)
	{
		if (!string.Equals(document.Schema, CliDecorationPackagePlanDocument.CurrentSchema, StringComparison.Ordinal) || document.Version != CliDecorationPackagePlanDocument.CurrentVersion)
			diagnostics.Add(new CliDecorationPackageDiagnostic("DecorationPackagePlanVersionUnsupported", "error", true, "装饰外部包计划的 schema 或版本不受支持。"));
		if (string.IsNullOrWhiteSpace(document.DisplayName) || string.IsNullOrWhiteSpace(document.LibraryFingerprint) || string.IsNullOrWhiteSpace(document.InputFingerprint) || document.Source is null || document.Attachment is null || document.Candidates is null || document.Selections is null)
			diagnostics.Add(new CliDecorationPackageDiagnostic("DecorationPackagePlanIncomplete", "error", true, "装饰外部包计划缺少必要字段。"));
		return !diagnostics.Any(item => item.Blocking);
	}

	private static void ValidateAttachment(
		CliDecorationPackageAttachment attachment,
		IReadOnlyList<CliDecorationPackageUnitSelection> selections,
		IReadOnlyDictionary<string, CliDecorationPackageUnitCandidate> candidatesById,
		ModNode source,
		IReadOnlyList<ModNode> hosts,
		ICollection<CliDecorationPackageDiagnostic> diagnostics)
	{
		if (attachment.TargetHostNodeIds.Count == 0 || attachment.TargetHostNodeIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != attachment.TargetHostNodeIds.Count)
			diagnostics.Add(new CliDecorationPackageDiagnostic("DecorationPackageHostInvalid", "error", true, "目标主体列表不能为空且不能包含重复 ID。"));
		if (hosts.Any(host => host.Id == source.Id))
			diagnostics.Add(new CliDecorationPackageDiagnostic("DecorationPackageHostEqualsSource", "error", true, "装饰来源不能同时作为目标主体。"));
		// 装饰只能挂在头/躯干/胯上。部位写法走 ArmorStructure，新名（helmet/hips）与
		// 旧规格里的写法（Head/Pelvis/Hip）都认。
		if (ArmorStructure.TryParse(attachment.TargetPart) is not { } targetPart
			|| targetPart is not (ArmorPart.Helmet or ArmorPart.Torso or ArmorPart.Hips))
			diagnostics.Add(new CliDecorationPackageDiagnostic("DecorationPackageTargetPartInvalid", "error", true, "TargetPart 只能是头盔（helmet）、躯干（torso）或胯（hips）。"));
		if (!new[] { "Dual", "Stocky", "Slim" }.Contains(attachment.TargetBodyVariant, StringComparer.OrdinalIgnoreCase))
			diagnostics.Add(new CliDecorationPackageDiagnostic("DecorationPackageTargetBodyVariantInvalid", "error", true, "TargetBodyVariant 只能是 Dual、Stocky 或 Slim。"));
		if (!new[] { "AutoAssign", "ApplyAllToBoth" }.Contains(attachment.DualVariantMode, StringComparer.OrdinalIgnoreCase))
			diagnostics.Add(new CliDecorationPackageDiagnostic("DecorationPackageDualVariantModeInvalid", "error", true, "DualVariantMode 只能是 AutoAssign 或 ApplyAllToBoth。"));
		if (attachment.SourcePartLayers.Any(string.IsNullOrWhiteSpace) || attachment.SourcePartLayers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != attachment.SourcePartLayers.Count)
			diagnostics.Add(new CliDecorationPackageDiagnostic("DecorationPackageSourcePartLayersInvalid", "error", true, "SourcePartLayers 不能包含空值或重复值。"));
		var expectedLayers = SourcePartLayers(selections
			.Where(selection => selection is not null && !string.IsNullOrWhiteSpace(selection.SelectionId))
			.Select(selection => candidatesById.TryGetValue(selection.SelectionId, out var candidate) ? candidate : null)
			.Where(candidate => candidate is not null)
			.Cast<CliDecorationPackageUnitCandidate>());
		var actualLayers = attachment.SourcePartLayers.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
		if (!expectedLayers.SequenceEqual(actualLayers, StringComparer.OrdinalIgnoreCase))
			diagnostics.Add(new CliDecorationPackageDiagnostic("DecorationPackageSourcePartLayersMismatch", "error", true, "SourcePartLayers 必须由当前 selections 的来源部位/层级精确推导，不能保留过期或手工伪造的层级。"));
	}

	private static void ValidateSourceNode(ModNode source)
	{
		if (source.Metadata.Kind is not (ModNodeKind.Standard or ModNodeKind.Option))
			throw new CliUserException("DecorationPackageSourceKindUnsupported", $"Mod “{source.Metadata.Name}”不能作为装饰来源；只支持带 Patch 的普通 Mod 或选项 Mod。 ");
		if (source.PatchGroups.Count == 0)
			throw new CliUserException("DecorationPackageSourcePatchMissing", $"Mod “{source.Metadata.Name}”没有可读取的 Patch 主文件。 ");
	}

	private static void ValidateHostNodes(ModNode source, IReadOnlyList<ModNode> hosts)
	{
		foreach (var host in hosts)
		{
			if (host.Id == source.Id)
				throw new CliUserException("DecorationPackageHostEqualsSource", "装饰来源不能同时作为目标主体。 ");
			if (host.Metadata.Kind is not (ModNodeKind.Standard or ModNodeKind.Option) || host.PatchGroups.Count == 0)
				throw new CliUserException("DecorationPackageHostUnsupported", $"Mod “{host.Metadata.Name}”不是带 Patch 的普通 Mod 或选项 Mod，不能作为装饰主体。 ");
		}
	}

	private static CliDecorationPackageUnitCandidate ToCandidate(CliDecorationSourceUnitCandidate item)
		=> new(item.SelectionId, item.ArchiveId, item.ArchiveCategory, item.ArchiveDisplayName, item.UnitAssetKey, item.MeshInfoIndex, item.Part, item.Layer, item.BodyVariant, item.SemanticName, item.IsCulling, item.GeometryQuality, item.StoredBytes, item.VertexCount, item.TriangleCount, item.SharedArchiveIds, item.IsDefaultSelected);

	private static bool TryToSourceUnit(CliDecorationPackageUnitCandidate candidate, out DecorationSourceUnit unit)
	{
		unit = new DecorationSourceUnit();
		if (!TryParseAssetKey(candidate.UnitAssetKey, out var typeId, out var fileId) || candidate.MeshInfoIndex < 0 || string.IsNullOrWhiteSpace(candidate.Layer) || string.IsNullOrWhiteSpace(candidate.BodyVariant)) return false;
		unit = new DecorationSourceUnit { TypeId = typeId, FileId = fileId, MeshInfoIndex = candidate.MeshInfoIndex, BodyVariant = candidate.BodyVariant, Layer = candidate.Layer, IsCulling = candidate.IsCulling };
		return true;
	}

	private static bool TryParseAssetKey(string value, out ulong typeId, out ulong fileId)
	{
		typeId = 0; fileId = 0;
		var parts = value?.Split('/', StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
		return parts.Length == 2 && TryParseHex(parts[0], out typeId) && TryParseHex(parts[1], out fileId);
	}

	private static bool TryParseHex(string value, out ulong result)
		=> ulong.TryParse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result);

	private static IReadOnlyList<string> SourcePartLayers(IEnumerable<CliDecorationPackageUnitCandidate> candidates)
		=> candidates.Where(item => !string.IsNullOrWhiteSpace(item.Part) && !string.IsNullOrWhiteSpace(item.Layer))
			.Select(item => $"{item.Part}/{item.Layer}").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.Ordinal).ToArray();

	private static string NormalizeBodyVariant(string value)
		=> value.Contains("Stocky", StringComparison.OrdinalIgnoreCase) || value.Contains("健") ? "Stocky"
			: value.Contains("Slim", StringComparison.OrdinalIgnoreCase) || value.Contains("纤") ? "Slim" : "Dual";

	private static async ValueTask<IReadOnlyList<CliDecorationPackagePatchFamily>> ComputePatchFamiliesAsync(string directory, CancellationToken cancellationToken)
	{
		if (!Directory.Exists(directory)) return Array.Empty<CliDecorationPackagePatchFamily>();
		var parser = CoreServices.CreatePatchFileNameParser();
		var patches = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
			.Where(path => parser.TryParse(Path.GetFileName(path), out var info) && info?.SidecarKind == PatchSidecarKind.Base)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
		var result = new List<CliDecorationPackagePatchFamily>();
		foreach (var patch in patches)
		{
			var files = new List<CliDecorationPackagePatchFile>();
			foreach (var path in new[] { patch, patch + ".stream", patch + ".gpu_resources" })
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!File.Exists(path)) { files.Add(new CliDecorationPackagePatchFile(Path.GetFileName(path), false, 0, null)); continue; }
				var info = new FileInfo(path);
				files.Add(new CliDecorationPackagePatchFile(Path.GetFileName(path), true, info.Length, await ComputeFileHashAsync(path, cancellationToken).ConfigureAwait(false)));
			}
			result.Add(new CliDecorationPackagePatchFamily(Path.GetFileName(patch), HashJson(files), files));
		}
		return result;
	}

	private static async ValueTask<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.SequentialScan);
		return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
	}

	private static string ComputeInputFingerprint(CliDecorationPackageSource source, IReadOnlyList<ModNode> hosts, IReadOnlyList<CliDecorationPackageUnitCandidate> candidates, string gameDataRoot)
		=> HashJson(new
		{
			source = new { source.NodeId, source.ModKind, source.ContentGeneration, patches = source.PatchFamilies.Select(item => new { item.PatchFileName, item.Fingerprint }).OrderBy(item => item.PatchFileName, StringComparer.Ordinal).ToArray() },
			hosts = hosts.Select(host => new { id = CliProjections.FormatId(host.Id), host.RelativePath, kind = host.Metadata.Kind.ToString(), patches = host.PatchGroups.OrderBy(item => item.ArchiveHex16, StringComparer.Ordinal).ThenBy(item => item.PatchIndex).ToArray() }).OrderBy(item => item.id, StringComparer.Ordinal).ToArray(),
			gameData = Path.GetFullPath(gameDataRoot),
			candidates = candidates.OrderBy(item => item.SelectionId, StringComparer.Ordinal).ToArray(),
		});

	private static string HashJson(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CliOutput.ToJson(value))));

	private static bool PatchFamiliesEqual(IReadOnlyList<CliDecorationPackagePatchFamily> left, IReadOnlyList<CliDecorationPackagePatchFamily> right)
		=> left.Count == right.Count && left.Zip(right).All(pair => string.Equals(pair.First.PatchFileName, pair.Second.PatchFileName, StringComparison.OrdinalIgnoreCase) && string.Equals(pair.First.Fingerprint, pair.Second.Fingerprint, StringComparison.OrdinalIgnoreCase));

	private static bool CandidatesEqual(IReadOnlyList<CliDecorationPackageUnitCandidate> left, IReadOnlyList<CliDecorationPackageUnitCandidate> right)
		=> string.Equals(CliOutput.ToJson(left.OrderBy(item => item.SelectionId, StringComparer.Ordinal).ToArray()), CliOutput.ToJson(right.OrderBy(item => item.SelectionId, StringComparer.Ordinal).ToArray()), StringComparison.Ordinal);

	private static CliDecorationPackageDiagnostic ToDiagnostic(CliDecorationSourceInspectionDiagnostic item)
		=> new(item.Code, item.Severity, item.Blocking, item.Message, item.Path, item.NodeId, item.Details);

	private static IReadOnlyList<CliDecorationPackageDiagnostic> DistinctDiagnostics(IEnumerable<CliDecorationPackageDiagnostic> diagnostics)
		=> diagnostics.GroupBy(item => new { item.Code, item.Severity, item.Message, item.Path, item.NodeId }).Select(group => group.First()).ToArray();

	internal static string ResolveSourceDirectory(StoragePaths paths, ModNode node)
	{
		var root = Path.GetFullPath(paths.ModsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var directory = Path.GetFullPath(Path.Combine(root, node.RelativePath));
		if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase) || (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !directory.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
			throw new CliUserException("DecorationPackageSourcePathInvalid", "当前 Mod 的存储路径不在目标 Mod 库内。 ");
		return directory;
	}
}
