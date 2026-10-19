using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 一条"目标护甲 → 视觉来源护甲"的映射。刻意不直接用注册协议的类型：
/// 这一层只关心两件事——谁是目标、谁是来源，协议怎么存是 CLI 的事。
/// </summary>
public sealed record WardrobeMapping(uint TargetItemId, uint SourceItemId);

/// <summary>一个掩码集是被谁要出来的：视觉来源自己的，还是某个目标独立设置的。</summary>
public enum WardrobeSetOrigin
{
	/// <summary>视觉来源自己的禁用集合；映射目标默认继承它。</summary>
	Source,

	/// <summary>某个映射目标独立设置的集合；它不再继承来源。</summary>
	Own,
}

/// <summary>编译产物里的一个掩码集，用于 CLI 摘要与诊断。</summary>
public sealed record WardrobeProjectionSet(
	uint SetId,
	uint OwnerItemId,
	WardrobeSetOrigin Origin,
	int EntryCount,
	IReadOnlyList<uint> InheritorItemIds);

/// <summary>投影结果。</summary>
public sealed record WardrobeMaskProjectionResult(
	bool IsValid,
	WardrobeMaskDocument Document,
	IReadOnlyList<WardrobeMaskDiagnostic> Diagnostics,
	IReadOnlyList<WardrobeProjectionSet> Sets,
	int SkippedEntryCount);

/// <summary>
/// 多份 <c>sections.json</c> 合并后的查询索引。
///
/// 为什么需要合并：一个护甲的多个 unit 可能由不同 mod 提供，而映射表是**随 mod 走**的
/// （§11），所以一次投影可能要同时查好几份表。合并时同一个 unit 在两份表里内容必须
/// 完全一致——不一致说明两份表来自不同的构建，按哪一份都会关错几何。
///
/// 合并结果**不落盘**：它没有单一的 patch 指纹可言，写成文件只会让人误以为可以复用。
/// </summary>
public sealed class WardrobeSectionIndex
{
	private readonly Dictionary<string, WardrobeSectionMapUnit> units;
	private readonly Dictionary<string, Dictionary<int, WardrobeSectionMapEntry>> sections;

	private WardrobeSectionIndex(
		Dictionary<string, WardrobeSectionMapUnit> units,
		Dictionary<string, Dictionary<int, WardrobeSectionMapEntry>> sections,
		IReadOnlyList<string> sourcePaths)
	{
		this.units = units;
		this.sections = sections;
		SourcePaths = sourcePaths;
	}

	/// <summary>参与合并的映射表路径，按读取顺序。</summary>
	public IReadOnlyList<string> SourcePaths { get; }

	public int UnitCount => units.Count;

	public IReadOnlyCollection<string> UnitIds => units.Keys;

	/// <summary>合并后的 unit 表，按 unit ID 去重。</summary>
	public IEnumerable<WardrobeSectionMapUnit> Units => units.Values;

	/// <summary>
	/// 合并多份映射表。同一 unit 出现在多份表里时必须逐 section 一致。
	/// </summary>
	public static WardrobeSectionIndex Create(IReadOnlyList<WardrobeSectionMapDocument> documents)
	{
		ArgumentNullException.ThrowIfNull(documents);
		var units = new Dictionary<string, WardrobeSectionMapUnit>(StringComparer.Ordinal);
		var sections = new Dictionary<string, Dictionary<int, WardrobeSectionMapEntry>>(StringComparer.Ordinal);
		var paths = new List<string>(documents.Count);
		foreach (var document in documents)
		{
			ArgumentNullException.ThrowIfNull(document);
			paths.Add(document.Patch.Path);
			foreach (var unit in document.Units)
			{
				var normalized = WardrobeSectionFingerprint.TryNormalizeUnit(unit.FileId, out var value)
					? value
					: unit.FileId;
				if (units.TryGetValue(normalized, out var existing))
				{
					if (!SameSections(existing, unit))
					{
						throw new InvalidDataException(
							$"unit {normalized} 同时出现在两份 section 映射表里，但内容不同：" +
							$"{existing.Sections.Count} 个 section vs {unit.Sections.Count} 个。" +
							"这说明两份表来自不同的构建，按哪一份都会关错几何；请重新生成。");
					}

					continue;
				}

				units.Add(normalized, unit);
				var byIndex = new Dictionary<int, WardrobeSectionMapEntry>(unit.Sections.Count);
				foreach (var section in unit.Sections)
					byIndex[section.Index] = section;
				sections.Add(normalized, byIndex);
			}
		}

		return new WardrobeSectionIndex(units, sections, paths);
	}

