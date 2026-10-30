using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 并行度的默认值。
/// <para>
/// 这里测的不是"能不能并行"，而是<b>默认值别漂</b>。开满逻辑核在 16 核 / 32 线程的机器上
/// 会让 32 个 texconv 抢 16 个物理核，而症状只是"慢一点"——没有任何断言会失败，
/// 实测同一份 4K 材质包 51.4 s 对 50.2 s，人眼看不出来，只有这条测试能钉住。
/// </para>
/// </summary>
public sealed class VpkParallelTests
{
	[Theory]
	[InlineData(32, 16)]   // 7950X3D：16 核 32 线程 → 按物理核
	[InlineData(16, 8)]
	[InlineData(8, 4)]
	[InlineData(4, 2)]
	[InlineData(2, 2)]     // 双核不减半：本来就慢，减半只会更慢
	[InlineData(1, 1)]
	public void 默认并行度按物理核算(int logicalProcessors, int expected)
		=> Assert.Equal(expected, VpkParallel.ResolveDegree(null, logicalProcessors));

	[Theory]
	[InlineData("6", 6)]
	[InlineData("1", 1)]
	[InlineData("64", 64)]
	public void 环境变量可以覆盖默认值(string configured, int expected)
		=> Assert.Equal(expected, VpkParallel.ResolveDegree(configured, 32));

	[Theory]
	[InlineData("")]
	[InlineData("0")]
	[InlineData("-4")]
	[InlineData("abc")]
	public void 非法覆盖值退回默认值(string configured)
		=> Assert.Equal(16, VpkParallel.ResolveDegree(configured, 32));

	/// <summary>静态属性必须真的用上默认值——否则上面那几条测的是死代码。</summary>
	[Fact]
	public void Options用的是解析出来的并行度()
		=> Assert.Equal(
			VpkParallel.ResolveDegree(Environment.GetEnvironmentVariable(VpkParallel.DegreeVariable), Environment.ProcessorCount),
			VpkParallel.Options.MaxDegreeOfParallelism);
}
