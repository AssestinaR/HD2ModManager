using HD2ModAdaptation.PatchReconstruction;
using HD2SourceModel;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 物理参数容器在"重新构建 patch"过程中的搬运（<see cref="PhysBoneCarrier"/>）。
/// <para>
/// 钉住的是**不会报错只会静默丢参数**的那几条：
/// 取不到容器、剥不干净（重复附加会越堆越大）、以及搬过去之后读不回来。
/// 这些都是纯字节逻辑，不需要游戏数据，所以能在 CI 里跑。
/// </para>
/// </summary>
public sealed class PhysBoneCarrierTests
{
	private static byte[] Rig(int size, byte seed = 0x5A)
	{
		var data = new byte[size];
		for (var i = 0; i < size; i++)
			data[i] = (byte)(seed + i);
		// 头部写成协议要求的 HD2SRG01，方便"看起来像真的 rig"
		"HD2SRG01"u8.CopyTo(data);
		return data;
	}

	private static byte[] Payload(int size, byte seed = 0x11) => Enumerable.Range(0, size).Select(i => (byte)(seed ^ i)).ToArray();

	[Fact]
	public void ExtractContainer_ReturnsNullForPlainCarrierWithoutFooter()
	{
		Assert.Null(PhysBoneCarrier.ExtractContainer(Payload(4096)));
	}

	[Fact]
	public void AppendThenExtract_RoundTripsTheContainerBytes()
	{
		var rig = Rig(9970);
		var container = PhysBoneContainer.Build([new PhysBoneContainer.Entry(PhysBoneCarrier.RigRecordName, rig)]);
		var carrier = Payload(65536);

		var combined = PhysBoneCarrier.Append(carrier, container);

		Assert.Equal(carrier.Length + container.Length, combined.Length);
		var extracted = PhysBoneCarrier.ExtractContainer(combined);
		Assert.NotNull(extracted);
		Assert.Equal(container.Length, extracted!.Length);
		Assert.Equal(container, extracted);
		Assert.Equal(rig.Length, PhysBoneCarrier.ReadRigRecordLength(combined));
		// 前缀必须原样保留：容器只能动尾巴，绝不能碰到受引用的数据
		Assert.Equal(carrier, combined.AsSpan(0, carrier.Length).ToArray());
	}

	[Fact]
	public void AppendIsIdempotent_StripRemovesOnlyTheTail()
	{
		var rig = Rig(1024);
		var container = PhysBoneContainer.Build([new PhysBoneContainer.Entry(PhysBoneCarrier.RigRecordName, rig)]);
		var carrier = Payload(8192);

		var once = PhysBoneCarrier.Append(carrier, container);
		var twice = PhysBoneCarrier.Append(once, container);

		// 第二次追加必须先剥掉旧的，否则文件会一层层长大（线上表现为 patch 越导越大）
		Assert.Equal(once.Length, twice.Length);
		Assert.Equal(once, twice);
		Assert.Equal(carrier, PhysBoneCarrier.Strip(once));
	}

	[Fact]
	public void Strip_LeavesPlainCarrierUntouched()
	{
		var carrier = Payload(2048);
		Assert.Equal(carrier, PhysBoneCarrier.Strip(carrier));
	}

	[Fact]
	public void ReadRigRecordLength_ReturnsMinusOneWhenRecordMissing()
	{
		var container = PhysBoneContainer.Build([new PhysBoneContainer.Entry("some_other_record.bin", Rig(64))]);
		var combined = PhysBoneCarrier.Append(Payload(1024), container);
		Assert.Equal(-1, PhysBoneCarrier.ReadRigRecordLength(combined));
	}
}
