namespace HD2SourceModel;

/// <summary>一个源模型需要的那三个文件。</summary>
/// <param name="Mdl">骨架、bodypart 结构与 jigglebone 参数。</param>
/// <param name="Vvd">顶点位置、法线、UV 与蒙皮权重。</param>
/// <param name="Vtx">三角形索引。</param>
public sealed record SourceModelFiles(VpkEntry Mdl, VpkEntry Vvd, VpkEntry Vtx);

/// <summary>
/// 在 VPK 里找出一个源模型的 MDL / VVD / VTX 三件套。
/// <para>
/// 三个文件必然同名（只差扩展名），所以定位的难点只在"选哪个模型"：一个 VPK 里通常同时有主模型、
/// 它的 <c>_light</c> 变体和第一人称手臂。默认取<b>体积最大</b>的那个，因为主模型总是比其余两个大得多。
/// </para>
/// </summary>
public static class SourceModelLocator
{
	/// <summary>
	/// 找出模型。不给 <paramref name="modelStem"/> 时取体积最大的 .mdl；给了就按"路径以它结尾"或
	/// "文件名包含它"匹配（大小写不敏感），仍然优先体积最大的那个。
	/// </summary>
	public static VpkEntry FindModel(VpkArchive archive, string? modelStem)
	{
		ArgumentNullException.ThrowIfNull(archive);
		var models = archive.WithExtension("mdl").ToArray();
		if (models.Length == 0)
			throw new InvalidDataException("VPK 里没有任何 .mdl 文件。");

		if (string.IsNullOrWhiteSpace(modelStem))
			return Largest(models);

		var needle = modelStem.Trim().Replace('\\', '/');
		var matches = models
			.Where(entry =>
				entry.Path.EndsWith(needle + ".mdl", StringComparison.OrdinalIgnoreCase)
				|| Path.GetFileName(entry.Path).Contains(needle, StringComparison.OrdinalIgnoreCase))
			.ToArray();

		if (matches.Length == 0)
			throw new InvalidDataException($"VPK 里找不到匹配 \"{modelStem}\" 的 .mdl。");

		return Largest(matches);
	}

	/// <summary>找出模型以及它的 VVD 与 VTX。</summary>
	public static SourceModelFiles Resolve(VpkArchive archive, string? modelStem)
	{
		ArgumentNullException.ThrowIfNull(archive);
		var mdl = FindModel(archive, modelStem);
		var stem = mdl.Path[..^4];
		var fileName = Path.GetFileName(stem);

		var vvd = archive.Find(stem + ".vvd")
			?? archive.WithExtension("vvd").FirstOrDefault(entry =>
				string.Equals(Path.GetFileName(entry.Path), fileName + ".vvd", StringComparison.OrdinalIgnoreCase))
			?? throw new InvalidDataException($"找不到 {stem}.vvd（顶点文件）。");

		// 索引文件常见名字是 <模型>.dx90.vtx，也可能是别的平台后缀，所以退一步按前缀找。
		var vtx = archive.Find(stem + ".dx90.vtx")
			?? archive.WithExtension("vtx")
				.Where(entry => Path.GetFileName(entry.Path)
					.StartsWith(fileName + ".", StringComparison.OrdinalIgnoreCase))
				.OrderBy(entry => entry.Path, StringComparer.Ordinal)
				.FirstOrDefault()
			?? throw new InvalidDataException($"找不到 {stem} 的 .vtx（索引文件）。");

		return new SourceModelFiles(mdl, vvd, vtx);
	}

	private static VpkEntry Largest(VpkEntry[] models) =>
		models
			.OrderByDescending(entry => entry.Length)
			.ThenBy(entry => entry.Path, StringComparer.Ordinal)
			.First();
}
