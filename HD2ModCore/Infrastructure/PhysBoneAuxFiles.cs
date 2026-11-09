namespace HD2ModCore.Infrastructure;

/// <summary>
/// 物理参数旁车（<c>physbone-*.bin</c>）的命名规则。
/// <para>
/// 这些文件是**转换期的附加产出**：既不是 patch，也不是 patch 的
/// <c>.gpu_resources</c> / <c>.stream</c>，它们按名字直接躺在载荷目录顶层
/// （见 <c>PhysBoneRigSidecar</c>）。
/// </para>
/// <para>
/// <b>为什么要有这条规则。</b>凡是"按固定名单搬运文件"的地方都得认得它们，
/// 名单里漏一个就会被**静默丢掉**——整条链上没有任何报错，只是"游戏里没效果"。
/// 2026-11-09 踩过一次：<see cref="VpkPayloadCollector"/> 的结构文件名单里只有
/// <c>physbone-rig.bin</c>，后来新增的 driver rig 被留在转换目录里，
/// 容器永远只有一条记录。所以这里按**前缀**认，以后再加旁车不必改任何名单。
/// </para>
/// </summary>
internal static class PhysBoneAuxFiles
{
	private const string Prefix = "physbone-";
	private const string Suffix = ".bin";

	/// <summary>这个文件名是不是物理旁车（只看前缀与扩展名，不看具体是哪一个）。</summary>
	public static bool IsAuxFile(string fileName)
		=> fileName.Length > Prefix.Length + Suffix.Length
			&& fileName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
			&& fileName.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);
}
