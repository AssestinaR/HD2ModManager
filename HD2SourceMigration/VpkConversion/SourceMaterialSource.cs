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
/// <item>模型 addon 自己<b>不定义材质</b>（<c>Lunalice_Zoey.vpk</c> 没有 <c>.vmt</c>）。
/// 材质散落在 L4D2 的 <c>pak01</c> 与 addons 目录里另外几百个包的某一个中。</item>
/// </list>
/// <para>
/// <b>内存策略</b>：只把"名字 → (哪个包, 第几条)"记下来，<b>不常驻</b>任何 VPK。
/// 每个包扫完就关，读的时候再开一次。几百个包 × 两万条目若常驻会吃掉大量内存，
/// 而真正要读的贴图只有几十张。
/// </para>
/// <para><b>同名冲突</b>：按传入顺序，<b>先出现的赢</b>——这是确定的，不是任意的。</para>
/// <para>
/// <b>作用域视图（<see cref="ScopedTo"/>）与"一个 VPK 一个 patch"的关系。</b>
/// 一次导入的产物必须<b>只含这个 VPK 自己的资源</b>，所以合成材质时必须先读"这个 VPK 自己那份"
/// ——否则 B1 与 B2 同名同路径的 53 个 <c>.vmt</c> 会让先入队的 B1 顶掉 B2，
/// B2 的 patch 就成了空包。但<b>解析</b>仍然要能跨 VPK：A1 的 <c>white.vmt</c> 指向的
/// <c>white.vtf</c> 只存在于 D1。所以规则是<b>自身优先、整批兜底</b>，
/// 这正是 <see cref="ScopedTo"/> 造出来的那个视图。
/// </para>
/// </summary>
public sealed class SourceMaterialSource
{
	private readonly Dictionary<string, (string VpkPath, string EntryPath)> vmts;
	private readonly Dictionary<string, (string VpkPath, string EntryPath)> vtfs;
	private readonly List<string> failures;
	private readonly HashSet<string> ownedVmts;
	private readonly HashSet<string> ownedVtfs;

	private SourceMaterialSource(
		IReadOnlyList<string> vpkPaths,
		Dictionary<string, (string VpkPath, string EntryPath)> vmts,
		Dictionary<string, (string VpkPath, string EntryPath)> vtfs,
		List<string> failures,
		string? primaryVpkPath,
		HashSet<string> ownedVmts,
		HashSet<string> ownedVtfs)
	{
		VpkPaths = vpkPaths;
		this.vmts = vmts;
		this.vtfs = vtfs;
		this.failures = failures;
		PrimaryVpkPath = primaryVpkPath;
		this.ownedVmts = ownedVmts;
		this.ownedVtfs = ownedVtfs;
	}

	/// <summary>实际参与索引的 VPK 路径（按传入顺序，去重后）。作用域视图里只有主 VPK 一个。</summary>
	public IReadOnlyList<string> VpkPaths { get; }

	/// <summary>
	/// 这个视图的"主 VPK"：<see cref="OwnedVmtNames"/> / <see cref="OwnedVtfNames"/> 就是它自己带的资源。
	/// <c>null</c> 表示这是一个整批视图，没有"自身"的概念（旧路径 <c>source convert</c> 用的就是它）。
	/// </summary>
	public string? PrimaryVpkPath { get; }

	/// <summary>主 VPK <b>自己定义</b>的材质名（不带扩展名）。整批视图里为空。</summary>
	public IReadOnlyCollection<string> OwnedVmtNames => ownedVmts;

	/// <summary>主 VPK <b>自己提供</b>的贴图名（不带扩展名）。整批视图里为空。</summary>
	public IReadOnlyCollection<string> OwnedVtfNames => ownedVtfs;

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

		var vmts = new Dictionary<string, (string VpkPath, string EntryPath)>(StringComparer.OrdinalIgnoreCase);
		var vtfs = new Dictionary<string, (string VpkPath, string EntryPath)>(StringComparer.OrdinalIgnoreCase);
		var failures = new List<string>();
		foreach (var path in ordered)
			Index(path, vmts, vtfs, failures);

		return new SourceMaterialSource(
			ordered, vmts, vtfs, failures,
			primaryVpkPath: null,
			ownedVmts: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			ownedVtfs: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
	}

	/// <summary>把一个目录（或一组目录）下所有 VPK 展开成路径列表，再交给 <see cref="Open"/>。</summary>
	public static SourceMaterialSource OpenDirectories(params string[] directories)
		=> Open(EnumerateDirectoryVpks(directories));

