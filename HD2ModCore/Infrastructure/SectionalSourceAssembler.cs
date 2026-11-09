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

		var notes = new List<string>();
		var entries = new List<SectionalSourceEntry>(sources.Count);
		var copied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

				// 同名（同 archive 同序号）后到的覆盖先到的：这与部署的"同名后者赢"一致，
				// 也是 sources[].order 存在的理由。
				if (copied.TryGetValue(name, out var previous))
					notes.Add($"来源「{source.Name}」覆盖了同名 patch「{name}」（原属「{previous}」）。");

				// 主文件 + 两份 sidecar 一起拷：只拷主文件的话产物读不出来。
				File.Copy(patch, Path.Combine(modDirectory, name), overwrite: true);
				foreach (var suffix in SidecarSuffixes)
				{
					var from = patch + suffix;
					if (!File.Exists(from)) continue;
					File.Copy(from, Path.Combine(modDirectory, name + suffix), overwrite: true);
				}

				copied[name] = source.Name;
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
}
