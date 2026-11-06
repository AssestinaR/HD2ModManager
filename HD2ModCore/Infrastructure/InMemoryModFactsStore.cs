using HD2ModAdaptation.Analysis;
using HD2ModCore.Application;
using HD2ModCore.Domain;
// 两个同名类型在这里都要用：查询接口用 Adaptation 的 AssetKey，投影用 Domain 的。
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using DomainAssetKey = HD2ModCore.Domain.AssetKey;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// Mod 的资产与引用事实，**只活在这次会话的内存里**。
/// <para>
/// <b>为什么不需要落盘。</b>这些事实完全由 Mod 的 patch 文件算出来，删掉重算即可——
/// 它们不是用户输入。为一个"随时可以重算"的东西维护一套 SQLite schema、迁移版本、
/// 损坏恢复与 21.7 MB 的库文件，成本远大于收益。
/// </para>
/// <para>
/// <b>代价是启动后要重算一次</b>（实测约 100 ms/Mod）。这是刻意的取舍：拿一次可接受的计算，
/// 换掉一个会损坏、会过期、会和 schema 版本打架的持久层。
/// </para>
/// </summary>
public sealed class InMemoryModFactsStore : IReferenceGraphQueryIndex, IReferenceGraphIndexWriter, IModDerivedDataCleanup
{
	private sealed record NodeEntry(
		IReadOnlyList<(string GroupId, AdaptationAssetKey Key)> Assets,
		IReadOnlyList<(string GroupId, PatchAssetReference Reference)> References);

	/// <summary>
	/// 同一个管理器目录共用一个实例。
	/// <para>
	/// <b>为什么需要共享。</b>以前这些事实存在 SQLite 文件里，所以"查询入口"和"写入入口"
	/// 各自 new 一个 store 也能看到同一份数据。改成内存之后，两个实例就是两份互不相干的内存——
	/// 写入的进不去查询的那个，全部查询都会返回空。按管理器目录共享同一个实例，
	/// 正好复现文件时代"谁都能看到同一份"的性质，而测试各自用临时目录，互不干扰。
	/// </para>
	/// </summary>
	private static readonly Dictionary<string, InMemoryModFactsStore> Instances = new(StringComparer.OrdinalIgnoreCase);

	public static InMemoryModFactsStore For(StoragePaths paths)
	{
		ArgumentNullException.ThrowIfNull(paths);
		var key = paths.IndexDirectory;
		lock (Instances)
		{
			if (!Instances.TryGetValue(key, out var store))
			{
				store = new InMemoryModFactsStore();
				Instances[key] = store;
			}
			return store;
		}
	}

	private readonly Dictionary<ModNodeId, NodeEntry> _nodes = new();
	private readonly object _gate = new();

	public ValueTask DeleteAsync(ModNodeId nodeId, CancellationToken cancellationToken = default)
	{
		lock (_gate) _nodes.Remove(nodeId);
		return ValueTask.CompletedTask;
	}

	public ValueTask DeleteNodeAsync(ModNodeId nodeId, CancellationToken cancellationToken = default)
		=> DeleteAsync(nodeId, cancellationToken);

	public ValueTask ReplaceNodeAsync(ReferenceGraphFacts facts, CancellationToken cancellationToken = default)
		=> Replace(facts.NodeId, facts.Analyses);

	public ValueTask ReplaceNodeAsync(AdvancedUnitAnalysisFacts facts, CancellationToken cancellationToken = default)
		=> Replace(facts.NodeId, facts.Analyses);

	private ValueTask Replace(ModNodeId nodeId, IReadOnlyList<PatchGroupAnalysis> analyses)
	{
		var assets = new List<(string, AdaptationAssetKey)>();
		var references = new List<(string, PatchAssetReference)>();
		foreach (var analysis in analyses)
		{
			var groupId = $"{nodeId.Value:N}:{Path.GetFileName(analysis.Input.PatchTocFilePath)}";
			foreach (var asset in analysis.Assets)
				assets.Add((groupId, asset.AssetKey));
			foreach (var reference in analysis.References)
				references.Add((groupId, reference));
		}

		lock (_gate) _nodes[nodeId] = new NodeEntry(assets, references);
		return ValueTask.CompletedTask;
	}

