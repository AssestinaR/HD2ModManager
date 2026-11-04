using System.IO;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// "这个 VPK 是不是库里某个 Mod 的来源"——重新导入同一个包时用它判断该**更新**还是**新建**。
/// <para>
/// <b>为什么需要它。</b>重复导入同一个包，过去每次都新建一个目录，于是库里堆出
/// <c>X</c> 与 <c>X (2)</c> 两个长得一模一样的 Mod：同一个素体 key 有两个持有者，
/// 护甲映射指哪一个都对不上另一半。用户看到的只是"库里的 Mod 莫名多了一份"。
/// </para>
/// <para>
/// <b>判据是「文件名 + 字节数」，两个都要一样。</b>导入时我们把来源 VPK 原件留在 Mod 根上
/// （那是"回到原始转换"的唯一依据），所以它天然是身份凭据。字节数必须比：
/// 同名但内容不同的两份包（作者发了第二版，或两个人各自把包命名成 <c>材质.vpk</c>）
/// 若按同源处理，等于**覆盖掉别人的 Mod**；而多建一个 Mod 只是让人烦。
/// 两个方向的代价差得远，所以这里保守。
/// </para>
/// </summary>
public static class VpkSourceIdentity
{
	/// <summary>库里"来自同一个 VPK"的那个 Mod。</summary>
	/// <param name="Directory">它的完整目录。</param>
	/// <param name="Name">目录名（给用户看的那个）。</param>
	/// <param name="CandidateCount">同源 Mod 一共有几个（&gt;1 说明还有历史遗留的重复）。</param>
	public sealed record ExistingMod(string Directory, string Name, int CandidateCount);

	/// <summary>
	/// 在 <paramref name="modsRoot"/> 里找根上躺着**同一个** VPK 的 Mod 目录；找不到返回 <c>null</c>。
	/// <para>
	/// 只认"转换产物目录"（含 <c>vpk2patch/</c>）：没有它的目录不是这套流程产出的东西，
	/// 手搓的 Mod 不该被我们覆盖。
	/// </para>
	/// <para>
	/// <b>多个命中取排序后的第一个，并把个数带出去。</b>那是历史遗留的重复（同一个包导入过两次）。
	/// 这里不替用户删东西 —— 报告里点名，让他自己决定。
	/// </para>
	/// </summary>
	public static ExistingMod? FindExistingMod(string modsRoot, string vpkPath)
	{
		if (string.IsNullOrWhiteSpace(modsRoot) || string.IsNullOrWhiteSpace(vpkPath))
			return null;
		var fileName = Path.GetFileName(vpkPath);
		if (string.IsNullOrWhiteSpace(fileName))
			return null;
		if (!Directory.Exists(modsRoot) || !File.Exists(vpkPath))
			return null;

		try
		{
			var length = new FileInfo(vpkPath).Length;
			var matches = new List<string>();
			foreach (var directory in Directory.EnumerateDirectories(modsRoot))
			{
				if (!Directory.Exists(Path.Combine(directory, ModNodePayloadLayout.VpkPatchDirectoryName)))
					continue;
				var candidate = Path.Combine(directory, fileName);
				if (!File.Exists(candidate)) continue;
				if (new FileInfo(candidate).Length != length) continue;
				matches.Add(directory);
			}

			if (matches.Count == 0)
				return null;
			matches.Sort(StringComparer.OrdinalIgnoreCase);
			var chosen = matches[0];
			return new ExistingMod(chosen, Path.GetFileName(chosen), matches.Count);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 认不出来就当新 Mod 处理：多一个 Mod，比"猜错把别人的产物覆盖掉"安全。
			return null;
		}
	}
}
