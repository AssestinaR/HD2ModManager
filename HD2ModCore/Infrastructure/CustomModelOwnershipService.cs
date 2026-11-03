using System.IO;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>一次对账的结果（也用于"只查不删"的预演）。</summary>
/// <param name="Models">失去 Mod 归属、已被（或将被）删除的素体名。</param>
/// <param name="DroppedMappings">跟着一起删掉的映射条数。</param>
public sealed record CustomModelPruneReport(IReadOnlyList<string> Models, int DroppedMappings)
{
	public static readonly CustomModelPruneReport Empty = new([], 0);

	public bool IsEmpty => Models.Count == 0;

	/// <summary>给用户看的一句话；没有孤儿时返回 <c>null</c>（不制造噪音）。</summary>
	public string? Describe()
		=> IsEmpty
			? null
			: $"已清理 {Models.Count} 个没有 Mod 归属的自定义素体（{string.Join("、", Models.Take(3))}"
				+ (Models.Count > 3 ? " 等" : string.Empty)
				+ "）"
				+ (DroppedMappings > 0 ? $"，以及指向它们的 {DroppedMappings} 条映射" : string.Empty)
				+ "。素体是 Mod 的产物：Mod 没了，素体也一起走。";
}

/// <summary>
/// 自定义素体与 Mod 的<b>生命周期绑定</b>：没有 Mod 归属的素体一律清掉。
/// <para>
/// <b>为什么需要它。</b>素体不是独立数据，而是某个 Mod 的转换产物 —— 那套 Unit 只有跟着
/// 那个 Mod 部署才有意义。但注册表（<c>armor-registry.workspace.json</c>）里没有"归属"这一维，
/// 于是删掉 Mod 之后记录还在：护甲映射页里挂着一个素体，选它却什么都不发生，
/// 而且没有任何提示说明为什么。实测用户库里就躺过 5 个这种孤儿
/// （milltina 的 5 个转换产物，Mod 从未导入成功）。
/// </para>
/// <para>
/// <b>归属的事实来源是 Mod 目录里的 <c>custom-model.json</c></b>（见
/// <see cref="VpkCustomModelFile.ReadOwners"/>）：它随 Mod 一起被删，不需要任何额外登记，
/// 也就不会出现"注册表说属于 A、磁盘上属于 B"的第二种真相。
/// </para>
/// <para>
/// <b>为什么不写进协议。</b>Lua 只消费协议，它不需要知道归属；给协议加字段要改版本、
/// 改解析、还要处理新旧文件共存 —— 收益为零。
/// </para>
/// </summary>
public sealed class CustomModelOwnershipService
{
	private readonly RuntimeArmorRegistryService registry;
	private readonly string modsDirectory;

	public CustomModelOwnershipService(RuntimeArmorRegistryService registry, string modsDirectory)
	{
		this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
		this.modsDirectory = modsDirectory ?? string.Empty;
	}

