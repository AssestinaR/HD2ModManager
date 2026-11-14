using System.Globalization;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.Sectional;

namespace HD2ModCore.Infrastructure.KitInstance;

/// <summary>下拉里的一条护甲记录（专属模式按 item_id 列）。</summary>
/// <param name="Status">「可专属」、「可专属·合并（实验）：…」或「不可：<缺口>」。</param>
/// <param name="Merged">P4b：逐格承载不了，模型要合并成一个 unit 再落到独有部件上。</param>
/// <param name="NonPlayerLabel">非玩家记录的标注（见 <see cref="KitNonPlayerArmors"/>）；玩家护甲为 <c>null</c>。</param>
/// <param name="Version">同名的玩家记录有两条以上时的编号（按 item_id 升序从 1 起）；否则为 <c>null</c>。</param>
public sealed record KitExclusiveOption(
	uint ItemId, string ArchiveId, string Name, bool Available, string Status, bool Merged = false,
	string? NonPlayerLabel = null, int? Version = null)
{
	/// <summary>同名记录靠编号区分，如 <c>FS-05“神射手”（3970674111）</c>。</summary>
	public string Label => $"{Name}（{ItemId.ToString(CultureInfo.InvariantCulture)}）";

	/// <summary>非玩家记录：手动可勾，但「全选」不选它。</summary>
	public bool IsNonPlayer => NonPlayerLabel is not null;

	/// <summary>下拉里挨在名字后面的标注：「版本 2」或「非玩家（…）」；没有则为 <c>null</c>。</summary>
	public string? Tag => NonPlayerLabel ?? (Version is { } version ? $"版本 {version.ToString(CultureInfo.InvariantCulture)}" : null);
}

/// <summary>专属多选的纯逻辑：搜索过滤与「全选」。放在 Core 里以便单测。</summary>
public static class KitExclusiveSelection
{
	/// <summary>按名字和 item_id 过滤（不区分大小写，空白分词、全部命中才算）。空查询 = 全部命中。</summary>
	public static bool Matches(KitExclusiveOption option, string? query)
	{
		ArgumentNullException.ThrowIfNull(option);
		if (string.IsNullOrWhiteSpace(query)) return true;
		var haystack = option.Name + " " + option.ItemId.ToString(CultureInfo.InvariantCulture) + " " + (option.Tag ?? string.Empty);
		return query.Split([' ', '\t', '　'], StringSplitOptions.RemoveEmptyEntries)
			.All(token => haystack.Contains(token, StringComparison.CurrentCultureIgnoreCase));
	}

	/// <summary>
	/// 「全选」：所有<b>可用</b>的<b>玩家</b>护甲（同名的玩家版本全部勾上）；跳过禁用项和非玩家记录。
	/// </summary>
	public static IReadOnlyList<uint> SelectAll(IEnumerable<KitExclusiveOption> options)
	{
		ArgumentNullException.ThrowIfNull(options);
		return options.Where(option => option.Available && !option.IsNonPlayer).Select(option => option.ItemId).Distinct().ToArray();
	}
}

/// <summary><paramref name="Unavailable"/> 非空 = 连候选都没法算（没有索引、Mod 没有 sections.json 等）。</summary>
public sealed record KitExclusiveCatalog(IReadOnlyList<KitExclusiveOption> Options, string? Unavailable);

/// <summary>确认弹窗的内容。<paramref name="Succeeded"/> 为假时 <paramref name="Lines"/> 是失败原因，不能确认。</summary>
public sealed record KitExclusivePreview(bool Succeeded, string Title, IReadOnlyList<string> Lines);

/// <summary>
/// 专属切换的只读侧：给下拉评估每条记录、给确认弹窗生成预览。数据来自装备权威表索引
/// （<see cref="ArmorAuthorityIndexReader"/>）与 Mod 的 <c>sections.json</c>，不写任何文件。
/// </summary>
public static class KitExclusiveService
{
	public static async Task<KitExclusiveCatalog> LoadCatalogAsync(
		string modDirectory, StoragePaths paths, bool headMerge, CancellationToken cancellationToken = default)
	{
		var loaded = await LoadAsync(modDirectory, paths, cancellationToken).ConfigureAwait(false);
		if (loaded.Error is not null) return new KitExclusiveCatalog([], loaded.Error);
		var index = SectionalExclusivePlanner.BuildIndex(loaded.Snapshot!.Kits);
		// 只列游戏本体有名字的护甲；0x… 的无名记录不是玩家能选的护甲（用户要求，2026-11-14）。
		var options = LabelOptions(SectionalExclusivePlanner.Evaluate(index, loaded.Source!, headMerge));
		return new KitExclusiveCatalog(options, null);
	}

