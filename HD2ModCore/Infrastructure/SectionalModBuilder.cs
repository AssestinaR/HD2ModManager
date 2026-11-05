using HD2ModAdaptation.PatchReconstruction;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>一条 sectional 产物的构建结果。</summary>
public sealed record SectionalModArtifacts(
	/// <summary>Mod 目录根：导入时就是这个目录。<c>vpk2patch/</c> 在它下面。</summary>
	string ModDirectory,
	/// <summary>实际生效的 patch 目录（<c>vpk2patch/</c>）。</summary>
	string PayloadDirectory,
	/// <summary>换甲产物里那份素体定义的新位置（搬进 <c>vpk2patch/</c> 之后）。</summary>
	string DefinitionPath,
	RuntimeArmorRegistryModel Model,
	SectionalSplitResult Split,
	CustomModelCompactionResult Compaction);

/// <summary>
/// 把「换甲产物」整理成「一个 sectional Mod 目录」。
/// <para>
/// <b>为什么要搬进 <c>vpk2patch/</c>。</b><see cref="ModNodePayloadLayout"/> 把目录分成两层：
/// 根是常规层（手写/导入的 patch），<c>vpk2patch/</c> 是<b>内容层</b>——那层的 patch
/// 「本来就不在根上」，所以必须能独立生效。转换产物正属于内容层。
/// </para>
/// <para>
/// 更直接的理由：<c>VpkModWardrobeService.IsVpkMod</c> 只认 <c>Overwrite/sections.json</c>
/// 与 <c>vpk2patch/sections.json</c>。产物写在根目录时它认不出来，于是详情页把分件模组
/// 当成 legacy —— 汉堡菜单留着 patch 级动作、section 组件卡不出现、section 树读不到。
/// </para>
/// <para>
/// 这条管线被管理器与 CLI 共用：两边都要"生成 → 归位 → 拆件 → 剪枝"，
/// 各写一遍必然漂移，而漂移的表现是"CLI 转出来的能用、界面转出来的不行"。
/// </para>
/// </summary>
public static class SectionalModBuilder
{
	/// <summary>会被搬进 <c>vpk2patch/</c> 的文件：patch 主文件与它的两份 sidecar，以及素体定义。</summary>
	private static bool ShouldMoveToPayload(string path)
	{
		var name = Path.GetFileName(path);
		if (name.Equals(VpkCustomModelFile.FileName, StringComparison.OrdinalIgnoreCase)) return true;
		if (name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase)) return true;
		if (name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase)) return true;
		return name.Contains(".patch_", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// 把 <c>modDirectory</c> 根上的 patch 族与素体定义搬进 <c>vpk2patch/</c>，返回内容层目录。
	/// <para>
	/// 单独暴露是因为<b>预演也要看真实布局</b>：<c>plan</c> 不写库、不剪枝，
	/// 但它必须能说出"产物会落在哪一层"，否则预演结论和实际执行对不上。
	/// </para>
	/// </summary>
	public static string RelocateToPayloadDirectory(string modDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		var payloadDirectory = Path.Combine(modDirectory, ModNodePayloadLayout.VpkPatchDirectoryName);
		Directory.CreateDirectory(payloadDirectory);
		// 根目录不再留 patch —— 留着就会在部署时和内容层各算一次，变成两份同名 patch。
		foreach (var path in Directory.EnumerateFiles(modDirectory, "*", SearchOption.TopDirectoryOnly))
		{
			if (!ShouldMoveToPayload(path)) continue;
			File.Move(path, Path.Combine(payloadDirectory, Path.GetFileName(path)), overwrite: true);
		}

		return payloadDirectory;
	}

	public static async ValueTask<SectionalModArtifacts> BuildAsync(
		CustomModelConversionService converter,
		ModEquipmentSourceFacts facts,
		IReadOnlyList<string> archiveIds,
		string name,
		string stagingRoot,
		string gameDataDirectory,
		RuntimeArmorRegistryService registry,
		CancellationToken cancellationToken = default,
		IProgress<CrossArmorTransferProgress>? progress = null)
	{
		ArgumentNullException.ThrowIfNull(converter);
		ArgumentNullException.ThrowIfNull(facts);
		ArgumentNullException.ThrowIfNull(archiveIds);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);
		ArgumentNullException.ThrowIfNull(registry);

		// 1. 换甲：产出一具自定义素体的 patch + custom-model.json，落在 <stagingRoot>/<name>-<时间>-<key6>/
		var generated = await converter
			.GenerateAsync(facts, archiveIds, name, stagingRoot, gameDataDirectory, cancellationToken, progress)
			.ConfigureAwait(false);

		var modDirectory = generated.OutputDirectory;

		// 2. 归位：把 patch 族与素体定义搬进内容层。
		var payloadDirectory = RelocateToPayloadDirectory(modDirectory);

		// 3. 拆件：就地覆盖内容层里的 patch（同名同目录，部署时靠同名顶掉）。
		var split = await SectionalSplitter
			.ExecuteAsync(payloadDirectory, gameDataDirectory, payloadDirectory, cancellationToken)
			.ConfigureAwait(false);
		if (!split.IsValid)
		{
			throw new InvalidDataException(
				"拆件失败：" + (split.Warnings.Count == 0 ? "（没有更多细节）" : string.Join("；", split.Warnings)));
		}

		// 4. 剪枝：合并把 donor unit 并进宿主，素体定义里指向它们的条目要一起剪掉，
		//    否则护甲映射页会列出"选了什么都不反应"的组件。
		var compaction = await CustomModelSectionalCompaction
			.PruneMergedPiecesAsync(
				registry,
				payloadDirectory,
				[.. split.Sections.Units.Select(unit => unit.FileId)],
				cancellationToken)
			.ConfigureAwait(false);

		return new SectionalModArtifacts(
			modDirectory,
			payloadDirectory,
			Path.Combine(payloadDirectory, VpkCustomModelFile.FileName),
			generated.Model,
			split,
			compaction);
	}
}
