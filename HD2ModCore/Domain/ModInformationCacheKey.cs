namespace HD2ModCore.Domain;

// 作用：L1 内存缓存的完整键 = 事实身份 + 内容修订 + 视图 + 选择器，形成不会误命中旧内容的键。
// Purpose: The L1 memory cache key: fact identity plus revision, view, and selector, so stale content is never hit.
//
// 统一身份说明：键的"身份"部分从 (NodeId, Property) 收敛为 FactKey，与信息中心其它层使用同一套命名。
public readonly record struct ModInformationCacheKey(
	FactKey Key,
	string RevisionKey,
	ModInformationContentView ContentView = ModInformationContentView.Effective,
	string SelectorKey = "")
{
	public static ModInformationCacheKey Create(
		FactKey key,
		ModContentRevision revision,
		ModInformationContentView contentView = ModInformationContentView.Effective,
		ModInformationSelector? selector = null)
	{
		ArgumentNullException.ThrowIfNull(revision);
		return new ModInformationCacheKey(key, revision.CacheKey, contentView, selector?.ToCacheKey() ?? string.Empty);
	}

	// 作用：该键所描述的事实是否属于这个 Mod 节点；供内容提交后按节点清理内存缓存。
	// Purpose: Whether this key describes a fact of the given node, used to drop memory cache entries on content commit.
	public bool BelongsTo(ModNodeId nodeId) => Key.Subject.IsNode(nodeId);

	public override string ToString()
		=> $"{Key}|{ContentView}|{RevisionKey}|{SelectorKey}";
}
