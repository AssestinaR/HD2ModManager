using System.Security.Cryptography;
using System.Text;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：缩略图"内容指纹"的**唯一定义**。事实缓存（中心）与磁盘缓存（管理器）都用它判断
//       "这份缩略图是否还代表当前的源图像"，于是全库只有一套缩略图失效协议（设计文档 §27）。
// Purpose: The single definition of the thumbnail content fingerprint. Both the fact cache
//          (center) and the disk cache (manager) use it, so the whole product has exactly one
//          thumbnail invalidation protocol (design doc §27).
//
// 为什么指纹必须包含"长度 + 修改时间"而不只是路径：
//   图标文件经常是**同名替换**（`icon.png` 换成另一张 `icon.png`）。
//   只有路径参与时，新内容会命中旧缩略图，用户看到的是"换了图标但卡片没变"，而且不会自愈。
public static class ThumbnailFingerprint
{
	// 源图像不存在时的稳定哨兵。刻意**不**退化成"整个 Mod 目录的哈希"：
	// 没有图标时，目录里任何无关文件的改动都不该让缩略图事实失效。
	public const string Missing = "missing";

	/// <summary>
	/// 源图像的内容指纹：绝对路径 + 文件长度 + 最后写入时间。
	/// 路径为空、非法或文件不存在时返回 <see cref="Missing"/>。
	/// </summary>
	public static string OfSource(string? sourcePath)
	{
		if (string.IsNullOrWhiteSpace(sourcePath)) return Missing;
		string absolute;
		try { absolute = Path.GetFullPath(sourcePath); }
		catch { return Missing; }

		FileInfo info;
		try
		{
			info = new FileInfo(absolute);
			if (!info.Exists) return Missing;
		}
		catch { return Missing; }

		return Sha256($"{absolute}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
	}

	/// <summary>
	/// 节点级指纹：在源指纹前加 <paramref name="nodeId"/> 命名空间，供事实缓存按节点寻址。
	/// </summary>
	public static string OfNode(ModNodeId nodeId, string? sourcePath)
		=> Sha256($"{nodeId.Value:N}|{OfSource(sourcePath)}");

	private static string Sha256(string text)
		=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
