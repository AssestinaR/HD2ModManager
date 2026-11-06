using HD2ModCore.Decoration;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 托管 <c>Overwrite/</c> 目录的**唯一**归属与事务入口。
/// <para>
/// <b>为什么必须集中。</b>Overwrite 是"派生出来、真正会进游戏的那一层"。它的安全性全靠
/// 一条规矩：<b>只覆盖/删除自己生成的东西</b>。这条规矩以前散在几个地方各写一半——
/// 头盔合并有备份与回滚、换甲什么都不写、界面的就地烘焙直接改字节。
/// 三种做法凑在一起的结果是：同一个目录有时算"管理器的"、有时算"用户的"，
/// 而"算错"的后果是删掉用户的文件，或者拒绝写自己刚生成的东西。
/// </para>
/// <para>
/// <b>四个动作，语义固定。</b>
/// <list type="number">
/// <item><see cref="Prepare"/>：把现有目录挪成备份、建一个空目录。**不是自己生成的直接拒绝**。</item>
/// <item><see cref="Commit"/>：写归属标记、删备份。只有走到这一步才算"这次派生成功了"。</item>
/// <item><see cref="Rollback"/>：扔掉半成品、把备份挪回来。</item>
/// <item><see cref="RemoveIfManaged"/>：连目录一起清掉（派生来源被关掉时用）。</item>
/// </list>
/// </para>
/// <para>
/// <b>为什么"没标记就拒绝"不能放宽。</b>Overwrite 里的文件会被部署。放宽一次，
/// 用户手工放进来的补丁就会在某次派生里被静默删除——那种事没有任何日志能补救。
/// </para>
/// </summary>
public static class ManagedOverwriteDirectory
{
	/// <summary>这个目录是不是管理器生成的（任一枚标记即算）。</summary>
	public static bool IsManaged(string directory)
		=> ManagedOverwriteMarkers.All.Any(marker =>
			File.Exists(Path.Combine(directory, marker)));

	/// <summary>
	/// 准备一次重写：把现有目录挪成备份、建一个新的空目录。
	/// 返回备份路径（原本不存在或原本是空目录时为 <c>null</c>）。
	/// </summary>
	/// <exception cref="InvalidOperationException">目录存在、**有内容**、且不是管理器生成的。</exception>
	public static string? Prepare(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		if (!Directory.Exists(directory))
		{
			Directory.CreateDirectory(directory);
			return null;
		}
		// 空目录直接接管：它不可能装着用户的东西（上次派生中断留下的壳就是这种形状）。
		// 只认标记会把这种壳判成"别人的"，于是一个能自己长好的状态变成必须人工清理。
		if (!IsManaged(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
			return null;
		if (!IsManaged(directory))
			throw new InvalidOperationException(
				"这个 Mod 的 Overwrite 目录不是管理器生成的，已拒绝覆盖。"
				+ "如果里面有你手工放进去的文件，请先把它移走再重试。");
		var backup = directory + ".backup-" + Guid.NewGuid().ToString("N");
		Directory.Move(directory, backup);
		Directory.CreateDirectory(directory);
		return backup;
	}

	/// <summary>这次派生成功了：盖上归属标记、丢掉备份。</summary>
	public static void Commit(string directory, string marker)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		ArgumentException.ThrowIfNullOrWhiteSpace(marker);
		File.WriteAllText(Path.Combine(directory, marker), "generated");
	}

	/// <summary>丢掉备份目录（提交之后调用）。</summary>
	public static void DiscardBackup(string? backup)
	{
		if (backup is null) return;
		try
		{
			if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or System.Security.SecurityException)
		{
			// 备份删不掉不影响正确性：它只是残留，下次派生会再产生一个。
		}
	}

	/// <summary>这次派生失败了：扔掉半成品、把备份挪回来。</summary>
	public static void Rollback(string directory, string? backup)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		try
		{
			if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or System.Security.SecurityException)
		{
			// 删不掉半成品时仍然尝试把备份挪回来，让目录至少回到上一版。
		}
		if (backup is not null && Directory.Exists(backup)) Directory.Move(backup, directory);
	}

	/// <summary>
	/// 整目录清掉。**只有自己生成的才删**；不是的话返回 <c>false</c> 而不是抛，
	/// 因为"派生来源被关掉"是一个正常的用户操作，不该变成一个错误弹窗。
	/// </summary>
	public static bool RemoveIfManaged(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		if (!Directory.Exists(directory)) return false;
		if (!IsManaged(directory)) return false;
		Directory.Delete(directory, recursive: true);
		return true;
	}

	/// <summary>严格版：不是自己生成的就抛。<see cref="HostDerivedOverwriteService"/> 用它。</summary>
	public static void RemoveManaged(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		if (!Directory.Exists(directory)) return;
		if (!IsManaged(directory))
			throw new InvalidOperationException("主体 Mod 的 Overwrite 目录不是管理器生成，已拒绝删除。");
		Directory.Delete(directory, recursive: true);
	}
}