	/// <summary>只算不写：这批素体里哪些没有 Mod 归属。</summary>
	public async ValueTask<CustomModelPruneReport> InspectAsync(CancellationToken cancellationToken = default)
	{
		var (snapshot, orphans) = await ReadAsync(cancellationToken).ConfigureAwait(false);
		if (orphans.Length == 0)
			return CustomModelPruneReport.Empty;
		var keys = orphans.Select(model => model.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
		return new CustomModelPruneReport(
			orphans.Select(Describe).ToArray(),
			snapshot.Document.Mappings.Count(mapping => mapping.ModelKey is not null && keys.Contains(mapping.ModelKey)));
	}

	/// <summary>
	/// 对账并落盘：删掉没有 Mod 归属的素体，以及<b>引用它们的映射</b>
	/// （不删映射的话，下一次校验会直接拒绝这份工作区）。
	/// </summary>
	public async ValueTask<CustomModelPruneReport> PruneAsync(CancellationToken cancellationToken = default)
	{
		var (snapshot, orphans) = await ReadAsync(cancellationToken).ConfigureAwait(false);
		if (orphans.Length == 0)
			return CustomModelPruneReport.Empty;

		var orphanKeys = orphans.Select(model => model.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var kept = snapshot.Document.ModelDefinitions
			.Where(model => !orphanKeys.Contains(model.Key))
			.ToArray();
		var droppedMappings = snapshot.Document.Mappings
			.Where(mapping => mapping.ModelKey is null || !orphanKeys.Contains(mapping.ModelKey))
			.ToArray();

		var pruned = snapshot.Document with
		{
			ModelDefinitions = kept,
			Mappings = droppedMappings,
		};
		// 用 Preserving 而不是裸 Save：万一此刻有另一个进程（`hd2mod vpk2patch`）刚登记了新素体，
		// 裸写会把它整份覆盖掉 —— 那正是这个 API 存在的理由。传"我刚读到的 key 集合"，
		// 于是任何我没见过的 key 都会被合并进来。
		var known = snapshot.Document.ModelDefinitions
			.Select(model => model.Key)
			.ToHashSet(StringComparer.Ordinal);
		await registry.SaveWorkspacePreservingExternalModelsAsync(pruned, known, cancellationToken)
			.ConfigureAwait(false);

		var report = new CustomModelPruneReport(
			orphans.Select(Describe).ToArray(),
			snapshot.Document.Mappings.Count - droppedMappings.Length);
		return report;
	}

	/// <summary>
	/// 把某个素体从注册表里摘掉（连同引用它的映射）。
	/// <para>
	/// <b>用途：生成成功但导入失败时的回滚。</b>素体登记发生在生成流程里（转换器写
	/// <c>custom-model.json</c> 时顺带登记），而"成为某个 Mod 的产物"要等导入完成。
	/// 导入失败就留下一条没有 Mod 的记录 —— 那正是"列表里挂着一个选了没反应的素体"。
	/// 与其等下一次对账清掉它，不如当场收回去并把失败原样报给用户。
	/// </para>
	/// </summary>
	/// <returns>是否真的删掉了记录（不存在时返回 false，不算错误）。</returns>
	public async ValueTask<bool> RemoveAsync(string modelKey, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modelKey);
		var snapshot = await registry.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
		var models = snapshot.Document.ModelDefinitions;
		if (!models.Any(model => string.Equals(model.Key, modelKey, StringComparison.OrdinalIgnoreCase)))
			return false;
		var keptModels = models
			.Where(model => !string.Equals(model.Key, modelKey, StringComparison.OrdinalIgnoreCase))
			.ToArray();
		var keptMappings = snapshot.Document.Mappings
			.Where(mapping => mapping.ModelKey is null
				|| !string.Equals(mapping.ModelKey, modelKey, StringComparison.OrdinalIgnoreCase))
			.ToArray();
		await registry.SaveWorkspaceAsync(
			snapshot.Document with { ModelDefinitions = keptModels, Mappings = keptMappings },
			cancellationToken).ConfigureAwait(false);
		return true;
	}

	private async ValueTask<(RuntimeArmorRegistrySnapshot Snapshot, RuntimeArmorRegistryModel[] Orphans)> ReadAsync(
		CancellationToken cancellationToken)
	{
		var snapshot = await registry.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
		var models = snapshot.Document.ModelDefinitions;
		if (models.Count == 0)
			return (snapshot, []);
		// 库目录不存在时**不动手**：那更可能是"Mod 库盘没挂上 / 路径配错"，
		// 而不是"用户的素体全都失去归属"。宁可这次不清，也不要一次清空。
		if (!Directory.Exists(modsDirectory))
			return (snapshot, []);
		var owners = VpkCustomModelFile.ReadOwners(modsDirectory);
		var orphans = VpkCustomModelFile.FindOrphans(models, owners);
		return (snapshot, orphans.ToArray());
	}

	private static string Describe(RuntimeArmorRegistryModel model)
		=> string.IsNullOrWhiteSpace(model.Name) ? model.Key : model.Name!;
}
