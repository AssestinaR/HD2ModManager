namespace HD2ModCore.Infrastructure;

/// <summary>
/// 一个组件开关包：<b>名字</b> + 它要藏起来的<b>材质裸名</b>。
/// <para>
/// 这是从 CLI 的规划结果里搬过来的最小信息（`VpkImportPlanner` 的 <c>VpkComponentSwitch</c> 在
/// <c>HD2SourceMigration</c> 里，而管理器不引用它 —— 所以这里只留数据）。
/// </para>
/// </summary>
public sealed record ComponentSwitchPack(
	string VpkPath,
	string DisplayName,
	IReadOnlyList<string> MaterialStems);

/// <summary>
/// 「这个开关包属于哪个模型主体」——按**材质裸名求交**判定。
/// <para>
/// <b>为什么要归属。</b>一次导入可能是 8 人替换包：8 个模型 + 各自的一堆材质 + 一堆开关包。
/// 把整批开关包都拷进每个模型目录，会让每个 Mod 都背上一批"要隐藏的材质在本 patch 里找不到"
/// 的开关（转换报告里会变成 <c>components.json</c> 的 <c>unmatched</c>，界面上是一串点不动的开关）。
/// 归属判据只有一条硬数据：开关包的 VMT 裸名与这个主体的 VMT 裸名有交集 —— 这正是
/// <c>CliSectionGroupResolver</c> 之后用来 join <c>sections.json</c> 的同一把钥匙。
/// </para>
/// </summary>
public static class ComponentSwitchAttribution
{
	/// <summary>
	/// 从一批开关包里挑出属于这个主体的那些（按材质裸名求交，不区分大小写）。
	/// </summary>
	/// <param name="packs">候选开关包。</param>
	/// <param name="targetMaterialStems">这个主体（模型 + 它的材质包）拥有的 VMT 裸名。</param>
	public static IReadOnlyList<ComponentSwitchPack> ForTarget(
		IReadOnlyList<ComponentSwitchPack> packs,
		IEnumerable<string> targetMaterialStems)
	{
		ArgumentNullException.ThrowIfNull(packs);
		ArgumentNullException.ThrowIfNull(targetMaterialStems);

		var owned = new HashSet<string>(
			targetMaterialStems.Select(Normalize).Where(stem => stem.Length != 0),
			StringComparer.OrdinalIgnoreCase);
		if (owned.Count == 0) return [];

		var result = new List<ComponentSwitchPack>();
		foreach (var pack in packs)
		{
			if (pack.MaterialStems.Any(stem => owned.Contains(Normalize(stem))))
				result.Add(pack);
		}

		return result;
	}

	/// <summary>去掉目录与扩展名，只留裸名（`opt/c_white_a_ear.vmt` → `c_white_a_ear`）。</summary>
	private static string Normalize(string name)
	{
		var bare = System.IO.Path.GetFileNameWithoutExtension(name);
		return string.IsNullOrWhiteSpace(bare) ? string.Empty : bare.Trim();
	}
}
