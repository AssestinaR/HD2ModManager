namespace HD2ModCore.Infrastructure;

/// <summary>
/// 一个 Mod 节点目录里，<b>实际生效的 patch 放在哪些目录</b>。
/// <para>
/// 节点目录本身是常规位置：手写的、从别处导入的 patch 都在根上。除此之外还有两种
/// 「由管理器或转换器生成」的子目录，它们各自承载一层内容，语义<b>不同</b>，不能混为一谈：
/// </para>
/// <list type="bullet">
/// <item><c>Overwrite/</c> —— <b>替换层</b>。装饰重建、同键重建这类"在已有 patch 上重算一遍"
/// 的产物放在这里：<b>只有同名根 patch 存在时才生效</b>，它自己不构成新内容。
/// 这条规则是有意为之——否则 Overwrite 里任何残留文件都会变成一次额外部署。</item>
/// <item><c>vpk2patch/</c> —— <b>内容层</b>。来源 VPK 转换出来的 patch 放在这里：
/// 这个 Mod 的 patch <b>本来就不在根上</b>，所以它必须能独立生效，不需要同名根 patch。
/// 它与 <c>Overwrite/</c> 是同一类"生成产物目录"，但它是加法而不是替换。</item>
/// </list>
/// <para>
/// <b>为什么两层的 patch 名字可以直接撞。</b>转换产物统一写 <c>&lt;archive&gt;.patch_0</c>，
/// 而 <c>Overwrite/</c> 的语义就是"顶掉同名根文件"。所以两者同名时按
/// 根 → vpk2patch → Overwrite 的顺序逐层覆盖，最后一层赢，行为与只有根 + Overwrite 时一致。
/// </para>
/// <para>
/// <b>为什么集中在这里。</b>发现（导入/对账）、索引（部署计划）、解析（实际部署文件）
/// 三处必须用同一套规则；各写一遍必然漂移，而漂移的表现是"库里看得见、部署时没有"，
/// 这类问题只能靠人肉比对才能发现。
/// </para>
/// </summary>
public static class ModNodePayloadLayout
{
	/// <summary>由 <c>vpk2patch</c> 生成的实际生效 patch 目录名。</summary>
	public const string VpkPatchDirectoryName = "vpk2patch";

	/// <summary>装饰/派生重建产物的替换目录名。</summary>
	public const string OverwriteDirectoryName = "Overwrite";

	/// <summary>这个目录名是不是保留给 vpk2patch 载荷的（大小写不敏感）。</summary>
	public static bool IsVpkPatchDirectoryName(string? name)
		=> !string.IsNullOrWhiteSpace(name)
			&& name.Equals(VpkPatchDirectoryName, StringComparison.OrdinalIgnoreCase);

	/// <summary>这个目录名是不是装饰重建的替换目录（大小写不敏感）。</summary>
	public static bool IsOverwriteDirectoryName(string? name)
		=> !string.IsNullOrWhiteSpace(name)
			&& name.Equals(OverwriteDirectoryName, StringComparison.OrdinalIgnoreCase);

	public static string GetVpkPatchDirectory(string nodeDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(nodeDirectory);
		return Path.Combine(nodeDirectory, VpkPatchDirectoryName);
	}

	public static string GetOverwriteDirectory(string nodeDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(nodeDirectory);
		return Path.Combine(nodeDirectory, OverwriteDirectoryName);
	}

	/// <summary>
	/// 这个节点自己的内容目录：先是节点根，再是（存在时的）<c>vpk2patch/</c>。
	/// <b>不含 <c>Overwrite/</c></b>——它是替换层，由调用方在内容定下来之后再叠上去。
	/// </summary>
	public static IReadOnlyList<string> EnumeratePayloadDirectories(string nodeDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(nodeDirectory);
		var vpkPatches = GetVpkPatchDirectory(nodeDirectory);
		return Directory.Exists(vpkPatches)
			? [nodeDirectory, vpkPatches]
			: [nodeDirectory];
	}

