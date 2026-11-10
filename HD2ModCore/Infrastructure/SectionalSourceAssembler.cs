using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>一个来源条目：库里的节点 id、它当时的名字、它在磁盘上的目录。</summary>
public sealed record SectionalSourceCandidate(string NodeId, string Name, string Directory);

/// <summary>装配结果。</summary>
public sealed record SectionalSourceAssemblyOutcome(
	bool Ok,
	string? Error,
	IReadOnlyList<string> Notes)
{
	public static SectionalSourceAssemblyOutcome Failed(string error, IReadOnlyList<string>? notes = null)
		=> new(false, error, notes ?? Array.Empty<string>());
}

/// <summary>
/// 「生成sectional版」的**导入侧**：把来源 patch 平铺拷进新条目根，并写下转换依据。
///
/// <para>
/// <b>它不转换。</b>按四段链路，导入只负责"把来源放进条目"；转换与编译是紧接着的
/// <see cref="Legacy2SectionalConverter"/>。vpk2patch 那条路早就是这个形状
/// （导入只放 <c>.vpk</c>，重构建才转），这里让 sectional 对齐。
/// </para>
///
/// <para>
/// <b>为什么来源必须是 legacy。</b>入口本身已经保证（详情页的「生成sectional版」
/// 对 <c>Vpk2Patch</c> / <c>Sectional</c> 是隐藏的），这里再挡一次是因为装配会**拷贝根上的 patch**：
/// 来源若是转换产物，它的 patch 在 payload 里，根上是空的或只有 <c>.vpk</c>，拷过去只会得到空条目。
/// </para>
/// </summary>
public static class SectionalSourceAssembler
{
	/// <summary>
	/// 建条目：按 <paramref name="sources"/> 的顺序把每个来源根上的 patch 平铺拷到 <paramref name="modDirectory"/>，
	/// 再写 <c>sectional-source.json</c>。
	/// </summary>
	/// <param name="modDirectory">新建的条目目录（调用方负责创建；本方法只往里写）。</param>
	/// <param name="armor">来源里选中的护甲（可空）。</param>
	/// <param name="helmet">来源里选中的头盔（可空）。</param>
	/// <param name="assetKeys">干净资产白名单。</param>
	public static SectionalSourceAssemblyOutcome Assemble(
		string modDirectory,
		IReadOnlyList<SectionalSourceCandidate> sources,
		SectionalSourceSelection? armor,
		SectionalSourceSelection? helmet,
		SectionalSourceAssetKeys assetKeys)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		ArgumentNullException.ThrowIfNull(sources);
		ArgumentNullException.ThrowIfNull(assetKeys);

		if (sources.Count == 0) return SectionalSourceAssemblyOutcome.Failed("没有来源条目。");
		if (armor?.HasValue != true && helmet?.HasValue != true)
			return SectionalSourceAssemblyOutcome.Failed("护甲与头盔都没有选：没有来源可转。");
		if (assetKeys.Units.Count == 0)
			return SectionalSourceAssemblyOutcome.Failed("干净资产白名单里没有任何 unit。");

		// 同名 patch 来自**不同来源**时不许悄悄覆盖。
		//
		// 2026-11-10 实测（水墨牛牛系列）：同一个游戏档案下，「本体」的 patch 有 21 个 unit，
		// 「翅膀」只有 1 个、「鞋子」只有 1 个，后两者的 unit 都已被「本体」包含。
		// 旧实现是"同名后者赢"，于是最后只剩 1 个 unit —— 产物看起来成功，实际上丢了 20 个，
		// 而依据文件还写着每个来源各自贡献了哪个文件，两边对不上。
		// 这类冲突没有安全的自动答案（合并要重写 patch，选谁都要用户定），所以挡在导入前。
		var conflicts = FindCrossSourceNameConflicts(sources);
		if (conflicts.Count != 0)
			return SectionalSourceAssemblyOutcome.Failed(
				"这些子级各自带了同一个 patch 文件名（说明它们指向同一个游戏档案、内容互不相同）："
				+ string.Join("；", conflicts)
				+ "。这种冲突没法自动决定用哪个 —— 直接覆盖会静默丢掉其它来源的绝大部分 unit。"
				+ "请从**其中一个**来源自己的详情页单独转换（通常是 unit 最多的那个「本体」）。");

		var notes = new List<string>();
		var entries = new List<SectionalSourceEntry>(sources.Count);

