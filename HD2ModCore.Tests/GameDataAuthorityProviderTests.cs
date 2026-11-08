using HD2GameItemAssets;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using FileDecryptor;

namespace HD2ModCore.Tests;

// Purpose: Locks down the installed-game authority reader independently from community metadata files.
public sealed class GameDataAuthorityProviderTests
{
	[Fact]
	public async Task LoadAsync_ValidPlaintextLibrary_UsesPlaintextWhenEncryptedMirrorIsAbsent()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-authority-tests", Guid.NewGuid().ToString("N"));
		var gameData = Path.Combine(root, "data");
		var gameDirectory = Path.Combine(gameData, "game");
		Directory.CreateDirectory(gameDirectory);
		try
		{
			var plaintext = BuildLibrary(
				(itemId: 101u, archiveId: 0x1111111111111111UL, kitType: CustomizationKitType.Armor,
					bodies: new[]
					{
						(bodyType: 0u, slot: CustomizationKitSlot.Torso, unitId: 0xaaaaUL),
						(bodyType: 1u, slot: CustomizationKitSlot.Torso, unitId: 0xbbbbUL),
						(bodyType: 3u, slot: CustomizationKitSlot.LeftArm, unitId: 0xccccUL),
					}));
			await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "generated_customization_armor_sets"), plaintext);
			var snapshot = await new GameDataAuthorityProvider().LoadAsync(gameData);

			Assert.Equal(GameDataAuthoritySourceKind.PlaintextDataLibrary, snapshot.SourceKind);
			var kit = Assert.Single(snapshot.Kits);
			Assert.Equal("1111111111111111", kit.ArchiveId);
			Assert.Equal(GameDataEquipmentKitKind.Armor, kit.Kind);
			Assert.Equal([UnitMeshBodyVariant.Stocky, UnitMeshBodyVariant.Slim, UnitMeshBodyVariant.Any], kit.Pieces.Select(piece => piece.BodyVariant).ToArray());
			Assert.Equal([UnitMeshPartKind.Torso, UnitMeshPartKind.Torso, UnitMeshPartKind.LeftArm], kit.Pieces.Select(piece => piece.PartKind).ToArray());
			Assert.DoesNotContain(snapshot.Issues, issue => issue.Code.Contains("Encrypted", StringComparison.Ordinal));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task LoadAsync_CurrentEncryptedLibrary_RefreshesOlderPlaintextCache()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-authority-tests", Guid.NewGuid().ToString("N"));
		var gameData = Path.Combine(root, "data");
		var gameDirectory = Path.Combine(gameData, "game");
		Directory.CreateDirectory(gameDirectory);
		try
		{
			var oldPlaintext = BuildLibrary(
				(itemId: 101u, archiveId: 0x1111111111111111UL, kitType: CustomizationKitType.Armor,
					bodies: [(bodyType: 0u, slot: CustomizationKitSlot.Torso, unitId: 0xaaaaUL)]));
			var currentPlaintext = BuildLibrary(
				(itemId: 202u, archiveId: 0x2222222222222222UL, kitType: CustomizationKitType.Helmet,
					bodies: [(bodyType: 3u, slot: CustomizationKitSlot.Helmet, unitId: 0xbbbbUL)]));
			var plaintextPath = Path.Combine(gameDirectory, "generated_customization_armor_sets");
			await File.WriteAllBytesAsync(plaintextPath, oldPlaintext);
#pragma warning disable CS0618
			var encrypted = Decryptor.CreateWithEmbeddedKeys().EncodeData(currentPlaintext);
#pragma warning restore CS0618
			await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "generated_customization_armor_sets.dl_bin"), encrypted);

			var snapshot = await new GameDataAuthorityProvider().LoadAsync(gameData, refreshPlaintextCache: true);

			Assert.Equal(GameDataAuthoritySourceKind.DecryptedDataLibrary, snapshot.SourceKind);
			var kit = Assert.Single(snapshot.Kits);
			Assert.Equal("2222222222222222", kit.ArchiveId);
			Assert.Equal(GameDataEquipmentKitKind.Helmet, kit.Kind);
			Assert.Equal(currentPlaintext, await File.ReadAllBytesAsync(plaintextPath));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	// 2026-11-06 实测回归：真映射会重写 live 的 .dl_bin，而它的件表只保留被映射的那几个
	// 来源模型的 unit（不同 unit 2006 → 66、件 3527 → 2080）。读 live 当权威等于读我们自己的
	// 输出，高级装备索引会一个 unit 都建不出来。权威源必须回到原件副本。
	[Fact]
	public async Task LoadAsync_PrefersOriginalCopyOverMappedLiveFile()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-authority-tests", Guid.NewGuid().ToString("N"));
		var gameData = Path.Combine(root, "data");
		var gameDirectory = Path.Combine(gameData, "game");
		var registryRoot = Path.Combine(root, "registry");
		var backupDirectory = Path.Combine(registryRoot, "backup");
		Directory.CreateDirectory(gameDirectory);
		Directory.CreateDirectory(backupDirectory);
		try
		{
			await WriteEncryptedAsync(
				Path.Combine(backupDirectory, ArmorRegistryDataMapper.BackupFileName),
				BuildLibrary(
					(itemId: 101u, archiveId: 0x1111111111111111UL, kitType: CustomizationKitType.Armor,
						bodies:
						[
							(bodyType: 0u, slot: CustomizationKitSlot.Torso, unitId: 0xaaaaUL),
							(bodyType: 1u, slot: CustomizationKitSlot.Torso, unitId: 0xbbbbUL),
							(bodyType: 3u, slot: CustomizationKitSlot.LeftArm, unitId: 0xccccUL),
						])));
			// live：模拟"真映射已应用"——只剩一个件、套件也换了。
			await WriteEncryptedAsync(
				Path.Combine(gameDirectory, "generated_customization_armor_sets.dl_bin"),
				BuildLibrary(
					(itemId: 202u, archiveId: 0x2222222222222222UL, kitType: CustomizationKitType.Helmet,
						bodies: [(bodyType: 3u, slot: CustomizationKitSlot.Helmet, unitId: 0xddddUL)])));

			var snapshot = await new GameDataAuthorityProvider(registryRoot: registryRoot).LoadAsync(gameData);

			var kit = Assert.Single(snapshot.Kits);
			Assert.Equal("1111111111111111", kit.ArchiveId);
			Assert.Equal(3, kit.Pieces.Count);
			Assert.EndsWith(ArmorRegistryDataMapper.BackupFileName, snapshot.SourcePath!, StringComparison.Ordinal);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task LoadAsync_NoOriginalCopy_AdoptsLiveFileAsOriginal()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-authority-tests", Guid.NewGuid().ToString("N"));
		var gameData = Path.Combine(root, "data");
		var gameDirectory = Path.Combine(gameData, "game");
		var registryRoot = Path.Combine(root, "registry");
		Directory.CreateDirectory(gameDirectory);
		try
		{
			var liveBytes = BuildLibrary(
				(itemId: 202u, archiveId: 0x2222222222222222UL, kitType: CustomizationKitType.Helmet,
					bodies: [(bodyType: 3u, slot: CustomizationKitSlot.Helmet, unitId: 0xddddUL)]));
			var livePath = Path.Combine(gameDirectory, "generated_customization_armor_sets.dl_bin");
			await WriteEncryptedAsync(livePath, liveBytes);

			var snapshot = await new GameDataAuthorityProvider(registryRoot: registryRoot).LoadAsync(gameData);

			Assert.Equal("2222222222222222", Assert.Single(snapshot.Kits).ArchiveId);
			var backupPath = Path.Combine(registryRoot, "backup", ArmorRegistryDataMapper.BackupFileName);
			Assert.True(File.Exists(backupPath));
			Assert.Equal(await File.ReadAllBytesAsync(livePath), await File.ReadAllBytesAsync(backupPath));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task LoadAsync_AfterGameUpdate_AdoptsLiveFileWhenItStillHasAllPieces()
	{
		var (root, gameData, registryRoot, backupPath) = await PrepareCopyAsync(pieceCount: 1);
		try
		{
			// 游戏更新过：Steam 覆盖了 live，所以 live 是官方版本、件数不少于副本。
			await WriteSteamManifestAsync(gameData, lastUpdatedUnix: 1_800_000_000);
			var livePath = Path.Combine(gameData, "game", "generated_customization_armor_sets.dl_bin");
			await WriteEncryptedAsync(livePath, BuildLibrary(
				(itemId: 202u, archiveId: 0x2222222222222222UL, kitType: CustomizationKitType.Helmet,
					bodies:
					[
						(bodyType: 3u, slot: CustomizationKitSlot.Helmet, unitId: 0xddddUL),
						(bodyType: 0u, slot: CustomizationKitSlot.Torso, unitId: 0xeeeeUL),
					])));

			var snapshot = await new GameDataAuthorityProvider(registryRoot: registryRoot).LoadAsync(gameData);

			Assert.Equal("2222222222222222", Assert.Single(snapshot.Kits).ArchiveId);
			Assert.Equal(await File.ReadAllBytesAsync(livePath), await File.ReadAllBytesAsync(backupPath));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task LoadAsync_AfterGameUpdate_KeepsCopyWhenLiveStillLooksMapped()
	{
		var (root, gameData, registryRoot, backupPath) = await PrepareCopyAsync(pieceCount: 3);
		try
		{
			// 游戏更新过，但 live 的件数比副本少 ⇒ 它仍带着真映射的改动，绝不能覆盖副本。
			await WriteSteamManifestAsync(gameData, lastUpdatedUnix: 1_800_000_000);
			await WriteEncryptedAsync(Path.Combine(gameData, "game", "generated_customization_armor_sets.dl_bin"),
				BuildLibrary(
					(itemId: 202u, archiveId: 0x2222222222222222UL, kitType: CustomizationKitType.Helmet,
						bodies: [(bodyType: 3u, slot: CustomizationKitSlot.Helmet, unitId: 0xddddUL)])));
			var pristineCopy = await File.ReadAllBytesAsync(backupPath);

			var snapshot = await new GameDataAuthorityProvider(registryRoot: registryRoot).LoadAsync(gameData);

			Assert.Equal("1111111111111111", Assert.Single(snapshot.Kits).ArchiveId);
			Assert.Equal(pristineCopy, await File.ReadAllBytesAsync(backupPath));
			Assert.Contains(snapshot.Issues, issue => issue.Code == "ArmorOriginalCopyKeptAfterGameUpdate");
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	/// <summary>
	/// 游戏更新过、但 live 其实是**我们自己编译出来的**产物时，绝不能采纳为原件副本。
	/// <para>
	/// 件数判据在这里是瞎的：新的编译只改件记录的 unit，**件数一件不变**，所以
	/// "live 的件数不比副本少"不能证明文件没被改过。真映射的产物有唯一签名 ——
	/// 内容哈希等于原件、字节却不同（伪造那一步就是为了把哈希拨回去）。
	/// </para>
	/// </summary>
	[Fact]
	public async Task LoadAsync_AfterGameUpdate_KeepsCopyWhenLiveIsOurForgedCompile()
	{
		var (root, gameData, registryRoot, backupPath) = await PrepareCopyAsync(pieceCount: 3);
		try
		{
			var livePath = Path.Combine(gameData, "game", ArmorRegistryDataMapper.FileName);
			var copySealed = await File.ReadAllBytesAsync(backupPath);
			var copyPlain = Decrypt(copySealed);
			var compiled = ArmorRegistryDataMapper.Compile(copySealed,
			[
				new ArmorRegistryDataMapping(101, null,
					[new ArmorRegistryDataSourcePiece(0, (uint)CustomizationKitSlot.Torso, 0, 0x9999UL)]),
			]);
			// 前提：这次编译真的改了内容、却没改件数。
			Assert.True(compiled.Changed);
			Assert.Equal(3, compiled.Hidden + compiled.Replaced);
			Assert.Equal(
				ArmorRegistryDataMapper.HashContent(copyPlain),
				ArmorRegistryDataMapper.HashContent(Decrypt(compiled.Sealed)));

			await File.WriteAllBytesAsync(livePath, compiled.Sealed);
			await WriteSteamManifestAsync(gameData, lastUpdatedUnix: 1_800_000_000);
			var pristine = await File.ReadAllBytesAsync(backupPath);

			var snapshot = await new GameDataAuthorityProvider(registryRoot: registryRoot).LoadAsync(gameData);

			Assert.Equal(pristine, await File.ReadAllBytesAsync(backupPath));
			Assert.Contains(snapshot.Issues, issue => issue.Code == "ArmorOriginalCopyKeptAfterGameUpdate");
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	private static byte[] Decrypt(byte[] sealedBytes)
	{
#pragma warning disable CS0618
		return Decryptor.CreateWithEmbeddedKeys().DecodeData(sealedBytes);
#pragma warning restore CS0618
	}

	private static async Task<(string Root, string GameData, string RegistryRoot, string BackupPath)> PrepareCopyAsync(int pieceCount)
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-authority-tests", Guid.NewGuid().ToString("N"));
		var gameData = Path.Combine(root, "data");
		var registryRoot = Path.Combine(root, "registry");
		var backupPath = Path.Combine(registryRoot, "backup", ArmorRegistryDataMapper.BackupFileName);
		Directory.CreateDirectory(Path.Combine(gameData, "game"));
		Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
		var bodies = new List<(uint bodyType, uint slot, ulong unitId)>();
		for (var index = 0; index < pieceCount; index++)
			bodies.Add(((uint)index, CustomizationKitSlot.Torso, 0xa000UL + (ulong)index));
		await WriteEncryptedAsync(backupPath, BuildLibrary(
			(itemId: 101u, archiveId: 0x1111111111111111UL, kitType: CustomizationKitType.Armor, bodies: [.. bodies])));
		// 副本早于这次游戏更新。
		File.SetLastWriteTimeUtc(backupPath, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
		return (root, gameData, registryRoot, backupPath);
	}

	private static async Task WriteSteamManifestAsync(string gameData, long lastUpdatedUnix)
	{
		var manifestPath = SteamAppManifest.ResolveManifestPath(gameData);
		Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
		await File.WriteAllTextAsync(manifestPath, "\"AppState\"\n{\n\t\"appid\"\t\t\"553850\"\n\t\"buildid\"\t\t\"18000000\"\n"
			+ $"\t\"LastUpdated\"\t\t\"{lastUpdatedUnix}\"\n\t\"StateFlags\"\t\t\"4\"\n}}\n");
	}

	private static async Task WriteEncryptedAsync(string path, byte[] plaintext)
	{
#pragma warning disable CS0618
		var encrypted = Decryptor.CreateWithEmbeddedKeys().EncodeData(plaintext);
#pragma warning restore CS0618
		await File.WriteAllBytesAsync(path, encrypted);
	}

	private static byte[] BuildLibrary(
		params (uint itemId, ulong archiveId, uint kitType, (uint bodyType, uint slot, ulong unitId)[] bodies)[] kits)
	{
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter(stream);
		writer.Write((uint)kits.Length);
		foreach (var kit in kits)
		{
			var payload = BuildKitPayload(kit.itemId, kit.archiveId, kit.kitType, kit.bodies);
			writer.Write("LDLD"u8.ToArray());
			writer.Write(0u);
			writer.Write(CustomizationArmorSetsReader.ArmorCustomizationTypeId);
			writer.Write((uint)payload.Length);
			writer.Write(0UL);
			writer.Write(payload);
		}
		return stream.ToArray();
	}

	private static byte[] BuildKitPayload(
		uint itemId,
		ulong archiveId,
		uint kitType,
		IReadOnlyList<(uint bodyType, uint slot, ulong unitId)> bodies)
	{
		const int kitSize = CustomizationArmorSetsReader.KitSize;
		const int bodySize = CustomizationArmorSetsReader.BodySize;
		const int pieceSize = CustomizationArmorSetsReader.PieceSize;
		var bodyOffset = kitSize;
		var pieceOffset = bodyOffset + bodySize * bodies.Count;
		var buffer = new byte[pieceOffset + pieceSize * bodies.Count];
		WriteUInt32(buffer, 0, itemId);
		WriteUInt64(buffer, 32, archiveId);
		WriteUInt32(buffer, 40, kitType);
		WriteInt64(buffer, 48, bodyOffset);
		WriteInt64(buffer, 56, bodies.Count);
		for (var index = 0; index < bodies.Count; index++)
		{
			var body = bodies[index];
			var bodyEntryOffset = bodyOffset + index * bodySize;
			var pieceEntryOffset = pieceOffset + index * pieceSize;
			WriteUInt32(buffer, bodyEntryOffset, body.bodyType);
			WriteInt64(buffer, bodyEntryOffset + 8, pieceEntryOffset);
			WriteInt64(buffer, bodyEntryOffset + 16, 1);
			WriteUInt64(buffer, pieceEntryOffset, body.unitId);
			WriteUInt32(buffer, pieceEntryOffset + 8, body.slot);
			WriteUInt32(buffer, pieceEntryOffset + 12, 2);
		}
		return buffer;
	}

	private static void WriteUInt32(byte[] buffer, int offset, uint value)
		=> System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, sizeof(uint)), value);

	private static void WriteUInt64(byte[] buffer, int offset, ulong value)
		=> System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, sizeof(ulong)), value);

	private static void WriteInt64(byte[] buffer, int offset, long value)
		=> System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, sizeof(long)), value);
}
