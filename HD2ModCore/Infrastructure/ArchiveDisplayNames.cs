namespace HD2ModCore.Infrastructure;

/// <summary>
/// Archive 显示名的可用性判定。
/// <para>
/// 存在的理由很具体：注册表里有 32 个 kit 的 <c>upper</c> / <c>cased</c> / <c>desc</c>
/// 三个 string id 全都不在 15 张语言表里（未发布或被砍掉的装备）。
/// <c>GameItemAssetCatalog</c> 会把查不到的 id 渲染成 <c>0x&lt;id&gt;</c>，
/// 那是**字符串 id**，不是名字。如果不挡住，列表里就会出现 <c>0xd8bce5f1</c> 这种"名字"，
/// 看起来像解析成功了，实际什么信息都没有。
/// </para>
/// </summary>
public static class ArchiveDisplayNames
{
	/// <summary>不是名字的显示值：空、字符串 id（<c>0x…</c>）、占位破折号。</summary>
	public static bool IsOpaque(string? value)
		=> string.IsNullOrWhiteSpace(value)
			|| value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
			|| value.Equals("—", StringComparison.Ordinal);

	/// <summary>能不能当显示名用（排除 <c>hash-</c> 这种外部来源的占位前缀）。</summary>
	public static bool IsReadable(string? value)
		=> value is not null
			&& !IsOpaque(value)
			&& !value.StartsWith("hash-", StringComparison.OrdinalIgnoreCase);

	/// <summary>还是不是"包名本身"（即从来没人给它起过名字）。</summary>
	public static bool IsPlaceholder(string? displayName, string packageName)
		=> string.IsNullOrWhiteSpace(displayName)
			|| displayName.Equals(packageName, StringComparison.OrdinalIgnoreCase);
}
