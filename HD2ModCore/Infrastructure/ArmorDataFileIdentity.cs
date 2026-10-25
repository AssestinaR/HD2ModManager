using System.Security.Cryptography;
using System.Text;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 护甲注册表文件"未被我们改动过"的身份，用于 GameData 索引指纹。
///
/// 真映射模式会重写 <c>generated_customization_armor_sets.dl_bin</c>，而这个文件直接参与
/// 索引指纹。关键是：**无论我们有没有写过它，指纹输入必须是同一个值**，否则每次应用真
/// 映射都会让索引误报过期。
///
/// 做法：一律返回"原始密文"的 SHA256。我们写过时，原始密文就是备份文件本身（它是逐字节
/// 的原件），所以两边算出来完全相同；游戏真更新时密文变了，身份随之变化，更新仍被识别。
/// </summary>
public static class ArmorDataFileIdentity
{
	/// <summary>状态文件：<c>原始密文SHA256|写出密文SHA256|原始内容SHA256|buildid</c>。</summary>
	public static string ResolveStatePath(string? registryRoot = null)
		=> Path.Combine(
			registryRoot ?? RuntimeArmorRegistryService.ResolveRegistryRoot(),
			"backup",
			ArmorRegistryDataMapper.StateFileName);

	/// <summary>
	/// 该读哪一份文件作为"权威数据"。
	///
	/// 信任模型：**备份 B 是唯一可信的原件**。只要 B 存在且属于当前游戏版本，权威数据就一律
	/// 读它——完全不看磁盘上的 R 是什么状态，因为用户可能用任何办法改过它（其他 mod、
	/// 手动替换、脚本）。B 只会在三种时机重建：首次运行、游戏更新（buildid 变化）、
	/// Steam 校验完成之后。
	///
	/// 没有状态文件、没有备份、或备份属于旧版本时，退回读 R。
	/// </summary>
	public static string GetUnmodifiedSealedPath(string sealedPath, string? registryRoot = null)
	{
		try
		{
			var statePath = ResolveStatePath(registryRoot);
			if (!File.Exists(statePath)) return sealedPath;
			var backupPath = Path.Combine(
				Path.GetDirectoryName(statePath)!, ArmorRegistryDataMapper.BackupFileName);
			if (!File.Exists(backupPath)) return sealedPath;

			// 备份必须属于**同一个游戏目录**且同一游戏版本。少了这个约束，测试环境或
			// 多份游戏安装会互相串用备份（读到别人机器上的原件）。
			var parts = File.ReadAllText(statePath).Trim().Split('|');
			var dataDirectory = Path.GetDirectoryName(Path.GetDirectoryName(sealedPath));
			if (dataDirectory is null) return sealedPath;
			var recordedDirectory = parts is { Length: > 4 } ? parts[4] : null;
			// 旧状态文件没有这一列，无法证明备份属于这个游戏目录，一律不信任。
			// 注意不能对空串调用 GetFullPath（会抛 ArgumentException）。
			if (recordedDirectory is not { Length: > 0 }) return sealedPath;
			if (!string.Equals(
					Path.GetFullPath(recordedDirectory),
					Path.GetFullPath(dataDirectory),
					StringComparison.OrdinalIgnoreCase))
			{
				return sealedPath;
			}
			var recordedBuild = parts is { Length: > 3 } ? parts[3] : null;
			if (recordedBuild is { Length: > 0 })
			{
				var currentBuild = SteamAppManifest
					.TryRead(SteamAppManifest.ResolveManifestPath(dataDirectory))?.TrustedBuildId;
				if (currentBuild is { Length: > 0 }
					&& !string.Equals(recordedBuild, currentBuild, StringComparison.Ordinal))
				{
					return sealedPath;
				}
			}
			return backupPath;
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or FormatException)
		{
			return sealedPath;
		}
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
