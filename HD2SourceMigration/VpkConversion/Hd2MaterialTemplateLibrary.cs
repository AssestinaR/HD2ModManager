namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// 社区 SDK 自带的材质模板库（<c>*.material</c> 文件）。
/// <para>
/// <b>为什么要模板。</b>一个新造的 HD2 材质不能凭空写：槽位键、父模板、着色器变量表与值区
/// 都是<b>模板的着色器契约</b>，只有贴图 id（<c>TexIDs</c>）是可以改的。
/// 凭空发明一个槽会产出游戏解析不了的材质（<c>hd2_asset.py:190-201</c>）。
/// </para>
/// <para>
/// 这些文件在仓库里有两份副本且逐字节相同：
/// <c>docs/HD2SDK-CommunityEdition-3.8.0/materials/</c> 与
/// <c>tools/ref/HD2SDK-CommunityEdition/materials/</c>。
/// </para>
/// <para>
/// <b>模板决定 TocData 大小</b>（basic 512 / basic+ 416 / advanced 1408 / alphaclip 448），
/// 换模板会让后续所有 TocData 的偏移整体平移，所以换模板必须整容器重写
/// （<c>fix_materials.py:258-262</c>）。
/// </para>
/// </summary>
public sealed class Hd2MaterialTemplateLibrary
{
	private static readonly string[] RelativeCandidates =
	[
		// 发布包布局：third_party\hd2sdk\materials\*.material。
		Path.Combine("third_party", "hd2sdk", "materials"),
		Path.Combine("docs", "HD2SDK-CommunityEdition-3.8.0", "materials"),
		Path.Combine("tools", "ref", "HD2SDK-CommunityEdition", "materials"),
	];

	/// <summary>找不到时给支持人员看的"找过哪些位置"。</summary>
	public static string DescribeSearchLocations()
		=> string.Join("、", RelativeCandidates);

	private readonly Dictionary<string, string> paths;

	private Hd2MaterialTemplateLibrary(string directory, Dictionary<string, string> paths)
	{
		Directory = directory;
		this.paths = paths;
	}

	public string Directory { get; }

	/// <summary>可用模板名（去掉 <c>.material</c> 后缀），例如 <c>advanced</c>。</summary>
	public IReadOnlyCollection<string> Names => paths.Keys;

	/// <summary>从 <paramref name="startDirectory"/> 逐级向上找模板目录；找不到返回 <c>null</c>。</summary>
	public static Hd2MaterialTemplateLibrary? Locate(string? explicitDirectory = null, string? startDirectory = null)
	{
		var candidates = new List<string>();
		if (!string.IsNullOrWhiteSpace(explicitDirectory))
			candidates.Add(explicitDirectory);
		var directory = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
		while (directory is not null)
		{
			foreach (var relative in RelativeCandidates)
				candidates.Add(Path.Combine(directory.FullName, relative));
			directory = directory.Parent;
		}

		foreach (var candidate in candidates)
		{
			if (!System.IO.Directory.Exists(candidate))
				continue;
			var files = System.IO.Directory
				.GetFiles(candidate, "*.material", SearchOption.TopDirectoryOnly)
				.ToDictionary(
					path => Path.GetFileNameWithoutExtension(path),
					path => path,
					StringComparer.OrdinalIgnoreCase);
			if (files.Count != 0)
				return new Hd2MaterialTemplateLibrary(Path.GetFullPath(candidate), files);
		}

		return null;
	}

	public bool Contains(string name) => paths.ContainsKey(name);

	/// <summary>按名字读一份模板并解析。名字不存在时抛，不静默回落到别的模板。</summary>
	public Hd2MaterialAsset Load(string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		if (!paths.TryGetValue(name, out var path))
			throw new FileNotFoundException(
				$"没有名为 '{name}' 的材质模板。可用：{string.Join("、", paths.Keys.OrderBy(key => key, StringComparer.Ordinal))}。");
		return Hd2MaterialAsset.Read(File.ReadAllBytes(path));
	}

	/// <summary>读原始模板字节（需要"逐字节照抄"语义时用它）。</summary>
	public byte[] LoadBytes(string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		if (!paths.TryGetValue(name, out var path))
			throw new FileNotFoundException($"没有名为 '{name}' 的材质模板。");
		return File.ReadAllBytes(path);
	}
}
