using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// <see cref="SectionalSplitter.SelectSourcePatchAsync"/> 的契约。
/// <para>
/// 挑错 patch 的后果很具体：拆件会去覆盖一个不含几何的附属 patch，产物里几何没了、
/// 附属成员也被打乱。所以这里把"空表要报错""只有一个就原样返回"两条先钉住；
/// 多 patch 的挑选规则（unit 最多的那个）需要真实 patch 文件，在 V0 离线验证里覆盖。
/// </para>
/// </summary>
public sealed class SectionalSplitterSourceSelectionTests
{
	[Fact]
	public async Task SelectSourcePatchAsync_Empty_Throws()
	{
		await Assert.ThrowsAsync<InvalidOperationException>(
			async () => await SectionalSplitter.SelectSourcePatchAsync([]));
	}

	[Fact]
	public async Task SelectSourcePatchAsync_SingleCandidate_ReturnsItWithoutScanning()
	{
		// 路径故意不存在：一个候选时不该去打开文件（省一次扫描，也不该因为文件缺失而失败）。
		var path = Path.Combine(Path.GetTempPath(), "hd2-split-single-" + Guid.NewGuid().ToString("N") + ".patch_0");

		var selected = await SectionalSplitter.SelectSourcePatchAsync([path]);

		Assert.Equal(path, selected);
	}
}
