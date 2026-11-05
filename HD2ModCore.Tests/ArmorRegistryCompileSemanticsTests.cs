using System.Buffers.Binary;
using FileDecryptor;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 受管 dl_bin 的**编译语义**（2026-11-05 用户口径）。
/// <para>
/// 它是<b>派生事实</b>：不保存、不做稳定性保证、永远不可信。所以这里钉住的都是
/// "每次重算、每次覆盖"的行为，而不是"要不要动它"的判断：
/// </para>
/// <list type="number">
/// <item>基底永远是**原件副本**，不是磁盘上那份被改过的文件；</item>
/// <item>每次都重写（关掉映射 = 用空结构编译 = 写回原件）；</item>
/// <item>只有两种失败：读不懂 dl_bin、目标不可写。</item>
/// </list>
/// </summary>
public sealed class ArmorRegistryCompileSemanticsTests : IDisposable
{
	private readonly string root = Path.Combine(
		Path.GetTempPath(), "hd2-dlbin-compile-" + Guid.NewGuid().ToString("N"));

	public ArmorRegistryCompileSemanticsTests() => Directory.CreateDirectory(root);

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	private string BackupDirectory => Path.Combine(root, "backup");

	private string CreateGameData()
	{
		var gameData = Path.Combine(root, "data");
		var game = Path.Combine(gameData, "game");
		Directory.CreateDirectory(game);
		File.WriteAllBytes(
			Path.Combine(game, ArmorRegistryDataMapper.FileName),
			Seal(Library(Kit(100, Piece(0xAAAAAAAAAAAAAAAA)), Kit(200, Piece(0xBBBBBBBBBBBBBBBB)))));
		return gameData;
	}

	private static byte[] Seal(byte[] plain)
	{
#pragma warning disable CS0618
		return Decryptor.CreateWithEmbeddedKeys().EncodeData(plain);
#pragma warning restore CS0618
	}

	private static byte[] Decrypt(byte[] sealedBytes)
	{
#pragma warning disable CS0618
		return Decryptor.CreateWithEmbeddedKeys().DecodeData(sealedBytes);
#pragma warning restore CS0618
	}

	private static byte[] Library(params byte[][] payloads)
	{
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter(stream);
		writer.Write((uint)payloads.Length);
		foreach (var payload in payloads)
		{
			writer.Write("LDLD"u8.ToArray());
			writer.Write(1u);
			writer.Write(0xd9a55aa0u);
			writer.Write((uint)payload.Length);
			writer.Write(1UL);
			writer.Write(payload);
		}
		return stream.ToArray();
	}

