namespace HD2ModCore.Infrastructure;

/// <param name="Directory">提供这些材质的 Mod 目录。</param>
/// <param name="DisplayName">它的显示名（取文件夹名）。</param>
/// <param name="SourceVpkPaths">它根上的来源 VPK；转换时要一起喂进去。</param>
/// <param name="MatchedMaterialIds">它命中的那些材质 id。</param>
/// <param name="IsPureMaterial">它是否只含材质/贴图两类资源。</param>
public sealed record MaterialSourceCandidate(
	string Directory,
	string DisplayName,
	IReadOnlyList<string> SourceVpkPaths,
	IReadOnlyList<ulong> MatchedMaterialIds,
	bool IsPureMaterial);

/// <param name="Candidates">命中的提供者，按命中数从多到少。</param>
/// <param name="UnresolvedMaterialIds">库内找不到提供者的材质 id。</param>
public sealed record MaterialSourceCollection(
	IReadOnlyList<MaterialSourceCandidate> Candidates,
	IReadOnlyList<ulong> UnresolvedMaterialIds)
{
	public IReadOnlyList<string> SourceVpkPaths
		=> Candidates.SelectMany(candidate => candidate.SourceVpkPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

/// <summary>
/// 按**材质 id** 在 Mod 库里找提供者——「生成sectional版」要靠它把配套材质一起收进来。
/// <para>
/// <b>为什么不按库里的父子关系找。</b>父子关系只是旧库的存放形态（材质包曾是独立的 Option 节点），
/// 折并之后已经不存在了。真正稳定的关系是 id：模型引用的材质 id，与材质包写出的材质 id 相等。
/// </para>
/// <para>
/// <b>只有 <c>emitted: true</c> 的条目才算"提供"。</b>转换报告里 <c>materialDetails</c> 同时包含
/// "引用了但没写出来"的条目（模型自己的报告里就有一堆）。把它们算成提供者，模型就会
/// "提供自己的材质"——真库上实测过，这样每个模型都会把自己列成提供者。
/// </para>
/// </summary>
public static class MaterialSourceCollector
{
	/// <summary>
	/// 在 <paramref name="libraryRoot"/> 里找提供 <paramref name="materialIds"/> 的 Mod。
	/// </summary>
	/// <param name="excludedDirectory">要排除的 Mod 目录（通常是发起转换的那个模型自己）。</param>
	public static MaterialSourceCollection Collect(
		string libraryRoot,
		IReadOnlyCollection<ulong> materialIds,
		string? excludedDirectory = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
		ArgumentNullException.ThrowIfNull(materialIds);

		var wanted = materialIds.Where(id => id != 0).ToHashSet();
		if (wanted.Count == 0 || !Directory.Exists(libraryRoot))
			return new MaterialSourceCollection(Array.Empty<MaterialSourceCandidate>(), wanted.ToArray());

		var candidates = new List<MaterialSourceCandidate>();
		foreach (var directory in Directory.EnumerateDirectories(libraryRoot))
		{
			var name = Path.GetFileName(directory);
			if (name.StartsWith('.')) continue;
			if (excludedDirectory is not null
				&& string.Equals(Path.GetFullPath(directory), Path.GetFullPath(excludedDirectory), StringComparison.OrdinalIgnoreCase))
				continue;

			var report = VpkConversionReport.TryRead(directory);
			if (report is null) continue;

			var provided = ProvidedIds(report);
			var matched = wanted.Where(provided.Contains).OrderBy(id => id).ToArray();
			if (matched.Length == 0) continue;

			candidates.Add(new MaterialSourceCandidate(
				directory,
				name,
				Directory.EnumerateFiles(directory, "*.vpk", SearchOption.TopDirectoryOnly).ToArray(),
				matched,
				// "纯材质" = 没有任何 unit。允许它还带别的资源，但只有命中的那些会被取用。
				report.Patches.All(patch => patch.UnitCount == 0)));
		}

		var resolved = candidates.SelectMany(candidate => candidate.MatchedMaterialIds).ToHashSet();
		return new MaterialSourceCollection(
			candidates
				.OrderByDescending(candidate => candidate.MatchedMaterialIds.Count)
				.ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
				.ToArray(),
			wanted.Where(id => !resolved.Contains(id)).OrderBy(id => id).ToArray());
	}

	/// <summary>一个 Mod 的转换报告里"确实写出来了"的材质与贴图 id。</summary>
	private static HashSet<ulong> ProvidedIds(VpkConversionReport report)
	{
		// 报告只暴露计数，逐条明细要自己读原始 JSON——见 VpkConversionReport 的说明。
		var ids = new HashSet<ulong>();
		foreach (var patch in report.Patches)
		{
			foreach (var id in patch.EmittedMaterialIds ?? Array.Empty<ulong>()) ids.Add(id);
			foreach (var id in patch.EmittedTextureIds ?? Array.Empty<ulong>()) ids.Add(id);
		}
		return ids;
	}
}
