namespace HD2ModCore.Domain;

/// <summary>
/// Mod 根目录里的 <c>sectional-source.json</c>：**「来源 → sectional」这一步的转换依据**。
///
/// <para>
/// <b>它解决什么问题。</b><c>sectional/</c> 里的产物是可复现的，前提是"来源"和"当初选了哪些 unit"
/// 都还在。名字里说的"来源"包括两件事：
/// </para>
/// <list type="number">
/// <item><b>来源条目</b>：主 Mod 加它的子级（递归）。它们的 patch 被原样平铺在 Mod 根上，
/// 是转换的输入。顺序要留住——部署按"同名后者覆盖"，来源之间的覆盖关系必须可复现。</item>
/// <item><b>用户选的护甲 / 头盔</b>：注意这是**来源里要转成 sectional 的对象**，
/// 不是"转换后指向哪个目标"。目标属于编译期条件，记在 <c>wardrobe.json</c> 里。</item>
/// </list>
///
/// <para>
/// <b>为什么不复用 <c>vpk2patch.json</c>。</b>那份记录的是"某一次 VPK 转换用了哪些 VPK、什么参数"；
/// 这里的来源是 patch 文件而不是 VPK，而且多了一样 VPK 那边不需要的东西：干净资产白名单。
/// 两个东西的字段几乎不重叠，硬塞进一份只会让两边都读不懂。
/// </para>
///
/// <para>
/// <b>它是权威输入，<c>sectional/patches.json</c> 是产物清单。</b>两者不得互相推导：
/// 清单允许缺失或过期（缺失时按目录现推），依据文件缺失就意味着"这个 Mod 不可重构建"。
/// </para>
/// </summary>
public static class SectionalSourceManifest
{
	public const string FileName = "sectional-source.json";

	public const string CurrentSchema = "hd2mod.sectional-source/1";

	/// <summary>能读的 schema；加字段时把新值加进来，旧值继续能读。</summary>
	public static IReadOnlyList<string> ReadableSchemas { get; } = [CurrentSchema];
}

/// <summary>
/// 用户从来源里挑中的一件装备（护甲或头盔）。
/// <para>
/// <b>它不是"目标"。</b>目标护甲属于编译期条件（<c>wardrobe.json</c>）；这里记的是
/// "把来源里的哪一套转成 sectional"。两者同名会让人以为可以互换，所以类型名带 <c>Source</c>。
/// </para>
/// </summary>
/// <param name="ArchiveId">装备归档 id；<c>null</c> 表示这一类没选（"不包含"）。</param>
/// <param name="Name">显示名，只用于给用户看和写日志，不参与任何判定。</param>
public sealed record SectionalSourceSelection(string? ArchiveId = null, string? Name = null)
{
	/// <summary>这一类是否真的选了东西。</summary>
	public bool HasValue => !string.IsNullOrWhiteSpace(ArchiveId);
}

/// <summary>一个来源条目。<see cref="Order"/> 决定平铺拷贝与叠加的先后（小的先）。</summary>
/// <param name="NodeId">来源在库里的节点 id（<c>N</c> 格式的 guid）。可能已不在库里——名字才是权威。</param>
/// <param name="Name">
/// 来源条目名。**材质组的命名靠它**：patch 文件本身没有名字，而库里条目可能已改名或删除，
/// 所以名字必须存进依据文件，不能等到转换时再去查库。
/// </param>
/// <param name="Order">0 起，升序即覆盖顺序。</param>
/// <param name="RelativePath">记录当时的库内相对路径，仅用于诊断与漂移报告。</param>
public sealed record SectionalSourceEntry(
	string NodeId,
	string Name,
	int Order,
	string? RelativePath = null);

/// <summary>
/// 干净资产白名单：转换时只从来源里取这些 key，其余内容一律不进 <c>sectional/</c>。
/// <para>
/// <b>为什么显式存而不是转换时重推。</b>重推依赖游戏资产索引的当前状态；一旦游戏更新导致
/// armor 表变化，重推会静默换掉一套 unit——产物在用户眼里"自己变了"。存下来才能比对并报告漂移。
/// </para>
/// </summary>
public sealed record SectionalSourceAssetKeys(
	IReadOnlyList<string> Units,
	IReadOnlyList<string> Materials,
	IReadOnlyList<string> Textures)
{
	public static SectionalSourceAssetKeys Empty { get; } =
		new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}

/// <summary><c>sectional-source.json</c> 的内容。</summary>
public sealed record SectionalSourceDocument(
	string Schema,
	DateTimeOffset GeneratedUtc,
	SectionalSourceSelection? Armor,
	SectionalSourceSelection? Helmet,
	IReadOnlyList<SectionalSourceEntry> Sources,
	SectionalSourceAssetKeys AssetKeys)
{
	/// <summary>护甲与头盔至少选了一个才有"来源"可转。</summary>
	public bool HasSelection => Armor?.HasValue == true || Helmet?.HasValue == true;
}
