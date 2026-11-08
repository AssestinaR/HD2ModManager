using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using AdaptEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;
using AdaptPayload = HD2ModAdaptation.PatchReconstruction.PatchEntryPayload;

namespace HD2ModCore.Infrastructure.Sectional;

/// <summary>
/// 一份"画不出东西"的 unit 载荷：TocData + GpuData。
/// <para>
/// 后四个字段是 patch 条目头里的原值（<c>Unknown1..4</c>）。它们是**条目级**的，不能写 0——
/// 写 0 会让这条条目与同 patch 里别的 unit 长得不一样，而游戏按这些字段解释载荷。
/// 所以隐藏壳一律沿用底座条目的值。
/// </para>
/// <param name="MeshCount">这份占位里有几个 mesh（每个 mesh 恰好 1 个三角形）。</param>
/// </summary>
internal sealed record SectionalHiddenUnitPayload(
	byte[] TocData,
	byte[] GpuResourceData,
	int MeshCount,
	ulong Unknown1,
	ulong Unknown2,
	uint Unknown3,
	uint Unknown4);

/// <summary>
/// 隐藏壳的载荷来源。**只要一份**——所有要隐藏的目标 unit 都复用它，只改 key。
/// <para>
/// 抽成接口只有一个理由：万一"一份模板复用 N 次"在实机上有问题，能换成别的来源
/// 而不动编译器主体。
/// </para>
/// </summary>
internal interface ISectionalHiddenUnitSource
{
	/// <summary>用底座条目造一份极小占位的 unit 载荷（已含 minify）。</summary>
	SectionalHiddenUnitPayload Create(AdaptEntry entry, AdaptPayload payload);
}

/// <summary>
/// 默认实现：拿**我们自己的一个来源 unit** 做底，把模型换成极小三角片（3 顶点 / 1 个三角形）。
/// <para>
/// <b>为什么用来源 unit 而不是内置模板。</b>来源是本次转换刚产出的：unit version、骨骼、
/// 材质 id 全都与这份 patch 自洽；游戏更新改了 unit 结构，下次转换出来的就是新的，隐藏壳自动跟上。
/// 内置的 <c>WardrobeHiddenUnitTemplate</c>（4.2 KB，2026-09-28 冻结）只能当兜底，而且它还在被
/// 旧衣柜运行时的部署链路使用，不能删。
/// </para>
/// <para>
/// <b><c>minifyCullingMeshes: true</c> 是强制的。</b>那个开关为 <c>false</c> 时，
/// <c>lod == -1</c> 的网格会被**原样保留**——而我们的每个 unit 都带一个 <c>lod=-1</c> 网格。
/// 不传 <c>true</c>，隐藏壳会画出**一整个我们的模型**。
/// </para>
/// </summary>
internal sealed class SourceUnitHiddenUnitSource : ISectionalHiddenUnitSource
{
	private readonly UnitMeshReader meshReader = new();

	public SectionalHiddenUnitPayload Create(AdaptEntry entry, AdaptPayload payload)
	{
		ArgumentNullException.ThrowIfNull(entry);
		ArgumentNullException.ThrowIfNull(payload);

		var model = meshReader.Read(payload.TocData, payload.GpuResourceData);
		var self = new GameDataUnitMesh(entry.AssetKey, entry.SourceFileName, payload, model);
		var hidden = new CanonicalHiddenUnitBuilder()
			.Build(self, UnitTransformInfo.Empty, minifyCullingMeshes: true);

		return new SectionalHiddenUnitPayload(
			hidden.Entry.EffectiveTocData,
			hidden.Entry.EffectiveGpuData,
			hidden.HiddenMeshCount,
			entry.Unknown1,
			entry.Unknown2,
			entry.Unknown3,
			entry.Unknown4);
	}
}
