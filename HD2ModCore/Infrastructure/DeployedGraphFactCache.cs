namespace HD2ModCore.Infrastructure;

// 作用：部署侧事实（整张 DeployedOverrideGraph）的进程内记忆，键是"这次构建会读到的每一样东西"的指纹。
// Purpose: The in-process memo for the deployed override graph, keyed by a fingerprint of every input the build reads.
//
// 为什么必须有它：DeployedOverrideGraphService.BuildAsync 会对**每个已部署 patch 文件**算两次 SHA-256
// （目标文件 + 激活状态里记录的源文件），再扫每个 base 的 TOC。而 CreateDeployedOverrideGraphService()
// 每次都 new 一个实例，护甲页 / GameData 浏览器页每开一次就把这些重算一遍——这是设计文档 §13.2
// 记录的性能热点之一。有了按指纹键控的记忆之后，"部署没变时二次打开零重算"是结构性的结果。
//
// Why it exists: the build hashes every deployed patch file twice (target plus recorded source) and scans each
// base's TOC, and the service is constructed fresh per call. Fingerprint-keyed memoization makes
// "no recompute on a second open when the deployment is unchanged" hold by construction.
//
// 契约与 GameDataFactCache 相同（设计文档 §20.3）：键即指纹、只是记忆不是真相、有界。
// 键的覆盖面见 DeployedOverrideGraphService.ComputeSourceCacheKey：激活状态内容 + 每个已部署文件的
// (名字, 长度, 修改时间) + 状态里每个源文件的 (存在性, 长度, 修改时间)。
// **源文件必须在键里**——否则"部署的 patch 没动、它引用的源被改了"会命中上一次的陈旧结论。
internal static class DeployedGraphFactCache
{
	// 部署图的条目本身很大，而实际工作集只有"一个游戏安装目录 × 一种激活状态"，取 4 足够。
	private static readonly SourceFingerprintMemo Memo = new(4);

	public static bool TryGet<T>(string key, out T value) => Memo.TryGet(key, out value);

	public static void Set<T>(string key, T value) => Memo.Set(key, value);
}
