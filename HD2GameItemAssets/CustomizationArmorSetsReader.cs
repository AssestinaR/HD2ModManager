using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace HD2GameItemAssets;

/// <summary>Reads Helldivers 2's generated_customization_armor_sets data-library file.</summary>
public static class CustomizationArmorSetsReader
{
	public const uint ArmorCustomizationTypeId = 0xd9a55aa0;
	private const int SubdataHeaderSize = 24;
	// Record sizes are public so fixtures and writers share one source of truth: the
	// 2026-10-20 stride change broke every consumer that had its own copy of 96.
	public const int KitSize = 64;
	public const int BodySize = 24;
	// 2026-10-20 的更新把 piece 记录从 96 字节改成 112 字节，字段偏移不变，多出的 16 字节
	// 追加在尾部。步长仍是 96 时每行的第一件读对、其余按 16 字节递减错位，表现为槽位越界、
	// weight 变成天文数字、unit 落到 0/1/0xFFFF —— 实测 RE-2310(820333399) 与
	// RE-824(4259903063) 两个 kit：23 件 × 112 + 3 行 × 24 + 64 = 2712 字节，正好等于
	// payload 长度；改成 112 后两个 kit 的 44 个 unit 哈希全部解析正确。
	// The 2026-10-20 update widened the piece record from 96 to 112 bytes with unchanged
	// field offsets and 16 new trailing bytes. Keeping 96 read the first piece of each body
	// row correctly and every later one 16 bytes early, which produced out-of-range slots,
	// absurd weights and unit ids of 0/1/0xFFFF.
	public const int PieceSize = 112;
	public static GameItemAssetCatalog Read(
		string path,
		IReadOnlyDictionary<uint, string>? localization = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		return Read(File.ReadAllBytes(path), localization);
	}

	public static GameItemAssetCatalog Read(
		ReadOnlySpan<byte> data,
		IReadOnlyDictionary<uint, string>? localization = null)
	{
		var reader = new Reader(data);
		var declaredCount = reader.ReadUInt32("item count");
		var items = new List<GameItemAssetRecord>(checked((int)Math.Min(declaredCount, 16_384)));

		for (var itemIndex = 0u; itemIndex < declaredCount; itemIndex++)
		{
			var headerOffset = reader.Position;
			if (reader.Remaining < SubdataHeaderSize)
				throw new InvalidDataException($"Item {itemIndex} header is truncated at 0x{headerOffset:x}.");

			var magic = reader.ReadBytes(4, "subdata magic");
			if (!magic.SequenceEqual("LDLD"u8))
				throw new InvalidDataException($"Item {itemIndex} has invalid magic '{Encoding.ASCII.GetString(magic)}' at 0x{headerOffset:x}.");
			reader.Skip(4, "subdata version");
			var type = reader.ReadUInt32("subdata type");
			var payloadSize = reader.ReadUInt32("subdata size");
			reader.Skip(8, "subdata platform fields");

			var payloadOffset = reader.Position;
			var payload = reader.ReadBytes(checked((int)payloadSize), "subdata payload");
			if (type == ArmorCustomizationTypeId)
				items.Add(ReadKit(payload, payloadOffset, itemIndex, localization));
		}

		return new GameItemAssetCatalog(items);
	}

	public static void WriteJson(Stream output, GameItemAssetCatalog catalog, bool indented = true)
	{
		ArgumentNullException.ThrowIfNull(output);
		ArgumentNullException.ThrowIfNull(catalog);
		JsonSerializer.Serialize(output, catalog, new JsonSerializerOptions { WriteIndented = indented });
	}