	private static bool SameSections(WardrobeSectionMapUnit left, WardrobeSectionMapUnit right)
	{
		if (left.Sections.Count != right.Sections.Count)
			return false;
		for (var index = 0; index < left.Sections.Count; index++)
		{
			var a = left.Sections[index];
			var b = right.Sections[index];
			if (a.Index != b.Index || !string.Equals(a.Fingerprint, b.Fingerprint, StringComparison.Ordinal))
				return false;
		}
		return true;
	}

	public WardrobeSectionMapUnit? FindUnit(string? fileId)
	{
		if (string.IsNullOrWhiteSpace(fileId))
			return null;
		var normalized = WardrobeSectionFingerprint.TryNormalizeUnit(fileId, out var value)
			? value
			: fileId.Trim().ToLowerInvariant();
		return units.TryGetValue(normalized, out var unit) ? unit : null;
	}

	public WardrobeSectionMapEntry? FindSection(string? fileId, int index)
	{
		if (string.IsNullOrWhiteSpace(fileId) || index < 0)
			return null;
		var normalized = WardrobeSectionFingerprint.TryNormalizeUnit(fileId, out var value)
			? value
			: fileId.Trim().ToLowerInvariant();
		return sections.TryGetValue(normalized, out var byIndex) && byIndex.TryGetValue(index, out var section)
			? section
			: null;
	}
}

/// <summary>
/// 权威工作区 + 注册协议 + 映射表 → <c>wardrobe.masks</c>。
///
/// 这是 §14.3 那张表的实现，也是"编辑即保存、重新应用即可再生成"里的"再生成"。
/// 三种角色落到同一套扁平投影上：
///
/// * 视觉来源 → 自己的掩码集；
/// * 映射目标（未独立编辑）→ **指向来源的同一个 set id**，这就是继承；
/// * 映射目标（独立编辑过）→ 自己的 set id，不再指向来源。
///
/// 两个刻意的设计：
///
/// 1. **解析不到的条目只警告、不报错**。工作区是惰性的、与 mod 无关的，所以它可能引用
///    当前没部署的 mod 的 unit。为此让整次编译失败，等于"换一次 mod 就再也编不出掩码"。
/// 2. **set id 按 itemId 升序分配**。同一个输入必须产出同一份字节，否则"内容没变"
///    会被误判成"变了"，白白触发一次重载。
///
/// This is the "regenerate by re-applying" half of the design. Sources get their own mask
/// set; inheriting targets point at that very set id; independently edited targets get their
/// own. Unresolvable entries warn instead of failing, because the workspace is deliberately
/// mod-independent and may reference units no deployed mod currently provides.
/// </summary>
public static class WardrobeMaskProjection
{
	/// <summary>
	/// 投影。返回的 <c>Document</c> 在 <c>IsValid</c> 为假时也会给出，但调用方不应写盘。
	/// </summary>
	public static WardrobeMaskProjectionResult Project(
		WardrobeWorkspaceDocument workspace,
		IReadOnlyList<WardrobeMapping> mappings,
		WardrobeSectionIndex sections,
		bool enabled = true)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(mappings);
		ArgumentNullException.ThrowIfNull(sections);
		var diagnostics = new List<WardrobeMaskDiagnostic>();

		// 同一件护甲在映射表里出现两次（且来源不同）会让"它到底继承谁"没有答案。
		var sourceByTarget = new Dictionary<uint, uint>();
		foreach (var mapping in mappings)
		{
			if (sourceByTarget.TryAdd(mapping.TargetItemId, mapping.SourceItemId))
				continue;
			var existing = sourceByTarget[mapping.TargetItemId];
			if (existing != mapping.SourceItemId)
			{
				diagnostics.Add(WardrobeMaskDiagnostic.Error(
					"target_conflict",
					$"目标 {mapping.TargetItemId} 同时映射到来源 {existing} 和 {mapping.SourceItemId}；" +
					"同一个目标只能有一个视觉来源。"));
			}
		}

