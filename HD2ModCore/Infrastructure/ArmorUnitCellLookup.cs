using HD2ModCore.Domain;
using Microsoft.Data.Sqlite;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 按 unit id 查**游戏件表**里的坐标：<c>(槽位, 原始身形, 层)</c>。
///
/// <para>
/// <b>为什么这是唯一正确的来源。</b>自定义素体命中目标部位靠的就是这个四元组格子对齐
/// （<c>armor_registry_composition.lua</c> 遍历 model.pieces，按 <c>{body,slot,piece_type}</c>
/// 找到目标 kit 的同格 piece 换掉 unit id）。而 legacy 来源的 unit **自己不带**这三个字段
/// ——实测某系列 21 个 unit 的 customization 与 semantic 全是空串。
/// 但从件表反查它们全都有明确坐标：20 个属于原版档案 <c>1e2ba3712fa1b030</c>、1 个属于头盔档案。
/// </para>
///
/// <para>
/// <b>不读游戏文件、只读索引库。</b>真映射会改写 <c>generated_customization_armor_sets.dl_bin</c>，
/// 直接读它会读到被我们自己改过的内容（见 <see cref="ArmorAuthorityIndexReader"/> 的说明）。
/// </para>
/// </summary>
public static class ArmorUnitCellLookup
{
	/// <summary>
	/// 查一批 unit 的坐标。返回的字典以 16 位 hex 为键；查不到的 unit 不放进结果
	/// （调用方用 <see cref="SectionalSourceUnitCell.Unknown"/> 补位）。
	/// </summary>
	public static async ValueTask<IReadOnlyDictionary<string, SectionalSourceUnitCell>> ReadAsync(
		StoragePaths paths,
		IReadOnlyCollection<string> unitIds,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(paths);
		ArgumentNullException.ThrowIfNull(unitIds);

		var result = new Dictionary<string, SectionalSourceUnitCell>(StringComparer.OrdinalIgnoreCase);
		if (unitIds.Count == 0 || !File.Exists(paths.DbPath)) return result;

		var wanted = ParseWantedUnitIds(unitIds);
		if (wanted.Count == 0) return result;

		await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using var command = connection.CreateCommand();
		var names = BindUnitIdParameters(command, wanted.Keys);

		command.CommandText =
			"SELECT unit_file_id, raw_body_type, slot, piece_type, count(*) AS n"
			+ " FROM game_data_authority_pieces"
			+ $" WHERE unit_file_id IN ({string.Join(',', names)})"
			+ " GROUP BY unit_file_id, raw_body_type, slot, piece_type"
			+ " ORDER BY unit_file_id, n DESC";

		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var raw = reader.GetInt64(0);
			if (!wanted.TryGetValue(raw, out var id)) continue;
			var body = reader.GetInt64(1);
			var slot = reader.GetInt64(2);
			var pieceType = reader.GetInt64(3);

			if (result.TryGetValue(id, out var existing))
			{
				// 同一个 unit 出现在多个坐标上：保留第一次（件数最多的那组），并如实标记。
				if (existing.Slot != slot || existing.Body != body || existing.PieceType != pieceType)
					result[id] = existing with { Ambiguous = true };
				continue;
			}

			result[id] = new SectionalSourceUnitCell(
				id,
				(int)Math.Clamp(slot, 0, 9),
				(int)Math.Clamp(body, 0, 3),
				(int)Math.Clamp(pieceType, 0, 2));
		}

		return result;
	}

	/// <summary>
	/// 找出只属于“未被选中的官方护甲/头盔档案”的 unit id（16 位 hex）。
	/// 未在 <c>game_data_authority_pieces</c> 里出现、或同时被任一已选档案引用的 unit 不会进入返回值。
	/// </summary>
	public static async ValueTask<IReadOnlySet<string>> FindForeignEquipmentUnitsAsync(
		StoragePaths paths,
		IReadOnlyCollection<string> unitIds,
		IReadOnlyCollection<string> selectedArchiveIds,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(paths);
		ArgumentNullException.ThrowIfNull(unitIds);
		ArgumentNullException.ThrowIfNull(selectedArchiveIds);

		var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (unitIds.Count == 0 || selectedArchiveIds.Count == 0 || !File.Exists(paths.DbPath)) return result;

		var selected = selectedArchiveIds
			.Where(id => !string.IsNullOrWhiteSpace(id))
			.Select(id => id.Trim())
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (selected.Count == 0) return result;

		var wanted = ParseWantedUnitIds(unitIds);
		if (wanted.Count == 0) return result;

		await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using var command = connection.CreateCommand();
		var names = BindUnitIdParameters(command, wanted.Keys);
		command.CommandText =
			"SELECT DISTINCT unit_file_id, archive_id"
			+ " FROM game_data_authority_pieces"
			+ $" WHERE unit_file_id IN ({string.Join(',', names)});";

		var owners = new Dictionary<string, (bool HasAny, bool HasSelected)>(StringComparer.OrdinalIgnoreCase);
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var raw = reader.GetInt64(0);
			if (!wanted.TryGetValue(raw, out var id)) continue;
			var archiveId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
			owners.TryGetValue(id, out var state);
			var matchesSelected = state.HasSelected || (archiveId.Length > 0 && selected.Contains(archiveId));
			owners[id] = (true, matchesSelected);
		}

		foreach (var (id, state) in owners)
		{
			if (state.HasAny && !state.HasSelected)
				result.Add(id);
		}

		return result;
	}

	private static Dictionary<long, string> ParseWantedUnitIds(IReadOnlyCollection<string> unitIds)
	{
		// unit_file_id 在库里是 INTEGER（有符号 64 位）；我们的 hex 是同一串字节的无符号写法。
		var wanted = new Dictionary<long, string>();
		foreach (var id in unitIds)
		{
			if (string.IsNullOrEmpty(id) || id.Length != 16 || !ulong.TryParse(id, System.Globalization.NumberStyles.HexNumber, null, out var value))
				continue;
			wanted[unchecked((long)value)] = id;
		}
		return wanted;
	}

	private static List<string> BindUnitIdParameters(SqliteCommand command, IEnumerable<long> rawIds)
	{
		var names = new List<string>();
		var index = 0;
		foreach (var rawId in rawIds)
		{
			var parameter = "$u" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
			command.Parameters.AddWithValue(parameter, rawId);
			names.Add(parameter);
			index++;
		}
		return names;
	}
}