	private static GameItemAssetRecord ReadKit(
		ReadOnlySpan<byte> payload,
		int payloadOffset,
		uint itemIndex,
		IReadOnlyDictionary<uint, string>? localization)
	{
		if (payload.Length < KitSize)
			throw new InvalidDataException($"Armor kit {itemIndex} at 0x{payloadOffset:x} is smaller than {KitSize} bytes.");

		var id = UInt32At(payload, 0);
		var dlcId = UInt32At(payload, 4);
		var setId = UInt32At(payload, 8);
		var nameUpperId = UInt32At(payload, 12);
		var nameCasedId = UInt32At(payload, 16);
		var descriptionId = UInt32At(payload, 20);
		var rarity = UInt32At(payload, 24);
		var passive = UInt32At(payload, 28);
		var archiveHash = UInt64At(payload, 32);
		var kitType = UInt32At(payload, 40);
		var bodyOffset = Int64At(payload, 48, "body array offset", itemIndex);
		var bodyCount = Int64At(payload, 56, "body count", itemIndex);

		var pieces = new List<GameItemPiece>();
		var bodyOffsetInt = CheckedOffset(bodyOffset, payload.Length, "body array", itemIndex);
		var bodyCountInt = CheckedCount(bodyCount, BodySize, payload.Length - bodyOffsetInt, "body", itemIndex);
		for (var bodyIndex = 0; bodyIndex < bodyCountInt; bodyIndex++)
		{
			var body = payload.Slice(bodyOffsetInt + bodyIndex * BodySize, BodySize);
			var bodyType = UInt32At(body, 0);
			var pieceOffset = Int64At(body, 8, "piece array offset", itemIndex);
			var pieceCount = Int64At(body, 16, "piece count", itemIndex);
			var pieceOffsetInt = CheckedOffset(pieceOffset, payload.Length, "piece array", itemIndex);
			var pieceCountInt = CheckedCount(pieceCount, PieceSize, payload.Length - pieceOffsetInt, "piece", itemIndex);

			for (var pieceIndex = 0; pieceIndex < pieceCountInt; pieceIndex++)
				pieces.Add(ReadPiece(payload.Slice(pieceOffsetInt + pieceIndex * PieceSize, PieceSize), bodyType));
		}

		return new GameItemAssetRecord(
			id, dlcId, setId, nameUpperId, nameCasedId, descriptionId,
			ResolveString(localization, nameCasedId, nameUpperId),
			ResolveString(localization, descriptionId),
			rarity, passive, archiveHash, kitType, pieces);
	}

	private static GameItemPiece ReadPiece(ReadOnlySpan<byte> piece, uint bodyType)
	{
		var references = new List<GameItemAssetReference>(8);
		AddReference(references, "MaterialLut", UInt64At(piece, 24));
		AddReference(references, "PatternLut", UInt64At(piece, 32));
		AddReference(references, "CapeLut", UInt64At(piece, 40));
		AddReference(references, "CapeGradient", UInt64At(piece, 48));
		AddReference(references, "CapeNac", UInt64At(piece, 56));
		AddReference(references, "DecalScalarFields", UInt64At(piece, 64));
		AddReference(references, "BaseData", UInt64At(piece, 72));
		AddReference(references, "DecalSheet", UInt64At(piece, 80));
		return new GameItemPiece(
			StingrayAssetId.Unit(UInt64At(piece, 0)),
			UInt32At(piece, 8), UInt32At(piece, 12), UInt32At(piece, 16), bodyType,
			piece[88], references);
	}

	private static void AddReference(List<GameItemAssetReference> references, string role, ulong hash)
	{
		if (hash != 0) references.Add(new GameItemAssetReference(role, hash));
	}

	private static string ResolveString(IReadOnlyDictionary<uint, string>? localization, params uint[] ids)
	{
		if (localization is not null)
		{
			foreach (var id in ids)
				if (id != 0 && localization.TryGetValue(id, out var value)) return value;
		}
		return ids.FirstOrDefault() == 0 ? string.Empty : $"0x{ids.FirstOrDefault():x8}";
	}

	private static uint UInt32At(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
	private static ulong UInt64At(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);

	private static long Int64At(ReadOnlySpan<byte> data, int offset, string field, uint itemIndex)
	{
		var value = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
		if (value < 0) throw new InvalidDataException($"Armor kit {itemIndex} has a negative {field}.");
		return value;
	}

	private static int CheckedOffset(long offset, int length, string field, uint itemIndex)
	{
		if (offset > length) throw new InvalidDataException($"Armor kit {itemIndex} {field} 0x{offset:x} is outside its payload.");
		return checked((int)offset);
	}

	private static int CheckedCount(long count, int itemSize, int available, string name, uint itemIndex)
	{
		if (count < 0 || count > available / itemSize)
			throw new InvalidDataException($"Armor kit {itemIndex} has invalid {name} count {count}.");
		return checked((int)count);
	}

	private ref struct Reader(ReadOnlySpan<byte> data)
	{
		private ReadOnlySpan<byte> _data = data;
		public int Position { get; private set; }
		public int Remaining => _data.Length - Position;

		public uint ReadUInt32(string field) => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(sizeof(uint), field));
		public void Skip(int count, string field) => _ = ReadBytes(count, field);

		public ReadOnlySpan<byte> ReadBytes(int count, string field)
		{
			if (count < 0 || count > Remaining) throw new InvalidDataException($"Unexpected end of file while reading {field} at 0x{Position:x}.");
			var result = _data.Slice(Position, count);
			Position += count;
			return result;
		}
	}
}
