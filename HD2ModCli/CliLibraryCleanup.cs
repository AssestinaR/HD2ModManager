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
		return entries;
	}

	public static CliLibraryCleanupResult Apply(string libraryRoot, IReadOnlyList<CliLibraryCleanupEntry> entries)
	{
		ArgumentNullException.ThrowIfNull(entries);
		long bytes = 0;
		var items = 0;
		var failures = new List<string>();
		foreach (var entry in entries)
		{
			var directory = Path.Combine(libraryRoot, entry.RelativePath);
			try
			{
				ClearReadOnly(directory);
				Directory.Delete(directory, recursive: true);
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
		return new CliLibraryCleanupResult(true, bytes, items, entries, failures);
	}

	private static CliLibraryCleanupEntry Create(string libraryRoot, string directory)
	{
		long bytes = 0;
		try
		{
			bytes = new DirectoryInfo(directory)
				.EnumerateFiles("*", SearchOption.AllDirectories)
				.Sum(file => file.Length);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 读不到就当 0：这是"能回收多少"的估计，不是账目。
		}
		return new CliLibraryCleanupEntry(Path.GetRelativePath(libraryRoot, directory), bytes);
	}

	/// <summary>库里的产物是只读的，不清属性会删不掉。</summary>
	private static void ClearReadOnly(string directory)
	{
		foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
			File.SetAttributes(file, System.IO.FileAttributes.Normal);
	}
}