		var sourceIds = sourceByTarget.Values.Distinct().Order().ToArray();
		var ownTargetIds = workspace.Armours
			.Where(armour => armour.Mode == WardrobeMaskMode.Own)
			.Select(armour => armour.ItemId)
			.Order()
			.ToArray();

		// 需要分配 set id 的"所有者"：有禁用内容的来源 + 显式独立设置的目标。
		// 目标即使一条都不关也要分配——空集正是"覆盖掉继承、什么都不关"的表达方式。
		var owners = sourceIds
			.Where(id => workspace.DisabledCount(id) > 0)
			.Concat(ownTargetIds)
			.Distinct()
			.Order()
			.ToArray();

		var setByOwner = new Dictionary<uint, uint>(owners.Length);
		for (var index = 0; index < owners.Length; index++)
			setByOwner.Add(owners[index], (uint)(index + 1));

		var entries = new List<WardrobeMaskEntry>();
		var skipped = 0;
		foreach (var owner in owners)
		{
			var setId = setByOwner[owner];
			var armour = workspace.FindArmour(owner);
			if (armour is null || armour.Mode != WardrobeMaskMode.Own)
				continue;
			foreach (var unit in armour.Units)
			{
				if (!WardrobeSectionFingerprint.TryNormalizeUnit(unit.Unit, out var unitId))
				{
					diagnostics.Add(WardrobeMaskDiagnostic.Error(
						"unit_invalid",
						$"护甲 {owner} 里的 unit '{unit.Unit}' 不是 16 位 hex。"));
					continue;
				}

				var mapUnit = sections.FindUnit(unitId);
				if (mapUnit is null)
				{
					diagnostics.Add(WardrobeMaskDiagnostic.Warning(
						"unit_not_in_any_map",
						$"护甲 {owner} 想禁用 unit {unitId} 的 {unit.Off.Count} 个 section，" +
						"但没有任何 section 映射表认识这个 unit（该 mod 当前可能没有部署）；已跳过。"));
					skipped += unit.Off.Count;
					continue;
				}

				foreach (var sectionIndex in unit.Off)
				{
					var section = sections.FindSection(unitId, sectionIndex);
					if (section is null)
					{
						diagnostics.Add(WardrobeMaskDiagnostic.Warning(
							"section_not_in_unit",
							$"护甲 {owner} 想禁用 {unitId} 的第 {sectionIndex} 个 section，" +
							$"但该 unit 只有 {mapUnit.Sections.Count} 个；已跳过。"));
						skipped++;
						continue;
					}

					entries.Add(new WardrobeMaskEntry(setId, unitId, sectionIndex, section.Fingerprint));
				}
			}
		}

		var targetSets = new Dictionary<uint, uint>();
		foreach (var pair in sourceByTarget.OrderBy(pair => pair.Key))
		{
			// 目标自己独立设置过 → 用自己的集合（这是"覆盖"，不再继承）。
			if (setByOwner.TryGetValue(pair.Key, out var ownSetId))
			{
				targetSets.Add(pair.Key, ownSetId);
				continue;
			}

			// 否则继承来源：**指向来源的同一个 set id**，继承就这么表达出来了。
			if (setByOwner.TryGetValue(pair.Value, out var sourceSetId))
				targetSets.Add(pair.Key, sourceSetId);
			// 来源一条都没关 → 不写 target 行。缺失即"什么都不关"，语义相同且更短。
		}

		var inheritors = targetSets
			.GroupBy(pair => pair.Value)
			.ToDictionary(group => group.Key, group => (IReadOnlyList<uint>)group.Select(pair => pair.Key).Order().ToArray());
		var sets = owners
			.Select(owner => new WardrobeProjectionSet(
				setByOwner[owner],
				owner,
				sourceIds.Contains(owner) ? WardrobeSetOrigin.Source : WardrobeSetOrigin.Own,
				entries.Count(entry => entry.SetId == setByOwner[owner]),
				inheritors.TryGetValue(setByOwner[owner], out var ids) ? ids : Array.Empty<uint>()))
			.ToArray();

		var document = WardrobeMaskFile.Normalize(new WardrobeMaskDocument(
			workspace.Revision,
			enabled,
			entries,
			targetSets));
		return new WardrobeMaskProjectionResult(
			diagnostics.All(diagnostic => !diagnostic.IsError),
			document,
			diagnostics,
			sets,
			skipped);
	}
}
