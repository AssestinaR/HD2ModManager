using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 撤销一次素体登记（<b>只用于"生成成功但导入失败"的回滚</b>）。
/// <para>
/// <b>它不再负责"清理没有 Mod 归属的素体"。</b>那条路（原来的 <c>PruneAsync</c>）已经被删除，
/// 原因是它在结构上会丢用户数据：判断"有没有归属"靠读每个 Mod 目录里的
/// <c>custom-model.json</c>，而"读不出来"（被占用 / 无权限 / 坏 JSON）与"没有这个文件"
/// 在旧实现里是同一个 <c>null</c> —— 于是某个文件一时读不到，那个素体就被判成孤儿，
/// <b>连同映射一起被删</b>（2026-11-05 用户反馈"重校验后素体消失"）。
/// </para>
/// <para>
/// 现在素体的生命周期完全由 Mod 目录决定（见 <see cref="CustomModelCatalog"/>）：
/// 素体定义只存在于 Mod 目录里，Mod 在它就在、Mod 没了它就没了，不需要任何"删除素体"的动作。
/// 唯一需要主动清掉的是<b>指向不存在素体的映射</b>，那由
/// <see cref="CustomModelReconciler"/> 按"首次只标记、之后才解散"的两阶段规则处理。
/// </para>
/// </summary>
public sealed class CustomModelOwnershipService
{
	private readonly RuntimeArmorRegistryService registry;

	public CustomModelOwnershipService(RuntimeArmorRegistryService registry)
		=> this.registry = registry ?? throw new ArgumentNullException(nameof(registry));

	/// <summary>
	/// 把某个素体从注册表里摘掉（连同引用它的映射）。
	/// <para>
	/// <b>用途：生成成功但导入失败时的回滚。</b>素体登记发生在生成流程里（转换器写
	/// <c>custom-model.json</c> 时顺带登记），而"成为某个 Mod 的产物"要等导入完成。
	/// 导入失败就留下一条没有 Mod 的记录 —— 那正是"列表里挂着一个选了没反应的素体"。
	/// 与其等下一次对账清掉它，不如当场收回去并把失败原样报给用户。
	/// </para>
	/// <para>
	/// 注意它<b>不删磁盘上的 <c>custom-model.json</c></b>：那份文件属于 Mod 目录，
	/// 回滚路径本来就是"这次产物没进库"，文件跟着输出目录一起被丢弃即可。
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
}
