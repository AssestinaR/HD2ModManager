using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <param name="FilesByName">会进游戏的文件（按文件名）。</param>
/// <param name="SkipReason">这一条不部署的原因；部署了就为 <c>null</c>。</param>
public sealed record ModDeployableSet(
	IReadOnlyDictionary<string, string> FilesByName,
	string? SkipReason);

/// <summary>
/// 一个 Mod 里**真正会进游戏**的文件。判定只看"有哪一层"，不逐个 patch 判断。
/// <para>
/// 顺序（与部署器的实际行为一一对应）：
/// <list type="number">
/// <item><b>Overwrite 里有有效 patch</b> ⇒ 只部署它，这个 Mod 结束。</item>
/// <item><b>sectional / vpk2patch 类</b>：有基底目录 ⇒ 把里面的文件**硬链接**进 Overwrite 并部署；
/// 没有 ⇒ 跳过这条并说明原因。</item>
/// <item><b>legacy / logical</b>：根目录有 patch ⇒ 部署；没有 ⇒ 跳过这条并说明原因。</item>
/// </list>
/// </para>
/// <para>
/// <b>为什么硬链接而不是复制。</b>Overwrite 是"顶掉同名 patch"的一层，把基底文件链进去之后，
/// 这个 Mod 下一次就命中第 1 条，判定变成一次目录列举——不需要每次部署都重新算一遍。
/// 硬链接不占额外空间（同一分区），也不产生第二份需要同步的副本。
/// </para>
/// <para>
/// <b>为什么不需要"每个槽位在基底里必须有一份默认版"。</b>那条规则是我多加的约束：
/// 部署器根本不按槽位判断，它按"这一层在不在"判断。基底里有什么就链什么，
/// 少一个槽位也只是少一个可选材质，不影响部署。
/// </para>
/// </summary>
public static class ModDeployableFiles
{
	public static ModDeployableSet Resolve(string nodeDirectory, IPatchFileNameParser parser)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(nodeDirectory);
		ArgumentNullException.ThrowIfNull(parser);

		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(nodeDirectory);

		// ① 生效层已经有内容 ⇒ 它就是全部，不再看任何别的层。
		if (HasValidPatch(overwrite, parser))
			return new ModDeployableSet(EnumerateFiles(overwrite), null);

		var kind = new ModStorageKindResolver(parser).Resolve(nodeDirectory);

		// ② sectional / vpk2patch：基底就是答案，把它物化成生效层。
		if (kind is ModStorageKind.Sectional or ModStorageKind.Vpk2Patch)
		{
			var payload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(nodeDirectory);
			if (payload is null)
				return new ModDeployableSet(Empty, $"这个 Mod 是 {kind} 类，但没有基底目录（sectional/），没有可部署的 patch。");
			return new ModDeployableSet(Materialize(payload, overwrite), null);
		}

		// ③ legacy / logical：patch 在根目录上。
		if (HasValidPatch(nodeDirectory, parser))
			return new ModDeployableSet(EnumerateFiles(nodeDirectory), null);

		return new ModDeployableSet(Empty,
			$"这个 Mod 是 {kind} 类，根目录也没有 patch，没有可部署的东西。");
	}

	private static readonly Dictionary<string, string> Empty = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>硬链接。net8 的 <c>File</c> 还没有这个 API（net9 才加），所以直接调 Win32。</summary>
	[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
	private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, nint lpSecurityAttributes);

	/// <summary>目录里至少有一份"能被解析成 patch"的文件——不要求每一个都是。</summary>
	private static bool HasValidPatch(string directory, IPatchFileNameParser parser)
	{
		if (!Directory.Exists(directory)) return false;
		foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
		{
			if (parser.TryParse(Path.GetFileName(path), out var info)
				&& info is not null
				&& info.SidecarKind == PatchSidecarKind.Base)
				return true;
		}
		return false;
	}

	private static Dictionary<string, string> EnumerateFiles(string directory)
	{
		var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
			files[Path.GetFileName(path)] = path;
		return files;
	}

	/// <summary>把基底目录里的文件链进 Overwrite，返回生效层的内容。</summary>
	private static Dictionary<string, string> Materialize(string payloadDirectory, string overwriteDirectory)
	{
		Directory.CreateDirectory(overwriteDirectory);
		
		foreach (var source in Directory.EnumerateFiles(payloadDirectory, "*", SearchOption.TopDirectoryOnly))
		{
			var target = Path.Combine(overwriteDirectory, Path.GetFileName(source));
			if (File.Exists(target)) continue;
			try
			{
				if (!CreateHardLinkW(target, source, 0))
					throw new IOException($"建立硬链接失败：{Path.GetFileName(source)}（Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）");
			}
			catch (Exception exception) when (exception is IOException
				or UnauthorizedAccessException
				or PlatformNotSupportedException)
			{
				// 硬链接建不了（跨卷、文件系统不支持）就复制：结果是同一份内容，只是多占一份空间。
				File.Copy(source, target, overwrite: false);
			}
		}
		return EnumerateFiles(overwriteDirectory);
	}
}
