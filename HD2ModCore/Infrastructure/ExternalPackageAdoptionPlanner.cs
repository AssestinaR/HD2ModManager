using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 一个「外部包」的判定结果：库根下的目录，自己这一层没有 patch，子树里却有。
/// <para>
/// 这类目录来自别的 mod manager（<c>包名/选项名/9ba626afa44a3aa3.patch_0</c>），
/// 直接拷进 <c>mods/</c> 之后重扫认不出来——<see cref="ModLibrarySynchronizer"/> 只读
/// 节点根与 <c>sectional/</c>，拿不到 patch 就静默跳过。这里只负责「判断能不能认领」，
/// 不碰文件、不写库；真正的搬运交给导入链路。
/// </para>
/// </summary>
/// <param name="BlockedReason">
/// 非空表示**不能**认领，且必须让用户看见原因（例如文件数超限、目录是 junction）。
/// 之所以不直接丢弃：一个真实包被跳过却没有任何痕迹，正是这次要修的毛病。
/// </param>
/// <param name="Warnings">
/// 不阻止认领、但值得说一声的情况（例如 manifest 引用的预览图不存在）。
/// </param>
public sealed record ExternalPackageAdoptionCandidate(
	string RelativePath,
	string FullPath,
	IReadOnlyList<string> OptionDirectoryNames,
	int BasePatchCount,
	int FileCount,
	string? ManifestName,
	string? BlockedReason,
	IReadOnlyList<string> Warnings)
{
	public bool IsAdoptable => BlockedReason is null;
}

/// <summary>
/// 判定「哪些顶层目录是未受管的外部包」。
/// <para>
/// <b>为什么判据是「子树里有 base patch」。</b>
/// 正常 Mod 的 patch 都在节点根上，重扫本来就能收；只有「归档躺在子目录里」的包才需要认领。
/// 反过来说，任何没有 base patch 的目录都不该被认领——那只会把纯图片目录、说明目录、
/// 残留碎片拖进库里。
/// </para>
/// <para>
/// <b>「本层也有 patch」不再是排除条件。</b>以前的判据是「本层没有、子树里有」，于是
/// <c>包名/patch_0</c> + <c>包名/选项/patch_0</c> 这种形状被当成正常 Mod 收下，子目录里的
/// 选项既不进库也不报错——而它恰好是<b>我们自己的「导出为文件夹」产物</b>的形状。
/// 现在这种形状也会被认领：本层内容作为 Mod 本体，子目录各成一个选项。
/// </para>
/// <para>
/// <b>为什么这里不做 <c>ResolvePackageRoot</c> 的「唯一子目录下移」。</b>
/// 那一步只决定<b>导入时拿哪个目录当包根</b>，与「要不要认领」无关。掺进来会让
/// <c>X/manifest.json + X/本体/</c> 与 <c>X/本体/</c> 两种形状走不同分支，
/// 判据就不再有单一含义了。
/// </para>
/// </summary>
public static class ExternalPackageAdoptionPlanner
{
	/// <summary>
	/// 往子树里找 patch 的最大深度。真实包的形状是 <c>包/选项/文件</c>，
	/// 留两层余量给「包/作者目录/选项/文件」，再深基本是解压时多套了一层壳。
	/// </summary>
	public const int MaxScanDepth = 4;

	/// <summary>
	/// 单个包最多数多少个文件。超过就说明这不像一个 Mod 包（更像是把整个游戏目录
	/// 拷进来了），拒绝认领并给出原因，避免一次扫描把磁盘拖死。
	/// </summary>
	public const int MaxScannedFiles = 200_000;

	public static IReadOnlyList<ExternalPackageAdoptionCandidate> Plan(
		string modsRootDirectory,
		IReadOnlySet<string>? managedRelativePaths = null,
		IReadOnlySet<string>? excludedRelativePaths = null,
		IPatchFileNameParser? fileNameParser = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modsRootDirectory);
		var root = Path.GetFullPath(modsRootDirectory);
		if (!Directory.Exists(root)) return Array.Empty<ExternalPackageAdoptionCandidate>();

