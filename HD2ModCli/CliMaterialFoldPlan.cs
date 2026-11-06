using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliMaterialFoldEntryProjection(
	string NodeId,
	string ModName,
	string SourcePatchPath,
	string TargetPatchFileName,
	int TargetPatchIndex,
	bool Enabled,
	int Order);

internal sealed record CliMaterialFoldHostProjection(
	string HostNodeId,
	string HostName,
	string HostRelativePath,
	string PayloadDirectory,
	string ArchiveHex16,
	IReadOnlyList<string> ExistingPatchFileNames,
	IReadOnlyList<CliMaterialFoldEntryProjection> Entries,
	IReadOnlyList<string> Blockers,
	IReadOnlyList<string> Skipped);

internal sealed record CliMaterialFoldPlanProjection(
	IReadOnlyList<CliMaterialFoldHostProjection> Hosts)
{
	public int HostCount => Hosts.Count;
	public int EntryCount => Hosts.Sum(host => host.Entries.Count);
	public int BlockerCount => Hosts.Sum(host => host.Blockers.Count);
	public bool CanApply => BlockerCount == 0 && EntryCount != 0;
}

/// <summary>折并的输入：一个宿主 + 它的材质成员。</summary>
internal sealed record CliMaterialFoldHost(
	ModNode Host,
	string HostDirectory,
	string PayloadDirectory,
	IReadOnlyList<MaterialFoldMember> Members);

/// <summary>
/// 「把材质 Option 折进宿主」的**只读**计划命令。
/// <para>
/// 这是会移动用户库文件的迁移，所以先只给计划：哪些宿主、搬哪些成员、搬到哪个序号、
/// 哪个是当前启用项、有什么挡着。执行是另一步，且要先备份。
/// </para>
/// </summary>
internal static class CliMaterialFoldPlan
{
	public static IReadOnlyList<CliMaterialFoldHost> BuildHosts(CliWorkspace workspace, CliLibraryState state)
	{
		var libraryRoot = workspace.LibraryRoot;
		if (string.IsNullOrWhiteSpace(libraryRoot)) return Array.Empty<CliMaterialFoldHost>();

		var graph = OptionRelationshipGraph.Create(state.Snapshot);
		var hosts = new List<CliMaterialFoldHost>();
		foreach (var host in state.Snapshot.Nodes.Values
			.OrderBy(node => node.Metadata.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(node => node.Id.Value))
		{
			var children = graph.GetDirectOptions(host.Id);
			if (children.Count == 0) continue;

			var hostDirectory = Path.Combine(libraryRoot, host.RelativePath);
			var hostPayload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(hostDirectory);
			// 没有基底目录的宿主不是折并对象（它没有 patch_0 可当 unit 层）。
			if (hostPayload is null) continue;

			var hostIdN = host.Id.Value.ToString("N");
			var members = children.Select(child =>
			{
				var childDirectory = Path.Combine(libraryRoot, child.RelativePath);
				var childPayload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(childDirectory)
					// 不存在时给一个确定不存在的路径：planner 会据此把它列为"跳过"，
					// 而不是把"没有 payload 的成员"当合法输入静默忽略。
					?? ModNodePayloadLayout.GetVpkPatchDirectory(childDirectory);
				var childIdN = child.Id.Value.ToString("N");
				var enabled = state.OptionActivations.IsEnabled(childIdN, hostIdN);
				// 顺序取**生效顺序**：`Metadata.OptionOrder` 在这些 mod 上普遍是 null，
				// 全当成 int.MaxValue 之后就只剩按名字排，而名字顺序与部署顺序无关。
				var order = state.OptionActivations.Options.TryGetValue(childIdN, out var optionHosts)
					&& optionHosts.TryGetValue(hostIdN, out var hostState)
					&& hostState.EffectiveOrder is { } effective
						? effective
						: child.Metadata.OptionOrder ?? int.MaxValue;
				return new MaterialFoldMember(childIdN, child.Metadata.Name, childPayload, enabled, order);
			}).ToArray();

			hosts.Add(new CliMaterialFoldHost(host, hostDirectory, hostPayload, members));
		}
		return hosts;
	}

	public static CliMaterialFoldPlanProjection Build(CliWorkspace workspace, CliLibraryState state)
	{
		var hosts = new List<CliMaterialFoldHostProjection>();
		foreach (var host in BuildHosts(workspace, state))
		{
			var plan = MaterialOptionFoldPlanner.Plan(host.PayloadDirectory, host.Members);
			hosts.Add(new CliMaterialFoldHostProjection(
				host.Host.Id.Value.ToString("N"),
				host.Host.Metadata.Name,
				host.Host.RelativePath,
				host.PayloadDirectory,
				plan.ArchiveHex16,
				ReadPatchFileNames(host.PayloadDirectory),
				plan.Entries
					.Select(entry => new CliMaterialFoldEntryProjection(
						entry.NodeId, entry.ModName, entry.SourcePatchPath,
						entry.TargetPatchFileName, entry.TargetPatchIndex, entry.Enabled, entry.Order))
					.ToArray(),
				plan.Blockers.Select(blocker => blocker.Message).ToArray(),
				plan.Skipped));
		}
		return new CliMaterialFoldPlanProjection(hosts);
	}

	public static string Format(CliMaterialFoldPlanProjection projection)
	{
		ArgumentNullException.ThrowIfNull(projection);
		var lines = new List<string>
		{
			$"材质折并计划（只读）：{projection.HostCount} 个宿主、{projection.EntryCount} 个成员、{projection.BlockerCount} 个阻断项。",
		};
		foreach (var host in projection.Hosts)
		{
			lines.Add(string.Empty);
			lines.Add($"{host.HostName}  （{host.HostRelativePath}）");
			lines.Add($"  基底目录：{host.PayloadDirectory}");
			lines.Add($"  现有 patch：{(host.ExistingPatchFileNames.Count == 0 ? "（无）" : string.Join("、", host.ExistingPatchFileNames))}");
			foreach (var entry in host.Entries)
			{
				lines.Add($"    -> {entry.TargetPatchFileName}  {entry.ModName}{(entry.Enabled ? "  【当前启用】" : string.Empty)}");
			}
			foreach (var blocker in host.Blockers)
				lines.Add($"    !! {blocker}");
			foreach (var skip in host.Skipped)
				lines.Add($"    .. 跳过 {skip}");
		}
		if (projection.HostCount == 0)
			lines.Add("没有可折并的宿主（库里没有带基底目录且含材质 Option 的 Mod）。");
		return string.Join(Environment.NewLine, lines);
	}

	private static IReadOnlyList<string> ReadPatchFileNames(string payloadDirectory)
	{
		if (!Directory.Exists(payloadDirectory)) return Array.Empty<string>();
		return Directory
			.EnumerateFiles(payloadDirectory, "*.patch_*", SearchOption.TopDirectoryOnly)
			.Select(Path.GetFileName)
			.Where(name => name is not null
				&& !name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase)
				&& !name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase))
			.Select(name => name!)
			.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}
}
