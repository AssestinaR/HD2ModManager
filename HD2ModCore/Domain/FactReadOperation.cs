namespace HD2ModCore.Domain;

// 作用：一次底层事实读取的结果：数据、体量估算，以及可选的附加状态与问题。
// Purpose: Result of one low-level fact read: data, size estimate, and optional status/issues.
public sealed record FactReadOutcome<T>(
	T Data,
	long EstimatedBytes,
	ModInformationPropertyStatus Status = ModInformationPropertyStatus.Fresh,
	IReadOnlyList<CoreIssue>? Issues = null) where T : class;

// 作用：统一身份之外，一次事实读取还需要的东西——修订来源文件、实际读取动作，以及区分同一 kind 不同口径的键后缀。
// Purpose: What a fact read needs beyond its FactKey: the revision source file, the actual read, and a key suffix distinguishing different readings of the same kind.
//
// 说明：设计文档给出的 RequestAsync 形参只有 (FactKey, request)。读取器不是"按 kind 分派"的工厂，
// 它需要调用方把底层读取动作一起交进来，因此这里用 FactReadOperation 承载那部分参数。
public sealed record FactReadOperation<T>(
	string RevisionPath,
	Func<CancellationToken, Task<FactReadOutcome<T>>> Produce,
	string ScopeSuffix = "") where T : class;
