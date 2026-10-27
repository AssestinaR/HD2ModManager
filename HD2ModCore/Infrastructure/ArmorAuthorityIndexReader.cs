using System.Security.Cryptography;
using System.Text;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using Microsoft.Data.Sqlite;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 从索引数据库（<c>asset-index.sqlite</c>）读出"装备权威表"，投影成 <see cref="GameDataAuthoritySnapshot"/>。
///
/// <para>
/// <b>为什么只有索引构建可以读游戏注册表。</b>真映射模式会重写
/// <c>generated_customization_armor_sets.dl_bin</c>；任何直接读它的地方都可能读到被我们改过的内容，
/// 表现是"替换护甲换到了错误的护甲上"，而且不会报错。索引构建把原始结论写进数据库，
/// 之后所有读者（护甲映射页、替换护甲、装备事实、信息中心）一律只查数据库。
/// </para>
///
/// <para>
/// <b>为什么不读备份。</b>备份路径要靠状态文件、游戏目录、buildid 三道判断才能解析出来
/// （见 <see cref="ArmorDataFileIdentity"/>），散落在每个读者身上必然有人忘记；
/// 只有索引构建需要知道这件事。
/// </para>
///
/// <para>
/// <b>读不到就返回 <c>null</c>，绝不退回读文件。</b>"数据库里没有护甲表"是一个必须如实报告的状态
/// （说明索引没建，或建的时候没能读到装备关系表），不是一个可以用别的手段绕过的错误。
/// </para>
/// </summary>
public static class ArmorAuthorityIndexReader
{
	/// <summary>数据库里的护甲/头盔/披风件表；表空时返回 <c>null</c>。</summary>
	public static async ValueTask<GameDataAuthoritySnapshot?> TryReadAsync(
		StoragePaths paths,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(paths);
		if (!File.Exists(paths.DbPath)) return null;

		var kits = new List<GameDataAuthorityKit>();
		var piecesByItem = new Dictionary<uint, List<GameDataAuthorityPiece>>();

		await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using (var command = connection.CreateCommand())
		{
			command.CommandText = """
				SELECT item_id, dlc_id, set_id, kit_kind, archive_id, display_name
				FROM game_data_authority_kits
				ORDER BY item_id;
				""";
			await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				var raw = reader.GetInt64(0);
				if (raw < 0 || raw > uint.MaxValue) continue;
				var itemId = (uint)raw;
				kits.Add(new GameDataAuthorityKit(
					itemId,
					(uint)Math.Clamp(reader.GetInt64(1), 0, uint.MaxValue),
					(uint)Math.Clamp(reader.GetInt64(2), 0, uint.MaxValue),
					reader.GetInt64(3) switch
					{
						0 => GameDataEquipmentKitKind.Armor,
						1 => GameDataEquipmentKitKind.Helmet,
						2 => GameDataEquipmentKitKind.Cape,
						_ => GameDataEquipmentKitKind.Unknown
					},
					reader.GetString(4),
					reader.GetString(5),
					Array.Empty<GameDataAuthorityPiece>()));
			}
		}

		if (kits.Count == 0) return null;

		await using (var command = connection.CreateCommand())
		{
			command.CommandText = """
				SELECT item_id, raw_body_type, body_variant, slot, part_kind, piece_type,
				       weight, tone_variations, unit_type_id, unit_file_id
				FROM game_data_authority_pieces
				ORDER BY item_id, slot, piece_type;
				""";
			await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				var rawItem = reader.GetInt64(0);
				if (rawItem < 0 || rawItem > uint.MaxValue) continue;
				var itemId = (uint)rawItem;
				if (!piecesByItem.TryGetValue(itemId, out var list))
					piecesByItem[itemId] = list = new List<GameDataAuthorityPiece>();
				list.Add(new GameDataAuthorityPiece(
					(uint)Math.Clamp(reader.GetInt64(1), 0, uint.MaxValue),
					(UnitMeshBodyVariant)reader.GetInt64(2),
					(uint)Math.Clamp(reader.GetInt64(3), 0, uint.MaxValue),
					(UnitMeshPartKind)reader.GetInt64(4),
					(uint)Math.Clamp(reader.GetInt64(5), 0, uint.MaxValue),
					(uint)Math.Clamp(reader.GetInt64(6), 0, uint.MaxValue),
					(byte)Math.Clamp(reader.GetInt64(7), 0, byte.MaxValue),
					new AssetKey(unchecked((ulong)reader.GetInt64(8)), unchecked((ulong)reader.GetInt64(9))),
					Array.Empty<GameDataAuthorityResourceReference>()));
			}
		}

		var withPieces = kits
			.Select(kit => piecesByItem.TryGetValue(kit.ItemId, out var pieces)
				? kit with { Pieces = pieces }
				: kit)
			.ToArray();

		return new GameDataAuthoritySnapshot(
			GameDataAuthoritySourceKind.IndexDatabase,
			paths.DbPath,
			ComputeFingerprint(withPieces),
			DateTimeOffset.UtcNow,
			withPieces,
			Array.Empty<CoreIssue>());
	}

	/// <summary>同一份数据库内容必须给出同一个指纹（它只用来判断"事实有没有变"）。</summary>
	private static string ComputeFingerprint(IReadOnlyList<GameDataAuthorityKit> kits)
	{
		var builder = new StringBuilder();
		foreach (var kit in kits.OrderBy(kit => kit.ItemId))
		{
			builder.Append(kit.ItemId).Append(':')
				.Append((uint)kit.Kind).Append(':')
				.Append(kit.ArchiveId).Append(':')
				.Append(kit.Pieces.Count).Append('\n');
		}

		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
	}
}
