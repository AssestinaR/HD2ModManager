using System.Buffers.Binary;
using System.Text;
using HD2GameItemAssets;

namespace HD2ModCore.Tests;

public sealed class GameDataCustomizationReadersTests
{
	[Fact]
	public void HashLookupReader_ReadsValidatedArchivePathMap()
	{
		const ulong path = 0x1111111111111111;
		const ulong archive = 0x2222222222222222;

		var map = GameDataHashLookupReader.ReadArchiveByPath(BuildMinimalHashLookup(path, archive));

		Assert.Equal(archive, map[path]);
	}

	[Fact]
	public void GeneratedWeaponReader_ReadsNameIdsAndArchivePath()
	{
		var payload = new byte[16 + 88 + 32];
		WriteUInt64(payload, 0, 16);
		WriteUInt64(payload, 8, 1);
		WriteUInt64(payload, 16, 104);
		WriteUInt32(payload, 24, 0x12345678);
		WriteUInt32(payload, 28, 0x11111111);
		WriteUInt32(payload, 32, 0x22222222);
		WriteUInt64(payload, 48, 0x3333333333333333);
		Encoding.UTF8.GetBytes("Rifle test\0").CopyTo(payload, 104);

		var reference = Assert.Single(GeneratedWeaponCustomizationSettingsReader.Read(BuildLibrary(GeneratedWeaponCustomizationSettingsReader.RecordTypeId, payload)));

		Assert.Equal(0x12345678u, reference.ItemId);
		Assert.Equal(0x11111111u, reference.NameUpperStringId);
		Assert.Equal(0x22222222u, reference.NameCasedStringId);
		Assert.Equal(0x3333333333333333UL, reference.ArchivePathHash);
		Assert.Equal("Rifle test", reference.DebugName);
		Assert.Equal("Rifle test", reference.FallbackDisplayName);
	}

	[Fact]
	public void GeneratedUnitReader_ReadsOnlyNamedCustomizationLayout()
	{
		var payload = new byte[0x140];
		WriteUInt64(payload, 0x18, 0x50);
		Encoding.UTF8.GetBytes("nui_customization_type_example\0").CopyTo(payload, 0x50);
		WriteUInt64(payload, 0x28, 0x78);
		WriteUInt64(payload, 0x30, 1);
		const int entry = 0x90;
		WriteUInt64(payload, entry, 0x12345678);
		WriteUInt64(payload, entry + 8, 0xD0);
		WriteUInt64(payload, entry + 0x28, 0xF0);
		WriteUInt32(payload, entry + 0x30, 0xABCDEF12);
		WriteUInt64(payload, entry + 0x38, 0x9999999999999999);
		Encoding.UTF8.GetBytes("nui_skin_vehicle_example_name\0").CopyTo(payload, 0xD0);
		Encoding.UTF8.GetBytes("Forest Camo\0").CopyTo(payload, 0xF0);

		var reference = Assert.Single(GeneratedUnitCustomizationSettingsReader.Read(BuildLibrary(GeneratedUnitCustomizationSettingsReader.RecordTypeId, payload)));

		Assert.Equal(0x12345678u, reference.ItemId);
		Assert.Equal(0xABCDEF12u, reference.NameCasedStringId);
		Assert.Equal(0x9999999999999999UL, reference.ArchivePathHash);
		Assert.Equal("nui_skin_vehicle_example_name", reference.DebugName);
		Assert.Equal("Forest Camo", reference.FallbackDisplayName);
	}

	[Fact]
	public void GeneratedUnitReader_SkipsItemIdsThatDoNotFitThePublicModel()
	{
		var payload = new byte[0x140];
		WriteUInt64(payload, 0x18, 0x50);
		Encoding.UTF8.GetBytes("nui_customization_type_example\0").CopyTo(payload, 0x50);
		WriteUInt64(payload, 0x28, 0x78);
		WriteUInt64(payload, 0x30, 1);
		const int entry = 0x90;
		WriteUInt64(payload, entry, (ulong)uint.MaxValue + 1);
		WriteUInt64(payload, entry + 8, 0xD0);
		WriteUInt64(payload, entry + 0x28, 0xF0);
		WriteUInt32(payload, entry + 0x30, 0xABCDEF12);
		WriteUInt64(payload, entry + 0x38, 0x9999999999999999);
		Encoding.UTF8.GetBytes("nui_skin_vehicle_example_name\0").CopyTo(payload, 0xD0);
		Encoding.UTF8.GetBytes("Forest Camo\0").CopyTo(payload, 0xF0);

		Assert.Empty(GeneratedUnitCustomizationSettingsReader.Read(
			BuildLibrary(GeneratedUnitCustomizationSettingsReader.RecordTypeId, payload)));
	}

	private static byte[] BuildMinimalHashLookup(ulong path, ulong archive)
	{
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter(stream);
		// Each count before map5 is zero; this exercises the same ordered schema
		// as the installed hash_lookup Asset with the irrelevant collections empty.
		for (var index = 0; index < 15; index++) writer.Write(0u);
		writer.Write(1u); // map5 count
		writer.Write(path);
		writer.Write(archive);
		writer.Write(0u); // lookup trees 4
		writer.Write(0xDEADBEE7u);
		return stream.ToArray();
	}

	private static byte[] BuildLibrary(uint typeId, byte[] payload)
	{
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter(stream);
		writer.Write(1u);
		writer.Write("LDLD"u8.ToArray());
		writer.Write(1u);
		writer.Write(typeId);
		writer.Write((uint)payload.Length);
		writer.Write(1UL);
		writer.Write(payload);
		return stream.ToArray();
	}

	private static void WriteUInt32(byte[] buffer, int offset, uint value)
		=> BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, sizeof(uint)), value);

	private static void WriteUInt64(byte[] buffer, int offset, ulong value)
		=> BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, sizeof(ulong)), value);
}
