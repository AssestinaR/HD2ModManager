namespace HD2ModCore.Infrastructure;

/// <param name="PatchPath">装进去之后主 patch 的完整路径（调用方后续就地改字节时要用）。</param>
/// <param name="WrittenFiles">这次实际写下的文件名（含 sidecar）。</param>
public sealed record OverwritePatchInstallResult(string PatchPath, IReadOnlyList<string> WrittenFiles);

/// <summary>
/// 把一份 patch（连同它的 sidecar）装进 <c>Overwrite/</c>，替换掉同名的旧版本。
/// <para>
/// <b>为什么单独抽出来。</b>"删掉同名旧文件 → 复制 TOC → 复制它实际存在的 sidecar"这段
/// 在换甲里有两条完全一样的实现（主路径与"退回来源"路径）。而它正好是未来"变体槽位物化"
/// 需要的最小原语：把选中的那一份放进 Overwrite。写两遍就一定会漂——
/// 漂的表现是 sidecar 与新 TOC 不配对（游戏里材质全黑），而那种错在包里看不出来。
/// </para>
/// <para>
/// <b>为什么必须先删同名旧文件。</b>Overwrite 是"顶掉同名 patch"的一层。旧变体的
/// <c>.gpu_resources</c> 若留着而 TOC 换成了新的，两者尺寸对不上，轻则材质错乱重则加载失败。
/// </para>
/// </summary>
public static class OverwritePatchInstaller
{
	/// <summary>patch 的 sidecar 后缀。顺序固定，便于日志比对。</summary>
	public static IReadOnlyList<string> SidecarSuffixes { get; } = ["", ".gpu_resources", ".stream"];

	public static OverwritePatchInstallResult Install(string destinationDirectory, string sourcePatchPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(sourcePatchPath);
		if (!File.Exists(sourcePatchPath))
			throw new FileNotFoundException("要装进 Overwrite 的 patch 不存在。", sourcePatchPath);

		Directory.CreateDirectory(destinationDirectory);
		var fileName = Path.GetFileName(sourcePatchPath);
		var patchPath = Path.Combine(destinationDirectory, fileName);

		// 先清同名旧版本（含它的 sidecar）。不删旧 sidecar 就会与新 TOC 错配。
		foreach (var stale in Directory.EnumerateFiles(destinationDirectory, fileName + "*"))
			DeleteQuietly(stale);

		var written = new List<string>(SidecarSuffixes.Count);
		foreach (var suffix in SidecarSuffixes)
		{
			var from = sourcePatchPath + suffix;
			if (!File.Exists(from)) continue;
			var to = patchPath + suffix;
			File.Copy(from, to, overwrite: true);
			written.Add(Path.GetFileName(to));
		}

		return new OverwritePatchInstallResult(patchPath, written);
	}

	/// <summary>
	/// 从 <c>Overwrite/</c> 里撤掉某个 patch（含 sidecar）。
	/// 用于"停用这个变体"——只删这一份，不动别的。
	/// </summary>
	public static bool Remove(string destinationDirectory, string patchFileName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(patchFileName);
		if (!Directory.Exists(destinationDirectory)) return false;
		var removed = false;
		foreach (var stale in Directory.EnumerateFiles(destinationDirectory, patchFileName + "*"))
		{
			DeleteQuietly(stale);
			removed = true;
		}
		return removed;
	}

	private static void DeleteQuietly(string path)
	{
		try
		{
			VpkPayloadSwap.ClearReadOnly(path);
			File.Delete(path);
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or System.Security.SecurityException)
		{
			// 删不掉就留着；调用方随后会用 overwrite: true 覆盖同名文件。
		}
	}
}
