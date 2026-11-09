using HD2ModCore.Domain;

namespace HD2ModCore.Application;

/// <summary>一个外部包的认领结局。三种状态互斥，且都要能被调用方记进日志。</summary>
public enum ExternalPackageAdoptionStatus
{
	/// <summary>已导入成受管节点，源目录已移进回收站。</summary>
	Adopted,

	/// <summary>刻意没动：判据判定不该认领（超限、junction、待删、已在库中、上一轮还没跑完…）。</summary>
	Skipped,

	/// <summary>动手了但失败了：源目录已保留，库不该出现半成品。</summary>
	Failed,
}

public sealed record ExternalPackageAdoptionOutcome(
	string RelativePath,
	ExternalPackageAdoptionStatus Status,
	IReadOnlyList<ModNodeId> NodeIds,
	string? Detail);

public sealed record ExternalPackageAdoptionResult(
	LibrarySnapshot Snapshot,
	IReadOnlyList<ExternalPackageAdoptionOutcome> Outcomes,
	IReadOnlyList<ModNodeId> ImportedNodeIds)
{
	/// <summary>这一轮是否真的改动了库（有包被认领）。</summary>
	public bool Changed => Outcomes.Any(outcome => outcome.Status == ExternalPackageAdoptionStatus.Adopted);
}

/// <summary>
/// 把 Mod 库根下「归档躺在子目录里」的外部包收编成受管节点。
/// <para>
/// 这些包来自别的 mod manager，重扫看不见它们（<c>ModLibrarySynchronizer</c> 只读节点根与
/// <c>sectional/</c>）。这里走的是**同一条导入链路**：manifest 的 Options 变成 Option 节点、
/// 无 manifest 的按子目录各成一个节点。区别只是源目录本来就在库根下，所以用硬链接省掉复制，
/// 导入完成后把源目录移进 <c>.hd2modmanager-trash</c>——不移走的话，每一轮重扫都会再导入一遍。
/// </para>
/// </summary>
public interface IExternalPackageAdoptionService
{
	ValueTask<ExternalPackageAdoptionResult> AdoptAsync(
		LibrarySnapshot snapshot,
		string modsRootDirectory,
		IReadOnlySet<string>? excludedRelativePaths = null,
		IProgress<string>? progress = null,
		CancellationToken cancellationToken = default);
}