		for (var order = 0; order < sources.Count; order++)
		{
			var source = sources[order];
			if (!Directory.Exists(source.Directory))
			{
				notes.Add($"来源「{source.Name}」的目录不存在，已跳过：{source.Directory}");
				continue;
			}

			var patchFiles = new List<string>();
			foreach (var patch in SectionalSplitter.EnumeratePatchFiles(source.Directory))
			{
				var name = Path.GetFileName(patch);
				// 根上看不到来源 patch ⇒ 它不是 legacy 类的来源，拷过去只会得到空条目。
				if (string.IsNullOrWhiteSpace(name)) continue;

				// 主文件 + 两份 sidecar 一起拷：只拷主文件的话产物读不出来。
				CopyInto(patch, Path.Combine(modDirectory, name));
				foreach (var suffix in SidecarSuffixes)
				{
					var from = patch + suffix;
					if (!File.Exists(from)) continue;
					CopyInto(from, Path.Combine(modDirectory, name + suffix));
				}

				if (!patchFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) patchFiles.Add(name);
			}

			if (patchFiles.Count == 0)
			{
				notes.Add($"来源「{source.Name}」根上没有来源 patch（不是 legacy 类），已跳过。");
				continue;
			}

			entries.Add(new SectionalSourceEntry(source.NodeId, source.Name, order, source.Directory, patchFiles));
		}

		if (entries.Count == 0)
			return SectionalSourceAssemblyOutcome.Failed("所有来源都没有可拷贝的 patch，条目里不会有任何内容。", notes);

		var document = new SectionalSourceDocument(
			SectionalSourceManifest.CurrentSchema,
			DateTimeOffset.UtcNow,
			armor, helmet, entries, assetKeys);

		var error = SectionalSourceFile.TryWrite(modDirectory, document);
		return error is null
			? new SectionalSourceAssemblyOutcome(true, null, notes)
			: SectionalSourceAssemblyOutcome.Failed($"写转换依据失败：{error}", notes);
	}

	/// <summary>patch 的两份 sidecar。它们必须跟着主文件走，否则产物读不出来。</summary>
	private static readonly string[] SidecarSuffixes = [".gpu_resources", ".stream"];

	/// <summary>
	/// 拷一个文件到新条目里。
	/// <para>
	/// <b>为什么要动属性。</b>来源 patch 常常是只读的（从 zip 导入时带着 ReadOnly），
	/// <c>File.Copy</c> 会把只读属性一起带给目标；此后任何"覆盖同名文件"都会
	/// <c>UnauthorizedAccessException: Access to the path … is denied</c>
	/// —— 2026-11-10 那两次失败就是这么来的。新条目是我们自己的产物，后面还要往里写
	/// （拆分、编译），所以复制完统一抹成 Normal。
	/// </para>
	/// </summary>
	private static void CopyInto(string sourcePath, string destinationPath)
	{
		if (File.Exists(destinationPath)) File.SetAttributes(destinationPath, FileAttributes.Normal);
		File.Copy(sourcePath, destinationPath, overwrite: true);
		File.SetAttributes(destinationPath, FileAttributes.Normal);
	}

	/// <summary>
	/// 找出"同一个 patch 文件名出现在多个不同来源里"的情况，连同各自的体量一起报出来。
	/// 体量是给用户判断用的：一个系列里"本体"通常是几十 KB 带全部 unit，"翅膀"只有几十 KB 的一个。
	/// </summary>
	private static List<string> FindCrossSourceNameConflicts(IReadOnlyList<SectionalSourceCandidate> sources)
	{
		var byName = new Dictionary<string, List<(string Source, long Size)>>(StringComparer.OrdinalIgnoreCase);
		foreach (var source in sources)
		{
			if (!Directory.Exists(source.Directory)) continue;
			foreach (var patch in SectionalSplitter.EnumeratePatchFiles(source.Directory))
			{
				var name = Path.GetFileName(patch);
				if (string.IsNullOrWhiteSpace(name)) continue;
				if (!byName.TryGetValue(name, out var owners)) byName[name] = owners = [];
				if (owners.Any(owner => string.Equals(owner.Source, source.Name, StringComparison.OrdinalIgnoreCase))) continue;
				owners.Add((source.Name, new FileInfo(patch).Length));
			}
		}

		return byName
			.Where(pair => pair.Value.Count > 1)
			.Select(pair => $"「{pair.Key}」在 "
				+ string.Join("、", pair.Value.Select(owner => $"{owner.Source}（{Math.Max(1, owner.Size / 1024)} KB）")))
			.ToList();
	}
}
