using System.Threading.Tasks;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// VPK 转换里那几处"逐项独立、每项都很重"的并行循环共用的并行参数。
/// <para>
/// <b>为什么要显式封顶。</b>这些循环体里都在<b>阻塞地等一个 texconv 子进程</b>
/// （<see cref="Bc7Encoder"/> 的契约就是不重定向 stdio，见那里的注释）。线程池发现线程被阻塞后
/// 会持续注入新线程，实测 62 张贴图跑出了 <b>33 个并发 texconv</b>——每个 texconv 都是 1 核的重活，
/// 超订之后互相踩，4K 材质包反而只快 2.7 倍（52 s，而按核数算应该是十几秒）。
/// </para>
/// <para>
/// <b>为什么是"物理核"而不是"逻辑核"。</b><see cref="Environment.ProcessorCount"/> 报的是逻辑核
/// （7950X3D 报 32），而 texconv 编 BC7 是纯计算，开满 32 路只会让 16 个物理核互相抢。
/// 实测同一份 4K 材质包：32 路 51.4 s，16 路 50.2 s，8 路 58.1 s——再往上没有收益，
/// 因为 BC7 编码受内存带宽限制，不是受核数限制。
/// </para>
/// </summary>
public static class VpkParallel
{
	/// <summary>覆盖并行度的环境变量。机器差异太大，硬编码一个数字总有一边是错的。</summary>
	public const string DegreeVariable = "HD2_VPK_PARALLEL";

	/// <summary>逐项并行的默认参数：并发上限 = 物理核数（见类型注释里的实测）。</summary>
	public static ParallelOptions Options { get; } = new()
	{
		MaxDegreeOfParallelism = ResolveDegree(
			Environment.GetEnvironmentVariable(DegreeVariable),
			Environment.ProcessorCount),
	};

	/// <summary>
	/// 算出并行度。抽成纯函数是为了能测：<see cref="Options"/> 是静态只读的，
	/// 而"默认值"这件事一旦漂回"开满逻辑核"，症状只是慢一点——没人会注意到。
	/// </summary>
	/// <param name="configured">环境变量里配的值；非正整数视为没配。</param>
	/// <param name="logicalProcessors">逻辑核数（<see cref="Environment.ProcessorCount"/>）。</param>
	public static int ResolveDegree(string? configured, int logicalProcessors)
	{
		if (int.TryParse(configured, out var parsed) && parsed > 0)
			return parsed;

		var logical = Math.Max(1, logicalProcessors);
		// 有 SMT 的机器上逻辑核是物理核的两倍；按物理核开，正好一核一个 texconv。
		// 双核以下不减半——那种机器本来就慢，减半只会更慢。
		return logical <= 2 ? logical : Math.Max(2, logical / 2);
	}
}
