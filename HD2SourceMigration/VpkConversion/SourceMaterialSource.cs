using HD2SourceModel;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// 在一组 VPK 里按<b>文件名</b>找材质（<c>.vmt</c>）与贴图（<c>.vtf</c>）。
/// <para>
/// <b>为什么必须是"一组 VPK + 按名字搜"。</b>实测（见文档第 3 轮）：
/// </para>
/// <list type="number">
/// <item>MDL 的材质表给的是<b>裸名</b>（<c>Shinano_face</c>、<c>ACC_Metal_1</c>…），
/// 既没有目录、也没有 <c>.vmt</c> 扩展名——按路径拼会 92 个全部找不到。</item>
/// <item>模型 addon 自己<b>不定义材质</b>（<c>Lunalice_Zoey.vpk</c> 没有 `.vmt`）。
/// 材质散落在 L4D2 的 <c>pak01</c> 与 addons 目录里另外几百个包的某一个中。</item>
/// </list>
/// <para>
/// <b>内存策略</b>：只把"名字 → (哪个包, 第几条)"记下来，<b>不常驻</b>任何 VPK。
/// 每个包扫完就关，读的时候再开一次。几百个包 × 两万条目若常驻会吃掉大量内存，
/// 而真正要读的贴图只有几十张。
/// </para>
/// <para><b>同名冲突</b>：按传入顺序，<b>先出现的赢</b>——这是确定的，不是任意的。</para>
/// </summary>
public sealed class SourceMaterialSource
{
	private readonly Dictionary<string, (string VpkPath, string EntryPath)> vmts = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, (string VpkPath, string EntryPath)> vtfs = new(StringComparer.OrdinalIgnoreCase);
	private readonly List<string> failures = [];

	private SourceMaterialSource(IReadOnlyList<string> vpkPaths)
	{
		VpkPaths = vpkPaths;
	}

	/// <summary>实际参与索引的 VPK 路径（按传入顺序，去重后）。</summary>
	public IReadOnlyList<string> VpkPaths { get; }

	public int VmtCount => vmts.Count;

	public int VtfCount => vtfs.Count;

	/// <summary>扫描时打不开的包。**不抛**——几百个包里坏一两个是常态，但调用方应当看得到。</summary>
	public IReadOnlyList<string> Failures => failures;

	public static SourceMaterialSource Open(IEnumerable<string> vpkPaths)
	{
		ArgumentNullException.ThrowIfNull(vpkPaths);
		var ordered = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var path in vpkPaths)
		{
			if (string.IsNullOrWhiteSpace(path))
				continue;
			var full = Path.GetFullPath(path);
			if (!File.Exists(full) || !seen.Add(full))
				continue;
			ordered.Add(full);
		}

		var source = new SourceMaterialSource(ordered);
		foreach (var path in ordered)
			source.Index(path);
		return source;
	}

	/// <summary>把一个目录（或一组目录）下所有 VPK 展开成路径列表，再交给 <see cref="Open"/>。</summary>
	public static SourceMaterialSource OpenDirectories(params string[] directories)
	{
		var files = new List<string>();
		foreach (var directory in directories)
		{
			if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
				continue;
			// `pak01_dir.vpk` 是目录文件，分卷 `pak01_000.vpk` 由 VpkArchive 自己去找，
			// 所以这里只收 `_dir.vpk` 与独立的单个 `.vpk`，跳过纯数字分卷。
			foreach (var path in Directory.EnumerateFiles(directory, "*.vpk", SearchOption.TopDirectoryOnly))
			{
				var stem = Path.GetFileNameWithoutExtension(path);
				var isVolume = stem.Length > 4
					&& stem[^4] == '_'
					&& stem[^3..].All(char.IsAsciiDigit);
				if (!isVolume)
					files.Add(path);
			}
		}

		return Open(files);
	}

	private void Index(string vpkPath)
	{
		try
		{
			using var archive = VpkArchive.Open(vpkPath);
			foreach (var entry in archive.Entries)
			{
				if (entry.Extension.Equals("vmt", StringComparison.OrdinalIgnoreCase))
					vmts.TryAdd(entry.FileName, (vpkPath, entry.Path));
				else if (entry.Extension.Equals("vtf", StringComparison.OrdinalIgnoreCase))
					vtfs.TryAdd(entry.FileName, (vpkPath, entry.Path));
			}
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			// 坏包跳过，但记下来。
			failures.Add($"{vpkPath}：{exception.Message}");
		}
	}

	/// <summary>按材质裸名读 <c>.vmt</c> 字节。名字可带或不带 <c>.vmt</c> 后缀。</summary>
	public bool TryReadVmt(string materialName, out byte[] bytes)
		=> TryRead(vmts, Stem(materialName), out bytes);

	/// <summary>
	/// 按贴图裸名读 <c>.vtf</c> 字节。名字可带或不带 <c>.vtf</c> 后缀，
	/// 也可以给带目录的路径——<b>只用最后一段</b>，与 Python 侧的贴图命名约定一致。
	/// </summary>
	public bool TryReadVtf(string textureName, out byte[] bytes)
		=> TryRead(vtfs, Stem(textureName), out bytes);

	public bool ContainsVmt(string materialName) => vmts.ContainsKey(Stem(materialName));

	public bool ContainsVtf(string textureName) => vtfs.ContainsKey(Stem(textureName));

	private static string Stem(string name)
	{
		var normalized = VmtDocument.NormalizePath(name);
		return Path.GetFileNameWithoutExtension(normalized);
	}

	private static bool TryRead(
		IReadOnlyDictionary<string, (string VpkPath, string EntryPath)> index, string name, out byte[] bytes)
	{
		bytes = [];
		if (string.IsNullOrWhiteSpace(name) || !index.TryGetValue(name, out var location))
			return false;

		using var archive = VpkArchive.Open(location.VpkPath);
		var entry = archive.Find(location.EntryPath);
		if (entry is null)
			return false;
		bytes = archive.Read(entry);
		return true;
	}
}
