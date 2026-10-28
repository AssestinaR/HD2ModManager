using System.Reflection;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// HD2 的 <c>.material</c> 模板表（<c>advanced</c>、<c>alphaclip</c>、<c>translucent</c>…）。
/// <para>
/// <b>模板随本程序集内置</b>（见 HD2SourceMigration.csproj 的 EmbeddedResource）。它们是
/// HD2 自己的材质定义，不是第三方模型的属性——把"能不能转换材质"绑在用户机器上有没有
/// HD2SDK 安装目录，是发布包里最容易被忽略的一种缺文件（实测漏过一次：整批材质转换 0 个）。
/// </para>
/// <para>
/// <b>磁盘优先。</b>开发期直接改 <c>tools/ref/HD2SDK-CommunityEdition/materials</c> 就能生效，
/// 不必重编；只有找不到目录时才用内嵌副本。发布包里两者一致。
/// </para>
/// </summary>
public sealed class Hd2MaterialTemplateLibrary
{
	/// <summary>内嵌资源的名字前缀（csproj 里的 LogicalName）。</summary>
	private const string EmbeddedPrefix = "HD2MaterialTemplates.";

	private static readonly string[] RelativeCandidates =
	[
		// 发布包布局：third_party\hd2sdk\materials\*.material。
		Path.Combine("third_party", "hd2sdk", "materials"),
		Path.Combine("docs", "HD2SDK-CommunityEdition-3.8.0", "materials"),
		Path.Combine("tools", "ref", "HD2SDK-CommunityEdition", "materials"),
	];

	/// <summary>找不到时给支持人员看的"找过哪些位置"。</summary>
	public static string DescribeSearchLocations()
		=> string.Join("、", RelativeCandidates) + "（以及程序集内置副本）";

	/// <summary>模板名 → 模板字节。总大小约 7 KB，随实例常驻内存是合理的。</summary>
	private readonly Dictionary<string, byte[]> templates;

	private Hd2MaterialTemplateLibrary(string source, Dictionary<string, byte[]> templates)
	{
		Source = source;
		this.templates = templates;
	}

	/// <summary>这批模板从哪里来的（目录路径，或 <c>embedded</c>）。只用于诊断输出。</summary>
	public string Source { get; }

	/// <summary>可用模板名（去掉 <c>.material</c> 后缀），例如 <c>advanced</c>。</summary>
	public IReadOnlyCollection<string> Names => templates.Keys;

	/// <summary>
	/// 取模板表：先按 <paramref name="explicitDirectory"/> / 逐级向上的目录找，
	/// 找不到就用程序集内置副本。**永远不返回 null**——内置副本不会缺。
	/// </summary>
	public static Hd2MaterialTemplateLibrary Locate(string? explicitDirectory = null, string? startDirectory = null)
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
			if (!Directory.Exists(candidate))
				continue;
			var files = Directory.GetFiles(candidate, "*.material", SearchOption.TopDirectoryOnly);
			if (files.Length == 0)
				continue;
			var loaded = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
			foreach (var path in files)
				loaded[Path.GetFileNameWithoutExtension(path)] = File.ReadAllBytes(path);
			return new Hd2MaterialTemplateLibrary(Path.GetFullPath(candidate), loaded);
		}

		return new Hd2MaterialTemplateLibrary("embedded", LoadEmbedded());
	}

	/// <summary>内嵌副本所需的模板名。少一份就是构建漏了 EmbeddedResource。</summary>
	private static readonly string[] RequiredTemplates =
	[
		"advanced", "alphaclip", "alphaclip+", "armorlut", "basic",
		"basic+", "basic+OLD", "emissive", "original", "translucent",
	];

	private static Dictionary<string, byte[]> LoadEmbedded()
	{
		var assembly = typeof(Hd2MaterialTemplateLibrary).Assembly;
		var loaded = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
		foreach (var resource in assembly.GetManifestResourceNames())
		{
			if (!resource.StartsWith(EmbeddedPrefix, StringComparison.Ordinal)) continue;
			var name = resource[EmbeddedPrefix.Length..];
			if (name.EndsWith(".material", StringComparison.OrdinalIgnoreCase))
				name = name[..^".material".Length];
			using var stream = assembly.GetManifestResourceStream(resource);
			if (stream is null) continue;
			using var buffer = new MemoryStream();
			stream.CopyTo(buffer);
			loaded[name] = buffer.ToArray();
		}

		var missing = RequiredTemplates.Where(name => !loaded.ContainsKey(name)).ToArray();
		if (missing.Length != 0)
		{
			throw new InvalidOperationException(
				"程序集里缺少材质模板：" + string.Join("、", missing)
				+ "。这是构建问题（HD2SourceMigration.csproj 的 EmbeddedResource 没打进去）。");
		}

		return loaded;
	}

	public bool Contains(string name) => templates.ContainsKey(name);

	/// <summary>按名字读一份模板并解析。名字不存在时抛，不静默回落到别的模板。</summary>
	public Hd2MaterialAsset Load(string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		return Hd2MaterialAsset.Read(LoadBytes(name));
	}

	/// <summary>读原始模板字节（需要"逐字节照抄"语义时用它）。</summary>
	public byte[] LoadBytes(string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		if (!templates.TryGetValue(name, out var bytes))
			throw new FileNotFoundException(
				$"没有名为 '{name}' 的材质模板。可用：{string.Join("、", templates.Keys.OrderBy(key => key, StringComparer.Ordinal))}。");
		return bytes;
	}
}
