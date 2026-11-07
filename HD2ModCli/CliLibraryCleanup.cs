namespace HD2ModCli;

internal sealed record CliLibraryCleanupEntry(string RelativePath, long Bytes);

internal sealed record CliLibraryCleanupResult(
	bool Applied,
	long Bytes,
	int Items,
	IReadOnlyList<CliLibraryCleanupEntry> Entries,
	IReadOnlyList<string> Failures);

/// <summary>
/// 清理库里的中间产物：换甲/重转留下的 <c>*.bak-*</c>、<c>*.backup-*</c> 目录。
/// <para>
/// 它们<b>没有任何用处</b>——真正的数据在 Mod 文件夹里的 patch 与来源 VPK 上，
/// 这些只是上一次操作前的临时副本。留着只占空间（真库现在 3 项、134 MB）。
/// </para>
/// <para>
/// <b>不传 <c>--confirm</c> 就只报大小，一个字节都不删。</b>让用户和 agent 先看清要回收多少，
/// 再决定动不动手。
/// </para>
/// </summary>
internal static class CliLibraryCleanup
{
	/// <summary>上一次 <see cref="Apply"/> 顺带清掉的空壳目录数量。</summary>
	public static int RemovedResidualFolders { get; private set; }

	private static bool IsJunk(string name)
		=> name.Contains(".bak-", StringComparison.OrdinalIgnoreCase)
		|| name.Contains(".backup-", StringComparison.OrdinalIgnoreCase);

	/// <summary>扫出所有可回收目录。它们既可能在库根下，也常在每个 Mod 文件夹里面。</summary>
	public static IReadOnlyList<CliLibraryCleanupEntry> Scan(string libraryRoot)
	{
		if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
			return Array.Empty<CliLibraryCleanupEntry>();

		var entries = new List<CliLibraryCleanupEntry>();
		foreach (var top in Directory.EnumerateDirectories(libraryRoot))
		{
			if (IsJunk(Path.GetFileName(top)))
				entries.Add(Create(libraryRoot, top));
			else
				entries.AddRange(Directory.EnumerateDirectories(top)
					.Where(inner => IsJunk(Path.GetFileName(inner)))
					.Select(inner => Create(libraryRoot, inner)));
		}

		// **同名垃圾也可能是文件而不是目录。** 实测真库里有一个 78 MB 的普通文件
		// `vpk2patch.bak-20261105-103748`：只扫目录的话它永远清不掉，而且它的扩展名
		// 不在"无害"清单里，会把整个退役文件夹卡成"不可删的残留"。
		//
		// 但**管理器自己占用的目录要跳过**：`.hd2modmanager\` 里那些
		// `armor-registry.workspace.json.bak-*` 是用户护甲映射的回滚点，不是垃圾。
		// 删掉它们等于把用户的映射历史抹了——那不是清理，那是丢数据。
		foreach (var top in Directory.EnumerateDirectories(libraryRoot).Prepend(libraryRoot))
		{
			if (!string.Equals(top, libraryRoot, StringComparison.Ordinal)
				&& Path.GetFileName(top).StartsWith(
					HD2ModCore.Infrastructure.CustomModelCatalog.ManagerDirectoryPrefix,
					StringComparison.OrdinalIgnoreCase))
				continue;
			entries.AddRange(Directory.EnumerateFiles(top, "*", SearchOption.TopDirectoryOnly)
				.Where(file => IsJunk(Path.GetFileName(file)))
				.Select(file => Create(libraryRoot, file)));
		}

		return entries;
	}

	public static CliLibraryCleanupResult Apply(
		string libraryRoot,
		IReadOnlyList<CliLibraryCleanupEntry> entries,
		HD2ModCore.Domain.LibrarySnapshot? snapshot)
	{
		ArgumentNullException.ThrowIfNull(entries);
		RemovedResidualFolders = 0;
		long bytes = 0;
		var items = 0;
		var failures = new List<string>();
		foreach (var entry in entries)
		{
			var target = Path.Combine(libraryRoot, entry.RelativePath);
			try
			{
				if (Directory.Exists(target))
				{
					ClearReadOnly(target);
					Directory.Delete(target, recursive: true);
				}
				else
				{
					File.SetAttributes(target, System.IO.FileAttributes.Normal);
					File.Delete(target);
				}
				bytes += entry.Bytes;
				items++;
			}
			catch (Exception exception) when (exception is IOException
				or UnauthorizedAccessException
				or System.Security.SecurityException)
			{
				failures.Add($"{entry.RelativePath}：{exception.Message}");
			}
		}
		if (snapshot is not null)
		{
			var residual = HD2ModCore.Infrastructure.CoreServices
				.CreateModLibraryResidualCleanupService()
				.Cleanup(snapshot, libraryRoot);
			RemovedResidualFolders = residual.RemovedCount;
			failures.AddRange(residual.Failures);
		}
		return new CliLibraryCleanupResult(true, bytes, items, entries, failures);
	}

	private static CliLibraryCleanupEntry Create(string libraryRoot, string path)
	{
		long bytes = 0;
		try
		{
			bytes = Directory.Exists(path)
				? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)
				: new FileInfo(path).Length;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 读不到就当 0：这是"能回收多少"的估计，不是账目。
		}
		return new CliLibraryCleanupEntry(Path.GetRelativePath(libraryRoot, path), bytes);
	}

	/// <summary>库里的产物是只读的，不清属性会删不掉。</summary>
	private static void ClearReadOnly(string directory)
	{
		foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
			File.SetAttributes(file, System.IO.FileAttributes.Normal);
	}
}