	/// <summary>
	/// 评估结果 → 下拉选项：只留有名字的护甲；同名的玩家记录有两条以上时按 item_id 升序编「版本 1/2/…」，
	/// 非玩家记录（<see cref="KitNonPlayerArmors"/>）不占编号、带「非玩家（…）」标注。
	/// </summary>
	internal static KitExclusiveOption[] LabelOptions(IEnumerable<SectionalExclusiveCandidate> candidates)
	{
		var named = candidates
			.Where(candidate => SectionalExclusivePlanner.IsNamed(candidate.Name))
			.ToArray();
		var versions = named
			.Where(candidate => !KitNonPlayerArmors.Contains(candidate.ItemId))
			.GroupBy(candidate => candidate.Name, StringComparer.Ordinal)
			.Where(group => group.Count() > 1)
			.SelectMany(group => group.OrderBy(candidate => candidate.ItemId).Select((candidate, order) => (candidate.ItemId, Version: order + 1)))
			.ToDictionary(pair => pair.ItemId, pair => pair.Version);
		return named
			.Select(candidate => new KitExclusiveOption(
				candidate.ItemId, candidate.ArchiveId, candidate.Name, candidate.Succeeded, candidate.Summary, candidate.Merged,
				KitNonPlayerArmors.TryGetLabel(candidate.ItemId, out var label) ? label : null,
				versions.TryGetValue(candidate.ItemId, out var version) ? version : null))
			.OrderBy(option => option.Name, StringComparer.CurrentCulture)
			.ThenBy(option => option.ItemId)
			.ToArray();
	}

	/// <summary>单目标预览（旧入口，行为不变）。</summary>
	public static Task<KitExclusivePreview> PreviewAsync(
		string modDirectory, StoragePaths paths, uint itemId, bool headMerge, CancellationToken cancellationToken = default)
		=> PreviewAsync(modDirectory, paths, [itemId], headMerge, null, null, cancellationToken);

	/// <summary>
	/// 多目标预览（P5）：按目标分段列出，最后汇总规则条数、承载件份数、合并模型份数。
	/// 任何目标规划失败、承载件冲突、或规则数超过 Hider 上限，都在这里拦下（此刻还没写任何文件）。
	/// </summary>
	/// <param name="nodeId">给了它才能算规则名长度、并核对别的 Mod 已有的规则。</param>
	/// <param name="modsRootDirectory">Mod 库根（读 workspace 里别的 Mod 的规则）；空 = 只核对这个 Mod 自己的。</param>
	public static async Task<KitExclusivePreview> PreviewAsync(
		string modDirectory, StoragePaths paths, IReadOnlyList<uint> itemIds, bool headMerge,
		ModNodeId? nodeId, string? modsRootDirectory, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(itemIds);
		var loaded = await LoadAsync(modDirectory, paths, cancellationToken).ConfigureAwait(false);
		if (loaded.Error is not null) return new KitExclusivePreview(false, "专属切换", [loaded.Error]);
		var index = SectionalExclusivePlanner.BuildIndex(loaded.Snapshot!.Kits);
		var ids = itemIds.Distinct().Order().ToArray();
		if (ids.Length == 0) return new KitExclusivePreview(false, "专属切换", ["还没有勾选任何目标护甲。"]);
		var plans = ids.Select(id => SectionalExclusivePlanner.BuildWithMerge(index, loaded.Source!, id, headMerge)).ToArray();
		if (ids.Length == 1) return Describe(plans[0], loaded.Snapshot.Kits);

		var failedPlans = plans.Where(plan => !plan.Succeeded).ToArray();
		var title = $"{ids.Length} 套护甲";
		if (failedPlans.Length != 0)
		{
			var lines = new List<string> { $"有 {failedPlans.Length} 个目标不能专属切换：" };
			foreach (var plan in failedPlans)
			{
				lines.Add($"【{plan.Name}（{plan.ItemId.ToString(CultureInfo.InvariantCulture)}）】缺口：");
				lines.AddRange(plan.Gaps.Select(gap => "· " + gap));
			}

			lines.Add("请把它们的勾去掉再切换；专属只写目标记录自己独有的部件，任何一个部位找不到就整体失败，不做半套。");
			return new KitExclusivePreview(false, title, lines);
		}

		var combined = SectionalExclusiveCombiner.Combine(plans);
		if (combined.Conflicts.Count != 0)
		{
			var lines = new List<string> { $"有 {combined.Conflicts.Count} 个目标和别的目标抢同一个承载件，不能一起切换：" };
			lines.AddRange(combined.Conflicts.Select(item => $"· {item.Title}：{item.Reason}"));
			lines.Add("请把它们的勾去掉再切换。");
			return new KitExclusivePreview(false, title, lines);
		}

		var seeds = plans.Select(SeedOf).ToArray();
		var others = KitInstanceWorkspace.Empty;
		if (nodeId is not null && !string.IsNullOrWhiteSpace(modsRootDirectory))
		{
			try { others = await KitInstanceWorkspaceStore.ReadAsync(modsRootDirectory, cancellationToken).ConfigureAwait(false); }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
			{
				return new KitExclusivePreview(false, title, ["无法读取「按实例隐藏」记录：" + exception.Message]);
			}
		}

		var limit = KitExclusiveWorkspace.CheckRuleLimits(nodeId ?? new ModNodeId(Guid.Empty), modDirectory, seeds, others);
		if (limit is not null) return new KitExclusivePreview(false, title, [limit]);

		var result = new List<string>
		{
			$"目标共 {ids.Length} 套：{string.Join("、", plans.Select(plan => plan.Name + "（" + plan.ItemId.ToString(CultureInfo.InvariantCulture) + "）"))}。"
				+ "这个 Mod 只会在穿这些记录的角色身上显示，其它护甲不受影响；确认后按勾选的整个集合重新编译整份 Overwrite。",
		};
		for (var order = 0; order < plans.Length; order++)
		{
			result.Add(string.Empty);
			result.Add($"━━ 目标 {order + 1}/{plans.Length}：{plans[order].Name}（{plans[order].ItemId.ToString(CultureInfo.InvariantCulture)}）━━");
			result.AddRange(DescribeLines(plans[order], loaded.Snapshot.Kits).Skip(1));   // 第一行是单目标口吻的标题行，分段标题已经替代它
		}

		var rules = KitExclusiveWorkspace.BuildRecord(nodeId ?? new ModNodeId(Guid.Empty), modDirectory, seeds)?.ToRules().Count() ?? 0;
		result.Add(string.Empty);
		result.Add("━━ 汇总 ━━");
		result.Add($"· 隐藏规则 {rules} 条（Hider 上限 {KitInstanceRulesProtocol.MaximumRules} 条，已核对）");
		result.Add($"· 承载件 {combined.CarrierCount} 份（写进 Overwrite 的原版部件键，被多个目标共用的只写一次）");
		result.Add($"· 合并模型 {combined.Merges.Count} 份（来源部位相同的合并只做一次）");
		result.Add("确认后会重写这个 Mod 的 Overwrite 目录（原文件不动），并记下这些隐藏规则；下次部署后生效。");
		return new KitExclusivePreview(true, title, result);
	}

