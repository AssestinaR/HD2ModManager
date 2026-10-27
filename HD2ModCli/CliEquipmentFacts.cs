using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

// Read-only access to the game's own equipment authority map
// (generated_customization_armor_sets, libsodium-sealed). This is the same
// facts source the desktop Manager uses to decide which archive is armor,
// helmet, or cape; exposing it lets a script answer questions like "which
// AssetKeys are capes?" without any guesswork from unit structure.

// 件级事实。字段顺序刻意对齐护甲结构的四元组：
//   (itemId → kit, BodyVariant, Slot, PieceType) → Unit
// PieceType 是**该部位的第几个 unit 槽**（0/1/2），不是部位编号。少了它，同一个
// slot 下的两个 unit（例如 slot=4 的内层与护甲）在输出里完全无法区分。
// 结构全貌见 HD2ModCore/Domain/ArmorStructure.cs。
internal sealed record CliEquipmentFactUnit(
	string TypeId,
	string FileId,
	string PartKind,
	string BodyVariant,
	uint Slot,
	uint Weight,
	uint PieceType,
	uint RawBodyType,
	byte Tone);

internal sealed record CliEquipmentFactKit(
	uint ItemId,
	uint DlcId,
	uint SetId,
	string Category,
	string ArchiveId,
	string DisplayName,
	IReadOnlyList<CliEquipmentFactUnit> Units);

internal sealed record CliEquipmentFactResult(
	string SourcePath,
	string SourceFingerprint,
	string PreferredLocale,
	int KitCount,
	IReadOnlyList<CliEquipmentFactKit> Kits);

internal static class CliEquipmentFacts
{
	/// <summary>
	/// 读装备权威表。**数据源是索引数据库，不是游戏注册表文件。**
	/// <para>
	/// 真映射会改写 <c>generated_customization_armor_sets.dl_bin</c>；读文件就会读到被改过的内容，
	/// 替换护甲于是换到错误的护甲上（而且不报错）。索引构建是唯一读原件/备份的地方，
	/// 它把结论写进数据库，这里只查数据库。
	/// </para>
	/// <para>数据库里没有这张表时直接报错——"读不到"必须让用户看见，不允许绕回读文件。</para>
	/// </summary>
	public static async ValueTask<GameDataAuthoritySnapshot> LoadAsync(StoragePaths paths, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(paths);
		try
		{
			return await ArmorAuthorityIndexReader.TryReadAsync(paths, cancellationToken).ConfigureAwait(false)
				?? throw new CliUserException(
					"ArmorIndexUnavailable",
					"索引数据库里没有装备关系表（护甲列表、替换护甲的数据源）。"
					+ "请先在管理器「设置与资产」里建立 / 重建资产索引。"
					+ $"数据库：{paths.DbPath}");
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or FileNotFoundException
			or DirectoryNotFoundException
			or Microsoft.Data.Sqlite.SqliteException)
		{
			throw new CliUserException("ArmorIndexUnreadable", $"无法读取索引数据库里的装备关系表：{exception.Message}");
		}
	}

	public static GameDataEquipmentKitKind? ParseKind(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return null;
		return raw.Trim().ToLowerInvariant() switch
		{
			"armor" => GameDataEquipmentKitKind.Armor,
			"helmet" => GameDataEquipmentKitKind.Helmet,
			"cape" => GameDataEquipmentKitKind.Cape,
			"all" or "*" => null,
			_ => throw new CliUserException(
				"EquipmentKindInvalid",
				"参数 --kind 只接受 armor、helmet、cape 或 all。"),
		};
	}

	public static CliEquipmentFactResult Project(GameDataAuthoritySnapshot snapshot, GameDataEquipmentKitKind? filter, string? archiveFilter)
	{
		var kits = snapshot.Kits.AsEnumerable();
		if (filter is not null) kits = kits.Where(kit => kit.Kind == filter.Value);
		if (!string.IsNullOrWhiteSpace(archiveFilter))
			kits = kits.Where(kit => string.Equals(kit.ArchiveId, archiveFilter.Trim(), StringComparison.OrdinalIgnoreCase));

		var projected = kits
			.Select(kit => new CliEquipmentFactKit(
				kit.ItemId,
				kit.DlcId,
				kit.SetId,
				kit.Category,
				kit.ArchiveId,
				kit.DisplayName,
				kit.Pieces.Select(piece => new CliEquipmentFactUnit(
					$"0x{piece.UnitAssetKey.TypeId:x16}",
					$"0x{piece.UnitAssetKey.FileId:x16}",
					piece.PartKind.ToString(),
					piece.BodyVariant.ToString(),
					piece.Slot,
					piece.Weight,
					piece.PieceType,
					piece.RawBodyType,
					piece.ToneVariations)).ToArray()))
			.ToArray();

		return new CliEquipmentFactResult(
			snapshot.SourcePath ?? string.Empty,
			snapshot.SourceFingerprint,
			snapshot.PreferredLocale,
			projected.Length,
			projected);
	}

	/// <summary>
	/// Flattens a projection to the unique Unit AssetKeys a script would feed
	/// into a hide/replace pipeline. Deduplication is deliberate: several kits
	/// share the same Unit payload.
	/// </summary>
	public static IReadOnlyList<(string TypeId, string FileId)> DistinctUnitKeys(CliEquipmentFactResult result)
		=> result.Kits
			.SelectMany(kit => kit.Units)
			.Select(unit => (unit.TypeId, unit.FileId))
			.Distinct()
			.ToArray();
}
