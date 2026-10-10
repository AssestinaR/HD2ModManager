using HD2ModAdaptation.PatchReconstruction.PatchWorkspace;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using AdaptationPatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;

namespace HD2ModCore.Application;

// 作用：统一提供 Patch 元数据、Payload 和 Unit 结构读取；业务不应自行 new 底层 reader。
// Purpose: Provides one read boundary for Patch metadata, payloads, and Unit structures.
public interface IModInformationReader : IAsyncDisposable
{
	// 作用：统一入口——按 FactKey 取得一个事实。下面 5 个具名读取方法全部转发到这里。
	// Purpose: The single entry point — obtains a fact by FactKey. The five named reads below all forward here.
	ValueTask<ModInformationPropertyResult<T>> RequestAsync<T>(
		FactKey key,
		ModInformationReadRequest request,
		FactReadOperation<T> operation,
		CancellationToken cancellationToken = default) where T : class;

	ValueTask<ModInformationPropertyResult<PatchWorkspaceIndex>> ReadPatchIndexAsync(
		ModInformationReadRequest request,
		CancellationToken cancellationToken = default);

	ValueTask<ModInformationPropertyResult<HD2ModCore.Domain.PatchEntryPayload>> ReadPatchPayloadAsync(
		AdaptationPatchTocEntry entry,
		ModInformationReadRequest request,
		CancellationToken cancellationToken = default);

	ValueTask<ModInformationPropertyResult<PatchUnitMesh>> ReadUnitAsync(
		AdaptationPatchTocEntry entry,
		IReadOnlyList<AdaptationPatchTocEntry>? patchEntries,
		PatchUnitDependencyPolicy dependencyPolicy,
		ModInformationReadRequest request,
		bool canonicalSource = false,
		CancellationToken cancellationToken = default);

	ValueTask<ModInformationPropertyResult<ModUnitStructureSummary>> ReadUnitSummaryAsync(
		AdaptationPatchTocEntry entry,
		IReadOnlyList<AdaptationPatchTocEntry>? patchEntries,
		PatchUnitDependencyPolicy dependencyPolicy,
		ModInformationReadRequest request,
		CancellationToken cancellationToken = default);

	ValueTask<ModInformationPropertyResult<ModSourceUnitFactsSnapshot>> ReadSourceUnitFactsAsync(
		PatchWorkspaceIndex index,
		ModInformationReadRequest request,
		PatchUnitDependencyPolicy dependencyPolicy = PatchUnitDependencyPolicy.RequirePatchLocalComposite,
		CancellationToken cancellationToken = default);

	void InvalidateNode(ModNodeId nodeId);
	void ClearOperation(Guid operationId);
	void ClearSession(Guid sessionId);
}
