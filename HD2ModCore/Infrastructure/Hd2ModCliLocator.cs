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
}
