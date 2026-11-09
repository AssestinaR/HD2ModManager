using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 「批量重构要动哪些 Mod」的纯逻辑：**作用域展开** + **sectional 系列判据**。
///
/// <para>
/// 抽出来的原因：这两件事都不该由某个界面组件决定。界面只负责"用户选了哪几个"，
/// 剩下"它们连同后代里谁真的要重构"由这里回答——只有一份实现，才不会出现
/// "一键重构"与"重构选中"给出不同目标集合。
/// </para>
///
/// <para>
/// 放在 Core 而不是管理器：它只依赖 <see cref="LibrarySnapshot"/> 与目录，可以离线测。
/// </para>
/// </summary>
public static class RebuildTargetScope
{
	/// <summary>
	/// 把"用户选中的 guid"展开成"选中的节点 + 它所有后代选项"。
	/// <para>
	/// 后代关系来自 <see cref="OptionRelationshipGraph"/>：选项是独立目录，父子关系只记在
	/// <c>HostModGuids</c> 里。它自带环保护与去重，这里不再写一套遍历。
	/// </para>
	/// <para>
	/// 逻辑容器（空壳）自身会被包含进集合，但它没有载荷、也不通过
	/// <see cref="IsSectionalSeries"/>，所以不会成为目标——它的 sectional 子级会。
	/// </para>
	/// </summary>
	/// <param name="scopeIds">节点 guid（<c>N</c> 格式）。<c>null</c> = 全库（返回 <c>null</c>）。</param>
	public static HashSet<ModNodeId>? Expand(LibrarySnapshot snapshot, IEnumerable<string>? scopeIds)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if (scopeIds is null) return null;

		var roots = new List<ModNodeId>();
		foreach (var raw in scopeIds)
		{
			if (!Guid.TryParse(raw, out var id)) continue;
			var nodeId = new ModNodeId(id);
			if (snapshot.Nodes.ContainsKey(nodeId)) roots.Add(nodeId);
		}

		if (roots.Count == 0) return [];

		var graph = OptionRelationshipGraph.Create(snapshot);
		var scope = new HashSet<ModNodeId>();
		foreach (var root in roots)
		{
			scope.Add(root);
			foreach (var descendant in graph.GetDescendantOptions(root)) scope.Add(descendant.Id);
		}

		return scope;
	}

	/// <summary>这个目录是不是「sectional 系列」（<c>Sectional</c> 或 <c>Vpk2Patch</c>）。</summary>
	public static bool IsSectionalSeries(string modDirectory, IModStorageKindResolver resolver)
	{
		ArgumentNullException.ThrowIfNull(resolver);
		if (string.IsNullOrWhiteSpace(modDirectory)) return false;
		return resolver.Resolve(modDirectory) is ModStorageKind.Sectional or ModStorageKind.Vpk2Patch;
	}
}