	public ValueTask<IReadOnlyList<ModAssetConsumerFact>> FindConsumerFactsAsync(
		AdaptationAssetKey targetAssetKey,
		CancellationToken cancellationToken = default)
		=> ValueTask.FromResult(FindConsumers([targetAssetKey]));

	public ValueTask<IReadOnlyList<ModAssetConsumerFact>> FindConsumerFactsAsync(
		IReadOnlyCollection<AdaptationAssetKey> targetAssetKeys,
		CancellationToken cancellationToken = default)
		=> ValueTask.FromResult(FindConsumers(targetAssetKeys));

	public ValueTask<IReadOnlyList<ModAssetProviderFact>> FindProviderFactsAsync(
		IReadOnlyCollection<AdaptationAssetKey> assetKeys,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(assetKeys);
		var wanted = assetKeys.Select(key => (key.TypeId, key.FileId)).ToHashSet();
		if (wanted.Count == 0) return ValueTask.FromResult<IReadOnlyList<ModAssetProviderFact>>(Array.Empty<ModAssetProviderFact>());

		var results = new List<ModAssetProviderFact>();
		lock (_gate)
		{
			foreach (var (nodeId, entry) in _nodes)
			{
				foreach (var (groupId, key) in entry.Assets)
				{
					if (wanted.Contains((key.TypeId, key.FileId)))
						results.Add(new ModAssetProviderFact(nodeId, groupId, new DomainAssetKey(key.TypeId, key.FileId)));
				}
			}
		}
		return ValueTask.FromResult<IReadOnlyList<ModAssetProviderFact>>(results);
	}

	public ValueTask<ProfileIndexedFacts> ReadProfileFactsAsync(
		IReadOnlyCollection<ModNodeId> nodeIds,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(nodeIds);
		if (nodeIds.Count == 0) return ValueTask.FromResult(ProfileIndexedFacts.Empty);

		var assets = new List<ProfileIndexedAsset>();
		var references = new List<ProfileIndexedReference>();
		lock (_gate)
		{
			foreach (var nodeId in nodeIds.Distinct())
			{
				if (!_nodes.TryGetValue(nodeId, out var entry)) continue;
				foreach (var (_, key) in entry.Assets.DistinctBy(item => (item.Key.TypeId, item.Key.FileId)))
					assets.Add(new ProfileIndexedAsset(nodeId, new DomainAssetKey(key.TypeId, key.FileId)));
				foreach (var (_, reference) in entry.References.DistinctBy(item => (
					item.Reference.SourceAssetKey.TypeId,
					item.Reference.SourceAssetKey.FileId,
					item.Reference.TargetAssetKey.TypeId,
					item.Reference.TargetAssetKey.FileId,
					item.Reference.Kind)))
				{
					references.Add(new ProfileIndexedReference(
						nodeId,
						new DomainAssetKey(reference.SourceAssetKey.TypeId, reference.SourceAssetKey.FileId),
						new DomainAssetKey(reference.TargetAssetKey.TypeId, reference.TargetAssetKey.FileId),
						(int)reference.Kind));
				}
			}
		}
		return ValueTask.FromResult(new ProfileIndexedFacts(assets, references));
	}

	private IReadOnlyList<ModAssetConsumerFact> FindConsumers(IEnumerable<AdaptationAssetKey> targetAssetKeys)
	{
		var wanted = targetAssetKeys.Select(key => (key.TypeId, key.FileId)).ToHashSet();
		if (wanted.Count == 0) return Array.Empty<ModAssetConsumerFact>();

		var results = new List<ModAssetConsumerFact>();
		lock (_gate)
		{
			foreach (var (nodeId, entry) in _nodes)
			{
				foreach (var (groupId, reference) in entry.References)
				{
					if (wanted.Contains((reference.TargetAssetKey.TypeId, reference.TargetAssetKey.FileId)))
						results.Add(new ModAssetConsumerFact(nodeId, groupId, reference));
				}
			}
		}
		return results;
	}
}
