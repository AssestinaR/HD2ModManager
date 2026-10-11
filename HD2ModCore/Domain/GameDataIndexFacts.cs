namespace HD2ModCore.Domain;

// 作用：资产索引这一"无节点事实"的产物——索引指纹 + 全部归档摘要。
// Purpose: The node-less asset-index fact: the index fingerprint plus every archive summary.
//
// 之所以把它包成一个值：这两样东西是**同一次读取**的产物（指纹决定摘要是否还新鲜），
// 拆成两个请求就会让"指纹是新的、摘要是旧的"成为可能。设计文档 §22。
public sealed record GameDataIndexFacts(
	GameDataIndexFingerprint Fingerprint,
	IReadOnlyList<GameDataArchiveSummary> Archives);
