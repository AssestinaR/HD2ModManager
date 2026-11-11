using System.Globalization;
using System.Text;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 把管理器的"护甲 A → 外观 B"意图编译成运行时能直接执行的 unit 对列表（<c>wardrobe.links</c>）。
///
/// 算法与旧 Lua 实现的 <c>composition_mode=existing_only</c> 一致：按
/// <c>(body, slot, piece_type)</c> 把来源的 unit 分组，目标逐件消费，<c>body=3</c>（Any）兜底，
/// 一件只消费一次。区别只有一处——**来源缺的槽位用隐藏 unit 填满**，这样目标件永远有
/// 对应物，不会留下"没被替换、照原样显示"的残留件。
///
/// 槽位/身形一律取自同一张权威表（注册文件的件表），两侧编号因此自洽；不混用 unit 自身
/// 的 <c>CustomizationInfo</c> 编号——两套编号在 5/6/7 档并不一致。
/// </summary>
public static class WardrobeLinkCompiler
{
	public const string FileName = "wardrobe.links";
	public const int Schema = 3;

	/// <summary>
	/// 来源缺槽时使用的隐藏 unit：一个真实存在、什么都不画的 unit，随 Lua 模组一起部署。
	/// 不能用"不存在的 id"——那会让引擎记住加载失败，之后军械库整页空白并崩溃。
	/// </summary>
	public const string HiddenUnit = "def7dc54e86d6054";

	private const ulong UnitTypeId = 0xe0a48d0be9a7453f;

	/// <summary>共用行（<see cref="UnitMeshBodyVariant.Any"/>）。它与 Stocky/Slim 互斥。</summary>
	private const uint AnyBody = (uint)UnitMeshBodyVariant.Any;

	private const uint StockyBody = (uint)UnitMeshBodyVariant.Stocky;
	private const uint SlimBody = (uint)UnitMeshBodyVariant.Slim;

	/// <summary>
	/// 编译结果。<see cref="Links"/> 为 0 表示没有任何可执行的映射，调用方不应覆盖运行时文件。
	/// </summary>
	public sealed record Result(string Text, int LinkCount, int HiddenCount, int UnresolvedTargets, int MissingSources);

	/// <summary>来源件表的一项，游戏护甲与自定义素体都归一成这个形状。</summary>
	private sealed record SourceUnit(uint Body, uint Slot, uint PieceType, string UnitHex);

	public static Result Compile(
		GameDataAuthoritySnapshot snapshot,
		IReadOnlyList<RuntimeArmorRegistryMapping> mappings,
		int revision,
		bool enabled,
		IReadOnlyList<RuntimeArmorRegistryModel>? models = null)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(mappings);

