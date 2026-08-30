using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// Adds the deployable output of one already-committed import operation to the
/// exact Profile frozen in its reviewed plan. The caller owns the library write
/// lease; this helper deliberately does not deploy or alter Option state.
/// </summary>
internal sealed record CliImportProfileAddition(
	string ProfileId,
	string ProfileName,
	long PreviousRevision,
	long CurrentRevision,
	IReadOnlyList<string> AddedNodeIds,
	IReadOnlyList<string> SkippedNodeIds);

internal static class CliImportProfileIntegration
{
	public static async ValueTask<CliImportProfileAddition?> AddAsync(
		StoragePaths paths,
		CliImportProfileTarget? target,
		IReadOnlyCollection<ModNodeId> importedNodeIds,
		CancellationToken cancellationToken = default)
	{
		if (target is null) return null;
		if (!Guid.TryParse(target.ProfileId, out var rawProfileId))
			throw new InvalidDataException("导入计划中的目标配置文件 ID 无效。");
		if (!PathsEqual(target.LibraryRoot, paths.ModsDirectory))
			throw new InvalidDataException("导入计划固定的 Mod 库与当前写入目标不同；已拒绝把导入内容加入错误配置文件。 ");

		var store = new JsonModLibraryStore(paths, allowLegacyMigration: false);
		var snapshot = await store.TryLoadReadOnlyAsync(cancellationToken).ConfigureAwait(false)
			?? throw new InvalidDataException("导入完成后无法重新读取 Mod 库，不能安全加入配置文件。");
		var profileId = new ProfileId(rawProfileId);
		var profile = snapshot.Profiles.FirstOrDefault(item => item.Id == profileId)
			?? throw new InvalidDataException($"导入后目标配置文件已不存在：{target.ProfileName}。");
		if (!string.Equals(profile.Name, target.ProfileName, StringComparison.Ordinal)
			|| profile.Revision != target.ProfileRevision)
		{
			throw new InvalidDataException("导入期间目标配置文件已变化；已保留导入内容，但拒绝把它加入可能不同的配置文件。");
		}

		var existing = profile.Entries.Select(entry => entry.NodeId).ToHashSet();
		var deployable = new List<ModNodeId>();
		var skipped = new List<string>();
		foreach (var nodeId in importedNodeIds.Distinct())
		{
			if (!snapshot.Nodes.TryGetValue(nodeId, out var node)
				|| existing.Contains(nodeId)
				|| !CanJoinProfile(node))
			{
				skipped.Add(CliProjections.FormatId(nodeId));
				continue;
			}
			deployable.Add(nodeId);
		}

		if (deployable.Count == 0)
		{
			return new CliImportProfileAddition(
				CliProjections.FormatId(profile.Id),
				profile.Name,
				profile.Revision,
				profile.Revision,
				Array.Empty<string>(),
				skipped);
		}

		var mutation = CoreServices.CreateProfileMutationService().AddEntries(snapshot, profile.Id, deployable);
		if (!mutation.Succeeded)
			throw new InvalidDataException($"导入内容无法加入配置文件：{mutation.Message ?? mutation.Failure?.ToString() ?? "未知原因"}。");
		if (mutation.Changed)
			await store.SaveAsync(mutation.Snapshot, cancellationToken).ConfigureAwait(false);
		var persisted = mutation.Changed
			? await store.TryLoadReadOnlyAsync(cancellationToken).ConfigureAwait(false)
			: snapshot;
		if (persisted is null)
			throw new InvalidDataException("保存后无法重新读取 Mod 库。");
		var persistedProfile = persisted.Profiles.FirstOrDefault(item => item.Id == profile.Id)
			?? throw new InvalidDataException("保存后无法重新定位目标配置文件。");
		return new CliImportProfileAddition(
			CliProjections.FormatId(profile.Id),
			persistedProfile.Name,
			profile.Revision,
			persistedProfile.Revision,
			deployable.Select(CliProjections.FormatId).ToArray(),
			skipped);
	}

	private static bool CanJoinProfile(ModNode node)
		=> node.Metadata.Kind is ModNodeKind.Standard or ModNodeKind.ReShade
			&& (node.Metadata.Kind == ModNodeKind.ReShade || node.PatchGroups.Count != 0);

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
}
