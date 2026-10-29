using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using Microsoft.Data.Sqlite;
using CoreAssetKey = HD2ModCore.Domain.AssetKey;

namespace HD2ModCore.Infrastructure.Sqlite;

/// <summary>
/// 「一个 unit 的 mesh 级部位事实」的**唯一**读取形状：列清单、权威格子的来源、以及行映射。
/// </summary>
/// <remarks>
/// 为什么要有这个类：同一张 <c>game_data_unit_parts</c> 以前有两个读法，一个读
/// <c>authority_slot/authority_piece_type</c>（目录），一个不读（<c>GetUnitPartFactsAsync</c>），
/// 于是同一份数据在两处给出不同的 <see cref="ArmorCell"/>——后者恒为"未定位"。
///
/// 格子现在**一律在读取时从注册表 JOIN 出来**，部位表里不再存副本：
/// 副本会过期（游戏更新、真映射改写注册表），而 JOIN 永远读的是当下那一份。
///
/// 列的顺序是契约：<see cref="Columns"/> 必须与 <see cref="ReadFact"/> 里的偏移一一对应。
/// 两个调用方都可以在自己的 SELECT 前面追加列，把追加的列数当 <c>offset</c> 传进来。
/// </remarks>
internal static class UnitPartFactQuery
{
	/// <summary>共享列清单（顺序即契约，见类注释）。</summary>
	public const string Columns = @"p.unit_type_id,p.unit_file_id,p.mesh_info_index,p.mesh_id,p.part_kind,p.part_layer,p.body_variant,p.semantic_name,p.piece_type,p.confidence,p.is_visual,p.is_lod,p.vertex_count,p.triangle_count,p.geometry_quality,p.fact_origin,p.reason,c.slot,c.piece_type";

	/// <summary>共享列数；调用方追加列时用它算偏移。</summary>
	public const int ColumnCount = 19;

	/// <summary>
	/// 共享 FROM：部位表 + 每个 unit 的权威格子。
	/// </summary>
	/// <remarks>
	/// 一个 unit 全局只属于一个格子（实测 2006 个零冲突），所以这里按 unit 聚合成一行；
	/// 万一注册表自相矛盾（同一 unit 出现两个槽位或两个层），两边都返回 -1，
	/// 让 <see cref="ArmorCell.FromRegistryCoordinates"/> 判成"未定位"——宁可说不知道，
	/// 也不要把两个格子折成一个。
	/// </remarks>
	public const string From = @"
FROM game_data_unit_parts p
LEFT JOIN (
	SELECT unit_type_id,unit_file_id,
	       CASE WHEN COUNT(DISTINCT slot)=1 AND COUNT(DISTINCT piece_type)=1 THEN MIN(slot) ELSE -1 END AS slot,
	       CASE WHEN COUNT(DISTINCT slot)=1 AND COUNT(DISTINCT piece_type)=1 THEN MIN(piece_type) ELSE -1 END AS piece_type
	FROM game_data_authority_pieces
	WHERE kit_kind IN (0,1)
	GROUP BY unit_type_id,unit_file_id
) c ON c.unit_type_id=p.unit_type_id AND c.unit_file_id=p.unit_file_id";

	/// <summary>把一行（从 <paramref name="offset"/> 开始按 <see cref="Columns"/> 排列）读成事实。</summary>
	public static GameDataUnitPartFact ReadFact(SqliteDataReader reader, int offset, string archiveId)
	{
		ArgumentNullException.ThrowIfNull(reader);
		return new GameDataUnitPartFact(
			archiveId,
			new CoreAssetKey(unchecked((ulong)reader.GetInt64(offset + 0)), unchecked((ulong)reader.GetInt64(offset + 1))),
			reader.GetInt32(offset + 2),
			unchecked((uint)reader.GetInt64(offset + 3)),
			(UnitMeshPartKind)reader.GetInt32(offset + 4),
			(UnitMeshPartLayer)reader.GetInt32(offset + 5),
			(UnitMeshBodyVariant)reader.GetInt32(offset + 6),
			reader.GetString(offset + 7),
			reader.GetInt32(offset + 9),
			reader.GetInt32(offset + 10) != 0,
			reader.GetInt32(offset + 11) != 0,
			reader.GetString(offset + 16))
		{
			PieceType = reader.GetString(offset + 8),
			VertexCount = reader.GetInt32(offset + 12),
			TriangleCount = reader.GetInt32(offset + 13),
			GeometryQuality = (UnitMeshGeometryQuality)reader.GetInt32(offset + 14),
			Origin = (GameDataFactOrigin)reader.GetInt32(offset + 15),
			Cell = ArmorCell.FromRegistryCoordinates(reader.GetInt32(offset + 17), reader.GetInt32(offset + 18)),
		};
	}
}
