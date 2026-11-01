using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HD2SourceModel.Tests;

/// <summary>
/// <c>HD2PBX01</c> 容器的字节布局回归。
/// <para>
/// 为什么要按偏移逐字段断言：这个容器是**跨语言契约**——C# 写、Lua/插件读。
/// 只测"自己写的自己读得回来"是假阳性（两边一起错就测不出来），
/// 所以这里直接钉死协议表里的每个偏移，并独立重算两处 SHA-256。
/// 协议见 <c>docs/hd2physbone-embedded-patch-protocol.md</c>。
/// </para>
/// </summary>
public sealed class PhysBoneContainerTests
{
	private static readonly byte[] Payload1 = Encoding.UTF8.GetBytes("spring-rig-payload");
	private static readonly byte[] Payload2 = [0x01, 0x02, 0x03, 0x04, 0x05];

	[Fact]
	public void Build_WritesHeaderIndexPayloadAndFooterAtTheDocumentedOffsets()
	{
		var container = PhysBoneContainer.Build([new PhysBoneContainer.Entry("hd2_spring_rig.bin", Payload1)]);

		// Header
		Assert.Equal("HD2PBX01"u8.ToArray(), container[..8]);
		Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(0x08)));
		Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(0x0C))); // bit0 = footer 存前缀长度
		Assert.Equal((ulong)container.Length, BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(0x10)));
		Assert.Equal(48ul, BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(0x18))); // 索引紧随 header
		Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(0x28)));

		var indexLength = BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(0x20));
		Assert.Equal(0x38ul + (ulong)"hd2_spring_rig.bin"u8.Length, indexLength);

		// Record（固定 0x38 字节 + 名字）
		var nameBytes = Encoding.UTF8.GetBytes("hd2_spring_rig.bin");
		Assert.Equal((ushort)nameBytes.Length, BinaryPrimitives.ReadUInt16LittleEndian(container.AsSpan(0x30)));
		var dataOffset = BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(0x30 + 0x04));
		var dataLength = BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(0x30 + 0x0C));
		Assert.Equal((ulong)Payload1.Length, dataLength);

		// 名称长度在 0x34（相对记录头）重复保存一次，且必须与前者一致。
		Assert.Equal((uint)nameBytes.Length, BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(0x30 + 0x34)));

		// 载荷内容与记录里声明的 SHA-256
		Assert.Equal(Payload1, container.AsSpan((int)dataOffset, (int)dataLength).ToArray());
		var declaredDigest = container.AsSpan(0x30 + 0x14, 32).ToArray();
		Assert.Equal(SHA256.HashData(Payload1), declaredDigest);

		// Footer
		var footerAt = container.Length - 56;
		Assert.Equal("HD2PBFT1"u8.ToArray(), container[footerAt..(footerAt + 8)]);
		Assert.Equal((ulong)container.Length,
			BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(footerAt + 0x10)));
	}

	[Fact]
	public void Build_ComputesTheContainerDigestOverTheWholeContainerWithTheDigestFieldZeroed()
	{
		var container = PhysBoneContainer.Build([new PhysBoneContainer.Entry("a.bin", Payload1)]);

		var footerAt = container.Length - 56;
		var expected = (byte[])container.Clone();
		expected.AsSpan(footerAt + 0x18, 32).Clear();

		Assert.Equal(SHA256.HashData(expected), container.AsSpan(footerAt + 0x18, 32).ToArray());
	}

	[Fact]
	public void Build_KeepsEveryRecordAndPayloadInOrder()
	{
		var entries = new[]
		{
			new PhysBoneContainer.Entry("lua_units.txt", Encoding.UTF8.GetBytes("0x1\n")),
			new PhysBoneContainer.Entry("hd2_spring_rig.bin", Payload1),
			new PhysBoneContainer.Entry("hd2_ib_needle.bin", Payload2),
		};

		var container = PhysBoneContainer.Build(entries);
		Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(0x28)));

		// 顺着索引把三条记录全走一遍：偏移递增、名字按传入顺序、载荷逐字节正确。
		var at = 48;
		var previousEnd = 0;
		foreach (var entry in entries)
		{
			var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(container.AsSpan(at));
			var offset = (int)BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(at + 0x04));
			var length = (int)BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(at + 0x0C));
			var name = Encoding.UTF8.GetString(container.AsSpan(at + 0x38, nameLength));

			Assert.Equal(entry.Name, name);
			Assert.Equal(entry.Data.Length, length);
			Assert.Equal(entry.Data, container.AsSpan(offset, length).ToArray());
			Assert.True(offset >= previousEnd, "记录的数据区必须单调向后排列");
			previousEnd = offset + length;
			at += 0x38 + nameLength;
		}
	}

	[Fact]
	public void AppendTo_RecordsTheOriginalLengthSoReadersCanFindTheContainerFromEof()
	{
		var carrier = new byte[1024];
		Random.Shared.NextBytes(carrier);

		var combined = PhysBoneContainer.AppendTo(carrier, [new PhysBoneContainer.Entry("a.bin", Payload1)]);

		// 载体原样保留
		Assert.Equal(carrier, combined.AsSpan(0, carrier.Length).ToArray());

		// 读取方从 EOF 回读 footer，用「文件长度 - 容器长度」求起点——不看绝对偏移。
		var footerAt = combined.Length - 56;
		var containerLength = (int)BinaryPrimitives.ReadUInt64LittleEndian(combined.AsSpan(footerAt + 0x10));
		var start = combined.Length - containerLength;
		Assert.Equal(carrier.Length, start);
		Assert.Equal("HD2PBX01"u8.ToArray(), combined[start..(start + 8)]);

		// flags bit0 开启时，footer 那 8 字节保存的正是原始前缀长度。
		Assert.Equal((ulong)carrier.Length,
			BinaryPrimitives.ReadUInt64LittleEndian(combined.AsSpan(footerAt + 0x08)));
	}

	[Fact]
	public void AppendTo_LeavesTheCarrierItselfByteIdentical()
	{
		var carrier = Encoding.UTF8.GetBytes("gpu-sidecar-contents");
		var combined = PhysBoneContainer.AppendTo(carrier, [new PhysBoneContainer.Entry("a.bin", Payload2)]);
		Assert.True(combined.AsSpan(0, carrier.Length).SequenceEqual(carrier));
		Assert.True(combined.Length > carrier.Length);
	}

	[Fact]
	public void StripIfPresent_RemovesExactlyOneLayerSoRepeatedConversionDoesNotStack()
	{
		var carrier = Encoding.UTF8.GetBytes("original-sidecar");
		var once = PhysBoneContainer.AppendTo(carrier, [new PhysBoneContainer.Entry("a.bin", Payload1)]);

		// 重复转换是真实会发生的事（同一输出目录重跑）：不先去重就会叠成两层。
		var stripped = PhysBoneContainer.StripIfPresent(once, out var didStrip).ToArray();
		Assert.True(didStrip);
		Assert.Equal(carrier, stripped);

		// 去重之后再追加，结果必须与只追加一次逐字节相同。
		var again = PhysBoneContainer.AppendTo(stripped, [new PhysBoneContainer.Entry("a.bin", Payload1)]);
		Assert.Equal(once, again);
	}

	[Fact]
	public void StripIfPresent_LeavesCleanCarriersAlone()
	{
		var carrier = Encoding.UTF8.GetBytes("no container here");
		var result = PhysBoneContainer.StripIfPresent(carrier, out var didStrip);
		Assert.False(didStrip);
		Assert.Equal(carrier, result.ToArray());
	}

	[Fact]
	public void StripIfPresent_RefusesToTruncateWhenTheFooterIsNotSelfConsistent()
	{
		// footer magic 在、但长度比文件还大：宁可当作"没有容器"也不要裁掉数据。
		var carrier = new byte[512];
		Random.Shared.NextBytes(carrier);
		PhysBoneContainer.FooterMagic.CopyTo(carrier.AsSpan(512 - 56));
		BinaryPrimitives.WriteUInt64LittleEndian(carrier.AsSpan(512 - 56 + 0x10), ulong.MaxValue);

		var result = PhysBoneContainer.StripIfPresent(carrier, out var didStrip);
		Assert.False(didStrip);
		Assert.Equal(carrier, result.ToArray());
	}

	[Fact]
	public void Build_RejectsNothingSilently_EmptyEntryListStillProducesAValidContainer()
	{
		// 没有资源时也要是一个合法容器（只有 header + 空索引 + footer），
		// 否则读取方会在"零长度索引"上算错起点。
		var container = PhysBoneContainer.Build([]);
		Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(0x28)));
		Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(0x20)));
		Assert.Equal(48 + 56, container.Length);
		Assert.Equal("HD2PBFT1"u8.ToArray(), container[(container.Length - 56)..(container.Length - 48)]);
	}
}
