using System.IO;
using System.Text;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <param name="OrphanModels">工作区里存在、但磁盘上已经没有归属的素体数。</param>
/// <param name="DissolvedMappings">被解除的映射条数。</param>
/// <param name="MarkedOnly">
/// 本次只做了标记（首次见到这些孤儿），<b>没有删任何东西</b>。
/// 下一次成功收集之后才会真正解除。
/// </param>
/// <param name="BlockedReason">收集不可信（库不可用 / 有文件读不出来）时的原因；此时什么都没做。</param>
public sealed record CustomModelReconcileReport(
	int OrphanModels,
	int DissolvedMappings,
	bool MarkedOnly,
	string? BlockedReason)
{
	public static readonly CustomModelReconcileReport Empty = new(0, 0, false, null);

	/// <summary>本轮补出来的素体标记（<c>null</c> = 没有需要补的）。</summary>
	public CustomModelRecoveryReport? Recovery { get; init; }

	public bool IsEmpty => OrphanModels == 0 && BlockedReason is null && Recovery is null;

	/// <summary>给用户看的一句话；没有任何事发生时返回 <c>null</c>。</summary>
	public string? Describe()
	{
		var parts = new List<string>();
		if (Recovery?.Describe() is { Length: > 0 } recovery) parts.Add(recovery);
		if (BlockedReason is not null)
		{
			parts.Add("素体对账已跳过：" + BlockedReason);
		}
		else if (OrphanModels != 0)
		{
			parts.Add(MarkedOnly
				? $"有 {OrphanModels} 个自定义素体在库里找不到归属它的 Mod（首次发现，仅标记、未删除）。"
					+ "如果这些 Mod 只是暂时没挂上，请先恢复库目录；否则下一次对账会解除指向它们的映射。"
				: $"已解除 {DissolvedMappings} 条指向已消失素体的映射（{OrphanModels} 个素体在库里找不到归属它的 Mod）。"
					+ "素体随 Mod 出现或消失，映射不会指向不存在的东西。");
		}

		return parts.Count == 0 ? null : string.Join("。", parts);
	}
}

/// <summary>
/// 素体 ↔ 映射的对账：<b>以 Mod 文件夹为权威，把"没有素体的映射"解散掉</b>。
/// <para>
/// <b>与旧实现的区别（旧的叫 <c>CustomModelOwnershipService.PruneAsync</c>）。</b>
/// 旧的是"删素体"：把注册表里没有 Mod 归属的素体连同映射一起删掉。它有两个结构性问题：
/// </para>
/// <list type="number">
/// <item><b>读不到 = 没有</b>：某个 <c>custom-model.json</c> 一时被占用/无权限，那个素体当场
/// 被判成孤儿删除。<see cref="CustomModelCatalog"/> 现在把"读不出来"单独报出来，
/// 本类遇到它<b>一个字都不删</b>。</item>
/// <item><b>删错了没法恢复</b>：删的是素体定义本身。现在素体定义只存在于 Mod 目录里，
/// 本类删的只有"指向不存在素体的映射"——那是可以从界面重配的，而素体再也不会被删。</item>
/// </list>
/// <para>
/// <b>两阶段迁移。</b>第一次见到"工作区里有、磁盘上没有"的素体时只写一个标记、<b>不删</b>
/// （用户可能只是库路径临时指错）。下一次收集成功之后才真正解除——用户确认的口径是
/// "首次只标记，正常扫描成功一次之后才自动解散"。
/// </para>
/// </summary>
public sealed class CustomModelReconciler
{
	/// <summary>标记文件：存在即表示"已经成功对过一次账"，之后的孤儿可以直接解散。</summary>
	public const string StateFileName = "custom-model-catalog.state";

	private readonly RuntimeArmorRegistryService registry;
	private readonly string modsDirectory;

	public CustomModelReconciler(RuntimeArmorRegistryService registry, string modsDirectory)
	{
		this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
		this.modsDirectory = modsDirectory ?? string.Empty;
	}

	private string StatePath => Path.Combine(registry.WorkspaceRoot, StateFileName);

	public async ValueTask<CustomModelReconcileReport> ReconcileAsync(CancellationToken cancellationToken = default)
	{
		// **先补文件，再对账。** 顺序不能反：历史素体只要对应的 Mod 目录还在，
		// 就应该先被补出 `custom-model.json`（并沿用原 key），而不是被当成"已消失"解散掉。
		var recovery = await new CustomModelRecovery(registry, modsDirectory)
			.RecoverAsync(cancellationToken).ConfigureAwait(false);
		var recoveryOrNull = recovery.IsEmpty ? null : recovery;

		var catalog = CustomModelCatalog.Collect(modsDirectory);
		// **fail-closed**：库不可用或有文件读不出来，本轮什么都不做（也不写标记）。
		if (!catalog.IsTrustworthy)
			return new CustomModelReconcileReport(0, 0, false, catalog.DescribeBlocked()) { Recovery = recoveryOrNull };

		var snapshot = await registry.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
		var models = snapshot.Document.ModelDefinitions;
		var missing = models
			.Where(model => !catalog.Entries.ContainsKey(model.Key))
			.ToArray();
		if (missing.Length == 0)
		{
			await MarkConfirmedAsync(cancellationToken).ConfigureAwait(false);
			return CustomModelReconcileReport.Empty with { Recovery = recoveryOrNull };
		}

		// 首次发现：只标记，不删。用户可能只是库路径临时指错，一次清空就再也回不来了。
		if (!File.Exists(StatePath))
		{
			await MarkConfirmedAsync(cancellationToken).ConfigureAwait(false);
			return new CustomModelReconcileReport(missing.Length, 0, MarkedOnly: true, null) { Recovery = recoveryOrNull };
		}

		var missingKeys = missing.Select(model => model.Key).ToHashSet(StringComparer.Ordinal);
		var keptMappings = snapshot.Document.Mappings
			.Where(mapping => mapping.ModelKey is null || !missingKeys.Contains(mapping.ModelKey))
			.ToArray();
		var dissolved = snapshot.Document.Mappings.Count - keptMappings.Length;
		// 素体列表只留磁盘上真实存在的那些（顺序按 key，稳定）。
		var keptModels = models
			.Where(model => !missingKeys.Contains(model.Key))
			.OrderBy(model => model.Key, StringComparer.Ordinal)
			.ToArray();

		var document = new RuntimeArmorRegistryDocument(
			snapshot.Document.Revision,
			snapshot.Document.Enabled,
			keptMappings,
			keptModels,
			snapshot.Document.PieceOverrideDefinitions,
			snapshot.Document.TargetModeDefinitions,
			snapshot.Document.CompositionMode);
		// known 传"我这次见到的全部 key"（含要删的那些），否则 Preserving 会把它们当成
		// "别人刚加的外部素体"又合并回来 —— 等于没删。
		var known = models.Select(model => model.Key).ToHashSet(StringComparer.Ordinal);
		await registry.SaveWorkspacePreservingExternalModelsAsync(document, known, cancellationToken)
			.ConfigureAwait(false);
		return new CustomModelReconcileReport(missing.Length, dissolved, MarkedOnly: false, null)
		{
			Recovery = recoveryOrNull,
		};
	}

	private async ValueTask MarkConfirmedAsync(CancellationToken cancellationToken)
	{
		try
		{
			Directory.CreateDirectory(registry.WorkspaceRoot);
			await File.WriteAllTextAsync(
				StatePath,
				"confirmed=true\n",
				new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
				cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 写不了标记只意味着"下次还会再标记一次"，不影响正确性。
		}
	}
}
