using System.Globalization;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using AdaptScanner = HD2ModAdaptation.PatchReconstruction.PatchTocScanner;

namespace HD2ModCore.Infrastructure.Sectional;

public enum SectionalOverflowConflictKind
{
	None = 0,
	SuspectedVariant = 1,
	ExplicitExclusive = 2,
}

public enum SectionalMergeOverflowAction
{
	Cancel = 0,
	DisableSelectedGroups = 1,
	FallbackToUnmerged = 2,
}

public sealed record SectionalMergeOverflowDecision(
	SectionalMergeOverflowAction Action,
	IReadOnlyList<string> DisabledGroupIds);

public sealed record SectionalMergeOverflowGroupItem(
	string GroupId,
	string GroupName,
	string DisplayName,
	SectionalOverflowConflictKind ConflictKind,
	string? ConflictLabel,
	ulong EstimatedBytes,
	ulong CoveredMergeIndices,
	bool RecommendedKeepEnabled,
	IReadOnlyList<SectionGroupTarget> Members);

public sealed record SectionalMergeOverflowContext(
	string ModDisplayName,
	string MergedSlotDisplayName,
	IReadOnlyList<string> SourceUnitIds,
	ulong ActualBufferBytes,
	ulong LimitBytes,
	ulong TotalEnabledMergeIndices,
	bool CanFallbackToUnmerged,
	IReadOnlyList<SectionalMergeOverflowGroupItem> Groups,
	IReadOnlyDictionary<string, IReadOnlyList<ulong>> SectionIndicesByUnit,
	IReadOnlyDictionary<string, IReadOnlyCollection<int>> BaseDisabledSections);

public delegate ValueTask<SectionalMergeOverflowDecision> SectionalMergeOverflowHandler(
	SectionalMergeOverflowContext context,
	CancellationToken cancellationToken);

public sealed class SectionalMergeBufferOverflowException : InvalidOperationException
{
	public SectionalMergeBufferOverflowException(
		IReadOnlyList<string> sourceFileIds,
		int streamIndex,
		string bufferKind,
		ulong actualBytes,
		ulong limitBytes,
		uint stride,
		string message)
		: base(message)
	{
		SourceFileIds = sourceFileIds ?? throw new ArgumentNullException(nameof(sourceFileIds));
		StreamIndex = streamIndex;
		BufferKind = bufferKind ?? "VertexBuffer";
		ActualBytes = actualBytes;
		LimitBytes = limitBytes;
		Stride = stride;
	}

	public IReadOnlyList<string> SourceFileIds { get; }

	public int StreamIndex { get; }

	public string BufferKind { get; }

	public ulong ActualBytes { get; }

	public ulong LimitBytes { get; }

	public uint Stride { get; }
}

