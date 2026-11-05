using System.Text.Json;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>剪掉多少个悬空的视觉组件。</summary>
public sealed record CustomModelCompactionResult(
	int RemovedPieceCount,
	int RemainingPieceCount,
	IReadOnlyList<string> RemovedUnits)
{
	public static readonly CustomModelCompactionResult NoChange = new(0, 0, []);

	public bool Changed => RemovedPieceCount != 0;

	public string Describe()
		=> Changed
			? $"剪掉 {RemovedPieceCount} 个悬空组件（unit 已被合并进宿主），剩 {RemainingPieceCount} 个"
			: "没有悬空组件需要剪";
}

/// <summary>
/// 拆件之后把自定义素体定义"对齐到产物"。
/// <para>
/// <b>为什么必须有这一步。</b>拆件会把同一 <c>(槽, 身形)</c> 的多个来源 unit 合并进一个宿主 unit，
/// 其余 donor unit 从 patch 里去掉。而自定义素体的定义是按
/// <c>(body, slot, pieceType)</c> → unit 记的（<c>pieceType</c> 是"该部位的第几个 unit 槽"），
/// 所以指向 donor 的那些条目会变成**悬空引用**：护甲映射页会列出一个选了什么都不反应的组件。
/// </para>
/// <para>
/// <b>要改两处，不能只改一处。</b>
/// 产物里的 <c>custom-model.json</c> 是一份副本（归属标记 + 可移植性），
/// 真正的权威是注册表工作区的 <c>ModelDefinitions</c>。
/// 只改产物文件 = 界面照旧显示悬空组件；只改工作区 = 下次从产物重读又长回来。
/// </para>
/// </summary>
public static class CustomModelSectionalCompaction
{
	private static readonly JsonSerializerOptions DefinitionOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
	};

	/// <summary>
	/// 只保留"拆件后仍然存在"的 unit 对应的组件条目。
	/// </summary>
	/// <param name="registry">注册表服务，用来更新权威定义。可为 <c>null</c>（只改产物里的副本）。</param>
	/// <param name="modDirectory">产物目录（含 <c>custom-model.json</c>）。</param>
	/// <param name="survivingUnitIds">拆件后产物里仍然存在的 unit fileId。</param>
	public static async ValueTask<CustomModelCompactionResult> PruneMergedPiecesAsync(
		RuntimeArmorRegistryService? registry,
		string modDirectory,
		IReadOnlyCollection<string> survivingUnitIds,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		ArgumentNullException.ThrowIfNull(survivingUnitIds);

		var definitionPath = Path.Combine(modDirectory, VpkCustomModelFile.FileName);
		if (!File.Exists(definitionPath)) return CustomModelCompactionResult.NoChange;

		var text = await File.ReadAllTextAsync(definitionPath, cancellationToken).ConfigureAwait(false);
		RuntimeArmorRegistryModel? model;
		try
		{
			model = JsonSerializer.Deserialize<RuntimeArmorRegistryModel>(text, DefinitionOptions);
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException($"自定义素体定义不是合法 JSON：{definitionPath}", exception);
		}
		if (model?.Pieces is not { Count: > 0 } pieces) return CustomModelCompactionResult.NoChange;

		var surviving = survivingUnitIds
			.Where(id => !string.IsNullOrWhiteSpace(id))
			.Select(id => id.Trim())
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var kept = pieces.Where(piece => surviving.Contains(piece.Unit)).ToArray();
		if (kept.Length == pieces.Count) return CustomModelCompactionResult.NoChange;

		var removedUnits = pieces
			.Select(piece => piece.Unit)
			.Where(unit => !surviving.Contains(unit))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(unit => unit, StringComparer.Ordinal)
			.ToArray();
		var pruned = model with { Pieces = kept };
		var result = new CustomModelCompactionResult(pieces.Count - kept.Length, kept.Length, removedUnits);

		await File.WriteAllTextAsync(
			definitionPath,
			JsonSerializer.Serialize(pruned, DefinitionOptions),
			cancellationToken).ConfigureAwait(false);

		if (registry is not null)
			await UpdateWorkspaceAsync(registry, pruned, cancellationToken).ConfigureAwait(false);

		return result;
	}

	private static async ValueTask UpdateWorkspaceAsync(
		RuntimeArmorRegistryService registry,
		RuntimeArmorRegistryModel pruned,
		CancellationToken cancellationToken)
	{
		var snapshot = await registry.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
		var document = snapshot.Document;
		// ⚠️ **"工作区里有没有这个素体"必须问工作区，不能问快照。**
		// 快照现在会把 Mod 目录里那份 `custom-model.json` 合并进来（文件是素体的权威来源），
		// 于是"转换后还没登记过、但文件已在磁盘上"的素体会出现在快照里 ——
		// 拿它判存在性就会把一具本不属于工作区的素体写进工作区。
		var workspaceModels = await registry.ReadWorkspaceModelsAsync(cancellationToken).ConfigureAwait(false);
		var index = -1;
		for (var i = 0; i < workspaceModels.Count; i++)
		{
			if (!string.Equals(workspaceModels[i].Key, pruned.Key, StringComparison.Ordinal)) continue;
			index = i;
			break;
		}
		// 工作区里没有这个素体（例如转换后还没登记过）就什么都不用做。
		if (index < 0) return;

		var models = workspaceModels.ToArray();
		models[index] = pruned;

		// ⚠️ **不能用 `document with { Models = models }`。** `ModelDefinitions` 是由 `Models`
		// 初始化的 init 属性；`with` 复制记录时会把已经算好的**旧值**一起复制过去，
		// 于是序列化出去的还是旧模型。必须整条重建（同 SaveWorkspacePreservingExternalModelsAsync 的注释）。
		var rebuilt = new RuntimeArmorRegistryDocument(
			document.Revision,
			document.Enabled,
			document.Mappings,
			models,
			document.PieceOverrideDefinitions,
			document.TargetModeDefinitions,
			document.CompositionMode);

		// 用"保留外部素体"的那条保存路径：拆件发生在一次长流程里，
		// 期间可能有别的进程（vpk2patch）往工作区加了素体，整份覆盖会把它们吞掉。
		var known = document.ModelDefinitions.Select(model => model.Key).ToHashSet(StringComparer.Ordinal);
		await registry.SaveWorkspacePreservingExternalModelsAsync(rebuilt, known, cancellationToken).ConfigureAwait(false);
	}
}
