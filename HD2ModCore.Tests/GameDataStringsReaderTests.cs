using System.Buffers.Binary;
using System.Text;
using HD2GameItemAssets;

namespace HD2ModCore.Tests;

public sealed class GameDataStringsReaderTests
{
	[Fact]
	public void Read_UsesEmbeddedLanguageHashAndAbsoluteOffsets()
	{
		var bytes = BuildStrings(
			"cn",
			(0x100u, "简体名称"),
			(0x200u, "说明"));

		var table = GameDataStringsReader.Read(bytes, 0x1234);

		Assert.Equal(0x1234UL, table.FileId);
		Assert.Equal("cn", table.LanguageCode);
		Assert.Equal("zh-CN", table.Locale);
		Assert.Equal("简体名称", table.Entries[0x100]);
		Assert.Equal("说明", table.Entries[0x200]);
		Assert.Empty(table.Issues);
	}

	[Fact]
	public void Read_KeepsValidEntriesWhenOneOffsetIsInvalid()
	{
		var bytes = BuildStrings("us", (0x100u, "valid"), (0x200u, "also valid"));
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16 + 2 * 4 + 4, 4), uint.MaxValue);

		var table = GameDataStringsReader.Read(bytes);

		Assert.Equal("valid", table.Entries[0x100]);
		Assert.DoesNotContain(0x200u, table.Entries.Keys);
		Assert.Contains(table.Issues, issue => issue.Code == "StringOffsetOutOfRange");
	}

	[Fact]
	public void Read_ReportsConflictingDuplicateIdsAndRetainsFirstValue()
	{
		var bytes = BuildStrings("us", (0x100u, "first"), (0x100u, "second"));

		var table = GameDataStringsReader.Read(bytes);

		Assert.Equal("first", table.Entries[0x100]);
		Assert.Contains(table.Issues, issue => issue.Code == "DuplicateStringId");
	}

	[Fact]
	public void UnknownLanguageHash_IsPreservedWithoutPretendingToKnowLocale()
	{
		var table = GameDataStringsReader.Read(BuildStrings(0x12345678, (1u, "value")));

		Assert.Equal("hash-12345678", table.LanguageCode);
		Assert.Equal("x-hd2-12345678", table.Locale);
		Assert.Equal("value", table.Entries[1]);
	}

	private static byte[] BuildStrings(string languageCode, params (uint Id, string Value)[] entries)
		=> BuildStrings(
			unchecked((uint)(StingrayHash.Murmur64A(languageCode) >> 32)),
			entries);

	private static byte[] BuildStrings(uint languageHash, params (uint Id, string Value)[] entries)
	{
		const int headerSize = 16;
		var tableSize = checked(headerSize + entries.Length * 4 + entries.Length * 4);
		var encoded = entries.Select(entry => Encoding.UTF8.GetBytes(entry.Value + "\0")).ToArray();
		var bytes = new byte[tableSize + encoded.Sum(value => value.Length)];
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), GameDataStringsReader.Magic);
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), checked((uint)entries.Length));
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), languageHash);
		var offset = tableSize;
		for (var index = 0; index < entries.Length; index++)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(headerSize + index * 4, 4), entries[index].Id);
			BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(headerSize + entries.Length * 4 + index * 4, 4), checked((uint)offset));
			encoded[index].CopyTo(bytes, offset);
			offset += encoded[index].Length;
		}
		return bytes;
	}
}
