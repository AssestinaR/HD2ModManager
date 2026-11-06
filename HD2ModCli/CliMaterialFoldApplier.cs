using HD2ModCore.Decoration;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliMaterialFoldApplyResult(
	bool Succeeded,
	int FoldedHosts,
	int MovedFiles,
	int RetiredNodes,
	IReadOnlyList<string> Notes,
	string? Error);

/// <summary>
/// 执行折并：把材质 Option 的 patch 搬进宿主、物化生效层、退役成员节点。
/// <para>
/// 三件事按顺序做，而且**只有前一件成功才做下一件**：搬 → 物化 → 退役。
/// 任何一步断了都停在"文件都还在"的状态，不会出现"宿主没拿到、成员也没了"。
/// </para>
/// </summary>
internal static class CliMaterialFoldApplier
{
	public static async ValueTask<CliMaterialFoldApplyResult> ApplyAsync(
		CliWorkspace workspace,
		CliLibraryState state,
		CliLibraryWriteSession session,
		CancellationToken cancellationToken = default)
	{
		var hosts = CliMaterialFoldPlan.BuildHosts(workspace, state);
		var notes = new List<string>();
		var retired = new List<ModNodeId>();
		var foldedHosts = 0;
		var movedFiles = 0;

		foreach (var host in hosts)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var plan = MaterialOptionFoldPlanner.Plan(host.PayloadDirectory, host.Members);
			if (!plan.CanApply)
			{
				var reason = plan.Blockers.Count != 0
					? string.Join("；", plan.Blockers.Select(blocker => blocker.Message))
					: "没有可折的材质包。";
				notes.Add($"{host.Host.Metadata.Name}：跳过（{reason}）");
				continue;
			}

			// ① 搬。
			var execution = MaterialFoldExecutor.Execute(plan, host.HostDirectory);
			notes.AddRange(execution.Warnings);
			if (!execution.Succeeded)
			{
				notes.Add($"{host.Host.Metadata.Name}：搬移失败，未改动库（{execution.Error}）");
				continue;
			}
			movedFiles += execution.Moves.Count;

			// ② 把（折并前就启用的）材质 patch 加进生效层。折并前的启用状态原样搬过来，
			//    所以用户看到的观感不该有任何变化。
			var materialization = AddMaterialsToOverwrite(host.HostDirectory, plan.EnabledTargetPatchFileNames);
			notes.AddRange(materialization.Warnings);
			if (!materialization.Succeeded)
			{
				notes.Add($"{host.Host.Metadata.Name}：文件已搬好，但加材质进生效层失败（{materialization.Error}）。重开管理器会再试一次。");
				continue;
			}

			// ③ 退役：成员的东西都搬走了，节点记录也该走。
			foreach (var entry in plan.Entries)
				retired.Add(new ModNodeId(Guid.ParseExact(entry.NodeId, "N")));
			foldedHosts++;
		}

		if (retired.Count == 0)
			return new CliMaterialFoldApplyResult(true, foldedHosts, movedFiles, 0, notes, null);

		var next = RemoveNodes(state.Snapshot, retired);
		// 成员节点从库里消失之后，配置里指向它们的条目也要一起收拾——
		// 否则用户的配置里会留下指向不存在节点的幽灵行。
		next = new ProfileStructuralMaintenanceService()
			.CleanupAfterMutation(state.Snapshot, next, retired.ToArray()).Snapshot;
		await session.SaveNodesOnlyAsync(next, cancellationToken).ConfigureAwait(false);
		return new CliMaterialFoldApplyResult(true, foldedHosts, movedFiles, retired.Count, notes, null);
	}

	/// <summary>
	/// 把启用的材质 patch 加进 <c>Overwrite/</c>，**只加不删**。
	/// <para>
	/// 已有生效层的宿主（老库里每个换过甲的都是）里面躺着换甲产物。这里刻意不做
	/// "整体重建"：那会把换甲后的 unit patch 换成未换甲的基底版本，用户选的护甲就失效了。
	/// 也不做归属检查：这些目录本来就是管理器自己写的，只是当时还没有标记这个东西——
	/// 在迁移里认领它们才对。认领只写一枚标记文件，不删任何东西。
	/// </para>
	/// <para>
	/// 没有生效层时走正常的物化（unit 层 + 材质），由 <see cref="OverwriteMaterializer"/> 一次建好。
	/// </para>
	/// </summary>
	private static OverwriteMaterializationResult AddMaterialsToOverwrite(
		string hostDirectory,
		IReadOnlyList<string> enabledMaterialFileNames)
	{
		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(hostDirectory);
		var payload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(hostDirectory);
		if (!Directory.Exists(overwrite) || !Directory.EnumerateFileSystemEntries(overwrite).Any())
			return OverwriteMaterializer.Materialize(hostDirectory, new OverwriteSelection(enabledMaterialFileNames));
		if (payload is null)
			return OverwriteMaterializationResult.Failure("宿主没有基底目录，无法把材质加进生效层。");

		var written = new List<string>();
		var warnings = new List<string>();
		foreach (var name in enabledMaterialFileNames)
		{
			var source = Path.Combine(payload, name);
			if (!File.Exists(source))
			{
				warnings.Add($"跳过了 {name}：它已不在基底目录里。");
				continue;
			}
			written.AddRange(OverwritePatchInstaller.Install(overwrite, source).WrittenFiles);
		}
		ManagedOverwriteDirectory.Commit(overwrite, ManagedOverwriteMarkers.VpkRekey);
		return new OverwriteMaterializationResult(true, written, warnings, null);
	}

	private static LibrarySnapshot RemoveNodes(LibrarySnapshot snapshot, IReadOnlyCollection<ModNodeId> removed)
	{
		var gone = removed.ToHashSet();
		var nodes = snapshot.Nodes
			.Where(pair => !gone.Contains(pair.Key))
			.ToDictionary(
				pair => pair.Key,
				pair => pair.Value with
				{
					Children = pair.Value.Children.Where(child => !gone.Contains(child)).ToArray(),
				});
		return snapshot with { Nodes = nodes };
	}

}
