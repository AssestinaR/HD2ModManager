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

	// 这一条盯的是"异常不再被当成控制流"。
	//
	// 载荷带 3792 字节头部时，读取器要逐个偏移试到第 3793 个起点才成功；
	// 以前每次失败都 throw 一次，于是**一次刷新就抛几千个异常**——
	// 调试器会把每一个都打印出来，界面直接卡死（真机症状）。
	// 现在整条路径一个异常都没有：失败只是 false。
	[Fact]
	public void TryReadArchiveByPath_FindsThePayloadBehindAHeader_WithoutThrowing()
	{
		var payload = BuildPayload([(0x1111111111111111UL, 0x2222222222222222UL)]);
		var header = new byte[3792];
		for (var index = 0; index < header.Length; index++) header[index] = (byte)(index * 31 + 7);
		var withHeader = header.Concat(payload).ToArray();

		Assert.True(GameDataHashLookupReader.TryReadArchiveByPath(withHeader, out var map, out var error));
		Assert.Null(error);
		Assert.Equal(0x2222222222222222UL, map[0x1111111111111111UL]);
	}

	[Fact]
	public void TryReadArchiveByPath_ReportsWhyItFailed()
	{
		var payload = BuildPayload([(0x1111111111111111UL, 0x2222222222222222UL)]);

		Assert.False(GameDataHashLookupReader.TryReadArchiveByPath(payload[..^4], out var map, out var error));
		Assert.Empty(map);
		Assert.False(string.IsNullOrWhiteSpace(error));
	}

	[Fact]
	public void TryReadArchiveByPath_OnGarbage_ReportsTheSafetyLimitItHit()
	{
		// 全是 0xFF 的载荷：第一个 "count" 就是个天文数字。这条同时证明
		// 失败原因带的是**第一个起点**的，是最有信息量的那一个。
		var garbage = Enumerable.Repeat((byte)0xFF, 4096).ToArray();

		Assert.False(GameDataHashLookupReader.TryReadArchiveByPath(garbage, out _, out var error));
		Assert.Contains("exceeds the safety limit", error, StringComparison.Ordinal);
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
