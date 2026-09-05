using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：生成 ApplyPlan：按 Profile 顺序和 hex 分组将真实 patch 索引重新连续编号到游戏 data 目录。
// Purpose: Builds an ApplyPlan: renumbers real patch index files continuously per archive hex according to profile order.
public sealed class ApplyPlanner : IApplyPlanner
{
	private readonly IPatchFileNameParser _fileNameParser;

	public ApplyPlanner(IPatchFileNameParser fileNameParser)
	{
		_fileNameParser = fileNameParser ?? throw new ArgumentNullException(nameof(fileNameParser));
	}

	public ValueTask<ApplyPlan> BuildPlanAsync(
		Profile profile,
		LibrarySnapshot snapshot,
		PatchFileIndex patchIndex,
		string gameDataDirectory,
		CancellationToken cancellationToken = default)
		=> BuildPlanAsync(profile, snapshot, patchIndex, gameDataDirectory, ApplyPlanAugmentation.Empty, cancellationToken);

	public ValueTask<ApplyPlan> BuildPlanAsync(
		Profile profile,
		LibrarySnapshot snapshot,
		PatchFileIndex patchIndex,
		string gameDataDirectory,
		ApplyPlanAugmentation augmentation,
		CancellationToken cancellationToken = default)
	{
		if (profile is null)
		{
			throw new ArgumentNullException(nameof(profile));
		}
		if (snapshot is null)
		{
			throw new ArgumentNullException(nameof(snapshot));
		}
		if (patchIndex is null)
		{
			throw new ArgumentNullException(nameof(patchIndex));
		}
		ArgumentNullException.ThrowIfNull(augmentation);
		if (string.IsNullOrWhiteSpace(gameDataDirectory))
		{
			throw new ArgumentException("Value cannot be null or whitespace.", nameof(gameDataDirectory));
		}

		var gameData = Path.GetFullPath(gameDataDirectory);
		var ops = new List<ApplyOperation>();
		var mappings = new List<DeploymentPatchMapping>();
		var issues = new List<CoreIssue>(patchIndex.Issues);

		foreach (var existing in EnumerateExistingPatchFiles(gameData))
		{
			cancellationToken.ThrowIfCancellationRequested();
			ops.Add(new ApplyOperation(
				ApplyOperationKind.DeletePatch,
				TargetPath: existing,
				SourcePath: null,
				ArchiveHex16: null,
				SourcePatchIndex: null,
				TargetPatchIndex: null,
				SidecarKind: null,
				NodeId: null));
		}

		var nextIndexByHex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		var plannedGroups = new List<PlannedPatchGroup>();
		var orderedEntries = profile.Entries
			.OrderBy(e => e.LoadOrder)
			.ThenBy(e => e.AddedUtc)
			.ThenBy(e => e.NodeId.Value)
			.ToList();

		foreach (var entry in orderedEntries)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!snapshot.Nodes.ContainsKey(entry.NodeId))
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "ProfileNodeMissing", $"Profile entry references a missing mod node: {entry.NodeId}", NodeId: entry.NodeId));
				continue;
			}

			if (!patchIndex.FilesByNode.TryGetValue(entry.NodeId, out var files) || files.Count == 0)
			{
				if (snapshot.Nodes.TryGetValue(entry.NodeId, out var noPayloadNode)
					&& (noPayloadNode.Metadata.Kind == ModNodeKind.ReShade
						|| noPayloadNode.PatchGroups.Count == 0))
				{
					// Profile entries are intent references. Logical/empty nodes are
					// allowed to remain in a Profile and have no GameData payload of
					// their own; ReShade is handled through external mappings.
					continue;
				}
				issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "ProfileModHasNoPatchFiles", $"Profile mod has no patch files: {entry.NodeId}", NodeId: entry.NodeId));
				continue;
			}

			foreach (var hexGroup in files.GroupBy(f => f.ArchiveHex16, StringComparer.OrdinalIgnoreCase))
			{
				var sourceBaseIndexes = hexGroup
					.Where(f => f.SidecarKind == PatchSidecarKind.Base)
					.Select(f => f.NormalizedOrder)
					.Distinct()
					.OrderBy(x => x)
					.ToList();

				foreach (var sourceBaseIndex in sourceBaseIndexes)
				{
					var groupFiles = hexGroup.Where(f => f.NormalizedOrder == sourceBaseIndex).OrderBy(f => f.SidecarKind).ToArray();
					if (groupFiles.Any(file => file.SidecarKind == PatchSidecarKind.Base))
						plannedGroups.Add(new PlannedPatchGroup(
							entry.LoadOrder,
							sourceBaseIndex,
							groupFiles[0].ArchiveHex16,
							groupFiles[0].NodeId,
							groupFiles,
							null));
				}
			}
		}

		foreach (var generatedGroup in augmentation.GeneratedPatchGroups)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (string.IsNullOrWhiteSpace(generatedGroup.ArchiveHex16)
				|| generatedGroup.Files is null
				|| generatedGroup.Files.Count == 0)
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "GeneratedPatchGroupInvalid", "A generated patch group has no archive identity or files.", NodeId: generatedGroup.OwnerNodeId));
				continue;
			}

			var archive = generatedGroup.ArchiveHex16.ToLowerInvariant();
			plannedGroups.Add(new PlannedPatchGroup(
				generatedGroup.PlacementOrder ?? int.MaxValue,
				generatedGroup.SourcePatchIndex,
				archive,
				generatedGroup.OwnerNodeId,
				generatedGroup.Files
					.Select(file => new IndexedPatchFile(
						generatedGroup.OwnerNodeId ?? default,
						file.FilePath,
						file.FileName,
						archive,
						generatedGroup.SourcePatchIndex,
						generatedGroup.SourcePatchIndex,
						file.SidecarKind,
						file.Length,
						file.LastWriteTimeUtc))
					.ToArray(),
				generatedGroup));
		}

		foreach (var group in plannedGroups
			.OrderBy(group => group.PlacementOrder)
			.ThenBy(group => group.ArchiveHex16, StringComparer.OrdinalIgnoreCase)
			.ThenBy(group => group.SourceOrder))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var archive = group.ArchiveHex16.ToLowerInvariant();
			var targetPatchIndex = nextIndexByHex.GetValueOrDefault(archive);
			foreach (var file in group.Files.OrderBy(file => file.SidecarKind))
			{
				var mapping = new DeploymentPatchMapping(
					group.NodeId,
					file.FilePath,
					Path.Combine(gameData, BuildFileName(archive, targetPatchIndex, file.SidecarKind)),
					archive,
					file.SourcePatchIndex,
					targetPatchIndex,
					file.SidecarKind);
				mappings.Add(mapping);
				ops.Add(new ApplyOperation(
					ApplyOperationKind.DeployPatch,
					mapping.TargetPath,
					mapping.SourcePath,
					mapping.ArchiveHex16,
					mapping.SourcePatchIndex,
					mapping.TargetPatchIndex,
					mapping.SidecarKind,
					mapping.NodeId));
			}
			nextIndexByHex[archive] = targetPatchIndex + 1;
		}

		return ValueTask.FromResult(new ApplyPlan(gameData, profile.Id, profile.Revision, DateTimeOffset.UtcNow, ops, issues)
		{
			PatchMappings = mappings
		});
	}

	private IEnumerable<string> EnumerateExistingPatchFiles(string gameData)
	{
		if (!Directory.Exists(gameData))
		{
			yield break;
		}

		foreach (var file in Directory.EnumerateFiles(gameData, "*", SearchOption.TopDirectoryOnly))
		{
			if (_fileNameParser.TryParse(Path.GetFileName(file), out _))
			{
				yield return file;
			}
		}
	}

	private static string BuildFileName(string hex, int index, PatchSidecarKind kind)
	{
		var suffix = kind switch
		{
			PatchSidecarKind.Base => string.Empty,
			PatchSidecarKind.Stream => ".stream",
			PatchSidecarKind.GpuResources => ".gpu_resources",
			_ => string.Empty,
		};

		return $"{hex}.patch_{index}{suffix}";
	}

	private sealed record PlannedPatchGroup(
		int PlacementOrder,
		int SourceOrder,
		string ArchiveHex16,
		ModNodeId? NodeId,
		IReadOnlyList<IndexedPatchFile> Files,
		GeneratedPatchGroup? GeneratedGroup);
}
