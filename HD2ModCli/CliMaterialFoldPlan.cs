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
	string? SelectedTargetPatchFileName,
	IReadOnlyList<CliMaterialFoldEntryProjection> Entries,
	IReadOnlyList<string> Blockers,
	IReadOnlyList<string> Warnings);

internal sealed record CliMaterialFoldPlanProjection(
	IReadOnlyList<CliMaterialFoldHostProjection> Hosts)
{
	public int HostCount => Hosts.Count;
	public int EntryCount => Hosts.Sum(host => host.Entries.Count);
	public int BlockerCount => Hosts.Sum(host => host.Blockers.Count);
	public bool CanApply => BlockerCount == 0 && EntryCount != 0;
}

/// <summary>
/// 「把材质 Option 折进宿主」的**只读**计划命令。
/// <para>
/// 这是会移动用户库文件的迁移，所以先只给计划：哪些宿主、搬哪些成员、搬到哪个序号、
/// 哪个是当前启用项、有什么挡着。执行是另一步，且要先备份。
/// </para>
/// </summary>
internal static class CliMaterialFoldPlan
{
	public static CliMaterialFoldPlanProjection Build(CliWorkspace workspace, CliLibraryState state)
	{
		var libraryRoot = workspace.LibraryRoot;
		if (string.IsNullOrWhiteSpace(libraryRoot))
			return new CliMaterialFoldPlanProjection(Array.Empty<CliMaterialFoldHostProjection>());

		var graph = OptionRelationshipGraph.Create(state.Snapshot);
		var hosts = new List<CliMaterialFoldHostProjection>();
		foreach (var host in state.Snapshot.Nodes.Values
			.OrderBy(node => node.Metadata.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(node => node.Id.Value))
		{
			var children = graph.GetDirectOptions(host.Id);
			if (children.Count == 0) continue;

			var hostDirectory = Path.Combine(libraryRoot, host.RelativePath);
			var hostPayload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(hostDirectory);
			// 没有基底目录的宿主不是"折并对象"（它没有 patch_0 可当 unit 层）。
			if (hostPayload is null) continue;

			var hostIdN = host.Id.Value.ToString("N");
			var members = children
				.Select(child =>
				{
					var childDirectory = Path.Combine(libraryRoot, child.RelativePath);
					var childPayload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(childDirectory)
						// 不存在时给一个确定不存在的路径：planner 会据此报 MemberHasNoPatch，
					// 而不是把"没有 payload 的成员"当成合法输入静默跳过。
						?? ModNodePayloadLayout.GetVpkPatchDirectory(childDirectory);
					var childIdN = child.Id.Value.ToString("N");
					var enabled = state.OptionActivations.IsEnabled(childIdN, hostIdN);
					// **顺序必须用生效顺序。** `Metadata.OptionOrder` 在这些 mod 上普遍是 null，
					// 全当成 int.MaxValue 之后就只剩"按名字排"——而名字顺序与部署顺序无关，
					// 于是"当前生效的是哪一份材质"会算错，折并后外观会悄悄变成另一个颜色。
					var order = state.OptionActivations.Options.TryGetValue(childIdN, out var hosts)
						&& hosts.TryGetValue(hostIdN, out var hostState)
						&& hostState.EffectiveOrder is { } effective
							? effective
							: child.Metadata.OptionOrder ?? int.MaxValue;
					return new MaterialFoldMember(
						childIdN,
						child.Metadata.Name,
						childPayload,
						enabled,
						order);
				})
				.ToArray();

			var plan = MaterialOptionFoldPlanner.Plan(hostPayload, members);
			hosts.Add(new CliMaterialFoldHostProjection(
				hostIdN,
				host.Metadata.Name,
				host.RelativePath,
				hostPayload,
				plan.ArchiveHex16,
				ReadPatchFileNames(hostPayload),
				plan.SelectedTargetPatchFileName,
				plan.Entries
					.Select(entry => new CliMaterialFoldEntryProjection(
						entry.NodeId, entry.ModName, entry.SourcePatchPath,
						entry.TargetPatchFileName, entry.TargetPatchIndex, entry.Enabled, entry.Order))
					.ToArray(),
				plan.Blockers.Select(blocker => blocker.Message).ToArray(),
				DescribeEnabledOverlap(plan, members)));
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
			foreach (var warning in host.Warnings)
				lines.Add($"    ** {warning}");
			if (host.SelectedTargetPatchFileName is not null)
				lines.Add($"  折并后应选中：{host.SelectedTargetPatchFileName}");
		}
		if (projection.HostCount == 0)
			lines.Add("没有可折并的宿主（库里没有带基底目录且含材质 Option 的 Mod）。");
		return string.Join(Environment.NewLine, lines);
	}

	/// <summary>
	/// "同时启用了多个材质"这件事必须说出来：部署按顺序叠 patch、后者覆盖前者，
	/// 所以用户今天看到的是**顺序最靠后**的那一个；折并之后只留那一份，其余的变成可选。
	/// 不说清楚的话，用户会以为折并"删掉了"他的材质。
	/// </summary>
	private static IReadOnlyList<string> DescribeEnabledOverlap(
		MaterialFoldPlan plan,
		IReadOnlyList<MaterialFoldMember> members)
	{
		var enabled = plan.EnabledTargetPatchFileNames;
		if (enabled.Count <= 1) return Array.Empty<string>();
		var selected = plan.SelectedTargetPatchFileName;
		var selectedName = plan.Entries
			.FirstOrDefault(entry => string.Equals(entry.TargetPatchFileName, selected, StringComparison.Ordinal))
			?.ModName ?? selected ?? string.Empty;
		return
		[
			$"折并前同时启用了 {enabled.Count} 个材质（部署时后者覆盖前者）；"
			+ $"折并后保留「{selectedName}」（{selected}），其余 {enabled.Count - 1} 个留在基底目录里、可随时切换。",
		];
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