		var kits = snapshot.Kits.ToDictionary(kit => kit.ItemId);
		var modelsByKey = (models ?? Array.Empty<RuntimeArmorRegistryModel>())
			.Where(model => !string.IsNullOrWhiteSpace(model.Key))
			.GroupBy(model => model.Key!, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

		// 先把每条映射的来源解析成统一的件表。来源既可以是游戏护甲，也可以是自定义素体
		// （素体的 unit 是全新 id，不在注册表里，所以只能从工作区定义取）。
		var work = new List<(RuntimeArmorRegistryMapping Mapping, GameDataAuthorityKit Target, List<SourceUnit> Source)>();
		var unresolved = 0;
		var missingSources = 0;
		foreach (var mapping in mappings)
		{
			if (!kits.TryGetValue(mapping.TargetItemId, out var targetKit))
			{
				unresolved++;
				continue;
			}
			var source = ResolveSource(mapping, kits, modelsByKey);
			if (source is null || source.Count == 0)
			{
				missingSources++;
				continue;
			}
			work.Add((mapping, targetKit, source));
		}

		var lines = new List<string>(work.Count * 24);
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var hidden = 0;

		foreach (var entry in work)
		{
			var targetKit = entry.Target;
			var index = IndexSource(entry.Source);

			// 第一遍：每个 (身形, 部位) 的最底层是第几层、整片到底有没有对上。
			// 「整片没对上」= 这个部位在目标的所有层上都找不到来源件 —— 素体没这个部位，
			// 或者只有它在原生里被放在 pt1/pt2、而素体按 pt0 声明。
			var lowestLayer = new Dictionary<(uint Body, uint Slot), uint>();
			var coveredParts = new HashSet<(uint Body, uint Slot)>();
			foreach (var piece in targetKit.Pieces)
			{
				if (!IsUnit(piece)) continue;
				var part = ((uint)piece.BodyVariant, piece.Slot);
				lowestLayer[part] = lowestLayer.TryGetValue(part, out var seenLayer)
					? Math.Min(seenLayer, piece.PieceType)
					: piece.PieceType;
				if (ResolveByLayer(index, part.Item1, piece.Slot, piece.PieceType) is not null)
					coveredParts.Add(part);
			}

			foreach (var piece in targetKit.Pieces)
			{
				if (!IsUnit(piece)) continue;
				var body = (uint)piece.BodyVariant;
				var part = (body, piece.Slot);
				var donor = ResolveByLayer(index, body, piece.Slot, piece.PieceType);
				// 整个部位都对不上时，让它的最底层借用来源该部位最底层的那一件 —— 否则整套护甲
				// 会"缺胯/缺腿/缺手"。只在整片对不上时借，不会把同一网格铺到两层。
				if (donor is null && !coveredParts.Contains(part)
					&& lowestLayer.TryGetValue(part, out var low) && piece.PieceType == low)
				{
					donor = ResolvePartFallback(index, body, piece.Slot);
				}
				// 来源缺这个槽位 ⇒ 隐藏。不能让目标件原样显示：那会留下"没被替换"的残留件。
				var donorHex = donor ?? HiddenUnit;
				var own = Hex(piece.UnitAssetKey.FileId);
				// 自链接也要发：键集必须只由"目标护甲有哪些件"决定，与来源无关。否则换源时
				// 键集会抖动（这套外观里恰好同键的件会消失），运行时那边就成了"删除"——
				// 而删除的映射按契约不释放，会在引擎覆盖表里不断累积，最终撞上表容量。
				var line = own + "," + donorHex;
				if (!seen.Add(line)) continue;
				if (donor is null) hidden++;
				lines.Add("link=" + line);
			}
		}

		var builder = new StringBuilder(64 + lines.Count * 40);
		builder.Append("schema=").Append(Schema).Append('\n');
		builder.Append("revision=").Append(revision.ToString(CultureInfo.InvariantCulture)).Append('\n');
		builder.Append("enabled=").Append(enabled ? "true" : "false").Append('\n');
		builder.Append("mode=apply\n");
		foreach (var entry in work)
		{
			// map= 只是管理器的意图记录，运行时读的是 link=。素体映射没有来源物品 id，
			// 所以只记录真实护甲之间的映射。
			if (entry.Mapping.IsModelMapping || entry.Mapping.SourceItemId is not uint sourceId) continue;
			builder.Append("map=")
				.Append(entry.Mapping.TargetItemId.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(sourceId.ToString(CultureInfo.InvariantCulture)).Append('\n');
		}
		foreach (var line in lines) builder.Append(line).Append('\n');

		return new Result(builder.ToString(), lines.Count, hidden, unresolved, missingSources);
	}

	private static List<SourceUnit>? ResolveSource(
		RuntimeArmorRegistryMapping mapping,
		IReadOnlyDictionary<uint, GameDataAuthorityKit> kits,
		IReadOnlyDictionary<string, RuntimeArmorRegistryModel> modelsByKey)
	{
		if (mapping.IsModelMapping)
		{
			if (mapping.ModelKey is null || !modelsByKey.TryGetValue(mapping.ModelKey, out var model)) return null;
			var pieces = model.Pieces?.Where(piece => piece.Enabled).ToArray();
			if (pieces is { Length: > 0 })
			{
				var resolved = new List<SourceUnit>(pieces.Length);
				foreach (var piece in pieces)
				{
					if (!TryParseUnitHex(piece.Unit, out var unit)) continue;
					// **身形编码必须先换算。** 素体件里存的是**注册表原始编码**
					// （Stocky=0 / Slim=1 / Any=3，见 SectionalModelDefinition），
					// 而目标件的键取自权威表的 `BodyVariant`（UnitMeshBodyVariant：
					// Slim=1 / Stocky=2 / Any=3）。两套编号在 Stocky 上错位，
					// 直接比较的后果是 **Stocky 件永远对不上、护甲主体全被隐藏**，
					// 而 Slim(1) 与 Any(3) 只是碰巧相同——于是现象表现为"只有头盔能映射"。
					// （2026-11-11 实测：一条素体映射 24 件里只有 2 件命中，正是此因。）
					resolved.Add(new SourceUnit(
						(uint)ArmorStructure.ToBodyVariant(piece.Body), piece.Slot, piece.PieceType, Hex(unit)));
				}
				if (resolved.Count > 0) return resolved;
			}
			// 素体没有显式件表时，件来自被当作模板的那套真实护甲。
			return model.SourceItemId is uint templateId && kits.TryGetValue(templateId, out var templateKit)
				? FromKit(templateKit)
				: null;
		}
		return mapping.SourceItemId is uint sourceId && kits.TryGetValue(sourceId, out var sourceKit)
			? FromKit(sourceKit)
			: null;
	}

	/// <summary>
	/// 来源件表按 <c>(身形, 槽位, 层)</c> 建索引。同一格出现两件时先来的赢
	/// （协议层已禁止同格重复，这里只是不让坏输入炸掉）。
	/// </summary>
	private static Dictionary<(uint Body, uint Slot, uint PieceType), string> IndexSource(
		IReadOnlyList<SourceUnit> pieces)
	{
		var index = new Dictionary<(uint, uint, uint), string>(pieces.Count);
		foreach (var piece in pieces)
			index.TryAdd((piece.Body, piece.Slot, piece.PieceType), piece.UnitHex);
		return index;
	}

	/// <summary>
	/// 单个格子的取值：按 <c>(目标身形行, 槽位, 层)</c> 找来源件。
	/// <para>
	/// 顺序：① 同身形同层 → ② 共用(Any)同层 → ③ 目标是共用行时接受来源按身形拆出来的件
	/// → ④ 没有就返回 <c>null</c>（这一层该隐藏）。
	/// </para>
	/// <para>
	/// <b>③ 为什么要容忍。</b>原生把腿、披风、臂内层放在共用行（网格不随身形变），
	/// 而素体可能把同一个槽位声明成按身形两份。这时共用格表达不了"两份不同的网格"：
	/// 两份相同就照用，不同就让这一层隐藏 —— 绝不挑一份塞给两种身形（那会让一半身形穿错网格）。
	/// 只声明了一份的，说明这个网格两种身形通用，直接用。
	/// </para>
	/// <para>
	/// 口径与真映射时代 <c>ArmorRegistryDataMapper.ResolveByLayer</c> 完全一致；
	/// 那边是同一套判据在原始身形编码上跑，两边不能分叉。
	/// </para>
	/// </summary>
	private static string? ResolveByLayer(
		Dictionary<(uint Body, uint Slot, uint PieceType), string> index,
		uint body, uint slot, uint pieceType)
	{
		if (index.TryGetValue((body, slot, pieceType), out var unit)) return unit;
		if (body != AnyBody && index.TryGetValue((AnyBody, slot, pieceType), out unit)) return unit;
		if (body != AnyBody) return null;

		var hasStocky = index.TryGetValue((StockyBody, slot, pieceType), out var stocky);
		var hasSlim = index.TryGetValue((SlimBody, slot, pieceType), out var slim);
		if (hasStocky && hasSlim)
			return string.Equals(stocky, slim, StringComparison.OrdinalIgnoreCase) ? stocky : null;
		if (hasStocky) return stocky;
		if (hasSlim) return slim;
		return null;
	}

	/// <summary>
	/// 该 <c>(身形, 部位)</c> 在来源里的最底层那一件。
	/// <para>
	/// <b>为什么需要它。</b><c>PieceType</c> 是"部位内的第几个 unit 槽"，不是稳定的内层/外层编号：
	/// 实测原生 138 套里，胯有 16 套只有 <c>pt1</c>（甚至 <c>pt1+pt2</c>）、左腿 9 套、右腿 11 套、
	/// 右臂 12 套同样只有 <c>pt1</c>。素体的件只按 <c>pt0</c> 声明，严格按层号对齐就会把这些部位
	/// 整片写成隐藏，表现为"这套护甲缺胯/缺腿/缺手"。
	/// </para>
	/// <para>
	/// 所以层号仍是首选对齐键，但**当整个部位一层都对不上时**，让目标该部位的最底层借用来源该部位
	/// 最底层的那一件。只在"整片对不上"时借，避免同一个网格被铺到两层上互相打架。
	/// </para>
	/// </summary>
	private static string? ResolvePartFallback(
		Dictionary<(uint Body, uint Slot, uint PieceType), string> index,
		uint body, uint slot)
	{
		foreach (var layer in index.Keys
			.Where(key => key.Slot == slot)
			.Select(key => key.PieceType)
			.Distinct()
			.OrderBy(layer => layer))
		{
			var unit = ResolveByLayer(index, body, slot, layer);
			if (unit is not null) return unit;
		}
		return null;
	}

	private static List<SourceUnit> FromKit(GameDataAuthorityKit kit)
		=> kit.Pieces
			.Where(IsUnit)
			.Select(piece => new SourceUnit(
				(uint)piece.BodyVariant, piece.Slot, piece.PieceType, Hex(piece.UnitAssetKey.FileId)))
			.ToList();

	private static bool TryParseUnitHex(string? text, out ulong unit)
	{
		unit = 0;
		if (string.IsNullOrWhiteSpace(text)) return false;
		var trimmed = text.Trim();
		if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
		return ulong.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out unit);
	}

	private static bool IsUnit(GameDataAuthorityPiece piece) =>
		piece.UnitAssetKey.TypeId == UnitTypeId && piece.UnitAssetKey.FileId != 0;

	private static string Hex(ulong value) =>
		value.ToString("x16", CultureInfo.InvariantCulture);
}
