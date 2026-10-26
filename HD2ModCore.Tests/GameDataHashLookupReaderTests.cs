using System.Buffers.Binary;
using HD2GameItemAssets;

namespace HD2ModCore.Tests;

/// <summary>
/// hash_lookup 的载荷前面可能带一段未公开的头部。实测 0x7056bc19c69f0f07 的 TocData
/// 前 3792 字节是头部，直接解析会读到底部的 0 而不是结束标记，于是整块
/// path→Archive 映射静默消失，依赖它的武器/载具定制名称全部丢失。
/// </summary>
public sealed class GameDataHashLookupReaderTests
{
	[Fact]
	public void ReadArchiveByPath_ParsesPlainPayload()
	{
		var payload = BuildPayload([(0x1111111111111111UL, 0x2222222222222222UL)]);

		var map = GameDataHashLookupReader.ReadArchiveByPath(payload);

		Assert.Single(map);
		Assert.Equal(0x2222222222222222UL, map[0x1111111111111111UL]);
	}

	[Fact]
	public void ReadArchiveByPath_SkipsUnknownHeaderPrefix()
	{
		// 头部内容不重要——真正要证明的是"解析失败时会向后找载荷起点"。
		var payload = BuildPayload(
		[
			(0x1111111111111111UL, 0x2222222222222222UL),
			(0x3333333333333333UL, 0x4444444444444444UL),
		]);
		var header = new byte[3792];
		for (var index = 0; index < header.Length; index++)
		{
			header[index] = (byte)(index * 31 + 7);
		}

		var withHeader = header.Concat(payload).ToArray();
		var map = GameDataHashLookupReader.ReadArchiveByPath(withHeader);

		Assert.Equal(2, map.Count);
		Assert.Equal(0x2222222222222222UL, map[0x1111111111111111UL]);
		Assert.Equal(0x4444444444444444UL, map[0x3333333333333333UL]);
	}

	[Fact]
	public void ReadArchiveByPath_RejectsTruncatedPayload()
	{
		var payload = BuildPayload([(0x1111111111111111UL, 0x2222222222222222UL)]);

		Assert.Throws<InvalidDataException>(() => GameDataHashLookupReader.ReadArchiveByPath(payload[..^4]));
	}

	private static byte[] BuildPayload(IReadOnlyList<(ulong Path, ulong Archive)> entries)
	{
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter(stream);

		writer.Write(0u); // parent collection count
		writer.Write(0u); // hash collection 1
		writer.Write(0u); // hash collection 2
		writer.Write(0u); // mapping collection 1
		writer.Write(0u); // hash collection 3
		writer.Write(0u); // type marker
		writer.Write(0u); // thin mapping collection
		writer.Write(0u); // thin hash collection
		writer.Write(0u); // hash collection 4
		writer.Write(0u); // mapping collection 2
		writer.Write(0u); // lookup tree collection 1
		writer.Write(0u); // mapping collection 3
		writer.Write(0u); // lookup tree collection 2
		writer.Write(0u); // mapping collection 4
		writer.Write(0u); // lookup tree collection 3

		writer.Write((uint)entries.Count);
		foreach (var entry in entries)
		{
			writer.Write(entry.Path);
			writer.Write(entry.Archive);
		}

		writer.Write(0u); // lookup tree collection 4
		writer.Write(0xDEADBEE7u);
		writer.Flush();
		return stream.ToArray();
	}
}
