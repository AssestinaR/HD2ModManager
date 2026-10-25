using System.Buffers.Binary;
using System.Text;
using FileDecryptor;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 真映射模式的数据改写器。这些用例不碰游戏目录：合成一份结构相同的 LDLD 容器，
/// 验证"改件集 + 把内容校验和伪造回原值"这条链，以及幂等与撤销。
/// </summary>
public sealed class ArmorRegistryDataMapperTests : IDisposable
{
	const uint ArmorType = 0xd9a55aa0;
	const int PieceSize = 112;
	const int BodySize = 24;

	readonly string _root = Path.Combine(Path.GetTempPath(), "hd2-armor-data-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
	}

	// 112 字节件记录：unit / slot / pieceType / weight 有用，+24 放一个 LUT 标记、+88 放 tone，
	// 89..111 是容器忽略的哨兵。
	static byte[] Piece(ulong unit, uint slot, uint pieceType, uint weight = 1, ulong lut = 0, byte tone = 0)
	{
		var record = new byte[PieceSize];
		BinaryPrimitives.WriteUInt64LittleEndian(record, unit);
		BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), slot);
		BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), pieceType);
		BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), weight);
		BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(24), lut);
		record[88] = tone;
		for (var i = 0; i < 7; i++) record[89 + i] = 0x00;
		for (var i = 0; i < 7; i++) record[96 + i] = 0xFF;
		return record;
	}

	static byte[] Kit(uint itemId, uint bodyType, params byte[][] pieces)
	{
		const int headerSize = 64;
		var payload = new byte[headerSize + BodySize + pieces.Length * PieceSize];
		BinaryPrimitives.WriteUInt32LittleEndian(payload, itemId);
		BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(48), headerSize);
		BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(56), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(headerSize), bodyType);
		BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(headerSize + 8), headerSize + BodySize);
		BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(headerSize + 16), pieces.Length);
		var cursor = headerSize + BodySize;
		foreach (var piece in pieces)
		{
			Array.Copy(piece, 0, payload, cursor, PieceSize);
			cursor += PieceSize;
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
				var bodyOffset = BinaryPrimitives.ReadInt64LittleEndian(payload[48..]);
				var pieceOffset = BinaryPrimitives.ReadInt64LittleEndian(payload[((int)bodyOffset + 8)..]);
				var pieceCount = BinaryPrimitives.ReadInt64LittleEndian(payload[((int)bodyOffset + 16)..]);
				var result = new List<(ulong, uint, uint)>();
				for (var p = 0; p < pieceCount; p++)
				{
					var at = payloadStart + (int)pieceOffset + p * PieceSize;
					result.Add((
						BinaryPrimitives.ReadUInt64LittleEndian(plain.AsSpan(at)),
						BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(at + 8)),
						BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(at + 12))));
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
				Piece(0xAAAAAAAAAAAAAAAA, 1, 0, targetWeight),
				Piece(0xBBBBBBBBBBBBBBBB, 2, 0, targetWeight)),
			Kit(200, 3,
				Piece(0xCCCCCCCCCCCCCCCC, 1, 0, sourceWeight),
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
				Kit(100, 3, Piece(0x1111111111111111, 1, 0, weight: 2, lut: targetLut, tone: 7)),
				Kit(200, 3, Piece(0x2222222222222222, 1, 0, weight: 0, lut: sourceLut, tone: 3)))));

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
			new ArmorRegistryDataSourcePiece(3, 1, 0, 0xC0FFEE0000000001),
			new ArmorRegistryDataSourcePiece(3, 2, 0, 0xC0FFEE0000000002),
		};
		var result = await new ArmorRegistryDataMapper().ApplyAsync(
			gameData, [new ArmorRegistryDataMapping(100, null, modelPieces)], backup);

		Assert.True(result.Written);
		var plain = Decrypt(await File.ReadAllBytesAsync(ArmorRegistryDataMapper.ResolveTargetPath(gameData)));
		var pieces = ReadPieces(plain, 100);
		Assert.Equal(2, pieces.Length);
		Assert.Contains(pieces, p => p.Unit == 0xC0FFEE0000000001);
		Assert.Contains(pieces, p => p.Unit == 0xC0FFEE0000000002);
		// 素体只提供 unit/slot/type，weight 仍然是目标自己的 2。
		Assert.All(ReadWeights(plain, 100), weight => Assert.Equal(2u, weight));
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
		// 件记录 +16 的 weight 是护甲等级（轻/中/重），属于玩法字段。映射只换外观，
		// 绝不能把目标甲的等级换成来源甲的——那等于改数值。
		var gameData = await CreateGameDataAsync(targetWeight: 2, sourceWeight: 0);
		var backup = Path.Combine(_root, "backup");
		await new ArmorRegistryDataMapper().ApplyAsync(
			gameData, [new ArmorRegistryDataMapping(100, 200)], backup);

		var plain = Decrypt(await File.ReadAllBytesAsync(ArmorRegistryDataMapper.ResolveTargetPath(gameData)));
		// 来源的件被搬过来了（3 件），但每一件的 weight 都必须是目标自己的 2。
		Assert.Equal(3, ReadWeights(plain, 100).Length);
		Assert.All(ReadWeights(plain, 100), weight => Assert.Equal(2u, weight));
		// 来源自己不受影响。
		Assert.All(ReadWeights(plain, 200), weight => Assert.Equal(0u, weight));
	}

	[Fact]
	public async Task Apply_RewritesTargetPieceSetAndKeepsContentHash()
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

		var targetPieces = ReadPieces(appliedPlain, 100);
		var sourcePieces = ReadPieces(appliedPlain, 200);
		Assert.Equal(
			sourcePieces.OrderBy(p => p),
			targetPieces.OrderBy(p => p));
		// 来源的件被完整搬过来（含目标原本没有的 pieceType=1）。
		Assert.Contains(targetPieces, p => p.Item3 == 1);
	}

	[Fact]
	public async Task Apply_IsIdempotent()
	{
		var gameData = await CreateGameDataAsync();
		var backup = Path.Combine(_root, "backup");
		var mapper = new ArmorRegistryDataMapper();

		var first = await mapper.ApplyAsync(gameData, [new ArmorRegistryDataMapping(100, 200)], backup);
		Assert.Equal(1, first.Applied);

		var second = await mapper.ApplyAsync(gameData, [new ArmorRegistryDataMapping(100, 200)], backup);
		// 结果与磁盘一致，不重写；映射本身仍被重新计算（基线永远是原始备份）。
		Assert.False(second.Written);
		Assert.Equal(1, second.Applied);
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
	public async Task Apply_SkipsMappingWhoseKitsAreMissing()
	{
		var gameData = await CreateGameDataAsync();
		var backup = Path.Combine(_root, "backup");
		var result = await new ArmorRegistryDataMapper().ApplyAsync(
			gameData, [new ArmorRegistryDataMapping(100, 999)], backup);

		Assert.False(result.Written);
		Assert.Equal(0, result.Applied);
		Assert.Equal(1, result.Skipped);
	}

	[Fact]
	public async Task Apply_DoesNotOverwriteAFileThatChangedOutsideOurControl()
	{
		// 场景：应用过真映射（备份=O，文件=M），随后 Steam 校验完整性把文件换成了别的内容。
		// 此时再走"还原"绝不能用旧备份覆盖它——既会毁掉外部写入的内容，也会让索引指纹
		// 对不上真原版而误报"游戏已更新"。
		var gameData = await CreateGameDataAsync();
		var target = ArmorRegistryDataMapper.ResolveTargetPath(gameData);
		var backup = Path.Combine(_root, "backup");
		var mapper = new ArmorRegistryDataMapper();

		await mapper.ApplyAsync(gameData, [new ArmorRegistryDataMapping(100, 200)], backup);

		// 外部替换成一份完全不同的合法库。
		var external = Seal(Library(Kit(300, 3, Piece(0x9999999999999999, 1, 0))));
		await File.WriteAllBytesAsync(target, external);

		var restore = await mapper.ApplyAsync(gameData, [], backup);
		Assert.False(restore.Written);
		Assert.Equal(Convert.ToHexString(external), Convert.ToHexString(await File.ReadAllBytesAsync(target)));
	}

	[Fact]
	public async Task Apply_SeedsFromCurrentWhenStateFileIsMissing()
	{
		// 备份在、状态文件没了：无法证明这份备份对应哪个原始文件，必须按当前文件重建基准。
		var gameData = await CreateGameDataAsync();
		var target = ArmorRegistryDataMapper.ResolveTargetPath(gameData);
		var backup = Path.Combine(_root, "backup");
		var mapper = new ArmorRegistryDataMapper();

		await mapper.ApplyAsync(gameData, [new ArmorRegistryDataMapping(100, 200)], backup);
		File.Delete(Path.Combine(backup, ArmorRegistryDataMapper.StateFileName));

		// 外部替换后再应用一次映射：新基准应当是外部文件，而不是旧备份。
		var external = Seal(Library(
			Kit(100, 3, Piece(0x1111111111111111, 1, 0)),
			Kit(200, 3, Piece(0x2222222222222222, 1, 0))));
		await File.WriteAllBytesAsync(target, external);
		await mapper.ApplyAsync(gameData, [new ArmorRegistryDataMapping(100, 200)], backup);

		var pristine = await File.ReadAllBytesAsync(Path.Combine(backup, ArmorRegistryDataMapper.BackupFileName));
		Assert.Equal(Convert.ToHexString(external), Convert.ToHexString(pristine));
	}

	[Fact]
	public async Task Apply_HandlesMultipleMappingsInOnePass()
	{
		// 一次应用多条映射时，每次重建都会移动后续套件的偏移；必须仍然落在正确位置。
		var gameData = await CreateGameDataAsync();
		var backup = Path.Combine(_root, "backup");
		var result = await new ArmorRegistryDataMapper().ApplyAsync(
			gameData,
			[new ArmorRegistryDataMapping(100, 200), new ArmorRegistryDataMapping(200, 100)],
			backup);

		Assert.True(result.Written);
		var plain = Decrypt(await File.ReadAllBytesAsync(ArmorRegistryDataMapper.ResolveTargetPath(gameData)));
		Assert.Equal(ReadPieces(plain, 200).OrderBy(p => p), ReadPieces(plain, 100).OrderBy(p => p));
	}

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