		var parser = fileNameParser ?? new PatchFileNameParser();
		var managed = NormalizeAll(managedRelativePaths);
		var excluded = NormalizeAll(excludedRelativePaths);
		var candidates = new List<ExternalPackageAdoptionCandidate>();

		foreach (var directory in EnumerateTopLevelDirectories(root))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var name = Path.GetFileName(directory);
			var relativePath = Normalize(Path.GetRelativePath(root, directory));

			// 管理器自己占用的目录、待删目录：一律不动。
			if (IsReservedDirectoryName(name) || excluded.Contains(relativePath)) continue;

			// 有设置文件说明它曾经是受管 Mod（或用户改过名）。这类目录的布局本来就是扁平的
			// （导入时已经摊平），重扫与残留清理负责它们，不需要认领。
			if (File.Exists(Path.Combine(directory, ModSettingsFile.FileName))) continue;

			// 有逻辑容器标记说明重扫已经能收它，不需要认领。
			if (ModNodeStorageProtocol.IsLogicalDirectory(directory)) continue;

			if (managed.Contains(relativePath))
			{
				// 已经作为普通 Mod 收录，但重扫只读「节点根 + sectional/」——子目录里的归档
				// 既不会进库，也不会报错。这里只**报告**，不动手：认领会给同一批内容再建一套
				// 节点，旧节点连同它的启用状态一起作废，比"少几个选项"更难收拾。
				var shallow = ScanSubtree(directory, parser, cancellationToken, maxDepth: 1);
				if (shallow.NestedPatchCount == 0) continue;
				candidates.Add(new ExternalPackageAdoptionCandidate(
					relativePath,
					directory,
					shallow.OptionDirectoryNames,
					shallow.NestedPatchCount,
					shallow.FileCount,
					StandardModManifest.TryLoad(directory)?.Name,
					$"已作为普通 Mod 收录，但子目录里还有 {shallow.NestedPatchCount} 个归档没进库"
					+ "（重扫只读节点根与 sectional/）。要收全：把它移出库根后校验一次（条目会消失），"
					+ "再移回来校验，就会按外部包认领。",
					Array.Empty<string>()));
				continue;
			}

			var rootPatchCount = CountBasePatchesAtRoot(directory, parser);
			var scan = ScanSubtree(directory, parser, cancellationToken);
			// 没有任何 base patch：纯图片/说明/碎片目录，不是包，不列进结果。
			if (scan.BasePatchCount == 0) continue;
			// 本层有 patch、子树里没有：这就是正常 Mod 的形状，重扫本来就能收。
			if (rootPatchCount > 0 && scan.NestedPatchCount == 0) continue;

			var blocked = IsReparsePoint(directory)
				? "目录是 junction 或符号链接，移动与硬链接都不可靠。"
				: null;
			if (blocked is null && scan.FileCount > MaxScannedFiles)
				blocked = $"文件数 {scan.FileCount} 超过上限 {MaxScannedFiles}，疑似不是 Mod 包。";

			var manifest = StandardModManifest.TryLoad(directory);
			var warnings = CollectManifestWarnings(directory, manifest).ToList();
			// 本层与子树都有 patch：本层的内容作为 Mod 本体，子目录各成一个选项。
			// 这正是「导出为文件夹」产物的形状——不认领的话子目录会被静默吞掉。
			if (rootPatchCount > 0)
				warnings.Add($"本层有 {rootPatchCount} 个归档、子目录里还有 {scan.NestedPatchCount} 个："
					+ "本层的作为 Mod 本体，子目录各成一个选项（「导出为文件夹」产物的形状）。");
			// manifest 声明了但作者没发布的选项目录：只警告，不拦。
			//
			// 曾经拿「部分 Include 缺失」当「复制到一半」的证据去阻止认领，实测误报率 1/15
			// （milltina 修女那个包声明了 9 个选项、只发了 8 个目录），代价是那个包**永久**
			// 进不了库——比它要防的风险更糟。真正的「复制中」由移动那一步兜住：
			// 文件句柄还开着时 Directory.Move 会失败，源目录原样保留。
			if (FindUnshippedInclude(directory, manifest) is { } unshipped)
				warnings.Add($"manifest 声明了选项目录「{unshipped}」，但包里没有——已按实际内容导入。");