	/// <summary>规划 → 规则种子（隐藏规则的内容）。</summary>
	internal static KitExclusiveSeed SeedOf(SectionalExclusivePlan plan)
	{
		static string Hex16(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);
		return new KitExclusiveSeed(
			plan.ItemId,
			plan.Name,
			SectionalExclusivePlanner.Radius,
			plan.Signature.Take(1).Select(Hex16).ToArray(),
			plan.Signature.Skip(1).Select(Hex16).ToArray(),
			plan.Hide.Select(Hex16).ToArray());
	}

	private sealed record Loaded(GameDataAuthoritySnapshot? Snapshot, IReadOnlyList<SectionalSourceUnit>? Source, string? Error);

	private static async Task<Loaded> LoadAsync(string modDirectory, StoragePaths paths, CancellationToken cancellationToken)
	{
		try
		{
			var source = await SectionalCompiler.ReadSourceUnitsAsync(modDirectory, cancellationToken).ConfigureAwait(false);
			if (source is null) return new Loaded(null, null, "这个 Mod 没有 sectional 产物（sections.json），不能专属切换。");
			var snapshot = await ArmorAuthorityIndexReader.TryReadAsync(paths, cancellationToken).ConfigureAwait(false);
			if (snapshot is null) return new Loaded(null, null, "索引库里没有装备权威表：请先在设置页重建游戏资产索引。");
			return new Loaded(snapshot, source, null);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			return new Loaded(null, null, "读取专属切换所需数据失败：" + exception.Message);
		}
	}

	private static string Hex(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);

	/// <summary>确认弹窗的文字：落点、隐藏、被占用独有件的提示、头盔处理（§4.1 第 3 条）。</summary>
	internal static KitExclusivePreview Describe(SectionalExclusivePlan plan, IReadOnlyList<GameDataAuthorityKit> kits)
	{
		var title = $"{plan.Name}（{plan.ItemId.ToString(CultureInfo.InvariantCulture)}）";
		if (!plan.Succeeded)
		{
			var lines = new List<string> { $"不能专属切换到 {title}，缺口：" };
			lines.AddRange(plan.Gaps.Select(gap => "· " + gap));
			lines.Add("专属只写目标记录自己独有的部件；任何一个部位找不到就整体失败，不做半套。");
			return new KitExclusivePreview(false, title, lines);
		}

		var result = DescribeLines(plan, kits);
		result.Add("确认后会重写这个 Mod 的 Overwrite 目录（原文件不动），并记下一条隐藏规则；下次部署后生效。");
		return new KitExclusivePreview(true, title, result);
	}

