using System.Buffers.Binary;
using System.Text;
using FileDecryptor;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 真映射模式的数据改写器。这些用例不碰游戏目录：合成一份结构相同的 LDLD 容器，
/// 验证"改件集 + 把内容校验和伪造回原值"这条链，以及幂等与撤销。
/// </summary>
[Collection(ArmorRegistryWriteGateCollection.Name)]
public sealed class ArmorRegistryDataMapperTests : IDisposable
{
	const uint ArmorType = 0xd9a55aa0;
	const int PieceSize = 112;
	const int BodySize = 24;

	readonly string _root = Path.Combine(Path.GetTempPath(), "hd2-armor-data-" + Guid.NewGuid().ToString("N"));

	/// <summary>
	/// 这些用例验证的是**编译算法**（件表改写、校验和伪造、对齐、幂等与撤销），
	/// 必须走真实写盘路径才能证明。而生产环境已停写真映射（游戏会闪退），
	/// 所以这里显式放开——只影响本进程内存里的标志，不碰任何游戏文件。
	/// 停写本身的行为由 <see cref="ArmorRegistryWriteDisabledTests"/> 覆盖。
	/// </summary>
	public ArmorRegistryDataMapperTests() => ArmorMappingModes.PushAllowWriteForTests();

	public void Dispose()
	{
		ArmorMappingModes.PopAllowWriteForTests();
		try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
	}

