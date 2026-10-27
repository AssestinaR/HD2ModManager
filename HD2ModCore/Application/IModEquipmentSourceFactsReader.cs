using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：组合统一 Mod 信息、Patch/Unit 读取和 GameData 部件映射，提供装备来源事实。
// Purpose: Composes Mod facts, unified Patch/Unit reads, and GameData part mapping for equipment sources.
public interface IModEquipmentSourceFactsReader
{
	ValueTask<ModEquipmentSourceFacts> ReadAsync(
		ModNode source,
		string modsRootDirectory,
		ModInformationRequest request,
		CancellationToken cancellationToken = default);

	/// <param name="includeUnitGeometry">
	/// false = 只读清单级事实（来源 Patch 路径、TOC、装备目录、Patch 里的全部 Unit 键），不解析网格。
	/// 页面靠这一步瞬间打开；true 才逐个解析 Unit 判断可转移性。
	/// </param>
	ValueTask<ModEquipmentSourceFacts> ReadAsync(
		ModNode source,
		string modsRootDirectory,
		ModInformationRequest request,
		bool includeUnitGeometry,
		CancellationToken cancellationToken = default);

	void ClearOperation(Guid operationId);
}