	/// <summary>一个成功目标的预览正文（第一行是「目标：…」标题行；不含结尾的确认提示）。</summary>
	private static List<string> DescribeLines(SectionalExclusivePlan plan, IReadOnlyList<GameDataAuthorityKit> kits)
	{
		var title = $"{plan.Name}（{plan.ItemId.ToString(CultureInfo.InvariantCulture)}）";
		var result = new List<string>
		{
			plan.IsMerged
				? $"目标：{title}（合并·实验）。这个 Mod 只会在穿这一条记录的角色身上显示，其它护甲不受影响；模型会合并成更少的 unit。"
				: $"目标：{title}。这个 Mod 只会在穿这一条记录的角色身上显示，其它护甲不受影响。",
		};

		var body = plan.Placements.Where(placement => placement.HelmetName is null).ToArray();
		result.Add("【落点】每个模型部位写到目标独有的原版部件上：");
		result.AddRange(body.Select(placement =>
			$"· {placement.Cell}（第 {placement.Layer} 层）：{placement.Role} 0x{placement.SourceFileId} → 0x{Hex(placement.TargetFileId)}"));
		if (plan.IsMerged)
		{
			result.Add("【合并（实验）】这些模型部位在目标上没有各自独有的部件，几何会合并成一个 unit 后写进承载件：");
			foreach (var merge in plan.Merges)
			{
				var bodies = string.Join("、", merge.Bodies.Select(item => item == 0 ? "健壮身形" : "纤细身形"));
				var parts = string.Join("、", merge.SourceFileIds.Select(id => plan.Placements
					.FirstOrDefault(placement => placement.SourceFileId == id && placement.TargetFileId == merge.CarrierFileId)?.Cell ?? id));
				result.Add($"· {parts} → 合并进「{merge.CarrierPart}」承载件 0x{Hex(merge.CarrierFileId)}（第 {merge.Layer} 层，{bodies}）");
			}

			if (plan.Merges.Any(merge => !merge.IsTorso))
				result.Add("⚠ 第一人称可能看到模型：承载件不是躯干，游戏在第一人称下只隐藏躯干和头盔。");
			if (plan.GroupMates.Count != 0)
				result.Add($"同名记录也会显示这个模型：{string.Join("、", plan.GroupMates)}。");
		}

		var helmets = plan.Placements.Where(placement => placement.HelmetName is not null).ToArray();
		if (helmets.Length != 0)
			result.AddRange(helmets.Select(placement =>
				$"· 头盔 {placement.HelmetName}（{placement.Cell}，第 {placement.Layer} 层）：0x{placement.SourceFileId} → 0x{Hex(placement.TargetFileId)}"));

		result.Add($"【被取代的外观】上面 {plan.Placements.Count} 处独有部件原本的外观会被模型取代（只在专属角色身上，别的护甲不受影响）。");

		var bySlot = plan.Hide
			.Select(file => (File: file, Slot: SlotOf(kits, file)))
			.GroupBy(pair => pair.Slot)
			.OrderBy(group => group.Key)
			.Select(group => $"{(group.Key < 0 ? "未定位" : ArmorStructure.Display(ArmorStructure.FromSlot((uint)group.Key)))} {group.Count()} 个");
		result.Add(plan.Hide.Count == 0
			? "【隐藏】目标没有需要隐藏的原版部件，不需要隐藏规则。"
			: $"【隐藏】穿它的角色身上这 {plan.Hide.Count} 个原版部件会按实例隐藏（{string.Join("、", bySlot)}），需要游戏里带上 Hider（随管理器部署）。");
		if (plan.Signature.Count > 1)
			result.Add($"签名：两种身形没有共用的承载件，会生成 {plan.Signature.Count} 条规则（每种身形各一条）。");

		result.Add("【头盔】");
		result.AddRange(plan.HelmetNotes.Count == 0 ? ["· 这个 Mod 没有头部件，头盔保持原版。"] : plan.HelmetNotes.Select(note => "· " + note));
		result.AddRange(plan.Info.Select(info => "· " + info));
		if (plan.Warnings.Count != 0)
		{
			result.Add("【注意】");
			result.AddRange(plan.Warnings.Select(warning => "· " + warning));
		}

		return result;
	}

	private static int SlotOf(IReadOnlyList<GameDataAuthorityKit> kits, ulong file)
	{
		foreach (var kit in kits)
			foreach (var piece in kit.Pieces)
				if (piece.UnitAssetKey.FileId == file)
					return (int)piece.Slot;
		return -1;
	}
}
