namespace HD2ModCore.Infrastructure;

// 作用：游戏数据侧事实的进程内记忆（映射结果 / 元数据目录），键完全由"来源指纹"构成。
// Purpose: The in-process memo for game-data facts (mapping result / metadata catalog), keyed by source fingerprint.
//
// 为什么必须是 static 而不是实例字段：CreateGameDataMappingFactsService(paths) 每次调用都 new 一个实例，
// 6 个消费者各持一份——实例级缓存等于零共享，加了也白加。
// Why static: the service is constructed fresh on every call, so a per-instance memo would share nothing.
//
// 契约（设计文档 §20.3 的硬约束，改动前请先读那一条）：
//   1. 键必须由来源指纹构成——来源一变，键就变，旧值自然不可达。因此"游戏更新了却还返回旧数据"在结构上不可能发生。
//   2. 它只是记忆，不是真相。任何时刻清空它，下一次请求都会回到磁盘与加密归档重新读取。
//      重建路径（AssetArchiveIndexService.BuildOrRebuildAsync）**不读**这里，永远直接从游戏目录重扫。
//   3. 有界：条目数达到上限时整体清空。这是缓存，丢光只是慢一点，不会丢数据。
//
// Contract: keys are source fingerprints (so a changed source can never hit a stale entry); the store is
// only a memo (dropping everything costs time, never data); and it is bounded.
//
// 与部署侧（DeployedGraphFactCache）**分成两份**：部署图的体积与访问节奏和映射结果差很多，
// 合用一个字典会让部署图把映射结果挤出去。分开后各自的预算才稳定。
internal static class GameDataFactCache
{
	// 上限取小值：这里缓存的是"整包映射结果 / 整个元数据目录"这类大对象，
	// 宁可偶尔清空重算，也不要让进程内存随使用时长增长。
	private static readonly SourceFingerprintMemo Memo = new(16);

	public static bool TryGet<T>(string key, out T value) => Memo.TryGet(key, out value);

	public static void Set<T>(string key, T value) => Memo.Set(key, value);
}
