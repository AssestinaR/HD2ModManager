using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <param name="FromPath">搬走之前的位置。</param>
/// <param name="ToPath">搬过去之后的位置。</param>
public sealed record MaterialFoldMove(string FromPath, string ToPath);

public sealed record MaterialFoldExecutionResult(
	bool Succeeded,
	IReadOnlyList<MaterialFoldMove> Moves,
	IReadOnlyList<string> Warnings,
	string? Error)
{
	public static MaterialFoldExecutionResult Failure(string error, IReadOnlyList<string>? warnings = null)
		=> new(false, Array.Empty<MaterialFoldMove>(), warnings ?? Array.Empty<string>(), error);
}

/// <summary>
/// 执行一份 <see cref="MaterialFoldPlan"/>：把成员的材质 patch 搬进宿主的基底目录（改名成
/// <c>patch_N</c>），并把成员的来源 <c>*.vpk</c> 一起搬到宿主根上。
/// <para>
/// <b>它只做"往宿主里搬"，不动库、也不删成员目录。</b>退役成员节点是调用方的事：
/// 先搬完、再核对宿主确实拿到了东西，最后才把成员从库里摘掉。把这三件事分开，
/// 任何一步失败都停在"数据都还在"的状态，而不是"两边都没有"。
/// </para>
/// <para>
/// <b>为什么连来源 VPK 一起搬。</b>折并的目的就是让宿主成为自洽单元——复制文件夹就能带走。
/// 只搬 patch 而把 VPK 留在成员目录里，宿主就再也没法重构建（重构建要从来源 VPK 重转）。
/// </para>
/// <para>
/// <b>为什么用移动而不是复制。</b>复制会让成员继续持有同一份 patch，于是它在部署里仍然生效，
/// 与宿主里的那份互相覆盖。移动之后成员只剩来源 VPK 与结构文件，退役它时不会丢任何权威数据。
/// </para>
/// </summary>
public static class MaterialFoldExecutor
{
	public static MaterialFoldExecutionResult Execute(MaterialFoldPlan plan, string hostModDirectory)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentException.ThrowIfNullOrWhiteSpace(hostModDirectory);

		if (plan.Blockers.Count != 0)
			return MaterialFoldExecutionResult.Failure(
				"折并计划有阻断项，未执行：" + string.Join("；", plan.Blockers.Select(blocker => blocker.Message))
				+ "（修好后重新生成计划即可。）");
		if (plan.Entries.Count == 0)
			return MaterialFoldExecutionResult.Failure("折并计划里没有可搬的成员。");

		var hostPayload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(hostModDirectory)
			?? ModNodePayloadLayout.GetVpkPatchDirectory(hostModDirectory);
		if (!Directory.Exists(hostPayload))
			return MaterialFoldExecutionResult.Failure($"宿主的基底目录不存在：{hostPayload}");

		// 先整体体检再动手：搬到一半才发现某个目标名已被占用，会留下"一半进来了"的状态。
		var collisions = plan.Entries
			.Where(entry => OverwritePatchInstaller.SidecarSuffixes.Any(suffix =>
				File.Exists(Path.Combine(hostPayload, entry.TargetPatchFileName + suffix))))
			.Select(entry => entry.TargetPatchFileName)
			.ToArray();
		if (collisions.Length != 0)
			return MaterialFoldExecutionResult.Failure(
				"宿主的基底目录里已经有同名 patch，未执行：" + string.Join("、", collisions)
				+ "（计划可能已过期，重新生成一次即可。）");

		var moves = new List<MaterialFoldMove>();
		// 计划里被跳过的成员照样说出来，但不影响其余成员折并。
		var warnings = new List<string>(plan.Skipped);
		foreach (var entry in plan.Entries.OrderBy(item => item.TargetPatchIndex))
		{
			try
			{
				MovePatchWithSidecars(entry, hostPayload, moves);
				MoveSourceVpks(entry, hostModDirectory, moves, warnings);
			}
			catch (Exception exception) when (exception is IOException
				or UnauthorizedAccessException
				or System.Security.SecurityException)
			{
				// 已经搬过去的不回退：报告里逐条列着 from/to，调用方（或用户）能据此还原。
				return new MaterialFoldExecutionResult(
					false, moves, warnings,
					$"搬 {entry.ModName} 失败：{exception.Message}");
			}
		}

		RefreshManifest(hostPayload, hostModDirectory, plan, warnings);
		return new MaterialFoldExecutionResult(true, moves, warnings, null);
	}

	private static void MovePatchWithSidecars(MaterialFoldEntry entry, string hostPayload, List<MaterialFoldMove> moves)
	{
		var target = Path.Combine(hostPayload, entry.TargetPatchFileName);
		foreach (var suffix in OverwritePatchInstaller.SidecarSuffixes)
		{
			var from = entry.SourcePatchPath + suffix;
			if (!File.Exists(from)) continue;
			var to = target + suffix;
			VpkPayloadSwap.ClearReadOnly(from);
			File.Move(from, to);
			moves.Add(new MaterialFoldMove(from, to));
		}
	}

	private static void MoveSourceVpks(
		MaterialFoldEntry entry,
		string hostModDirectory,
		List<MaterialFoldMove> moves,
		List<string> warnings)
	{
		if (!Directory.Exists(entry.MemberRootDirectory)) return;
		foreach (var vpk in Directory.EnumerateFiles(entry.MemberRootDirectory, "*.vpk", SearchOption.TopDirectoryOnly))
		{
			var destination = Path.Combine(hostModDirectory, Path.GetFileName(vpk));
			if (File.Exists(destination))
			{
				// 宿主已经有同名来源：**不覆盖**。两份不同来源用了同一个文件名时，
				// 覆盖会让其中一个来源永久消失——那不可逆，所以只报告。
				warnings.Add($"{entry.ModName} 的来源 {Path.GetFileName(vpk)} 与宿主里已有的同名文件冲突，未搬。");
				continue;
			}
			VpkPayloadSwap.ClearReadOnly(vpk);
			File.Move(vpk, destination);
			moves.Add(new MaterialFoldMove(vpk, destination));
		}
	}

	/// <summary>
	/// 重写 <c>patches.json</c>：每个材质 patch 都要有一个**选项名**，它才是用户在界面上看到的那一行。
	/// <para>
	/// 折并进来的材质包用成员自己的名字。宿主自己那次转换产出的材质 patch 也是选项之一，
	/// 名字固定为「模型内置材质」——它和别的材质包是同一类东西（一个 patch、可勾选），
	/// 不该被当成"永远生效的基底"而特殊对待。
	/// </para>
	/// </summary>
	private static void RefreshManifest(
		string hostPayload,
		string hostModDirectory,
		MaterialFoldPlan plan,
		List<string> warnings)
	{
		var report = VpkConversionReport.TryRead(hostModDirectory);
		var optionNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (report is not null)
		{
			foreach (var patch in report.Patches)
			{
				if (string.IsNullOrWhiteSpace(patch.MaterialPatch)) continue;
				optionNames[patch.MaterialPatch] = SectionalPatchManifest.BuiltInMaterialOptionName;
			}
		}
		foreach (var entry in plan.Entries)
			optionNames[entry.TargetPatchFileName] = entry.ModName;

		var document = SectionalPatchManifestFile.Build(
			hostPayload,
			SectionalPatchManifestFile.ReadArchiveHex16(hostPayload),
			report,
			DateTimeOffset.UtcNow,
			optionNames);
		var error = SectionalPatchManifestFile.TryWrite(hostPayload, document);
		if (error is not null)
			warnings.Add($"折并完成，但 patches.json 写不进去：{error}");
	}
}
