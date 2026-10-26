using HD2ModAdaptation.Analysis;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 一个 archive 的内容画像：它属于哪个内容域、具体是什么东西、以及据此推出的显示名。
/// <para>
/// 这不是权威事实——权威事实只有注册表（护甲/头盔/披风）和实体百科（装备/敌人/NPC）。
/// 这里补的是**两者都覆盖不到的那 2600 多个 archive**：环境、目标点、设施、音效、共享美术。
/// 所以 <see cref="Confidence"/> 只有 20，且任何权威/社区结论都会覆盖它。
/// </para>
/// </summary>
public sealed record ArchiveContentProfile(
	string Domain,
	string Subject,
	string DerivedName,
	int Confidence,
	bool FromContentPath)
{
	/// <summary>
	/// 这个名字能不能当显示名用。
	/// 只有从内容路径推出来的（<c>env_super_earth/habs</c>）才算名字；
	/// 按构成兜底的（<c>unit×9</c>、<c>贴图包</c>）不唯一，拿去当名字只会让列表更难读，
	/// 所以只记分类、不改显示名。
	/// </summary>
	public bool HasUsableName => FromContentPath;
}

/// <summary>
/// 从 archive 的内容反推它是什么。
/// <para>
/// 唯一的信号是 <c>friendlynames.txt</c>：它把资源哈希映射到内容路径
/// （例如 <c>content/env_super_earth/habs/...</c>）。实测 4036 条里能覆盖
/// 2607 个无名 archive 中的 1460 个（56%）。
/// </para>
/// </summary>
public static class ArchiveContentProfiler
{
	/// <summary>内容域置信度。永远低于权威（100）与社区（60）。</summary>
	public const int DerivedConfidence = 20;

	// 路径前缀 → 内容域。按实测的条目量排序，长的前缀先匹配。
	private static readonly (string Prefix, string Domain)[] Domains =
	[
		("env_super_earth", "Environment"),
		("env_bugs", "Environment"),
		("env_cyborg", "Environment"),
		("env_illuminate", "Environment"),
		("env_shared", "Environment"),
		("env_ship", "Environment"),
		("env_space", "Environment"),
		("env_", "Environment"),
		("objectives", "Objective"),
		("fac_", "Facility"),
		("planet_", "Planet"),
		("level_generation_settings", "LevelSettings"),
		("art_shared", "SharedArt"),
		("audio", "Audio"),
		("effects", "Effect"),
		("markers", "Marker"),
		("levels", "Level"),
	];

	/// <summary>
	/// 给一个 archive 算画像。<c>null</c> 表示没有任何可用信号，调用方应保持原样。
	/// </summary>
	/// <param name="entries">该 archive 的全部条目。</param>
	/// <param name="friendlyNames">资源 fileId → 名字或内容路径。</param>
	public static ArchiveContentProfile? Profile(
		IReadOnlyList<GameDataArchiveEntryFact> entries,
		IReadOnlyDictionary<ulong, string> friendlyNames)
	{
		ArgumentNullException.ThrowIfNull(entries);
		ArgumentNullException.ThrowIfNull(friendlyNames);

		if (entries.Count == 0)
		{
			return new ArchiveContentProfile("Empty", "empty", "空 archive", DerivedConfidence, FromContentPath: false);
		}

		// 内容路径投票：同一个 archive 里出现最多的 (域键, 二级目录) 就是它的主题。
		var votes = new Dictionary<(string Key, string Subject), int>();
		foreach (var entry in entries)
		{
			if (!friendlyNames.TryGetValue(entry.AssetKey.FileId, out var name))
			{
				continue;
			}

			if (!TrySplitContentPath(name, out var key, out var subject))
			{
				continue;
			}

			var bucket = (key, subject);
			votes[bucket] = votes.TryGetValue(bucket, out var count) ? count + 1 : 1;
		}

		if (votes.Count != 0)
		{
			var best = votes.OrderByDescending(pair => pair.Value)
				.ThenBy(pair => pair.Key.Key, StringComparer.Ordinal)
				.ThenBy(pair => pair.Key.Subject, StringComparer.Ordinal)
				.First().Key;
			var domain = ResolveDomain(best.Key);
			var derived = string.IsNullOrEmpty(best.Subject) ? best.Key : $"{best.Key}/{best.Subject}";
			return new ArchiveContentProfile(domain, best.Key, derived, DerivedConfidence, FromContentPath: true);
		}

		// 没有路径信号时，至少按内容构成给一个粗分类——比一律 Other 有用。
		var unitCount = entries.Count(entry => entry.AssetKey.TypeId == UnitTypeId);
		var hasMaterial = entries.Any(entry => entry.AssetKey.TypeId == MaterialTypeId);
		var hasTexture = entries.Any(entry => entry.AssetKey.TypeId == TextureTypeId);
		if (unitCount != 0)
		{
			// 有 unit 但名字全是截断的（friendlynames.txt 里确实存在 `ortifications/banner_02`
			// 这种丢了首字母的条目），那就别硬猜它是什么，只如实说"这是个 unit 包"。
			return new ArchiveContentProfile("UnitBundle", $"{unitCount} unit", $"unit×{unitCount}", DerivedConfidence, FromContentPath: false);
		}

		if (hasMaterial && hasTexture)
		{
			return new ArchiveContentProfile("MaterialPack", "material+texture", "材质贴图包", DerivedConfidence, FromContentPath: false);
		}

		if (hasTexture)
		{
			return new ArchiveContentProfile("TexturePack", "texture", "贴图包", DerivedConfidence, FromContentPath: false);
		}

		if (hasMaterial)
		{
			return new ArchiveContentProfile("MaterialPack", "material", "材质包", DerivedConfidence, FromContentPath: false);
		}

		return new ArchiveContentProfile("Data", "data", "数据包", DerivedConfidence, FromContentPath: false);
	}

	/// <summary>
	/// 把 <c>content/&lt;key&gt;/&lt;subject&gt;/...</c> 拆成 (key, subject)。
	/// 不是 content 路径的（例如 <c>GL-21 Grenade Launcher</c>）返回 false——那是**资源名**，
	/// 不是内容分类，用它分类会把武器名当成域。
	/// </summary>
	private static bool TrySplitContentPath(string name, out string key, out string subject)
	{
		key = string.Empty;
		subject = string.Empty;
		var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length < 2 || !segments[0].Equals("content", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		key = segments[1];
		subject = segments.Length >= 3 ? segments[2] : string.Empty;
		return true;
	}

	private static string ResolveDomain(string key)
	{
		foreach (var (prefix, domain) in Domains)
		{
			if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				return domain;
			}
		}

		return "Content";
	}

	private static readonly ulong UnitTypeId = HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId;
	private const ulong MaterialTypeId = 0xeac0b497876adedf;
	private const ulong TextureTypeId = 0xcd4238c6a0c69e32;
}