	// 112 字节件记录：unit / slot / pieceType / weight 有用，+24 放一个 LUT 标记、+88 放 tone。
	//
	// ⚠️ +89..111 **不是**一整片自由区：
	//   +89..95   全 0
	//   +96..103  一个 int64 —— 指向 payload 内第二张表的偏移，-1 = 没有表（实测 6 套有表）
	//   +104      条目数（0 或 4）
	//   +105..111 全 0
	// 所以能借的自由字节只有 +89..95 与 +105..111 各 7 字节，都不够一个 64 位未知量。
	static byte[] Piece(ulong unit, uint slot, uint pieceType, uint weight = 1, ulong lut = 0, byte tone = 0)
	{
		var record = new byte[PieceSize];
		BinaryPrimitives.WriteUInt64LittleEndian(record, unit);
		BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), slot);
		BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), pieceType);
		BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), weight);
		BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(24), lut);
		record[88] = tone;
		for (var i = 0; i < 8; i++) record[96 + i] = 0xFF;   // 没有第二张表
		return record;
	}

	static byte[] Kit(uint itemId, uint bodyType, params byte[][] pieces)
		=> MultiKit(itemId, (bodyType, pieces));

	/// <summary>
	/// 多身形行的套件。真实文件里护甲套件恒有 3 行：<c>Any(3) / Stocky(0) / Slim(1)</c>，
	/// 而且 Any 与两个身形行在同一 (slot, pieceType) 上**互斥**。单行夹具测不出这些规则。
	/// </summary>
	static byte[] MultiKit(uint itemId, params (uint Body, byte[][] Pieces)[] rows)
	{
		const int headerSize = 64;
		var totalPieces = rows.Sum(row => row.Pieces.Length);
		var payload = new byte[headerSize + rows.Length * BodySize + totalPieces * PieceSize];
		BinaryPrimitives.WriteUInt32LittleEndian(payload, itemId);
		BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(48), headerSize);
		BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(56), rows.Length);
		var cursor = headerSize + rows.Length * BodySize;
		for (var r = 0; r < rows.Length; r++)
		{
			var rowAt = headerSize + r * BodySize;
			BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(rowAt), rows[r].Body);
			BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(rowAt + 8), cursor);
			BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(rowAt + 16), rows[r].Pieces.Length);
			foreach (var piece in rows[r].Pieces)
			{
				Array.Copy(piece, 0, payload, cursor, PieceSize);
				cursor += PieceSize;
			}
		}
		return payload;
	}

	static byte[] Library(params byte[][] payloads)
	{
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter(stream);
		writer.Write((uint)payloads.Length);
		foreach (var payload in payloads)
		{
			writer.Write("LDLD"u8.ToArray());
			writer.Write(1u);
			writer.Write(ArmorType);
			writer.Write((uint)payload.Length);
			writer.Write(1UL);          // platform 字段：低字节 1，高 7 字节是伪造自由区
			writer.Write(payload);
		}
		writer.Flush();
		return stream.ToArray();
	}

	/// <summary>读出某套件的全部件（**跨所有身形行**，按文件顺序）。</summary>
	(ulong Unit, uint Slot, uint PieceType)[] ReadPieces(byte[] plain, uint itemId)
	{
		var position = 4;
		while (position + 24 <= plain.Length)
		{
			if (!new ReadOnlySpan<byte>(plain, position, 4).SequenceEqual("LDLD"u8)) break;
			var size = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(position + 12));
			var payloadStart = position + 24;
			var payload = plain.AsSpan(payloadStart, (int)size);
			if (BinaryPrimitives.ReadUInt32LittleEndian(payload) == itemId)
			{
				var bodyOffset = (int)BinaryPrimitives.ReadInt64LittleEndian(payload[48..]);
				var bodyCount = (int)BinaryPrimitives.ReadInt64LittleEndian(payload[56..]);
				var result = new List<(ulong, uint, uint)>();
				for (var b = 0; b < bodyCount; b++)
				{
					var pieceOffset = BinaryPrimitives.ReadInt64LittleEndian(payload[(bodyOffset + b * BodySize + 8)..]);
					var pieceCount = BinaryPrimitives.ReadInt64LittleEndian(payload[(bodyOffset + b * BodySize + 16)..]);
					for (var p = 0; p < pieceCount; p++)
					{
						var at = payloadStart + (int)pieceOffset + p * PieceSize;
						result.Add((
							BinaryPrimitives.ReadUInt64LittleEndian(plain.AsSpan(at)),
							BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(at + 8)),
							BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(at + 12))));
					}
				}
				return result.ToArray();
			}
			position = payloadStart + (int)size;
		}
		return [];
	}

	byte[] Decrypt(byte[] sealedBytes)
	{
#pragma warning disable CS0618
		return Decryptor.CreateWithEmbeddedKeys().DecodeData(sealedBytes);
#pragma warning restore CS0618
	}

	byte[] Seal(byte[] plain)
	{
#pragma warning disable CS0618
		return Decryptor.CreateWithEmbeddedKeys().EncodeData(plain);
#pragma warning restore CS0618
	}

	/// <summary>搭一个带两份护甲的最小游戏数据目录。</summary>
	async Task<string> CreateGameDataAsync(uint targetWeight = 1, uint sourceWeight = 1)
	{
		var gameData = Path.Combine(_root, "data");
		Directory.CreateDirectory(Path.Combine(gameData, "game"));
		var library = Library(
			Kit(100, 3,
				Piece(0xAAAAAAAAAAAAAAAA, 3, 0, targetWeight),
				Piece(0xBBBBBBBBBBBBBBBB, 2, 0, targetWeight)),
			Kit(200, 3,
				Piece(0xCCCCCCCCCCCCCCCC, 3, 0, sourceWeight),
				Piece(0xDDDDDDDDDDDDDDDD, 2, 0, sourceWeight),
				Piece(0xEEEEEEEEEEEEEEEE, 2, 1, sourceWeight)));
		await File.WriteAllBytesAsync(
			Path.Combine(gameData, "game", ArmorRegistryDataMapper.FileName),
			Seal(library));
		return gameData;
	}

	[Fact]
	public async Task Apply_TakesOnlyUnitSlotAndTypeFromSource()
	{
		// 字段来源规则：只有 unit / slot / piece_type 取自来源。目标的 weight（护甲等级）、
		// 资源哈希（配色 LUT）、tone 一律保留，否则映射会顺手改掉玩法数值和配色。
		var gameData = Path.Combine(_root, "data");
		Directory.CreateDirectory(Path.Combine(gameData, "game"));
		const ulong targetLut = 0xAAAAAAAAAAAAAAAA;
		const ulong sourceLut = 0xBBBBBBBBBBBBBBBB;
		await File.WriteAllBytesAsync(
			Path.Combine(gameData, "game", ArmorRegistryDataMapper.FileName),
			Seal(Library(
				Kit(100, 3, Piece(0x1111111111111111, 2, 0, weight: 2, lut: targetLut, tone: 7)),
				Kit(200, 3, Piece(0x2222222222222222, 2, 0, weight: 0, lut: sourceLut, tone: 3)))));

		var backup = Path.Combine(_root, "backup");
		await new ArmorRegistryDataMapper().ApplyAsync(
			gameData, [new ArmorRegistryDataMapping(100, 200)], backup);

		var plain = Decrypt(await File.ReadAllBytesAsync(ArmorRegistryDataMapper.ResolveTargetPath(gameData)));
		var pieces = ReadPieces(plain, 100);
		var single = Assert.Single(pieces);
		Assert.Equal(0x2222222222222222UL, single.Unit);   // unit 来自来源
		var record = ReadRecord(plain, 100);
		Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(16)));       // weight 留目标
		Assert.Equal(targetLut, BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(24))); // LUT 留目标
		Assert.Equal(7, record[88]);                                                        // tone 留目标
	}

	[Fact]
	public async Task Apply_SupportsCustomModelAsSource()
	{
		// 自定义素体没有 kit：件表由调用方直接给出，来源 unit 是全新 id。
		var gameData = await CreateGameDataAsync(targetWeight: 2, sourceWeight: 0);
		var backup = Path.Combine(_root, "backup");
		var modelPieces = new[]
		{
			new ArmorRegistryDataSourcePiece(3, 3, 0, 0xC0FFEE0000000001),
			new ArmorRegistryDataSourcePiece(3, 2, 0, 0xC0FFEE0000000002),
		};
		var result = await new ArmorRegistryDataMapper().ApplyAsync(
			gameData, [new ArmorRegistryDataMapping(100, null, modelPieces)], backup);

		Assert.True(result.Written);
		var plain = Decrypt(await File.ReadAllBytesAsync(ArmorRegistryDataMapper.ResolveTargetPath(gameData)));
		var pieces = ReadPieces(plain, 100);
		Assert.Equal(0xC0FFEE0000000001UL, pieces.Single(p => p.Slot == 3).Unit);
		Assert.Equal(0xC0FFEE0000000002UL, pieces.Single(p => p.Slot == 2).Unit);
		// 素体只提供 unit/slot/type，weight 仍然是目标自己的 2。
		Assert.Equal(2, ReadWeights(plain, 100).Length);
		Assert.All(ReadWeights(plain, 100), weight => Assert.Equal(2u, weight));
	}

	[Fact]
	public async Task Apply_CustomModelNeverLeaksItsHelmetUnitIntoABodyKit()
	{
		// 自定义素体是一个整体（7 部位 + 1 头盔），而护甲与头盔是两个独立装备。
		// 头盔件（slot 0）绝不能写进护甲套件——那会表现成"装身体也冒出头盔"。
		var gameData = await CreateGameDataAsync(targetWeight: 2, sourceWeight: 0);
		var backup = Path.Combine(_root, "backup");
		var modelPieces = new[]
		{
			new ArmorRegistryDataSourcePiece(3, 0, 0, 0xC0FFEE0000000010), // 头盔
			new ArmorRegistryDataSourcePiece(3, 2, 0, 0xC0FFEE0000000011), // 身体
		};
		await new ArmorRegistryDataMapper().ApplyAsync(
			gameData, [new ArmorRegistryDataMapping(100, null, modelPieces)], backup);

		var plain = Decrypt(await File.ReadAllBytesAsync(ArmorRegistryDataMapper.ResolveTargetPath(gameData)));
		var pieces = ReadPieces(plain, 100);
		Assert.DoesNotContain(pieces, p => p.Unit == 0xC0FFEE0000000010);
		Assert.Contains(pieces, p => p.Unit == 0xC0FFEE0000000011);
	}

	[Fact]
	public async Task Apply_HidesCellsTheSourceDoesNotCoverAndNeverWritesACapeUnit()
	{
		// 覆盖语义（用户口径 2026-11-08）：目标套件里每一格都要有结论。
		// 来源没覆盖到的格子写 0（隐藏），否则会露出原版件，变成"新外观 + 原版件"的缝合怪。
		// 披风也一样：来源给了 slot 1 的件也不采用——披风是独立装备，映射不谈它。
		var gameData = await CreateGameDataAsync(targetWeight: 2, sourceWeight: 0);
		var backup = Path.Combine(_root, "backup");
		var modelPieces = new[]
		{
			new ArmorRegistryDataSourcePiece(3, 1, 0, 0xC0FFEE0000000020), // 披风：应被忽略
			new ArmorRegistryDataSourcePiece(3, 2, 0, 0xC0FFEE0000000021), // 身体：应生效
		};
		await new ArmorRegistryDataMapper().ApplyAsync(
			gameData, [new ArmorRegistryDataMapping(100, null, modelPieces)], backup);

		var plain = Decrypt(await File.ReadAllBytesAsync(ArmorRegistryDataMapper.ResolveTargetPath(gameData)));
		var pieces = ReadPieces(plain, 100);
		// 件数一件不增不减。
		Assert.Equal(2, pieces.Length);
		Assert.DoesNotContain(pieces, p => p.Unit == 0xC0FFEE0000000020);
		// 来源没覆盖 slot 3 → 写 0 隐藏。
		Assert.Equal(0UL, pieces.Single(p => p.Slot == 3).Unit);
		Assert.Equal(0xC0FFEE0000000021UL, pieces.Single(p => p.Slot == 2).Unit);
	}

	byte[] ReadRecord(byte[] plain, uint itemId)
	{
		var position = 4;
		while (position + 24 <= plain.Length)
		{
			if (!new ReadOnlySpan<byte>(plain, position, 4).SequenceEqual("LDLD"u8)) break;
			var size = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(position + 12));
			var payloadStart = position + 24;
			var payload = plain.AsSpan(payloadStart, (int)size);
			if (BinaryPrimitives.ReadUInt32LittleEndian(payload) == itemId)
			{
				var bodyOffset = BinaryPrimitives.ReadInt64LittleEndian(payload[48..]);
				var pieceOffset = BinaryPrimitives.ReadInt64LittleEndian(payload[((int)bodyOffset + 8)..]);
				var record = new byte[PieceSize];
				Array.Copy(plain, payloadStart + (int)pieceOffset, record, 0, PieceSize);
				return record;
			}
			position = payloadStart + (int)size;
		}
		return [];
	}

	uint[] ReadWeights(byte[] plain, uint itemId)
	{
		var position = 4;
		while (position + 24 <= plain.Length)
		{
			if (!new ReadOnlySpan<byte>(plain, position, 4).SequenceEqual("LDLD"u8)) break;
			var size = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(position + 12));
			var payloadStart = position + 24;
			var payload = plain.AsSpan(payloadStart, (int)size);
			if (BinaryPrimitives.ReadUInt32LittleEndian(payload) == itemId)
			{
				var bodyOffset = BinaryPrimitives.ReadInt64LittleEndian(payload[48..]);
				var pieceOffset = BinaryPrimitives.ReadInt64LittleEndian(payload[((int)bodyOffset + 8)..]);
				var pieceCount = BinaryPrimitives.ReadInt64LittleEndian(payload[((int)bodyOffset + 16)..]);
				var result = new List<uint>();
				for (var p = 0; p < pieceCount; p++)
					result.Add(BinaryPrimitives.ReadUInt32LittleEndian(
						plain.AsSpan(payloadStart + (int)pieceOffset + p * PieceSize + 16)));
				return result.ToArray();
			}
			position = payloadStart + (int)size;
		}
		return [];
	}

	[Fact]
	public async Task Apply_KeepsTargetArmorRatingWeight()
	{
		// 件记录 +16 的 weight 是护甲等级（0/1/2 = 轻/中/重 = 50/100/150 护甲值），
		// 属于玩法字段。映射只换 unit，**这条字段一个字节都不该被写**。
		var gameData = await CreateGameDataAsync(targetWeight: 2, sourceWeight: 0);
		var backup = Path.Combine(_root, "backup");
		await new ArmorRegistryDataMapper().ApplyAsync(
			gameData, [new ArmorRegistryDataMapping(100, 200)], backup);

		var plain = Decrypt(await File.ReadAllBytesAsync(ArmorRegistryDataMapper.ResolveTargetPath(gameData)));
		// 目标仍然只有自己原来那 2 件，每件 weight 都是 2。
		Assert.Equal(2, ReadWeights(plain, 100).Length);
		Assert.All(ReadWeights(plain, 100), weight => Assert.Equal(2u, weight));
		// 来源自己不受影响。
		Assert.All(ReadWeights(plain, 200), weight => Assert.Equal(0u, weight));
	}

	[Fact]
	public async Task Apply_ChangesOnlyTheUnitFieldAndKeepsContentHash()
	{
		var gameData = await CreateGameDataAsync();
		var target = ArmorRegistryDataMapper.ResolveTargetPath(gameData);
		var originalPlain = Decrypt(await File.ReadAllBytesAsync(target));
		var originalHash = ArmorRegistryDataMapper.HashContent(originalPlain);

		var backup = Path.Combine(_root, "backup");
		var result = await new ArmorRegistryDataMapper().ApplyAsync(
			gameData, [new ArmorRegistryDataMapping(100, 200)], backup);

		Assert.True(result.Written);
		Assert.Equal(1, result.Applied);

		var appliedPlain = Decrypt(await File.ReadAllBytesAsync(target));
		// 内容变了，但校验和必须还是原值——这正是游戏用来拒绝改动的那一项。
		Assert.Equal(originalHash, ArmorRegistryDataMapper.HashContent(appliedPlain));
		Assert.NotEqual(Convert.ToHexString(originalPlain), Convert.ToHexString(appliedPlain));

		// ① 件的结构与数量完全不变：来源多出来的 pieceType=1 不会被注入。
		var targetPieces = ReadPieces(appliedPlain, 100);
		Assert.Equal(
			ReadPieces(originalPlain, 100).Select(p => (p.Slot, p.PieceType)),
			targetPieces.Select(p => (p.Slot, p.PieceType)));
		Assert.DoesNotContain(targetPieces, p => p.PieceType == 1);

		// ② 只有 unit 变了，而且取的是来源同一格子的 unit。
		var sourcePieces = ReadPieces(appliedPlain, 200).ToDictionary(p => (p.Slot, p.PieceType), p => p.Unit);
		Assert.All(targetPieces, p => Assert.Equal(sourcePieces[(p.Slot, p.PieceType)], p.Unit));
	}

	/// <summary>
	/// **结构（件数、格子、顺序、weight、资源哈希、tone、+96 第二张表）一个字节都不许动。**
	/// <para>
	/// 这是整条实现的全部承诺。以前的重建写法在这条上全错：它把整条 112 字节从别的件抄过来，
	/// 于是 weight（护甲值）、材质 LUT、tone、第二张表偏移全部跟着搬。
	/// </para>
	/// </summary>
	[Fact]
	public async Task Apply_LeavesEveryNonUnitByteAlone()
	{
		var gameData = await CreateGameDataAsync();
		var target = ArmorRegistryDataMapper.ResolveTargetPath(gameData);
		var originalPlain = Decrypt(await File.ReadAllBytesAsync(target));
		var backup = Path.Combine(_root, "backup");

		await new ArmorRegistryDataMapper().ApplyAsync(
			gameData, [new ArmorRegistryDataMapping(100, 200)], backup, CancellationToken.None);
		var appliedPlain = Decrypt(await File.ReadAllBytesAsync(target));

		// 每一条件记录：除前 8 字节外逐字节相同（用绝对值偏移比，结构变了就比不出来）。
		var before = ReadPieceRecords(originalPlain);
		var after = ReadPieceRecords(appliedPlain);
		Assert.Equal(before.Count, after.Count);
		foreach (var (at, record) in before)
		{
			Assert.True(after.TryGetValue(at, out var updated), $"件记录的绝对偏移 {at} 变了，说明结构被动过。");
			Assert.Equal(
				Convert.ToHexString(record.AsSpan(8)),
				Convert.ToHexString(updated.AsSpan(8)));
		}
	}

	/// <summary>
	/// 伪造用的自由字必须落在**件记录之外**。
	/// <para>
	/// 件记录里没有连续 8 个自由字节：<c>+89..95</c> 与 <c>+105..111</c> 各 7 字节，
	/// 中间 <c>+96..104</c> 是第二张表的偏移与条目数。旧实现按 <c>+89</c> 对齐求解，
	/// 落点是 <c>件+92..99</c>，每次应用都写脏那个偏移的低 4 字节。
	/// </para>
	/// </summary>
	[Fact]
	public async Task Compile_PutsTheForgeWordOutsideEveryPieceRecord()
	{
		var gameData = await CreateGameDataAsync();
		var target = ArmorRegistryDataMapper.ResolveTargetPath(gameData);
		var originalPlain = Decrypt(await File.ReadAllBytesAsync(target));
		await new ArmorRegistryDataMapper().ApplyAsync(
			gameData, [new ArmorRegistryDataMapping(100, 200)], Path.Combine(_root, "backup"), CancellationToken.None);
		var appliedPlain = Decrypt(await File.ReadAllBytesAsync(target));

		// 件记录里除前 8 字节（unit）之外一个字节都没动 —— 含 +89..111 那一片。
		var before = ReadPieceRecords(originalPlain);
		var after = ReadPieceRecords(appliedPlain);
		Assert.Equal(before.Count, after.Count);
		Assert.Equal(
			string.Join("|", before.OrderBy(pair => pair.Key).Select(pair => pair.Key + ":" + Convert.ToHexString(pair.Value.AsSpan(8)))),
			string.Join("|", after.OrderBy(pair => pair.Key).Select(pair => pair.Key + ":" + Convert.ToHexString(pair.Value.AsSpan(8)))));
		// 至少有一件的 unit 真的换了，否则上面的比较没有意义。
		Assert.Contains(before.Keys, at => !before[at].AsSpan(0, 8).SequenceEqual(after[at].AsSpan(0, 8)));
		// 文件长了几字节（对齐 + 8 字节自由字），且长度是 8 的倍数。
		Assert.True(appliedPlain.Length > originalPlain.Length);
		Assert.Equal(0, appliedPlain.Length % 8);
		// 挪出来的那一段都在最后一条 subdata 块的 payload 里，块头 size 也同步跟上了。
		var lastEnd = LastSubdataEnd(appliedPlain);
		Assert.Equal(appliedPlain.Length, lastEnd);
	}

	Dictionary<int, byte[]> ReadPieceRecords(byte[] plain)
	{
		var result = new Dictionary<int, byte[]>();
		var position = 4;
		while (position + 24 <= plain.Length && plain.AsSpan(position, 4).SequenceEqual("LDLD"u8))
		{
			var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(position + 12));
			var payloadStart = position + 24;
			var payload = plain.AsSpan(payloadStart, size);
			var bodyOffset = (int)BinaryPrimitives.ReadInt64LittleEndian(payload[48..]);
			var bodyCount = (int)BinaryPrimitives.ReadInt64LittleEndian(payload[56..]);
			for (var b = 0; b < bodyCount; b++)
			{
				var pieceOffset = (int)BinaryPrimitives.ReadInt64LittleEndian(payload[(bodyOffset + b * BodySize + 8)..]);
				var pieceCount = (int)BinaryPrimitives.ReadInt64LittleEndian(payload[(bodyOffset + b * BodySize + 16)..]);
				for (var p = 0; p < pieceCount; p++)
				{
					var at = payloadStart + pieceOffset + p * PieceSize;
					result[at] = plain.AsSpan(at, PieceSize).ToArray();
				}
			}
			position = payloadStart + size;
		}
		return result;
	}

	static int LastSubdataEnd(byte[] plain)
	{
		var position = 4;
		var end = position;
		while (position + 24 <= plain.Length && plain.AsSpan(position, 4).SequenceEqual("LDLD"u8))
		{
			var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(position + 12));
			end = position + 24 + size;
			position = end;
		}
		return end;
	}


	[Fact]
	public async Task Apply_WithNoMappings_RestoresOriginalBytes()
	{
		var gameData = await CreateGameDataAsync();
		var target = ArmorRegistryDataMapper.ResolveTargetPath(gameData);
		var originalSealed = await File.ReadAllBytesAsync(target);
		var backup = Path.Combine(_root, "backup");
		var mapper = new ArmorRegistryDataMapper();

		await mapper.ApplyAsync(gameData, [new ArmorRegistryDataMapping(100, 200)], backup);
		Assert.NotEqual(
			Convert.ToHexString(originalSealed),
			Convert.ToHexString(await File.ReadAllBytesAsync(target)));

		var revert = await mapper.ApplyAsync(gameData, [], backup);
		Assert.True(revert.Written);
		Assert.Equal(
			Convert.ToHexString(originalSealed),
			Convert.ToHexString(await File.ReadAllBytesAsync(target)));
	}



	[Fact]
	public async Task Apply_AlwaysCompilesFromTheOriginalCopyNotFromTheLiveFile()
	{
		// 受管 dl_bin 是派生事实：它随时可能被外部替换，**绝不能**被当成编译基底。
		// 基底永远是原件副本（第一次应用时记下的那份）。
		var gameData = await CreateGameDataAsync();
		var target = ArmorRegistryDataMapper.ResolveTargetPath(gameData);
		var backup = Path.Combine(_root, "backup");
		var mapper = new ArmorRegistryDataMapper();

		await mapper.ApplyAsync(gameData, [new ArmorRegistryDataMapping(100, 200)], backup);
		var original = await File.ReadAllBytesAsync(Path.Combine(backup, ArmorRegistryDataMapper.BackupFileName));

		// 外部把文件换成一份完全不同的库（例如别的工具/别的安装）。
		var external = Seal(Library(
			Kit(100, 3, Piece(0x1111111111111111, 1, 0)),
			Kit(200, 3, Piece(0x2222222222222222, 1, 0))));
		await File.WriteAllBytesAsync(target, external);

		var result = await mapper.ApplyAsync(gameData, [new ArmorRegistryDataMapping(100, 200)], backup);

		Assert.True(result.Written);
		// 原件副本一个字节都不动。
		Assert.Equal(
			Convert.ToHexString(original),
			Convert.ToHexString(await File.ReadAllBytesAsync(Path.Combine(backup, ArmorRegistryDataMapper.BackupFileName))));
		// 产物是按原件 + 映射编译出来的：100 的 (3,0) 拿到了 200 的 unit。
		var plain = Decrypt(await File.ReadAllBytesAsync(target));
		Assert.Equal(
			ReadPieces(plain, 200).Single(p => p.Slot == 3).Unit,
			ReadPieces(plain, 100).Single(p => p.Slot == 3).Unit);
	}

	[Fact]
	public async Task Apply_HandlesMultipleMappingsInOnePass()
	{
		// 一次应用多条映射。两条映射都从**基底**取来源（不是从已经改过的缓冲区取），
		// 所以「100←200」+「200←100」是一次干净的互换，而不是"改完之后自己抄自己"。
		var gameData = await CreateGameDataAsync();
		var backup = Path.Combine(_root, "backup");
		var original = Decrypt(await File.ReadAllBytesAsync(
			ArmorRegistryDataMapper.ResolveTargetPath(gameData)));
		var result = await new ArmorRegistryDataMapper().ApplyAsync(
			gameData,
			[new ArmorRegistryDataMapping(100, 200), new ArmorRegistryDataMapping(200, 100)],
			backup);

		Assert.True(result.Written);
		Assert.Equal(2, result.Applied);
		var plain = Decrypt(await File.ReadAllBytesAsync(ArmorRegistryDataMapper.ResolveTargetPath(gameData)));

		// 100 的两格换成 200 原来那两格；200 的 (3,0)/(2,0) 换成 100 原来的，
		// (2,1) 在 100 里没有对应格子 —— 来源没覆盖，写 0 隐藏。
		Assert.Equal(0xCCCCCCCCCCCCCCCCUL, ReadPieces(plain, 100).Single(p => p.Slot == 3).Unit);
		Assert.Equal(0xDDDDDDDDDDDDDDDDUL, ReadPieces(plain, 100).Single(p => p.Slot == 2).Unit);
		Assert.Equal(0xAAAAAAAAAAAAAAAAUL, ReadPieces(plain, 200).Single(p => p.Slot == 3).Unit);
		Assert.Equal(0xBBBBBBBBBBBBBBBBUL, ReadPieces(plain, 200).Single(p => p.Slot == 2 && p.PieceType == 0).Unit);
		Assert.Equal(0UL, ReadPieces(plain, 200).Single(p => p.PieceType == 1).Unit);
		// 原件里两个套件的格子数一件不多一件不少。
		Assert.Equal(ReadPieces(original, 100).Length, ReadPieces(plain, 100).Length);
		Assert.Equal(ReadPieces(original, 200).Length, ReadPieces(plain, 200).Length);
	}

	/// <summary>
	/// **有些部位在原生里只存在于层 1**（2026-11-08 上机回归：胯 16 套、左腿 9 套、右腿 11 套、
	/// 右臂 12 套都只有 <c>pt1</c>，甚至有 <c>pt1+pt2</c> 而没有 <c>pt0</c>）。
	/// <para>
	/// 素体的件只按层 0 声明，严格按层号对齐会把整个部位写成 0 —— 表现为"这套护甲缺胯/缺腿/缺手"。
	/// 所以整个部位一层都对不上时，让它最底层借用来源该部位最底层的那一件。
	/// </para>
	/// </summary>
	[Fact]
	public async Task Apply_BorrowsThePartsOnlyLayerWhenTheTargetHasNoLayerZero()
	{
		var gameData = Path.Combine(_root, "data");
		Directory.CreateDirectory(Path.Combine(gameData, "game"));
		await File.WriteAllBytesAsync(
			Path.Combine(gameData, "game", ArmorRegistryDataMapper.FileName),
			Seal(Library(MultiKit(100, (0,
				[
					Piece(0x1111111111111111, 2, 0, weight: 2),   // 躯干：有层 0
					Piece(0x2222222222222222, 3, 1, weight: 2),   // 胯：**只有层 1**
				])))));

		var source = new[]
		{
			new ArmorRegistryDataSourcePiece(0, 2, 0, 0xC0FFEE0000000002),
			new ArmorRegistryDataSourcePiece(0, 3, 0, 0xC0FFEE0000000003),
		};
		var result = await new ArmorRegistryDataMapper().ApplyAsync(
			gameData, [new ArmorRegistryDataMapping(100, null, source)], Path.Combine(_root, "backup"));

		var pieces = ReadPieces(
			Decrypt(await File.ReadAllBytesAsync(ArmorRegistryDataMapper.ResolveTargetPath(gameData))), 100);
		Assert.Equal(0xC0FFEE0000000002UL, pieces.Single(p => p.Slot == 2).Unit);   // 层号直接对上
		Assert.Equal(0xC0FFEE0000000003UL, pieces.Single(p => p.Slot == 3).Unit);   // 借来源最底层
		Assert.Equal(0, result.Hidden);
		Assert.Equal(2, result.Replaced);
	}

	/// <summary>
	/// 反过来：部位**有一层对上了**就不再兜底，否则同一个网格会被铺到两层上互相打架。
	/// </summary>
	[Fact]
	public async Task Apply_DoesNotRepeatOneMeshAcrossTwoLayersOfTheSamePart()
	{
		var gameData = Path.Combine(_root, "data");
		Directory.CreateDirectory(Path.Combine(gameData, "game"));
		await File.WriteAllBytesAsync(
			Path.Combine(gameData, "game", ArmorRegistryDataMapper.FileName),
			Seal(Library(MultiKit(100, (0,
				[
					Piece(0x1111111111111111, 2, 0, weight: 2),   // 躯干内层
					Piece(0x2222222222222222, 2, 1, weight: 2),   // 躯干外层
				])))));

		var source = new[] { new ArmorRegistryDataSourcePiece(0, 2, 0, 0xC0FFEE0000000002) };
		await new ArmorRegistryDataMapper().ApplyAsync(
			gameData, [new ArmorRegistryDataMapping(100, null, source)], Path.Combine(_root, "backup"));

		var pieces = ReadPieces(
			Decrypt(await File.ReadAllBytesAsync(ArmorRegistryDataMapper.ResolveTargetPath(gameData))), 100);
		Assert.Equal(0xC0FFEE0000000002UL, pieces.Single(p => p.PieceType == 0).Unit);
		Assert.Equal(0UL, pieces.Single(p => p.PieceType == 1).Unit);   // 外层隐藏，而不是重复同一网格
	}

	/// <summary>
	/// **素体没有头盔件时，头盔必须被隐藏**，而不是继续显示原版头盔。
	/// <para>
	/// 这是覆盖语义最典型的一条：头盔与护甲是两套装备（不同 item id），素体不给 slot 0 的件，
	/// 那一格就只能写 0。写 0 是游戏自己的写法 —— 123 套披风套件里有 118 套的披风格就是 0。
	/// </para>
	/// </summary>
	[Fact]
	public async Task Apply_HidesAnUncoveredHelmetInsteadOfShowingTheOriginalOne()
	{
		var gameData = Path.Combine(_root, "data");
		Directory.CreateDirectory(Path.Combine(gameData, "game"));
		await File.WriteAllBytesAsync(
			Path.Combine(gameData, "game", ArmorRegistryDataMapper.FileName),
			Seal(Library(MultiKit(100, (3, [Piece(0x1111111111111111, 0, 0, weight: 1)])))));

		var bodyOnly = new[] { new ArmorRegistryDataSourcePiece(3, 2, 0, 0xC0FFEE0000000002) };
		var result = await new ArmorRegistryDataMapper().ApplyAsync(
			gameData, [new ArmorRegistryDataMapping(100, null, bodyOnly)], Path.Combine(_root, "backup"));

		Assert.True(result.Written);
		Assert.Equal(0UL, Assert.Single(ReadPieces(
			Decrypt(await File.ReadAllBytesAsync(ArmorRegistryDataMapper.ResolveTargetPath(gameData))), 100)).Unit);
		Assert.Equal(1, result.Hidden);
		Assert.Equal(0, result.Replaced);
	}

	/// <summary>
	/// 身形行没有对应件时回退到 <c>Any</c> 行；反过来共用行取不到时不会挑一份塞给两种身形。
	/// <para>
	/// 原生把腿、披风、臂内层放在共用行（网格不随身形变），所以"来源只声明了 Any"是常态。
	/// </para>
	/// </summary>
	[Fact]
	public async Task Apply_FallsBackToTheAnyRowWhenTheTargetRowHasNoSuchPiece()
	{
		var gameData = Path.Combine(_root, "data");
		Directory.CreateDirectory(Path.Combine(gameData, "game"));
		await File.WriteAllBytesAsync(
			Path.Combine(gameData, "game", ArmorRegistryDataMapper.FileName),
			Seal(Library(MultiKit(100,
				(3, [Piece(0x1111111111111111, 1, 0)]),
				(0, [Piece(0x2222222222222222, 2, 0)])))));

		// 来源只给了 Any 行的躯干件。
		var source = new[] { new ArmorRegistryDataSourcePiece(3, 2, 0, 0xC0FFEE0000000002) };
		await new ArmorRegistryDataMapper().ApplyAsync(
			gameData, [new ArmorRegistryDataMapping(100, null, source)], Path.Combine(_root, "backup"));

		var pieces = ReadPieces(
			Decrypt(await File.ReadAllBytesAsync(ArmorRegistryDataMapper.ResolveTargetPath(gameData))), 100);
		Assert.Equal(0xC0FFEE0000000002UL, pieces.Single(p => p.Slot == 2).Unit);   // 回退到 Any
		Assert.Equal(0UL, pieces.Single(p => p.Slot == 1).Unit);                    // 披风格：没覆盖 → 隐藏
	}

	/// <summary>
	/// 目标那一格是**共用行**（Any）时，来源按身形拆成的两份怎么处理。
	/// <para>
	/// 原生把腿、披风、臂内层放在共用行，所以"来源只声明了按身形两份"是会撞上的。
	/// 共用格表达不了两份不同的网格 —— 那就隐藏；绝不挑一份塞给两种身形（那会让一半身形穿错网格）。
	/// </para>
	/// </summary>
	[Fact]
	public async Task Apply_HidesASharedCellWhenTheSourceSplitsItPerShapeDifferently()
	{
		Assert.Equal(0UL, await CompileSharedCellAsync(0xCCCCCCCCCCCCCCCCUL, 0xDDDDDDDDDDDDDDDDUL));
	}

	[Fact]
	public async Task Apply_UsesASharedCellWhenBothShapesPointAtTheSameUnit()
	{
		Assert.Equal(0xCCCCCCCCCCCCCCCCUL, await CompileSharedCellAsync(0xCCCCCCCCCCCCCCCCUL, 0xCCCCCCCCCCCCCCCCUL));
	}

	[Fact]
	public async Task Apply_UsesTheOnlyShapeUnitWhenTheSourceDeclaresJustOne()
	{
		Assert.Equal(0xCCCCCCCCCCCCCCCCUL, await CompileSharedCellAsync(0xCCCCCCCCCCCCCCCCUL, null));
	}

	/// <summary>目标是一个只有共用行的套件；来源对 (4,0) 只给 <paramref name="stocky"/> 和/或 <paramref name="slim"/>。</summary>
	async Task<ulong> CompileSharedCellAsync(ulong stocky, ulong? slim)
	{
		var gameData = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(gameData, "game"));
		await File.WriteAllBytesAsync(
			Path.Combine(gameData, "game", ArmorRegistryDataMapper.FileName),
			Seal(Library(MultiKit(100, (3, [Piece(0x1111111111111111, 4, 0)])))));

		var source = new List<ArmorRegistryDataSourcePiece> { new(0, 4, 0, stocky) };
		if (slim is ulong slimUnit) source.Add(new ArmorRegistryDataSourcePiece(1, 4, 0, slimUnit));
		await new ArmorRegistryDataMapper().ApplyAsync(
			gameData,
			[new ArmorRegistryDataMapping(100, null, source)],
			Path.Combine(_root, "backup-" + Guid.NewGuid().ToString("N")));

		return Assert.Single(ReadPieces(
			Decrypt(await File.ReadAllBytesAsync(ArmorRegistryDataMapper.ResolveTargetPath(gameData))), 100)).Unit;
	}

	/// <summary>
	/// **给了映射、却一条都没应用上时，绝不能走"还原"。**
	/// <para>
	/// 旧实现把"全部跳过"和"要求关闭"当成同一件事：都进还原分支，用原始备份覆盖当前文件。
	/// 后果是——上一次已经生效的映射被静默抹掉，而用户明明是想"打开/更新"。
	/// （2026-11-05 反馈：Steam 校验完整性之后真映射"打开没效果"。）
	/// </para>
	/// </summary>

	/// <summary>
	/// 要求关闭、但当前文件已经被外部替换（Steam 校验 / 其它工具）时：不覆盖、**并且说出来**。
	/// 旧实现这里返回 <c>null</c> 消息，界面于是什么都不显示 —— 用户看到"关不掉"。
	/// </summary>

	/// <summary>要求关闭、文件本来就是原版：不写，但要说清"无需还原"。</summary>

	/// <summary>
	/// 明文前缀长度是**未公开格式**，不能写死。这里造一个"前缀比我们预期的多 2 个字节"的文件：
	/// 旧实现从固定偏移 4 开始找 <c>LDLD</c>，第一次迭代就 break，解析出 0 个套件，
	/// 于是 237 条映射全被报成"目标护甲找不到"（实测 2026-11-05）。
	/// </summary>
	[Fact]
	public async Task Apply_ToleratesADifferentPlaintextPrefixLength()
	{
		var gameData = Path.Combine(_root, "data");
		Directory.CreateDirectory(Path.Combine(gameData, "game"));
		var library = Library(
			Kit(100, 3, Piece(0xAAAAAAAAAAAAAAAA, 3, 0)),
			Kit(200, 3, Piece(0xCCCCCCCCCCCCCCCC, 3, 0)));
		// 在 4 字节计数前缀之后再插 2 个字节（模拟格式演进）。
		var shifted = new byte[library.Length + 2];
		library.AsSpan(0, 4).CopyTo(shifted);
		library.AsSpan(4).CopyTo(shifted.AsSpan(6));
		await File.WriteAllBytesAsync(
			Path.Combine(gameData, "game", ArmorRegistryDataMapper.FileName), Seal(shifted));

		var result = await new ArmorRegistryDataMapper().ApplyAsync(
			gameData, [new ArmorRegistryDataMapping(100, 200)], Path.Combine(_root, "backup"));

		Assert.True(result.Written, result.Message);
		Assert.Equal(1, result.Applied);
	}

	/// <summary>
	/// 完全解析不出套件时，消息必须说"文件读不懂"，而不是"目标护甲找不到" ——
	/// 后者会把一次格式不兼容伪装成"用户映射配错了"。
	/// </summary>

	[Fact]
	public async Task UnmodifiedPath_PointsAtTheBackupOnceWeWrite()
	{
		// 权威数据必须始终读原件：按被改过的件表重建索引，会让 link 编译认为
		// "目标已等于来源"而静默失效。
		var gameData = await CreateGameDataAsync();
		var target = ArmorRegistryDataMapper.ResolveTargetPath(gameData);
		var backup = Path.Combine(_root, "backup");
		var backupFile = Path.Combine(backup, ArmorRegistryDataMapper.BackupFileName);

		Assert.Equal(target, ArmorDataFileIdentity.GetUnmodifiedSealedPath(target, _root));

		await new ArmorRegistryDataMapper().ApplyAsync(
			gameData, [new ArmorRegistryDataMapping(100, 200)], backup);

		Assert.Equal(backupFile, ArmorDataFileIdentity.GetUnmodifiedSealedPath(target, _root));
	}

	[Fact]
	public async Task Identity_IsTheSameValueWhetherOrNotWeWroteTheFile()
	{
		// 指纹不能随"我们写没写过"改变形态，否则每应用一次真映射都会误报索引过期。
		var gameData = await CreateGameDataAsync();
		var target = ArmorRegistryDataMapper.ResolveTargetPath(gameData);
		var backup = Path.Combine(_root, "backup");

		var pristine = ArmorDataFileIdentity.GetUnmodifiedSealedSha(target, _root);

		await new ArmorRegistryDataMapper().ApplyAsync(
			gameData, [new ArmorRegistryDataMapping(100, 200)], backup);

		Assert.Equal(pristine, ArmorDataFileIdentity.GetUnmodifiedSealedSha(target, _root));

		// 新信任模型：备份是唯一可信原件，所以**外部替换 R 也不改变身份**。
		// 游戏更新改由 buildid 判据识别（见 SteamAppManifestTests），不再靠文件哈希。
		await File.WriteAllBytesAsync(target, Seal(Library(Kit(300, 3, Piece(0x1111111111111111, 1, 0)))));
		Assert.Equal(pristine, ArmorDataFileIdentity.GetUnmodifiedSealedSha(target, _root));
	}
}
