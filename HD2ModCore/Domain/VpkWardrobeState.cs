namespace HD2ModCore.Domain;

/// <summary>
/// VPK 模组的"衣物装饰组件"开关状态：每个 unit 里哪些 section 不画。
/// <para>
/// <b>为什么是独立的一份数据，而不是复用衣柜的护甲掩码。</b>衣柜掩码回答的是
/// "<b>这件护甲</b>隐藏了哪些 section"，主语是护甲；这份回答的是
/// "<b>这个 VPK 模组</b>的哪几块几何不画"，主语是模组。同一个模组可以换到不同护甲上，
/// 但它的组件开关不该跟着护甲走——混进注册表就会在切换模组时互相污染。
/// </para>
/// <para>
/// 纯函数：<see cref="SetEnabled"/> 返回新状态，不碰文件。这样"勾一下到底改了什么"能离线测。
/// </para>
/// </summary>
public sealed record VpkWardrobeState(IReadOnlyDictionary<string, IReadOnlyCollection<int>> Disabled)
{
	public static VpkWardrobeState Empty { get; } = new(new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.Ordinal));

	public bool IsDisabled(string unit, int sectionIndex)
		=> Disabled.TryGetValue(unit, out var indices) && indices.Contains(sectionIndex);

	/// <summary>把某个子树整体设为启用/禁用（点 unit 就是整块，点 section 就是那一块）。</summary>
	public VpkWardrobeState SetEnabled(WardrobeTreeNode node, bool enabled)
	{
		ArgumentNullException.ThrowIfNull(node);
		var targets = node.Kind == WardrobeTreeNodeKind.Section
			? new[] { node }
			: node.Sections().ToArray();
		var byUnit = Disabled.ToDictionary(
			pair => pair.Key,
			pair => pair.Value.ToHashSet(),
			StringComparer.Ordinal);
		foreach (var section in targets)
		{
			if (section.Unit is null || section.SectionIndex is null)
				continue;
			if (!byUnit.TryGetValue(section.Unit, out var indices))
			{
				indices = new HashSet<int>();
				byUnit.Add(section.Unit, indices);
			}

			if (enabled) indices.Remove(section.SectionIndex.Value);
			else indices.Add(section.SectionIndex.Value);
		}

		// 空条目要删掉：文件里"有这个 unit 但一条都不关"和"没有这个 unit"是同一件事。
		var normalized = byUnit
			.Where(pair => pair.Value.Count > 0)
			.ToDictionary(
				pair => pair.Key,
				pair => (IReadOnlyCollection<int>)pair.Value.Order().ToArray(),
				StringComparer.Ordinal);
		return new VpkWardrobeState(normalized);
	}

	/// <summary>
	/// 直接把某个 unit 的一组 section 设成启用/禁用。
	/// <para>
	/// <b>为什么不复用 <see cref="SetEnabled(WardrobeTreeNode, bool)"/>。</b>那条路要一棵树节点，
	/// 而组件开关（<c>components.json</c> 里的一个开关）描述的是"某几个 unit 的第几节"——
	/// 它可能跨多个 unit（一个开关关掉披风 + 披风的挂件），也可能与树上的某个节点没有对应关系。
	/// 让调用方去树上找节点再反过来算下标，会把"开关是什么"和"树怎么画"耦合起来。
	/// </para>
	/// </summary>
	public VpkWardrobeState SetSectionsEnabled(string unit, IReadOnlyList<int> sections, bool enabled)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(unit);
		ArgumentNullException.ThrowIfNull(sections);

		var byUnit = Disabled.ToDictionary(
			pair => pair.Key,
			pair => pair.Value.ToHashSet(),
			StringComparer.Ordinal);
		if (!byUnit.TryGetValue(unit, out var indices))
		{
			if (!enabled)
			{
				indices = new HashSet<int>();
				byUnit.Add(unit, indices);
			}
		}

		if (indices is not null)
		{
			foreach (var section in sections)
			{
				if (enabled) indices.Remove(section);
				else indices.Add(section);
			}
		}

		// 与 SetEnabled 同一条归一规则：空条目等于"没有这个 unit"。
		var normalized = byUnit
			.Where(pair => pair.Value.Count > 0)
			.ToDictionary(
				pair => pair.Key,
				pair => (IReadOnlyCollection<int>)pair.Value.Order().ToArray(),
				StringComparer.Ordinal);
		return new VpkWardrobeState(normalized);
	}
}

/// <summary>
/// VPK 模组的组件树：只画<b>基础 7 槽</b>的 unit。
/// <para>
/// 换甲之后 patch 里会多出目标护甲的另一层（护肩、护胸板、护腿板…）以及一堆隐藏占位。
/// 那些不是"可开关的装饰"，而是"被隐藏掉的东西"——把它们画进树里，用户会以为能打开，
/// 而打开只会露出一个 1 个 section 的空壳。
/// </para>
/// <para>
/// 判据用 unit 自带的层（undergarment）与槽位（helmet），不靠名字猜。
/// </para>
/// </summary>
public static class VpkModWardrobeTree
{
	public static bool IsBaseSlotUnit(WardrobeSectionMapUnit unit)
	{
		ArgumentNullException.ThrowIfNull(unit);
		return string.Equals(unit.PieceType, "Undergarment", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(unit.Slot, "Helmet", StringComparison.OrdinalIgnoreCase);
	}

	public static WardrobeTreeNode Build(string label, WardrobeSectionMapDocument map)
	{
		ArgumentNullException.ThrowIfNull(map);
		return WardrobeTree.Build(label, map.Units.Where(IsBaseSlotUnit).ToArray());
	}
}
