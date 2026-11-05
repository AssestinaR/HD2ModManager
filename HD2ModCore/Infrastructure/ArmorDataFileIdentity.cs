using System.Security.Cryptography;
using System.Text;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 护甲注册表文件"未被我们改动过"的身份，用于 GameData 索引指纹。
///
/// 真映射模式会重写 <c>generated_customization_armor_sets.dl_bin</c>，而这个文件直接参与
/// 索引指纹。关键是：**无论我们有没有写过它，指纹输入必须是同一个值**，否则每应用一次真
/// 映射都会让索引误报过期。
///
/// 做法：一律返回"原件"的 SHA256。原件就是 dl_bin 编辑器记下的那份副本
/// （<see cref="ArmorRegistryDataMapper.BackupFileName"/>）——它只在第一次应用时从当时的
/// 官方文件记一份，之后不再变；没有副本时才回退读当前位置的文件。
/// </summary>
/// <remarks>
/// <b>这里不做任何"过期 / 归属"判断。</b>受管 dl_bin 是派生事实，不值得为它维护状态；
/// 副本是否存在是唯一判据。
/// </remarks>
public static class ArmorDataFileIdentity
{
	/// <summary>原件副本的位置（与 <c>ArmorRegistryDataMapper</c> 同一个 backup 目录）。</summary>
	public static string ResolveBackupPath(string? registryRoot = null)
		=> Path.Combine(
			registryRoot ?? RuntimeArmorRegistryService.ResolveRegistryRoot(),
			"backup",
			ArmorRegistryDataMapper.BackupFileName);

	/// <summary>
	/// 该读哪一份文件作为"未改动过"的身份：有原件副本就读它，否则读当前位置的文件。
	/// <para>
	/// <b>没给 <paramref name="registryRoot"/> 就一律读当前位置的文件。</b>不给根目录意味着
	/// 调用方不知道自己在哪个安装里，这时去猜一个全局路径会把**另一台安装/另一份游戏**的副本
	/// 读进来（实测：测试里构造的临时游戏目录被本机真实副本顶掉，读出一份 422 个套件的库）。
	/// </para>
	/// </summary>
	public static string GetUnmodifiedSealedPath(string sealedPath, string? registryRoot = null)
	{
		if (string.IsNullOrWhiteSpace(registryRoot)) return sealedPath;
		var backupPath = ResolveBackupPath(registryRoot);
		return File.Exists(backupPath) ? backupPath : sealedPath;
	}

	/// <summary>
	/// 返回"未改动过"的密文哈希。任何情况下都不返回 null——调用方必须始终使用它，
	/// 不能按"是否我们写的"切换成另一种形态。
	/// </summary>
	public static string GetUnmodifiedSealedSha(string sealedPath, string? registryRoot = null)
	{
		var effective = GetUnmodifiedSealedPath(sealedPath, registryRoot);
		try
		{
			return File.Exists(effective) ? Sha256File(effective) : "missing";
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return "unreadable";
		}
	}

	/// <summary>把身份串转成可直接并入指纹的字节。</summary>
	public static byte[] IdentityBytes(string identity)
		=> Encoding.UTF8.GetBytes("armor-sets-sealed:" + identity);

	static string Sha256File(string path)
		=> Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