	/// <summary>
	/// 按覆盖顺序枚举节点内容目录里的<b>文件</b>：后出现的同名文件覆盖先出现的。
	/// 只读每个目录的顶层——载荷目录里不再有嵌套。
	/// </summary>
	public static IEnumerable<string> EnumeratePayloadFiles(string nodeDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(nodeDirectory);
		var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var directory in EnumeratePayloadDirectories(nodeDirectory))
		{
			foreach (var path in EnumerateFiles(directory))
				selected[Path.GetFileName(path)] = path;
		}

		return selected.Values;
	}

	/// <summary>同 <see cref="EnumeratePayloadFiles"/>，但给"文件名 → 路径"，供需要按名覆盖的调用方使用。</summary>
	public static Dictionary<string, string> EnumeratePayloadFilesByName(string nodeDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(nodeDirectory);
		var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var directory in EnumeratePayloadDirectories(nodeDirectory))
		{
			foreach (var path in EnumerateFiles(directory))
				selected[Path.GetFileName(path)] = path;
		}

		return selected;
	}

	/// <summary>
	/// 这个节点<b>实际生效</b>的文件：载荷（根 + <c>vpk2patch/</c>，同名后者赢），
	/// 再让 <c>Overwrite/</c> 顶掉同名的那几个。
	/// <para>
	/// <b>为什么三处（部署解析、部署索引、内容事实）必须共用这一个函数。</b>
	/// 它们回答的问题不同，但都建立在"哪些文件算这个 Mod 的"之上。各写一遍，
	/// 迟早出现"详情页看得见、部署时没有"或者反过来——那种不一致只能靠人肉比对才能发现。
	/// </para>
	/// <para>
	/// <c>Overwrite/</c> 里的文件只有在<b>同名载荷存在</b>且<b>本身是一份能读的 patch TOC</b>
	/// 时才顶替：前者是"替换层"的定义，后者防的是把残留文件当成一次额外部署。
	/// </para>
	/// </summary>
	public static Dictionary<string, string> EnumerateEffectiveFilesByName(string nodeDirectory)
	{
		var selected = EnumeratePayloadFilesByName(nodeDirectory);
		var overwrite = GetOverwriteDirectory(nodeDirectory);
		if (!Directory.Exists(overwrite))
			return selected;

		// ① 先定 base：必须是一份能读的 patch TOC 才顶替。
		var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var path in EnumerateFiles(overwrite))
		{
			var name = Path.GetFileName(path);
			if (IsSidecarName(name)) continue;
			if (!selected.ContainsKey(name)) continue;
			if (!IsPatchToc(path)) continue;
			selected[name] = path;
			accepted.Add(name);
		}

		// ② sidecar 跟着它的 base 走：base 没被顶替时，孤立的 sidecar 也不参与。
		//    为什么不逐个做 TOC 校验：sidecar 本来就不是 TOC，拿 TOC 魔数去判它必然失败。
		foreach (var path in EnumerateFiles(overwrite))
		{
			var name = Path.GetFileName(path);
			if (!IsSidecarName(name)) continue;
			if (!selected.ContainsKey(name)) continue;
			var baseName = name[..name.LastIndexOf('.')];
			if (!accepted.Contains(baseName)) continue;
			selected[name] = path;
		}

		return selected;
	}

	private static bool IsSidecarName(string name)
		=> name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase)
			|| name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// 这个文件是不是一份 patch TOC（首 4 字节是固定魔数）。
	/// <para>读不出来一律当"不是"——宁可少顶替一个，也不要把半截文件当 patch 部署。</para>
	/// </summary>
	public static bool IsPatchToc(string path)
	{
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			Span<byte> header = stackalloc byte[sizeof(uint)];
			return stream.Read(header) == header.Length && BitConverter.ToUInt32(header) == 4026531857;
		}
		catch (IOException) { return false; }
		catch (UnauthorizedAccessException) { return false; }
	}

	private static IEnumerable<string> EnumerateFiles(string directory)
	{
		try
		{
			return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly);
		}
		catch (DirectoryNotFoundException)
		{
			return Array.Empty<string>();
		}
		catch (IOException)
		{
			return Array.Empty<string>();
		}
		catch (UnauthorizedAccessException)
		{
			return Array.Empty<string>();
		}
	}
}
