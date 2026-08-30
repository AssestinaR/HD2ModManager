using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// A reviewed whole-entry Patch resource selection.  The document is editable
/// by an Agent only at <see cref="Selections"/>: validation reconstructs the
/// current library facts and rejects every stale or invented selection.
/// </summary>
public sealed record CliPatchResourceRepackagingPlanDocument(
	string Schema,
	int Version,
	DateTimeOffset CreatedUtc,
	string InputFingerprint,
	string? LibraryRoot,
	IReadOnlyList<string> SourceNodeIds,
	IReadOnlyList<CliPatchResourcePatchGroup> PatchGroups,
	IReadOnlyList<CliPatchResourceCandidate> Candidates,
	IReadOnlyList<CliPatchResourceSelection> Selections,
	IReadOnlyList<CliPatchResourceDiagnostic> Diagnostics)
{
	public const string CurrentSchema = "hd2mod.patch-resource-repackage-plan";
	public const int CurrentVersion = 1;

	public bool IsReady
		=> Selections.Count > 0 && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

public sealed record CliPatchResourcePatchGroup(
	string Id,
	string NodeId,
	string ModName,
	string ArchiveHex,
	int PatchIndex,
	string TocRelativePath,
	string TocSha256);

public sealed record CliPatchResourceCandidate(
	string VariantId,
	string AssetKey,
	string TypeName,
	bool IsConflict,
	long TotalBytes,
	IReadOnlyList<string> ReferenceTargets,
	IReadOnlyList<CliPatchResourceOccurrence> Occurrences);

public sealed record CliPatchResourceOccurrence(
	string PatchGroupId,
	string NodeId,
	string ModName,
	string PatchLabel,
	string PayloadFingerprint,
	uint TocBytes,
	uint StreamBytes,
	uint GpuBytes);

/// <summary>
/// Select exactly one occurrence for each chosen VariantId.  A VariantId is
/// immutable payload identity; PatchGroupId pins the header source used while
/// writing the standalone Patch and therefore participates in validation.
/// </summary>
public sealed record CliPatchResourceSelection(string VariantId, string PatchGroupId);

public sealed record CliPatchResourceDiagnostic(
	string Code,
	string Severity,
	bool Blocking,
	string Message,
	string? Path = null,
	string? NodeId = null,
	string? Details = null);

internal sealed record CliPatchResourceRepackagingBuildResult(
	CliPatchResourceRepackagingPlanDocument Document,
	PatchResourceRepackagingInspection Inspection,
	IReadOnlyDictionary<string, PatchResourceAssetVariant> VariantsById);

internal sealed record CliPatchResourceRepackagingValidationResult(
	CliPatchResourceRepackagingPlanDocument Document,
	CliPatchResourceRepackagingBuildResult? Current,
	IReadOnlyList<PatchResourceRepackagingSelection> ResolvedSelections,
	IReadOnlyList<CliPatchResourceDiagnostic> Diagnostics)
{
	public bool IsValid => Current is not null && ResolvedSelections.Count > 0 && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

/// <summary>
/// Turns the UI's resource-repackaging facts into a stable, plan-gated CLI
/// protocol.  It deliberately works at whole AssetKey payload granularity and
/// never exposes a mechanism for arbitrary Patch byte mutation.
/// </summary>
internal static class CliPatchResourceRepackagingPlanArtifacts
{
	public static async ValueTask<CliPatchResourceRepackagingBuildResult> BuildAsync(
		CliWorkspace workspace,
		IReadOnlyList<string> nodeSelectors,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(nodeSelectors);
		if (nodeSelectors.Count == 0)
			throw new CliUserException("PatchResourceSourceRequired", "patch repackage plan 至少需要一个 Mod GUID 或唯一前缀作为来源。");

		var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(workspace.LibraryRoot))
			throw new CliUserException("LibraryNotConfigured", "Patch 资源重打包需要明确的 Mod 库。请传入 --library 或 --manager-root。");

		var nodes = nodeSelectors
			.Select(selector => CliProjections.ResolveMod(state.Snapshot.Nodes, selector))
			.GroupBy(node => node.Id)
			.Select(group => group.First())
			.OrderBy(node => node.Metadata.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(node => node.Id.Value)
			.ToArray();

		try
		{
			var service = CoreServices.CreatePatchResourceRepackagingService();
			var groups = service.DiscoverPatchGroups(nodes, workspace.LibraryRoot);
			var before = await ComputePatchGroupHashesAsync(groups, workspace.LibraryRoot, cancellationToken).ConfigureAwait(false);
			var inspection = await service.InspectAsync(groups, cancellationToken).ConfigureAwait(false);
			var after = await ComputePatchGroupHashesAsync(groups, workspace.LibraryRoot, cancellationToken).ConfigureAwait(false);
			if (!HashesEqual(before, after))
			{
				throw new CliUserException(
					"PatchResourceSourceChangedDuringInspection",
					"Patch 来源在资源扫描过程中发生变化；请等待 Mod 文件写入完成后重新生成计划。");
			}

			var groupDocuments = groups
				.Select(group => CreatePatchGroupDocument(group, workspace.LibraryRoot, after[Path.GetFullPath(group.PatchTocPath)]))
				.OrderBy(group => group.ModName, StringComparer.OrdinalIgnoreCase)
				.ThenBy(group => group.ArchiveHex, StringComparer.OrdinalIgnoreCase)
				.ThenBy(group => group.PatchIndex)
				.ToArray();
			var groupIdsByPath = groupDocuments.ToDictionary(
				group => Path.GetFullPath(Path.Combine(workspace.LibraryRoot, group.TocRelativePath)),
				group => group.Id,
				StringComparer.OrdinalIgnoreCase);
			var candidates = inspection.Variants
				.Select(variant => CreateCandidate(variant, groupIdsByPath))
				.OrderBy(candidate => candidate.AssetKey, StringComparer.Ordinal)
				.ThenBy(candidate => candidate.VariantId, StringComparer.Ordinal)
				.ToArray();
			var selections = candidates
				.Where(candidate => !candidate.IsConflict)
				.Select(candidate => new CliPatchResourceSelection(candidate.VariantId, candidate.Occurrences[0].PatchGroupId))
				.ToArray();
			var diagnostics = inspection.Issues
				.Select(issue => CreateDiagnostic(issue))
				.ToList();
			if (groups.Count == 0)
			{
				diagnostics.Add(new CliPatchResourceDiagnostic(
					"PatchResourceNoPatchGroups",
					"error",
					true,
					"所选 Mod 中未发现可重打包的 Patch 组。"));
			}
			if (selections.Length == 0 && candidates.Length > 0)
			{
				diagnostics.Add(new CliPatchResourceDiagnostic(
					"PatchResourceNoDefaultSelections",
					"warning",
					false,
					"所有候选均存在来源冲突，计划未默认选择任何 AssetKey。请在计划的 selections 中明确保留每个 AssetKey 的一个 VariantId/PatchGroupId 组合后再验证。"));
			}

			var document = new CliPatchResourceRepackagingPlanDocument(
				CliPatchResourceRepackagingPlanDocument.CurrentSchema,
				CliPatchResourceRepackagingPlanDocument.CurrentVersion,
				DateTimeOffset.UtcNow,
				ComputeInputFingerprint(nodes, groupDocuments, candidates),
				workspace.LibraryRoot,
				nodes.Select(node => CliProjections.FormatId(node.Id)).ToArray(),
				groupDocuments,
				candidates,
				selections,
				diagnostics);
			var variantsById = inspection.Variants.ToDictionary(CreateVariantId, StringComparer.Ordinal);
			return new CliPatchResourceRepackagingBuildResult(document, inspection, variantsById);
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or DirectoryNotFoundException
			or FileNotFoundException)
		{
			throw new CliUserException("PatchResourceInspectionFailed", $"无法完成 Patch 资源扫描：{exception.Message}");
		}
	}

	public static async ValueTask<CliPatchResourceRepackagingPlanDocument> ReadDocumentAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("PatchResourcePlanRequired", "请通过 --plan <Patch资源重打包计划.json> 提供计划文件。");
		var fullPath = CliOutput.NormalizeDirectoryPath(path, "PatchResourcePlanPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("PatchResourcePlanNotFound", $"Patch 资源重打包计划不存在：{fullPath}");
		try
		{
			var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
			var document = JsonSerializer.Deserialize<CliPatchResourceRepackagingPlanDocument>(json, CliOutput.JsonOptions);
			if (document is null)
				throw new CliUserException("PatchResourcePlanUnreadable", "Patch 资源重打包计划为空或无法解析。");
			if (!string.Equals(document.Schema, CliPatchResourceRepackagingPlanDocument.CurrentSchema, StringComparison.Ordinal)
				|| document.Version != CliPatchResourceRepackagingPlanDocument.CurrentVersion)
			{
				throw new CliUserException(
					"PatchResourcePlanVersionUnsupported",
					$"Patch 资源重打包计划版本不受支持。需要 {CliPatchResourceRepackagingPlanDocument.CurrentSchema} v{CliPatchResourceRepackagingPlanDocument.CurrentVersion}；请重新运行 patch repackage plan。");
			}
			if (string.IsNullOrWhiteSpace(document.InputFingerprint)
				|| document.SourceNodeIds is null
				|| document.SourceNodeIds.Count == 0
				|| document.PatchGroups is null
				|| document.Candidates is null
				|| document.Selections is null
				|| document.Diagnostics is null)
			{
				throw new CliUserException("PatchResourcePlanUnreadable", "Patch 资源重打包计划缺少来源、候选或选择信息。");
			}
			return document;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("PatchResourcePlanUnreadable", $"无法读取 Patch 资源重打包计划：{exception.Message}");
		}
	}

	public static async ValueTask<CliPatchResourceRepackagingValidationResult> ValidateAsync(
		CliPatchResourceRepackagingPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);
		var diagnostics = new List<CliPatchResourceDiagnostic>();
		CliPatchResourceRepackagingBuildResult? current = null;
		try
		{
			current = await BuildAsync(workspace, document.SourceNodeIds, cancellationToken).ConfigureAwait(false);
		}
		catch (CliUserException exception)
		{
			diagnostics.Add(new CliPatchResourceDiagnostic(exception.Code, "error", true, exception.Message));
			return new CliPatchResourceRepackagingValidationResult(document, null, Array.Empty<PatchResourceRepackagingSelection>(), diagnostics);
		}

		if (!string.Equals(document.InputFingerprint, current.Document.InputFingerprint, StringComparison.OrdinalIgnoreCase))
		{
			diagnostics.Add(new CliPatchResourceDiagnostic(
				"PatchResourceInputChanged",
				"error",
				true,
				"计划来源的 Patch 组、TOC 头或资源 Payload 已变化；请重新生成计划并重新审阅选择。"));
		}
		diagnostics.AddRange(current.Document.Diagnostics.Where(diagnostic => diagnostic.Blocking));

		var selections = ResolveSelections(document, current, diagnostics);
		return new CliPatchResourceRepackagingValidationResult(document, current, selections, diagnostics);
	}

	internal static string CreateVariantId(PatchResourceAssetVariant variant)
		=> $"0x{variant.AssetKey.TypeId:x16}/0x{variant.AssetKey.FileId:x16}/{variant.PayloadFingerprint}";

	private static IReadOnlyList<PatchResourceRepackagingSelection> ResolveSelections(
		CliPatchResourceRepackagingPlanDocument document,
		CliPatchResourceRepackagingBuildResult current,
		ICollection<CliPatchResourceDiagnostic> diagnostics)
	{
		if (document.Selections.Count == 0)
		{
			diagnostics.Add(new CliPatchResourceDiagnostic(
				"PatchResourceSelectionRequired",
				"error",
				true,
				"计划没有选择任何 AssetKey。请先在 selections 中保留至少一个候选。"));
			return Array.Empty<PatchResourceRepackagingSelection>();
		}

		var candidates = current.Document.Candidates.ToDictionary(candidate => candidate.VariantId, StringComparer.Ordinal);
		var actualVariants = current.VariantsById;
		var result = new List<PatchResourceRepackagingSelection>();
		var selectedAssetKeys = new HashSet<string>(StringComparer.Ordinal);
		var seenVariantGroups = new HashSet<(string VariantId, string PatchGroupId)>();
		foreach (var selection in document.Selections)
		{
			if (string.IsNullOrWhiteSpace(selection.VariantId) || string.IsNullOrWhiteSpace(selection.PatchGroupId))
			{
				diagnostics.Add(new CliPatchResourceDiagnostic(
					"PatchResourceSelectionInvalid",
					"error",
					true,
					"计划中存在缺少 VariantId 或 PatchGroupId 的资源选择。"));
				continue;
			}
			if (!seenVariantGroups.Add((selection.VariantId, selection.PatchGroupId)))
			{
				diagnostics.Add(new CliPatchResourceDiagnostic(
					"PatchResourceSelectionDuplicate",
					"error",
					true,
					$"计划重复选择了同一资源来源：{selection.VariantId} / {selection.PatchGroupId}。"));
				continue;
			}
			if (!candidates.TryGetValue(selection.VariantId, out var candidate)
				|| !actualVariants.TryGetValue(selection.VariantId, out var actualVariant))
			{
				diagnostics.Add(new CliPatchResourceDiagnostic(
					"PatchResourceVariantNotFound",
					"error",
					true,
					$"计划选择的 VariantId 已不在当前来源中：{selection.VariantId}。"));
				continue;
			}
			if (!selectedAssetKeys.Add(candidate.AssetKey))
			{
				diagnostics.Add(new CliPatchResourceDiagnostic(
					"PatchResourceAssetConflict",
					"error",
					true,
					$"同一 AssetKey 选择了多个版本：{candidate.AssetKey}。每个 AssetKey 只能保留一个 VariantId。"));
				continue;
			}
			if (!candidate.Occurrences.Any(occurrence => string.Equals(occurrence.PatchGroupId, selection.PatchGroupId, StringComparison.Ordinal)))
			{
				diagnostics.Add(new CliPatchResourceDiagnostic(
					"PatchResourceOccurrenceNotFound",
					"error",
					true,
					$"计划为 {candidate.AssetKey} 指定的 PatchGroupId 并不提供该 Variant：{selection.PatchGroupId}。"));
				continue;
			}

			var actualOccurrence = actualVariant.Occurrences.FirstOrDefault(occurrence =>
				string.Equals(CreatePatchGroupId(occurrence, current.Document.PatchGroups), selection.PatchGroupId, StringComparison.Ordinal));
			if (actualOccurrence is null)
			{
				diagnostics.Add(new CliPatchResourceDiagnostic(
					"PatchResourceSourceOccurrenceChanged",
					"error",
					true,
					$"计划为 {candidate.AssetKey} 固定的来源 Patch 已不再可用：{selection.PatchGroupId}。"));
				continue;
			}
			result.Add(new PatchResourceRepackagingSelection(actualOccurrence));
		}

		return result;
	}

	private static CliPatchResourcePatchGroup CreatePatchGroupDocument(
		PatchResourcePatchGroup group,
		string libraryRoot,
		string tocSha256)
	{
		var relativePath = ToLibraryRelativePath(libraryRoot, group.PatchTocPath);
		return new CliPatchResourcePatchGroup(
			CreatePatchGroupId(group.NodeId, relativePath),
			CliProjections.FormatId(group.NodeId),
			group.ModName,
			group.ArchiveHex,
			group.PatchIndex,
			relativePath,
			tocSha256);
	}

	private static CliPatchResourceCandidate CreateCandidate(
		PatchResourceAssetVariant variant,
		IReadOnlyDictionary<string, string> groupIdsByPath)
	{
		var occurrences = variant.Occurrences
			.Select(occurrence =>
			{
				var path = Path.GetFullPath(occurrence.PatchTocPath);
				if (!groupIdsByPath.TryGetValue(path, out var groupId))
					throw new InvalidDataException($"Patch 资源候选引用了未知 Patch 组：{path}");
				return new CliPatchResourceOccurrence(
					groupId,
					CliProjections.FormatId(occurrence.NodeId),
					occurrence.ModName,
					occurrence.PatchLabel,
					occurrence.PayloadFingerprint,
					occurrence.TocBytes,
					occurrence.StreamBytes,
					occurrence.GpuBytes);
			})
			.OrderBy(occurrence => occurrence.ModName, StringComparer.OrdinalIgnoreCase)
			.ThenBy(occurrence => occurrence.PatchLabel, StringComparer.OrdinalIgnoreCase)
			.ThenBy(occurrence => occurrence.PatchGroupId, StringComparer.Ordinal)
			.ToArray();
		return new CliPatchResourceCandidate(
			CreateVariantId(variant),
			FormatAssetKey(variant.AssetKey),
			variant.TypeName,
			variant.IsConflict,
			variant.Occurrences[0].TotalBytes,
			variant.ReferenceTargets.Select(FormatAssetKey).ToArray(),
			occurrences);
	}

	private static CliPatchResourceDiagnostic CreateDiagnostic(CoreIssue issue)
		=> new(
			issue.Code,
			issue.Severity == CoreIssueSeverity.Error ? "error" : "warning",
			issue.Severity == CoreIssueSeverity.Error,
			issue.Message,
			issue.FilePath,
			issue.NodeId is { } nodeId ? CliProjections.FormatId(nodeId) : null,
			issue.ExceptionMessage);

	private static async ValueTask<IReadOnlyDictionary<string, string>> ComputePatchGroupHashesAsync(
		IReadOnlyList<PatchResourcePatchGroup> groups,
		string libraryRoot,
		CancellationToken cancellationToken)
	{
		var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var group in groups)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var fullPath = Path.GetFullPath(group.PatchTocPath);
			_ = ToLibraryRelativePath(libraryRoot, fullPath);
			await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.SequentialScan);
			result[fullPath] = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
		}
		return result;
	}

	private static bool HashesEqual(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
		=> left.Count == right.Count
			&& left.All(pair => right.TryGetValue(pair.Key, out var value)
				&& string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase));

	private static string ComputeInputFingerprint(
		IReadOnlyList<ModNode> nodes,
		IReadOnlyList<CliPatchResourcePatchGroup> groups,
		IReadOnlyList<CliPatchResourceCandidate> candidates)
	{
		var payload = CliOutput.ToJson(new
		{
			nodes = nodes.Select(node => CliProjections.FormatId(node.Id)).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
			groups = groups.Select(group => new { group.Id, group.TocRelativePath, group.TocSha256 }).OrderBy(group => group.Id, StringComparer.Ordinal).ToArray(),
			candidates = candidates.Select(candidate => new
			{
				candidate.VariantId,
				candidate.AssetKey,
				candidate.IsConflict,
				occurrences = candidate.Occurrences.Select(occurrence => new { occurrence.PatchGroupId, occurrence.PayloadFingerprint }).OrderBy(occurrence => occurrence.PatchGroupId, StringComparer.Ordinal).ToArray(),
			}).OrderBy(candidate => candidate.VariantId, StringComparer.Ordinal).ToArray(),
		});
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
	}

	private static string CreatePatchGroupId(ModNodeId nodeId, string tocRelativePath)
		=> $"{CliProjections.FormatId(nodeId)}/{tocRelativePath.Replace('\\', '/')}";

	private static string CreatePatchGroupId(
		PatchResourceOccurrence occurrence,
		IReadOnlyList<CliPatchResourcePatchGroup> groups)
	{
		var nodeId = CliProjections.FormatId(occurrence.NodeId);
		var matching = groups.FirstOrDefault(group =>
			string.Equals(group.NodeId, nodeId, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(group.ArchiveHex, occurrence.ArchiveHex, StringComparison.OrdinalIgnoreCase)
			&& group.PatchIndex == occurrence.PatchIndex);
		return matching?.Id ?? string.Empty;
	}

	private static string ToLibraryRelativePath(string libraryRoot, string path)
	{
		var root = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var fullPath = Path.GetFullPath(path);
		var relative = Path.GetRelativePath(root, fullPath);
		if (relative.Equals("..", StringComparison.Ordinal) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative.StartsWith("../", StringComparison.Ordinal))
			throw new InvalidDataException($"Patch 路径不位于当前 Mod 库中：{fullPath}");
		return relative.Replace('\\', '/');
	}

	private static string FormatAssetKey(AssetKey key)
		=> $"0x{key.TypeId:x16}/0x{key.FileId:x16}";
}