			candidates.Add(new ExternalPackageAdoptionCandidate(
				relativePath,
				directory,
				scan.OptionDirectoryNames,
				scan.BasePatchCount,
				scan.FileCount,
				manifest?.Name,
				blocked,
				warnings));
		}

		return candidates
			.OrderBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	/// <summary>管理器自己在库根上占用的目录名，以及转换产物的层目录名。</summary>
	/// <remarks>
	/// <b>为什么「以点开头」整类都算保留。</b>库根上的点目录全是管理器的记账目录：
	/// <c>.hd2modmanager*</c>（标记/回收站）、<c>.runtime</c>（Lua 运行时产物，
	/// 形状恰好是 <c>.runtime/lua/&lt;指纹&gt;/9ba…patch_0</c>）。按名字逐个列举一定会漏，
	/// 漏掉 <c>.runtime</c> 的后果是把它当成外部包导入一遍——既重复部署，又毁掉运行时缓存。
	/// </remarks>
	public static bool IsReservedDirectoryName(string? name)
	{
		if (string.IsNullOrWhiteSpace(name)) return true;
		return name.StartsWith('.')
			|| ModNodePayloadLayout.IsVpkPatchDirectoryName(name)
			|| ModNodePayloadLayout.IsOverwriteDirectoryName(name);
	}

	/// <summary>
	/// manifest 引用了不存在的预览图时给个提示。
	/// <para>
	/// 这批包的通病：<c>IconPath</c> 写着 <c>0.png</c> 而目录里只有 <c>1.png</c>。
	/// 它不影响导入（图片是可选展示），但用户会以为「图都没了 = 包没认出来」，
	/// 所以先把话说在前面。
	/// </para>
	/// </summary>
	private static IReadOnlyList<string> CollectManifestWarnings(string directory, StandardModManifest? manifest)
	{
		if (manifest is null) return Array.Empty<string>();
		var warnings = new List<string>();
		var iconPath = manifest.IconPath;
		if (!string.IsNullOrWhiteSpace(iconPath) && !ExistsRelativeTo(directory, iconPath)) 
			warnings.Add($"manifest 的 IconPath 指向不存在的文件：{iconPath}");
		foreach (var option in manifest.Options)
		{
			var image = option.Image;
			if (string.IsNullOrWhiteSpace(image) || ExistsRelativeTo(directory, image)) continue;
			warnings.Add($"选项「{option.Name}」的预览图不存在：{image}");
		}
		return warnings;
	}

	private static bool ExistsRelativeTo(string directory, string relativePath)
		=> File.Exists(Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

	/// <summary>
	/// 找出「manifest 里声明了、但包里根本没有」的选项目录。
	/// <para>
	/// 只在**部分存在**时才返回：一个都不存在说明这个 manifest 的 <c>Include</c> 语义和我们的
	/// 假设不同（例如写的是 VPK 内的虚拟路径），那种情况整包都会被当成"没有选项目录"处理，
	/// 不值得单独报。
	/// </para>
	/// <para>
	/// <b>它只是警告，不是准入判据。</b>作者漏发一个目录是很常见的（实测 15 个包里 1 个），
	/// 拿它当"复制中断"会把这个包永久挡在库外。
	/// </para>
	/// </summary>
	private static string? FindUnshippedInclude(string directory, StandardModManifest? manifest)
	{
		if (manifest is null || manifest.Options.Count == 0) return null;
		var includes = new List<string>();
		Collect(manifest.Options);
		if (includes.Count < 2) return null;

		var existing = 0;
		string? firstMissing = null;
		foreach (var include in includes.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (Directory.Exists(Path.Combine(directory, include.Replace('/', Path.DirectorySeparatorChar))))
			{
				existing++;
				continue;
			}
			firstMissing ??= include;
		}
		return existing > 0 && existing < includes.Distinct(StringComparer.OrdinalIgnoreCase).Count()
			? firstMissing
			: null;

		void Collect(IEnumerable<StandardModManifestOption> options)
		{
			foreach (var option in options)
			{
				if (option.Include is not null)
					includes.AddRange(option.Include.Where(include => !string.IsNullOrWhiteSpace(include)));
				if (option.SubOptions is not null)
					Collect(option.SubOptions);
			}
		}
	}

	private static SubtreeScan ScanSubtree(
		string directory,
		IPatchFileNameParser parser,
		CancellationToken cancellationToken,
		int maxDepth = MaxScanDepth)
	{
		var basePatchCount = 0;
		var rootPatchCount = 0;
		var fileCount = 0;
		var optionNames = new List<string>();
		Walk(directory, depth: 0);
		return new SubtreeScan(basePatchCount, rootPatchCount, fileCount, optionNames);

		void Walk(string current, int depth)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (fileCount > MaxScannedFiles) return;

			foreach (var file in EnumerateFiles(current))
			{
				fileCount++;
				if (!parser.TryParse(Path.GetFileName(file), out var info)
					|| info?.SidecarKind != PatchSidecarKind.Base)
					continue;
				basePatchCount++;
				if (depth == 0) rootPatchCount++;
			}

			if (depth >= maxDepth) return;
			foreach (var child in EnumerateDirectories(current))
			{
				if (IsReservedDirectoryName(Path.GetFileName(child))) continue;
				if (IsReparsePoint(child)) continue;
				var before = basePatchCount;
				Walk(child, depth + 1);
				// 直接子目录（含更深层）出现了 base patch，就把它的目录名当作一个选项名。
				if (depth == 0 && basePatchCount > before)
					optionNames.Add(Path.GetFileName(child));
			}
		}
	}

	/// <summary>只数本层的 base patch。用来区分「正常 Mod」与「本层 + 子树都有」。</summary>
	private static int CountBasePatchesAtRoot(string directory, IPatchFileNameParser parser)
	{
		var count = 0;
		foreach (var file in EnumerateFiles(directory))
		{
			if (parser.TryParse(Path.GetFileName(file), out var info)
				&& info?.SidecarKind == PatchSidecarKind.Base)
				count++;
		}
		return count;
	}

	private static IEnumerable<string> EnumerateTopLevelDirectories(string root)
	{
		try
		{
			return Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly);
		}
		catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
		catch (IOException) { return Array.Empty<string>(); }
		catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
	}

	private static IEnumerable<string> EnumerateDirectories(string directory)
	{
		try
		{
			return Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly);
		}
		catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
		catch (IOException) { return Array.Empty<string>(); }
		catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
	}

	private static IEnumerable<string> EnumerateFiles(string directory)
	{
		try
		{
			return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly);
		}
		catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
		catch (IOException) { return Array.Empty<string>(); }
		catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
	}

	private static bool IsReparsePoint(string directory)
	{
		try
		{
			return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
		}
		catch (IOException) { return true; }
		catch (UnauthorizedAccessException) { return true; }
	}

	private static HashSet<string> NormalizeAll(IReadOnlySet<string>? paths)
		=> paths is null
			? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			: new HashSet<string>(paths.Select(Normalize), StringComparer.OrdinalIgnoreCase);

	private static string Normalize(string path)
		=> string.IsNullOrWhiteSpace(path) || path == "."
			? string.Empty
			: path.Replace('\\', '/').Trim('/');

	/// <param name="RootPatchCount">本层（<paramref name="BasePatchCount"/> 里）有多少个。用于区分「正常 Mod」与「本层 + 子树都有」。</param>
	private sealed record SubtreeScan(
		int BasePatchCount,
		int RootPatchCount,
		int FileCount,
		IReadOnlyList<string> OptionDirectoryNames)
	{
		/// <summary>子目录里的 base patch 数量——这些是重扫**看不见**的那部分内容。</summary>
		public int NestedPatchCount => BasePatchCount - RootPatchCount;
	}
}
