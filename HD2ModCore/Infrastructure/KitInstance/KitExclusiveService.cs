using System.Globalization;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.Sectional;

namespace HD2ModCore.Infrastructure.KitInstance;

/// <summary>下拉里的一条护甲记录（专属模式按 item_id 列）。</summary>
/// <param name="Status">「可专属」或「不可：<缺口>」。</param>
public sealed record KitExclusiveOption(uint ItemId, string ArchiveId, string Name, bool Available, string Status)
{
	/// <summary>同名记录靠编号区分，如 <c>FS-05“神射手”（3970674111）</c>。</summary>
	public string Label => $"{Name}（{ItemId.ToString(CultureInfo.InvariantCulture)}）";
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
		var options = SectionalExclusivePlanner.Evaluate(index, loaded.Source!, headMerge)
			.Select(candidate => new KitExclusiveOption(
				candidate.ItemId, candidate.ArchiveId, candidate.Name, candidate.Succeeded, candidate.Summary))
			.OrderBy(option => option.Name, StringComparer.CurrentCulture)
			.ThenBy(option => option.ItemId)
			.ToArray();
		return new KitExclusiveCatalog(options, null);
	}

	public static async Task<KitExclusivePreview> PreviewAsync(
		string modDirectory, StoragePaths paths, uint itemId, bool headMerge, CancellationToken cancellationToken = default)
	{
		var loaded = await LoadAsync(modDirectory, paths, cancellationToken).ConfigureAwait(false);
		if (loaded.Error is not null) return new KitExclusivePreview(false, "专属切换", [loaded.Error]);
		var index = SectionalExclusivePlanner.BuildIndex(loaded.Snapshot!.Kits);
		var plan = SectionalExclusivePlanner.Build(index, loaded.Source!, itemId, headMerge);
		return Describe(plan, loaded.Snapshot.Kits);
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

		var result = new List<string> { $"目标：{title}。这个 Mod 只会在穿这一条记录的角色身上显示，其它护甲不受影响。" };

		var body = plan.Placements.Where(placement => placement.HelmetName is null).ToArray();
		result.Add("【落点】每个模型部位写到目标独有的原版部件上：");
		result.AddRange(body.Select(placement =>
			$"· {placement.Cell}（第 {placement.Layer} 层）：{placement.Role} 0x{placement.SourceFileId} → 0x{Hex(placement.TargetFileId)}"));

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

		result.Add("确认后会重写这个 Mod 的 Overwrite 目录（原文件不动），并记下一条隐藏规则；下次部署后生效。");
		return new KitExclusivePreview(true, title, result);
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
