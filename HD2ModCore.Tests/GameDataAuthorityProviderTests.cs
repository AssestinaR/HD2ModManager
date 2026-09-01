using HD2GameItemAssets;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// Purpose: Locks down the installed-game authority reader independently from community metadata files.
public sealed class GameDataAuthorityProviderTests
{
	[Fact]
	public async Task LoadAsync_ValidPlaintextLibrary_UsesPlaintextWithoutEncryptedMirror()
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
			// Deliberately invalid encrypted data: a valid plaintext source must not load
			// Decryptor/native libsodium merely to "verify" this optional mirror.
			await File.WriteAllBytesAsync(Path.Combine(gameDirectory, "generated_customization_armor_sets.dl_bin"), [1, 2, 3, 4]);

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
		const int kitSize = 64;
		const int bodySize = 24;
		const int pieceSize = 96;
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
