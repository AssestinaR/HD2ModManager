using System.Buffers.Binary;
using HD2GameItemAssets;

namespace HD2ModCore.Tests;

public sealed class GeneratedEntityEncyclopediaReaderTests
{
	[Fact]
	public void Read_ParsesEntityUnitNamesAndComponentEvidence()
	{
		const ulong entityId = 0x1111111111111111UL;
		const ulong unitPath = 0x2222222222222222UL;
		var data = BuildGeneratedEntities(
			entityId,
			unitPath,
			new uint[]
			{
				GeneratedEntityTypeIds.EquipmentComponentData,
				GeneratedEntityTypeIds.WieldableComponentData,
				GeneratedEntityTypeIds.WeaponDataComponentData,
			});

		var catalog = GeneratedEntityEncyclopediaReader.Read(data);

		var entry = Assert.Single(catalog.Entries);
		Assert.Equal(entityId, entry.EntityId);
		Assert.Equal(unitPath, entry.UnitPath);
		Assert.False(entry.IsExactUnitMatch);
		Assert.True(entry.HasComponent(GeneratedEntityTypeIds.EquipmentComponentData));
		Assert.True(entry.HasComponent(GeneratedEntityTypeIds.WieldableComponentData));
		Assert.True(entry.HasComponent(GeneratedEntityTypeIds.WeaponDataComponentData));
		Assert.Equal(0x12345678u, entry.NameStringIds[0]);
		Assert.Equal(0x87654321u, entry.NameStringIds[2]);
	}

	[Fact]
	public void Read_RejectsTruncatedEntitySettings()
	{
		var data = BuildGeneratedEntities(
			0x1111111111111111UL,
			0x2222222222222222UL,
			Array.Empty<uint>());

		Assert.Throws<InvalidDataException>(() => GeneratedEntityEncyclopediaReader.Read(data[..^1]));
	}

	/// <summary>
	/// 游戏更新把表长改掉时，常量会对不上——这时解析器必须自己从数据里推出分段，
	/// 而不是整块事实静默消失。用一套和常量完全不同的布局验证这条兜底路径。
	/// </summary>
	[Fact]
	public void Read_DerivesLayoutWhenValidatedConstantsDoNotMatch()
	{
		var data = BuildGeneratedEntities(
			0x1111111111111111UL,
			0x2222222222222222UL,
			Array.Empty<uint>(),
			unitIndexCount: 4,
			unitComponentCount: 2,
			encyclopediaIndexCount: 4,
			encyclopediaComponentCount: 2);

		var catalog = GeneratedEntityEncyclopediaReader.Read(data);

		var entry = Assert.Single(catalog.Entries);
		Assert.Equal(0x2222222222222222UL, entry.UnitPath);
		Assert.Equal(0x12345678u, entry.NameStringIds[0]);
		// unit 与 encyclopedia 两张表都走了推导（equipment 这张夹具里没有）。
		Assert.Equal(2, catalog.DerivedLayouts.Count);
		Assert.Contains(catalog.DerivedLayouts, item => item.StartsWith("UnitComponentData", StringComparison.Ordinal));
		Assert.Contains(catalog.DerivedLayouts, item => item.StartsWith("EncyclopediaEntryComponentData", StringComparison.Ordinal));
	}

	private static byte[] BuildGeneratedEntities(
		ulong entityId,
		ulong unitPath,
		IReadOnlyList<uint> componentTypes,
		int unitIndexCount = GeneratedEntityEncyclopediaReader.UnitIndexCount,
		int unitComponentCount = GeneratedEntityEncyclopediaReader.UnitComponentCount,
		int encyclopediaIndexCount = GeneratedEntityEncyclopediaReader.EncyclopediaIndexCount,
		int encyclopediaComponentCount = GeneratedEntityEncyclopediaReader.EncyclopediaComponentCount)
	{
		// 夹具与解析器共用同一组常量——各自抄一份正是件表 96→112 那次回归的根源。
		const int indexEntrySize = GeneratedEntityEncyclopediaReader.ComponentIndexDataSize;
		var entitySettingsSize = GeneratedEntityEncyclopediaReader.EntitySettingsEntryCount
			* GeneratedEntityEncyclopediaReader.EntitySettingsEntrySize;
		var unitPayloadSize = unitIndexCount * indexEntrySize
			+ unitComponentCount * GeneratedEntityEncyclopediaReader.UnitComponentSize;
		var encyclopediaPayloadSize = encyclopediaIndexCount * indexEntrySize
			+ encyclopediaComponentCount * GeneratedEntityEncyclopediaReader.EncyclopediaComponentSize;
		var entitySettings = new byte[entitySettingsSize + componentTypes.Count * sizeof(ushort)];
		WriteUInt64(entitySettings, 0, entityId);
		WriteUInt64(entitySettings, 8, (ulong)entitySettingsSize);
		WriteUInt64(entitySettings, 16, (ulong)componentTypes.Count);
		for (var index = 0; index < componentTypes.Count; index++)
		{
			WriteUInt16(entitySettings, entitySettingsSize + index * sizeof(ushort), checked((ushort)(index + 1)));
		}

		var unit = new byte[unitPayloadSize];
		WriteUInt64(unit, 0, entityId);
		WriteUInt32(unit, 8, 0);
		var unitComponentsOffset = unitIndexCount * indexEntrySize;
		WriteUInt64(unit, unitComponentsOffset, unitPath);

		var encyclopedia = new byte[encyclopediaPayloadSize];
		WriteUInt64(encyclopedia, 0, entityId);
		WriteUInt32(encyclopedia, 8, 0);
		var encyclopediaComponentsOffset = encyclopediaIndexCount * indexEntrySize;
		WriteUInt32(encyclopedia, encyclopediaComponentsOffset, 0x12345678u);
		WriteUInt32(encyclopedia, encyclopediaComponentsOffset + 8, 0x87654321u);

		using var stream = new MemoryStream();
		WriteInstance(stream, GeneratedEntityTypeIds.EntitySettingsHashmap, entitySettings, index: null);
		for (var index = 0; index < componentTypes.Count; index++)
		{
			WriteInstance(stream, componentTypes[index], Array.Empty<byte>(), checked((uint)(index + 1)));
		}
		WriteInstance(stream, GeneratedEntityTypeIds.UnitComponentData, unit, checked((uint)(componentTypes.Count + 1)));
		WriteInstance(stream, GeneratedEntityTypeIds.EncyclopediaEntryComponentData, encyclopedia, checked((uint)(componentTypes.Count + 2)));
		return stream.ToArray();
	}

	private static void WriteInstance(Stream stream, uint type, byte[] payload, uint? index)
	{
		using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
		if (index is uint value)
		{
			writer.Write(value);
		}
		writer.Write(type);
		writer.Write("LDLD"u8.ToArray());
		writer.Write(1u);
		writer.Write(type);
		writer.Write((uint)payload.Length);
		writer.Write(0u);
		writer.Write(0u);
		writer.Write(payload);
	}

	private static void WriteUInt16(byte[] buffer, int offset, ushort value)
		=> BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, sizeof(ushort)), value);

	private static void WriteUInt32(byte[] buffer, int offset, uint value)
		=> BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, sizeof(uint)), value);

	private static void WriteUInt64(byte[] buffer, int offset, ulong value)
		=> BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, sizeof(ulong)), value);
}