public static class SectionalMergeOverflowAnalyzer
{
	public static string FormatMiB(ulong bytes)
		=> string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024d):F1} MiB");

	public static async Task<SectionalMergeOverflowContext> AnalyzeAsync(
		string modDirectory,
		string? modDisplayName,
		string payloadDirectory,
		string sourcePatchPath,
		string mergedSlotDisplayName,
		IReadOnlyList<string> sourceUnitIds,
		ulong actualBufferBytes,
		ulong limitBytes,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabledSections,
		bool canFallbackToUnmerged,
		CancellationToken cancellationToken = default)
	{
		var effectiveDisplayName = !string.IsNullOrWhiteSpace(modDisplayName)
			? modDisplayName.Trim()
			: ModSettingsFile.TryRead(modDirectory)?.Name is { Length: > 0 } configured
				? configured.Trim()
				: Path.GetFileName(modDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

		var components = await SectionGroupFile.TryReadAsync(payloadDirectory, cancellationToken).ConfigureAwait(false);
		var sectionIndicesByUnit = await ReadSectionIndicesByUnitAsync(sourcePatchPath, cancellationToken).ConfigureAwait(false);
		return Analyze(
			effectiveDisplayName,
			mergedSlotDisplayName,
			sourceUnitIds,
			actualBufferBytes,
			limitBytes,
			components,
			sectionIndicesByUnit,
			disabledSections,
			canFallbackToUnmerged);
	}

	public static SectionalMergeOverflowContext Analyze(
		string modDisplayName,
		string mergedSlotDisplayName,
		IReadOnlyList<string> sourceUnitIds,
		ulong actualBufferBytes,
		ulong limitBytes,
		SectionGroupDocument? components,
		IReadOnlyDictionary<string, IReadOnlyList<ulong>> sectionIndicesByUnit,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabledSections,
		bool canFallbackToUnmerged)
	{
		ArgumentNullException.ThrowIfNull(sourceUnitIds);
		ArgumentNullException.ThrowIfNull(sectionIndicesByUnit);

		var normalizedDisabled = NormalizeDisabled(disabledSections);
		var mergeUnits = sourceUnitIds.Select(NormalizeUnit).Where(unit => unit.Length != 0).ToHashSet(StringComparer.Ordinal);
		var normalizedIndicesByUnit = sectionIndicesByUnit.ToDictionary(
			pair => NormalizeUnit(pair.Key),
			pair => pair.Value,
			StringComparer.Ordinal);

		bool IsDisabled(string unit, int section)
			=> normalizedDisabled.TryGetValue(unit, out var set) && set.Contains(section);

		ulong totalEnabledMergeIndices = 0;
		foreach (var unit in mergeUnits)
		{
			if (!normalizedIndicesByUnit.TryGetValue(unit, out var sectionIndices)) continue;
			for (var section = 0; section < sectionIndices.Count; section++)
			{
				if (!IsDisabled(unit, section))
					totalEnabledMergeIndices = checked(totalEnabledMergeIndices + sectionIndices[section]);
			}
		}

		var candidates = new List<(
			SectionGroup Group,
			string DisplayName,
			ulong MergeIndices,
			ulong EstimatedBytes,
			HashSet<string> EnabledCoveredUnits)>();

		if (components is not null)
		{
			foreach (var group in components.Groups)
			{
				if (group.Members.Count == 0) continue;
				var anyEnabledAnywhere = group.Members.Any(member =>
				{
					var unit = NormalizeUnit(member.Unit);
					return member.Sections.Any(section => !IsDisabled(unit, section));
				});
				if (!anyEnabledAnywhere) continue;

				ulong mergeIndices = 0;
				var coveredUnits = new HashSet<string>(StringComparer.Ordinal);
				foreach (var member in group.Members)
				{
					var unit = NormalizeUnit(member.Unit);
					if (member.Sections.Any(section => !IsDisabled(unit, section)))
						coveredUnits.Add(unit);
					if (!mergeUnits.Contains(unit) || !normalizedIndicesByUnit.TryGetValue(unit, out var sectionIndices))
						continue;
					foreach (var section in member.Sections)
					{
						if (section >= 0 && section < sectionIndices.Count && !IsDisabled(unit, section))
							mergeIndices = checked(mergeIndices + sectionIndices[section]);
					}
				}

				if (mergeIndices == 0) continue;
				var estimatedBytes = totalEnabledMergeIndices == 0
					? 0UL
					: (ulong)Math.Round((double)actualBufferBytes * mergeIndices / totalEnabledMergeIndices);
				candidates.Add((group, ShortenDisplayName(group.Name, modDisplayName), mergeIndices, estimatedBytes, coveredUnits));
			}
		}

		// Classify ExplicitExclusive conflicts first.
		var explicitConflictSets = candidates
			.Where(item => !string.IsNullOrWhiteSpace(item.Group.ExclusiveGroup))
			.GroupBy(item => item.Group.ExclusiveGroup!, StringComparer.OrdinalIgnoreCase)
			.Where(bucket => bucket.Count() >= 2)
			.ToDictionary(bucket => bucket.Key, bucket => bucket.Select(item => item.Group.EffectiveId).ToList(), StringComparer.OrdinalIgnoreCase);

		// Classify SuspectedVariant among remaining groups:
		// Two groups without explicit conflict that overlap on >= 2 enabled units AND each carries >= 12% of limitBytes (or >= 16 MiB when limit is 128 MiB).
		var variantMinBytes = Math.Max(1UL, limitBytes * 12UL / 100UL);
		var suspectedIds = new HashSet<string>(StringComparer.Ordinal);
		var suspectedPeers = candidates
			.Where(item => string.IsNullOrWhiteSpace(item.Group.ExclusiveGroup)
				|| !explicitConflictSets.ContainsKey(item.Group.ExclusiveGroup!))
			.Where(item => item.EstimatedBytes >= variantMinBytes)
			.ToList();
		for (var i = 0; i < suspectedPeers.Count; i++)
		{
			for (var j = i + 1; j < suspectedPeers.Count; j++)
			{
				var sharedUnits = suspectedPeers[i].EnabledCoveredUnits.Count(suspectedPeers[j].EnabledCoveredUnits.Contains);
				if (sharedUnits >= 2)
				{
					suspectedIds.Add(suspectedPeers[i].Group.EffectiveId);
					suspectedIds.Add(suspectedPeers[j].Group.EffectiveId);
				}
			}
		}

		// Determine initial RecommendedKeepEnabled selections:
		var keptIds = candidates.Select(item => item.Group.EffectiveId).ToHashSet(StringComparer.Ordinal);

		// 1) Explicit exclusive groups: keep first, uncheck rest.
		foreach (var ids in explicitConflictSets.Values)
		{
			foreach (var extraId in ids.Skip(1))
				keptIds.Remove(extraId);
		}

		var baseDisabledSnapshot = normalizedDisabled.ToDictionary(
			pair => pair.Key,
			pair => (IReadOnlyCollection<int>)pair.Value.Order().ToArray(),
			StringComparer.Ordinal);

		ulong ComputeProjected(IReadOnlySet<string> currentKept)
			=> ProjectRemainingBytesCore(
				mergeUnits,
				actualBufferBytes,
				totalEnabledMergeIndices,
				normalizedIndicesByUnit,
				normalizedDisabled,
				candidates.Select(item => (item.Group.EffectiveId, item.Group.Members)).ToArray(),
				currentKept);

		// 2) Suspected variants: keep first, uncheck subsequent suspected variants until < limitBytes.
		var orderedSuspected = candidates
			.Where(item => suspectedIds.Contains(item.Group.EffectiveId))
			.Select(item => item.Group.EffectiveId)
			.ToList();
		if (orderedSuspected.Count >= 2)
		{
			foreach (var extraVariantId in orderedSuspected.Skip(1))
			{
				if (ComputeProjected(keptIds) < limitBytes)
					break;
				keptIds.Remove(extraVariantId);
			}
		}

		// 3) If still >= limitBytes, uncheck largest remaining groups (leaving at least 1 if possible) until < limitBytes.
		if (ComputeProjected(keptIds) >= limitBytes && candidates.Count > 1)
		{
			foreach (var candidate in candidates
				.OrderByDescending(item => item.EstimatedBytes)
				.Select(item => item.Group.EffectiveId))
			{
				if (ComputeProjected(keptIds) < limitBytes || keptIds.Count <= 1)
					break;
				keptIds.Remove(candidate);
			}
		}

		var items = candidates
			.Select(item =>
			{
				var id = item.Group.EffectiveId;
				var kind = SectionalOverflowConflictKind.None;
				string? label = null;
				if (!string.IsNullOrWhiteSpace(item.Group.ExclusiveGroup)
					&& explicitConflictSets.ContainsKey(item.Group.ExclusiveGroup!))
				{
					kind = SectionalOverflowConflictKind.ExplicitExclusive;
					label = $"互斥组冲突（{item.Group.ExclusiveGroup}）";
				}
				else if (suspectedIds.Contains(id))
				{
					kind = SectionalOverflowConflictKind.SuspectedVariant;
					label = "疑似互斥变体";
				}

				return new SectionalMergeOverflowGroupItem(
					id,
					item.Group.Name,
					item.DisplayName,
					kind,
					label,
					item.EstimatedBytes,
					item.MergeIndices,
					keptIds.Contains(id),
					item.Group.Members);
			})
			.ToArray();

		return new SectionalMergeOverflowContext(
			modDisplayName,
			mergedSlotDisplayName,
			[.. mergeUnits.Order(StringComparer.Ordinal)],
			actualBufferBytes,
			limitBytes,
			totalEnabledMergeIndices,
			canFallbackToUnmerged,
			items,
			normalizedIndicesByUnit,
			baseDisabledSnapshot);
	}

	public static ulong ProjectRemainingBytes(
		SectionalMergeOverflowContext context,
		IReadOnlySet<string> keptGroupIds)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(keptGroupIds);
		return ProjectRemainingBytesCore(
			context.SourceUnitIds.Select(NormalizeUnit).ToHashSet(StringComparer.Ordinal),
			context.ActualBufferBytes,
			context.TotalEnabledMergeIndices,
			context.SectionIndicesByUnit,
			NormalizeDisabled(context.BaseDisabledSections),
			context.Groups.Select(group => (group.GroupId, group.Members)).ToArray(),
			keptGroupIds);
	}

	public static IReadOnlyDictionary<string, IReadOnlyCollection<int>> ApplyDisabledGroups(
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? baseDisabled,
		SectionGroupDocument? components,
		IReadOnlyCollection<string> disabledGroupIds)
	{
		var merged = NormalizeDisabled(baseDisabled);
		if (components is not null && disabledGroupIds.Count != 0)
		{
			var ids = disabledGroupIds.ToHashSet(StringComparer.Ordinal);
			foreach (var group in components.Groups.Where(group => ids.Contains(group.EffectiveId)))
			{
				foreach (var member in group.Members)
				{
					var unit = NormalizeUnit(member.Unit);
					if (unit.Length == 0) continue;
					if (!merged.TryGetValue(unit, out var set))
						merged[unit] = set = [];
					foreach (var section in member.Sections.Where(section => section >= 0))
						set.Add(section);
				}
			}
		}

		return merged
			.Where(pair => pair.Value.Count != 0)
			.ToDictionary(
				pair => pair.Key,
				pair => (IReadOnlyCollection<int>)pair.Value.Order().ToArray(),
				StringComparer.Ordinal);
	}

	public static string DescribeConflicts(SectionalMergeOverflowContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		var conflicts = context.Groups
			.Where(group => group.ConflictKind != SectionalOverflowConflictKind.None)
			.ToArray();
		if (conflicts.Length != 0)
		{
			return $"检测到冲突/变体组件同时开启：{string.Join("、", conflicts.Select(group => $"{group.DisplayName}（~{FormatMiB(group.EstimatedBytes)}）"))}";
		}

		if (context.Groups.Count != 0)
		{
			return $"参与合并的高占用组件：{string.Join("、", context.Groups.Take(4).Select(group => $"{group.DisplayName}（~{FormatMiB(group.EstimatedBytes)}）"))}";
		}

		return "该模组没有可单独开关的组件组";
	}

	private static ulong ProjectRemainingBytesCore(
		IReadOnlySet<string> mergeUnits,
		ulong actualBufferBytes,
		ulong totalEnabledMergeIndices,
		IReadOnlyDictionary<string, IReadOnlyList<ulong>> sectionIndicesByUnit,
		IReadOnlyDictionary<string, HashSet<int>> baseDisabled,
		IReadOnlyList<(string GroupId, IReadOnlyList<SectionGroupTarget> Members)> groups,
		IReadOnlySet<string> keptGroupIds)
	{
		if (totalEnabledMergeIndices == 0) return actualBufferBytes;

		var newlyDisabled = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
		foreach (var (groupId, members) in groups)
		{
			if (keptGroupIds.Contains(groupId)) continue;
			foreach (var member in members)
			{
				var unit = NormalizeUnit(member.Unit);
				if (!mergeUnits.Contains(unit)) continue;
				if (!newlyDisabled.TryGetValue(unit, out var set))
					newlyDisabled[unit] = set = [];
				foreach (var section in member.Sections.Where(section => section >= 0))
					set.Add(section);
			}
		}

		if (newlyDisabled.Count == 0) return actualBufferBytes;

		ulong removedIndices = 0;
		foreach (var (unit, sections) in newlyDisabled)
		{
			if (!sectionIndicesByUnit.TryGetValue(unit, out var unitIndices)) continue;
			var alreadyOff = baseDisabled.GetValueOrDefault(unit);
			foreach (var section in sections)
			{
				if (section < 0 || section >= unitIndices.Count) continue;
				if (alreadyOff is not null && alreadyOff.Contains(section)) continue;
				removedIndices = checked(removedIndices + unitIndices[section]);
			}
		}

		if (removedIndices >= totalEnabledMergeIndices) return 0UL;
		var remainingIndices = totalEnabledMergeIndices - removedIndices;
		return (ulong)Math.Round((double)actualBufferBytes * remainingIndices / totalEnabledMergeIndices);
	}

	private static async Task<IReadOnlyDictionary<string, IReadOnlyList<ulong>>> ReadSectionIndicesByUnitAsync(
		string patchTocPath,
		CancellationToken cancellationToken)
	{
		var entries = await new AdaptScanner().ScanEntriesAsync(patchTocPath, cancellationToken).ConfigureAwait(false);
		var reader = new PatchUnitMeshReader();
		var result = new Dictionary<string, IReadOnlyList<ulong>>(StringComparer.Ordinal);
		foreach (var entry in entries.Where(candidate => candidate.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId))
		{
			var fileId = entry.AssetKey.FileId.ToString("x16", CultureInfo.InvariantCulture);
			try
			{
				var unit = await reader.ReadStructureOnlyAsync(
					entry, entries, PatchUnitDependencyPolicy.RequirePatchLocalComposite, cancellationToken).ConfigureAwait(false);
				var visibleMeshes = unit.Model.Meshes.Where(mesh => mesh.LodIndex >= 0 && mesh.Sections.Count != 0).ToArray();
				if (visibleMeshes.Length == 0) continue;
				var maxSections = visibleMeshes.Max(mesh => mesh.Sections.Count);
				var totals = new ulong[maxSections];
				foreach (var mesh in visibleMeshes)
				{
					for (var index = 0; index < mesh.Sections.Count; index++)
						totals[index] = checked(totals[index] + mesh.Sections[index].NumIndices);
				}

				result[fileId] = totals;
			}
			catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
			{
				// Ignore unreadable entries; WardrobeSectionMapFile skips them on the same criteria.
			}
		}

		return result;
	}

	private static string ShortenDisplayName(string groupName, string modDisplayName)
	{
		if (string.IsNullOrWhiteSpace(groupName)) return "未命名组件";
		var trimmed = groupName.Trim();
		if (!string.IsNullOrWhiteSpace(modDisplayName))
		{
			var modPrefix = modDisplayName.Trim();
			if (trimmed.Length > modPrefix.Length
				&& trimmed.StartsWith(modPrefix, StringComparison.OrdinalIgnoreCase))
			{
				var rest = trimmed[modPrefix.Length..].TrimStart('-', '_', ' ', '·', '/');
				if (rest.Length != 0) return rest;
			}
		}

		return trimmed;
	}

	private static Dictionary<string, HashSet<int>> NormalizeDisabled(
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabled)
	{
		var result = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
		if (disabled is null) return result;
		foreach (var (rawUnit, sections) in disabled)
		{
			var unit = NormalizeUnit(rawUnit);
			if (unit.Length == 0 || sections.Count == 0) continue;
			if (!result.TryGetValue(unit, out var set))
				result[unit] = set = [];
			foreach (var section in sections.Where(index => index >= 0))
				set.Add(section);
		}

		return result;
	}

	private static string NormalizeUnit(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
		var text = raw.Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
		return text.ToLowerInvariant();
	}
}