	private static byte[] Kit(uint itemId, byte[] piece)
	{
		const int headerSize = 64;
		const int bodySize = 24;
		var payload = new byte[headerSize + bodySize + piece.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(payload, itemId);
		BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(48), headerSize);
		BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(56), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(headerSize), 3);
		BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(headerSize + 8), headerSize + bodySize);
		BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(headerSize + 16), 1);
		piece.CopyTo(payload.AsSpan(headerSize + bodySize));
		return payload;
	}

	private static byte[] Piece(ulong unit)
	{
		var record = new byte[112];
		BinaryPrimitives.WriteUInt64LittleEndian(record, unit);
		BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), 3);
		for (var i = 0; i < 7; i++) record[96 + i] = 0xFF;
		return record;
	}

	private static ulong ReadUnit(byte[] plain, uint kitId)
	{
		var position = 4;
		while (position + 24 <= plain.Length)
		{
			if (!plain.AsSpan(position, 4).SequenceEqual("LDLD"u8)) break;
			var type = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(position + 8));
			var size = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(position + 12));
			var payloadStart = position + 24;
			if (type == 0xd9a55aa0u && BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(payloadStart)) == kitId)
			{
				var bodyOffset = BinaryPrimitives.ReadInt64LittleEndian(plain.AsSpan(payloadStart + 48));
				var pieceOffset = BinaryPrimitives.ReadInt64LittleEndian(plain.AsSpan(payloadStart + (int)bodyOffset + 8));
				return BinaryPrimitives.ReadUInt64LittleEndian(plain.AsSpan(payloadStart + (int)pieceOffset));
			}
			position = payloadStart + (int)size;
		}
		throw new InvalidDataException($"找不到套件 {kitId}。");
	}

	[Fact]
	public async Task Compile_IsIdempotentInContentEvenThoughItRewritesEveryTime()
	{
		var gameData = CreateGameData();
		var target = ArmorRegistryDataMapper.ResolveTargetPath(gameData);
		var mapper = new ArmorRegistryDataMapper();

		var first = await mapper.ApplyAsync(gameData, [new ArmorRegistryDataMapping(100, 200)], BackupDirectory);
		var afterFirst = Decrypt(await File.ReadAllBytesAsync(target));
		var second = await mapper.ApplyAsync(gameData, [new ArmorRegistryDataMapping(100, 200)], BackupDirectory);
		var afterSecond = Decrypt(await File.ReadAllBytesAsync(target));

		Assert.Equal(1, first.Applied);
		Assert.Equal(1, second.Applied);
		// 每次都写（派生文件随时重算），但内容必须一致。
		Assert.Equal(Convert.ToHexString(afterFirst), Convert.ToHexString(afterSecond));
	}

	[Fact]
	public async Task Compile_WithNoMappingsWritesTheOriginalBack()
	{
		var gameData = CreateGameData();
		var target = ArmorRegistryDataMapper.ResolveTargetPath(gameData);
		var originalUnit = ReadUnit(Decrypt(await File.ReadAllBytesAsync(target)), 100);
		var mapper = new ArmorRegistryDataMapper();

		await mapper.ApplyAsync(gameData, [new ArmorRegistryDataMapping(100, 200)], BackupDirectory);
		Assert.NotEqual(originalUnit, ReadUnit(Decrypt(await File.ReadAllBytesAsync(target)), 100));

		var revert = await mapper.ApplyAsync(gameData, [], BackupDirectory);

		Assert.True(revert.Written);
		Assert.Equal(originalUnit, ReadUnit(Decrypt(await File.ReadAllBytesAsync(target)), 100));
	}

	[Fact]
	public async Task Compile_WithOnlySkippedMappingsWritesTheOriginalBack()
	{
		// "全部跳过"不是错误、也不保留旧改动：结构里没有任何能应用的东西，
		// 编译结果就是原件 —— 于是磁盘上那份被写回原件。用户看到的是"映射没了"，
		// 重新配一份即可（用户明确接受这个最坏情况）。
		var gameData = CreateGameData();
		var target = ArmorRegistryDataMapper.ResolveTargetPath(gameData);
		var originalUnit = ReadUnit(Decrypt(await File.ReadAllBytesAsync(target)), 100);
		var mapper = new ArmorRegistryDataMapper();

		await mapper.ApplyAsync(gameData, [new ArmorRegistryDataMapping(100, 200)], BackupDirectory);
		var skipped = await mapper.ApplyAsync(gameData, [new ArmorRegistryDataMapping(999, 200)], BackupDirectory);

		Assert.Equal(0, skipped.Applied);
		Assert.Equal(1, skipped.Skipped);
		Assert.Null(skipped.Message);   // 不是错误场景，不打扰用户
		Assert.Equal(originalUnit, ReadUnit(Decrypt(await File.ReadAllBytesAsync(target)), 100));
	}

	[Fact]
	public async Task Compile_AlwaysStartsFromTheOriginalCopyNotTheLiveFile()
	{
		// 派生文件被外部替换过？无所谓：基底是原件副本，编译结果与它无关。
		var gameData = CreateGameData();
		var target = ArmorRegistryDataMapper.ResolveTargetPath(gameData);
		var originalUnit = ReadUnit(Decrypt(await File.ReadAllBytesAsync(target)), 100);
		var mapper = new ArmorRegistryDataMapper();

		await mapper.ApplyAsync(gameData, [new ArmorRegistryDataMapping(100, 200)], BackupDirectory);
		// 外部把文件换成一份完全不同的库。
		await File.WriteAllBytesAsync(
			target, Seal(Library(Kit(100, Piece(0x1111111111111111)), Kit(200, Piece(0x2222222222222222)))));

		await mapper.ApplyAsync(gameData, [], BackupDirectory);

		// 关掉映射 = 写回原件（不是"保留外部那份"）。
		Assert.Equal(originalUnit, ReadUnit(Decrypt(await File.ReadAllBytesAsync(target)), 100));
	}

	[Fact]
	public async Task Compile_ReportsUnparsableBaseAsTheOnlyReadFailure()
	{
		var gameData = Path.Combine(root, "data-broken");
		var game = Path.Combine(gameData, "game");
		Directory.CreateDirectory(game);
		await File.WriteAllBytesAsync(
			Path.Combine(game, ArmorRegistryDataMapper.FileName), Seal(new byte[256]));

		var result = await new ArmorRegistryDataMapper().ApplyAsync(
			gameData, [new ArmorRegistryDataMapping(100, 200)], BackupDirectory);

		Assert.False(result.Written);
		Assert.Contains("解析不出任何护甲套件", result.Message);
	}

	[Fact]
	public async Task Compile_ReportsUnwritableTargetAsTheOnlyWriteFailure()
	{
		var gameData = CreateGameData();
		var target = ArmorRegistryDataMapper.ResolveTargetPath(gameData);
		var mapper = new ArmorRegistryDataMapper();
		// 先用一次应用把原件副本准备好，再让目标不可写。
		await mapper.ApplyAsync(gameData, [], BackupDirectory);

		await using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			var result = await mapper.ApplyAsync(gameData, [new ArmorRegistryDataMapping(100, 200)], BackupDirectory);
			Assert.False(result.Written);
			Assert.Contains("写不进注册表文件", result.Message);
		}
	}
}
