using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure.KitInstance;

/// <summary>
/// 内容指纹：生成「按实例隐藏」记录时 Mod 的<b>基底</b>载荷长什么样（文件名、长度、最后写入时间）。
/// 不含 <c>Overwrite/</c>，所以我们自己写的派生 patch 不会让指纹变掉；Mod 被更新（基底变了）才会变。
/// </summary>
public static class KitInstanceContentFingerprint
{
	public static string Compute(string nodeDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(nodeDirectory);
		var builder = new StringBuilder("kit-instance-content:v1\n");
		if (Directory.Exists(nodeDirectory))
		{
			foreach (var (name, path) in ModNodePayloadLayout.EnumeratePayloadFilesByName(nodeDirectory)
				.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
			{
				if (!name.Contains(".patch_", StringComparison.OrdinalIgnoreCase)) continue;
				var info = new FileInfo(path);
				builder.Append(name.ToLowerInvariant()).Append('|')
					.Append(info.Length.ToString(CultureInfo.InvariantCulture)).Append('|')
					.Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
			}
		}

		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
	}
}
