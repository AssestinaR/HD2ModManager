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
	private const uint AnyBody = (uint)UnitMeshBodyVariant.Any;

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

		// 目标在所有被映射护甲上对每个槽位的最大需求，用来决定来源要补多少件。
		var maxNeed = new Dictionary<(uint Body, uint Slot, uint PieceType), int>();
		foreach (var entry in work)
		{
			var counts = new Dictionary<(uint, uint, uint), int>();
			foreach (var piece in entry.Target.Pieces.Where(IsUnit))
			{
				var key = ((uint)piece.BodyVariant, piece.Slot, piece.PieceType);
				counts[key] = counts.GetValueOrDefault(key) + 1;
			}
			foreach (var pair in counts)
				maxNeed[pair.Key] = Math.Max(maxNeed.GetValueOrDefault(pair.Key), pair.Value);
		}

		var lines = new List<string>(work.Count * 8);
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var hidden = 0;

		foreach (var entry in work)
		{
			var targetKit = entry.Target;
			var grouped = new Dictionary<(uint Body, uint Slot, uint PieceType), Queue<string>>();
			foreach (var piece in entry.Source)
			{
				var key = (piece.Body, piece.Slot, piece.PieceType);
				if (!grouped.TryGetValue(key, out var queue)) grouped[key] = queue = new Queue<string>();
				queue.Enqueue(piece.UnitHex);
			}
			foreach (var pair in maxNeed)
			{
				if (!grouped.TryGetValue(pair.Key, out var queue)) grouped[pair.Key] = queue = new Queue<string>();
				while (queue.Count < pair.Value) queue.Enqueue(HiddenUnit);
			}

			foreach (var piece in targetKit.Pieces)
			{
				if (!IsUnit(piece)) continue;
				var body = (uint)piece.BodyVariant;
				var key = (body, piece.Slot, piece.PieceType);
				if (!grouped.TryGetValue(key, out var queue) || queue.Count == 0)
				{
					// 自定义件可能用 body=3（Any）定义，可以填任意身形。
					if (body != AnyBody && grouped.TryGetValue((AnyBody, piece.Slot, piece.PieceType), out var anyQueue))
						queue = anyQueue;
				}
				if (queue is null || queue.Count == 0) continue;
				var donor = queue.Dequeue();
				var own = Hex(piece.UnitAssetKey.FileId);
				// 自链接也要发：键集必须只由"目标护甲有哪些件"决定，与来源无关。否则换源时
				// 键集会抖动（这套外观里恰好同键的件会消失），运行时那边就成了"删除"——
				// 而删除的映射按契约不释放，会在引擎覆盖表里不断累积，最终撞上表容量。
				var line = own + "," + donor;
				if (!seen.Add(line)) continue;
				if (string.Equals(donor, HiddenUnit, StringComparison.Ordinal)) hidden++;
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
					resolved.Add(new SourceUnit(piece.Body, piece.Slot, piece.PieceType, Hex(unit)));
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
