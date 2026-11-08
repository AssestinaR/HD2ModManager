namespace HD2ModCore.Infrastructure;

// 作用：定位随包分发的命令行程序 hd2mod.exe。
// Purpose: Locates the companion CLI executable that ships with the manager package.
//
// 为什么需要"多个位置"：开发构建把 hd2mod.exe 复制到管理器旁边（HD2ModManager.csproj 的
// CopyHd2ModCliArtifacts），发布包把它放进 bin\（tools\PublishRelease.ps1 的 CLI 布局）。
// 只认 AppContext.BaseDirectory 下的一个固定路径，必然在其中一种布局里失效——用户装发布版
// 后导入 VPK 会直接报"找不到 hd2mod.exe"。
//
// The development build copies hd2mod.exe next to the manager, while the release package puts it
// under bin\. A single hard-coded path therefore fails in one of the two layouts.
public static class Hd2ModCliLocator
{
	/// <summary>命令行程序的文件名。</summary>
	public const string ExecutableFileName = "hd2mod.exe";

	/// <summary>发布包里命令行程序所在的子目录。</summary>
	public const string ReleaseSubdirectory = "bin";

	/// <summary>管理器设置文件名（管理器根目录下的标志文件）。</summary>
	public const string SettingsFileName = "settings.json";

	/// <summary>管理器的数据目录名（索引与资源表都在它下面，同样是根目录的标志）。</summary>
	public const string DataDirectoryName = "data";

	/// <summary>
	/// 按优先级返回全部候选路径（绝对路径、大小写不敏感去重）。
	/// <para>第一个存在的就是应当使用的那个；<paramref name="baseDirectory"/> 留空时取 <see cref="AppContext.BaseDirectory"/>。</para>
	/// </summary>
	public static IReadOnlyList<string> GetCandidatePaths(string? baseDirectory = null)
	{
		var root = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
		var candidates = new List<string>(2);
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		void Add(string path)
		{
			var full = Path.GetFullPath(path);
			if (seen.Add(full)) candidates.Add(full);
		}

		// 开发构建：hd2mod.exe 就在管理器旁边。
		// Development build: the CLI sits directly beside the manager.
		Add(Path.Combine(root, ExecutableFileName));

		// 发布包：hd2mod.exe 在 bin\ 下，和完整版的私有运行时放在一起。
		// Release package: the CLI lives under bin\, together with the private runtime.
		Add(Path.Combine(root, ReleaseSubdirectory, ExecutableFileName));

		return candidates;
	}

	/// <summary>返回第一个存在的候选路径；一个都不存在时返回 <c>null</c>。</summary>
	public static string? TryResolve(string? baseDirectory = null)
	{
		foreach (var candidate in GetCandidatePaths(baseDirectory))
		{
			if (File.Exists(candidate)) return candidate;
		}

		return null;
	}

	/// <summary>把查找过的位置拼成一句话，供错误提示与日志使用。</summary>
	public static string DescribeSearchedLocations(string? baseDirectory = null)
		=> string.Join("、", GetCandidatePaths(baseDirectory));

	/// <summary>
	/// 从命令行程序自己所在的目录反推**管理器根目录**（设置、<c>data\</c>、Mod 库都在那儿）。
	/// <para>
	/// <b>为什么不能直接用 <see cref="AppContext.BaseDirectory"/>。</b>发布包的布局是
	/// <c>&lt;安装根&gt;\HD2ModManager.exe</c> + <c>&lt;安装根&gt;\bin\hd2mod.exe</c>；
	/// 命令行程序自己的目录是 <c>bin\</c>，而 <c>settings.json</c> 与 <c>data\</c> 在上一级。
	/// 把 <c>bin\</c> 当成管理器根，CLI 就会去找 <c>bin\settings.json</c> 与
	/// <c>bin\data\indexes\asset-index.sqlite</c> —— 两个都不存在，于是"没配游戏 data、
	/// 没有骨名表"，整批 VPK 导入在规划阶段全部失败（1.5.5 的实机回归就是这样）。
	/// </para>
	/// <para>
	/// 判据只用两样"只有管理器自己会产出"的东西：<c>settings.json</c> 与 <c>data\</c> 目录。
	/// 且**只在目录名正好是 <c>bin</c> 且它的上一级确实像管理器根时才往上走**，
	/// 绝不会一路往上找到某个无关目录 —— 开发布局（程序就在根目录里）因此完全不受影响。
	/// </para>
	/// </summary>
	/// <param name="baseDirectory">起点目录，留空时取 <see cref="AppContext.BaseDirectory"/>。</param>
	public static string ResolveManagerRoot(string? baseDirectory = null)
	{
		var directory = Normalize(string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory);
		if (LooksLikeManagerRoot(directory)) return directory;
		return TryGetManagerRootParent(directory) ?? directory;
	}

	/// <summary>
	/// 这个目录是"命令行程序所在的 <c>bin\</c>"、且它的上一级像管理器根时，返回那个上一级；否则 <c>null</c>。
	/// <para>给两处用：<see cref="ResolveManagerRoot"/> 的默认推导，以及
	/// <c>--manager-root</c> 被人手工指到 <c>bin\</c> 时的明确提示。</para>
	/// </summary>
	public static string? TryGetManagerRootParent(string? directory)
	{
		if (string.IsNullOrWhiteSpace(directory)) return null;
		var normalized = Normalize(directory);
		if (!string.Equals(Path.GetFileName(normalized), ReleaseSubdirectory, StringComparison.OrdinalIgnoreCase))
			return null;
		var parent = Directory.GetParent(normalized)?.FullName;
		return parent is not null && LooksLikeManagerRoot(parent) ? parent : null;
	}

	/// <summary>这个目录像不像管理器根（有设置文件，或有数据目录）。</summary>
	public static bool LooksLikeManagerRoot(string? directory)
	{
		if (string.IsNullOrWhiteSpace(directory)) return false;
		var normalized = Normalize(directory);
		return File.Exists(Path.Combine(normalized, SettingsFileName))
			|| Directory.Exists(Path.Combine(normalized, DataDirectoryName));
	}

	private static string Normalize(string path)
	{
		var full = Path.GetFullPath(path);
		// 目录名里不可能有结尾分隔符，而 AppContext.BaseDirectory 一定带着它：
		// 不剪掉的话 GetFileName 会返回空串，"这个目录是不是 bin"就永远判不出来。
		return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
	}
}