	/// <summary>
	/// 展开目录下的 VPK。`pak01_dir.vpk` 是目录文件，分卷 `pak01_000.vpk` 由 VpkArchive 自己去找，
	/// 所以这里只收 `_dir.vpk` 与独立的单个 `.vpk`，跳过纯数字分卷。
	/// </summary>
	public static IReadOnlyList<string> EnumerateDirectoryVpks(IEnumerable<string> directories)
	{
		var files = new List<string>();
		foreach (var directory in directories)
		{
			if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
				continue;
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

		return files;
	}

	private static void Index(
		string vpkPath,
		Dictionary<string, (string VpkPath, string EntryPath)> vmts,
		Dictionary<string, (string VpkPath, string EntryPath)> vtfs,
		List<string> failures)
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

	/// <summary>
	/// 造一个"以 <paramref name="vpkPath"/> 为主、本索引兜底"的视图。
	/// <para>
	/// 主 VPK 自己的 <c>.vmt</c>/<c>.vtf</c> 排在索引最前面，所以 <see cref="TryReadVmt"/> /
	/// <see cref="TryReadVtf"/> 读到的一定是<b>它自己那份</b>；它没有的名字才落到整批索引上。
	/// 这两件事正好对应契约里的两条：patch 只含自己的资源（靠 <see cref="OwnedVmtNames"/>），
	/// 而解析可以跨 VPK（靠兜底）。
	/// </para>
	/// <para>
	/// 代价只是复制一百多条字典记录，不常驻任何包。
	/// </para>
	/// </summary>
	public SourceMaterialSource ScopedTo(string vpkPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(vpkPath);
		var full = Path.GetFullPath(vpkPath);

		var ownedVmts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var ownedVtfs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var vmts = new Dictionary<string, (string VpkPath, string EntryPath)>(StringComparer.OrdinalIgnoreCase);
		var vtfs = new Dictionary<string, (string VpkPath, string EntryPath)>(StringComparer.OrdinalIgnoreCase);
		var failures = new List<string>();

		try
		{
			using var archive = VpkArchive.Open(full);
			foreach (var entry in archive.Entries)
			{
				if (entry.Extension.Equals("vmt", StringComparison.OrdinalIgnoreCase))
				{
					ownedVmts.Add(entry.FileName);
					vmts.TryAdd(entry.FileName, (full, entry.Path));
				}
				else if (entry.Extension.Equals("vtf", StringComparison.OrdinalIgnoreCase))
				{
					ownedVtfs.Add(entry.FileName);
					vtfs.TryAdd(entry.FileName, (full, entry.Path));
				}
			}
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			failures.Add($"{full}：{exception.Message}");
		}

		// 兜底：整批索引里主 VPK 没有的名字。
		foreach (var (name, location) in this.vmts)
			vmts.TryAdd(name, location);
		foreach (var (name, location) in this.vtfs)
			vtfs.TryAdd(name, location);

		// 主 VPK 本身打不开时，`failures` 已经有它；整批的旧失败也一并带过来，别丢信息。
		failures.AddRange(this.failures);

		return new SourceMaterialSource([full], vmts, vtfs, failures, full, ownedVmts, ownedVtfs);
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

	/// <summary>这个材质名最终由哪个 VPK 提供（自身优先、整批兜底之后的答案）。</summary>
	public bool TryGetVmtSource(string materialName, out string vpkPath, out string entryPath)
		=> vmts.TryGetValue(Stem(materialName), out var location)
			? Found(location, out vpkPath, out entryPath)
			: NotFound(out vpkPath, out entryPath);

	/// <summary>这个贴图名最终由哪个 VPK 提供（自身优先、整批兜底之后的答案）。</summary>
	public bool TryGetVtfSource(string textureName, out string vpkPath, out string entryPath)
		=> vtfs.TryGetValue(Stem(textureName), out var location)
			? Found(location, out vpkPath, out entryPath)
			: NotFound(out vpkPath, out entryPath);

	public bool ContainsVmt(string materialName) => vmts.ContainsKey(Stem(materialName));

	public bool ContainsVtf(string textureName) => vtfs.ContainsKey(Stem(textureName));

	/// <summary>这个材质是不是<b>主 VPK 自己</b>定义的（而不是从整批里借来的）。</summary>
	public bool OwnsVmt(string materialName) => ownedVmts.Contains(Stem(materialName));

	/// <summary>这张贴图是不是<b>主 VPK 自己</b>提供的。</summary>
	public bool OwnsVtf(string textureName) => ownedVtfs.Contains(Stem(textureName));

	private static bool Found(
		(string VpkPath, string EntryPath) location, out string vpkPath, out string entryPath)
	{
		(vpkPath, entryPath) = location;
		return true;
	}

	private static bool NotFound(out string vpkPath, out string entryPath)
	{
		vpkPath = string.Empty;
		entryPath = string.Empty;
		return false;
	}

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
