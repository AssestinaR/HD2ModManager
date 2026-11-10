using System.Globalization;
using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;

namespace HD2ModCore.Infrastructure;

/// <summary>一次「来源 → sectional」转换的结果。</summary>
/// <param name="UnitCount">产物里有多少个 unit 组（目前恒为 1：折叠成 <c>patch_0</c>）。</param>
/// <param name="MaterialGroups">材质组数量（含内置材质）。</param>
public sealed record Legacy2SectionalOutcome(
	bool Ok,
	string? Error,
	int UnitCount,
	int MaterialGroups,
	IReadOnlyList<string> Notes)
{
	public static Legacy2SectionalOutcome Failed(string error, IReadOnlyList<string>? notes = null)
		=> new(false, error, 0, 0, notes ?? Array.Empty<string>());
}

/// <summary>
/// 「来源 → sectional」的转换机（文档里的 <c>legacy2sectional</c>）。
///
/// <para>
/// <b>它与 <see cref="VpkRebuildService"/> 的关系。</b>两者都产出"标准 sectional 产物"：
/// 自定义素体 + 标准 7 槽 unit 组（<c>patch_0</c>）+ 材质 patch 组（<c>patch_1</c> 起）。
/// 差别只在**来源不同**：一个读根上的来源 VPK（交给 CLI 转），一个读根上的来源 patch。
/// 产物之后的一切（编译、部署）完全共用，所以这里写完之后只调 <see cref="ModCompileService"/>。
/// </para>
///
/// <para>
/// <b>为什么先写临时目录、成功后整批换。</b><c>sectional/</c> 是 patch 级权威事实，
/// 就地转换一旦中途失败，用户手上就只剩半成品。换载荷走 <see cref="VpkPayloadSwap"/>，
/// 与 vpk2patch 那条路共用同一份实现。
/// </para>
/// </summary>
public static class Legacy2SectionalConverter
{
	public static async Task<Legacy2SectionalOutcome> RunAsync(
		string modSelector,
		string modDirectory,
		VpkHostContext context,
		Action<string>? onStage = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modSelector);
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		ArgumentNullException.ThrowIfNull(context);

		var notes = new List<string>();
		if (!Directory.Exists(modDirectory))
			return Legacy2SectionalOutcome.Failed($"Mod 目录不存在：{modDirectory}");

		var document = await SectionalSourceFile.TryReadAsync(modDirectory, cancellationToken).ConfigureAwait(false);
		var documentError = SectionalSourceFile.Validate(document);
		if (documentError is not null) return Legacy2SectionalOutcome.Failed(documentError);
		if (string.IsNullOrWhiteSpace(context.GameDataFolder) || !Directory.Exists(context.GameDataFolder))
			return Legacy2SectionalOutcome.Failed("没有配置游戏 data 目录：拆件要读外壳 unit 与 avatar 骨架。");

		var sources = SectionalSplitter.EnumeratePatchFiles(modDirectory);
		if (sources.Count == 0)
			return Legacy2SectionalOutcome.Failed(
				"Mod 根目录里没有来源 patch。来源必须是 legacy 类 Mod（patch 在根上）——"
				+ "已经是 sectional / vpk2patch 的条目应该用它们各自的来源重构建。");

		onStage?.Invoke("正在读取来源 patch…");
		var contents = await ScanAsync(sources, cancellationToken).ConfigureAwait(false);
		var plan = SectionalConversionPlanner.Plan(
			document!,
			contents,
			[PatchUnitMeshReader.UnitTypeId],
			[MaterialDependencyResolver.MaterialTypeId],
			[MaterialDependencyResolver.TextureTypeId]);
		notes.AddRange(plan.Warnings);
		if (plan.Unit is not { Selections.Count: > 0 } unitGroup)
			return Legacy2SectionalOutcome.Failed("来源里没有白名单内的 unit，无法产出 sectional 产物。", notes);

		var archiveHex = ArchiveHexOf(unitGroup.Selections[0].SourcePatch);
		var stagingRoot = Path.Combine(Path.GetTempPath(), "hd2modmanager-legacy2sectional", Guid.NewGuid().ToString("N"));
		var workRoot = Path.Combine(stagingRoot, "work");
		var producedRoot = Path.Combine(stagingRoot, "produced");
		Directory.CreateDirectory(workRoot);
		Directory.CreateDirectory(producedRoot);

		try
		{
			var writer = new PatchSubsetWriter();

			// ① unit 组先单独落到一个目录：拆件只吃 unit，材质必须留在外面。
			onStage?.Invoke("正在抽取 unit…");
			var unitStage = Path.Combine(stagingRoot, "unit-stage");
			Directory.CreateDirectory(unitStage);
			var unitWritten = await WriteGroupAsync(writer, modDirectory, unitGroup, unitStage, cancellationToken).ConfigureAwait(false);
			if (unitWritten is null)
				return Legacy2SectionalOutcome.Failed("写出 unit 组失败：来源里没有任何一个 patch 提供被选中的 unit。", notes);

			// ② 拆 7 槽。输出目录单独给，避免边读边写。
			onStage?.Invoke("正在按 7 槽拆分…");
			var splitOutput = Path.Combine(stagingRoot, "unit-split");
			var split = await SectionalSplitter
				.ExecuteAsync(unitStage, context.GameDataFolder, splitOutput, cancellationToken)
				.ConfigureAwait(false);
			if (!split.IsValid)
			{
				notes.AddRange(split.Warnings);
				return Legacy2SectionalOutcome.Failed(
					"拆件失败：" + (split.Warnings.Count == 0 ? "（没有更多细节）" : string.Join("；", split.Warnings)), notes);
			}

			MoveAs(Path.Combine(splitOutput, split.PatchPath), Path.Combine(producedRoot, $"{archiveHex}.patch_0"));
			CopyIfExists(splitOutput, producedRoot, WardrobeSectionMapFile.FileName);
			CopyIfExists(splitOutput, producedRoot, SectionGroupFile.FileName);

			// ③ 材质组：一组一个 patch，从 patch_1 起向后排。
			var materialIndex = 1;
			var groupNames = new Dictionary<int, (string? Option, string? Source)>();
			foreach (var group in plan.Patches.Where(patch => patch.Role == SectionalPatchRole.Material))
			{
				onStage?.Invoke($"正在抽取材质组：{group.OptionName ?? "（未命名）"}…");
				var target = Path.Combine(stagingRoot, $"material-{materialIndex}");
				Directory.CreateDirectory(target);
				var written = await WriteGroupAsync(writer, modDirectory, group, target, cancellationToken).ConfigureAwait(false);
				if (written is null)
				{
					notes.Add($"材质组「{group.OptionName}」里没有任何一个 patch 提供被选中的 key，已跳过。");
					continue;
				}

				MoveAs(written, Path.Combine(producedRoot, $"{archiveHex}.patch_{materialIndex}"));
				groupNames[materialIndex] = (group.OptionName, group.SourceName);
				materialIndex++;
			}

			var materialGroups = materialIndex - 1;

			// ④ 换载荷：先备份旧的，整批替换；失败时它自己还原。
			onStage?.Invoke("正在替换 sectional/…");
			var payloadDirectory = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(modDirectory)
				?? ModNodePayloadLayout.GetVpkPatchDirectory(modDirectory);
			var swap = VpkPayloadSwap.Swap(payloadDirectory, producedRoot, Path.Combine(stagingRoot, "backup"));
			if (!swap.Succeeded)
				return Legacy2SectionalOutcome.Failed(swap.Error ?? "替换 sectional/ 失败。", notes);

			// ⑤ 清单：`patches.json` 是派生的，读一次旧的把用户填的选项名/顺序/备注接过来。
			//
			// **组名必须由这里补写。** `Rebuild` 的名字来自 `vpk2patch.json`（转换器写的报告），
			// 而 sectional 档根本没有那份报告 —— 不补的话材质选项在界面上全是无名条目，
			// 用户分不出哪一组是哪个来源。名字在依据文件里（`sources[].name`），规划也知道
			// 每一组属于谁，所以在这里按 patchIndex 盖回去。
			var previous = await SectionalPatchManifestFile.TryReadAsync(payloadDirectory, cancellationToken).ConfigureAwait(false);
			var manifest = SectionalPatchManifestFile.Rebuild(modDirectory, DateTimeOffset.UtcNow, previous);
			if (manifest is not null)
			{
				var named = manifest with
				{
					Patches = manifest.Patches
						.Select(entry => groupNames.TryGetValue(entry.PatchIndex, out var name)
							? entry with { OptionName = entry.OptionName ?? name.Option, SourceName = entry.SourceName ?? name.Source }
							: entry)
						.ToArray(),
				};
				var manifestError = SectionalPatchManifestFile.TryWrite(payloadDirectory, named);
				if (manifestError is not null) notes.Add($"写 patches.json 失败：{manifestError}");
			}

			// ⑦ 素体定义：与 vpk2patch 对称 —— 7 槽产物同样**是一具自定义素体**。
			//
			// 不写这份文件，sectional 产物就只是"一堆 patch + 一张 section 表"，护甲映射页、
			// 换甲、按槽位开关都认不出它。vpk2patch 那条路一直写着，这里漏了。
			// 位置必须在编译**之前**：ModCompileService 会把 payload 里的 custom-model.json
			// 一起带进 Overwrite，与 vpk2patch 的产物形状保持一致。
			var mapPath = Path.Combine(payloadDirectory, WardrobeSectionMapFile.FileName);
			if (File.Exists(mapPath))
			{
				onStage?.Invoke("正在登记自定义素体…");
				var map = await WardrobeSectionMapFile.ReadAsync(mapPath, cancellationToken).ConfigureAwait(false);
				var definition = SectionalModelDefinition.Build(
					map.Units,
					"custom-" + Guid.NewGuid().ToString("N"),
					Path.GetFileName(modDirectory));
				// 一条 piece 都没有就不落盘：空定义比没有定义更糟 —— 它会让"这是一具素体"
				// 成立却不含任何组件，界面上是一具点了没反应的素体。
				if (definition.Pieces is { Count: > 0 })
				{
					await File.WriteAllTextAsync(
						Path.Combine(payloadDirectory, VpkCustomModelFile.FileName),
						JsonSerializer.Serialize(definition, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
						cancellationToken).ConfigureAwait(false);
				}
				else
				{
					notes.Add("没有从 section 表里认出任何槽位，跳过写 custom-model.json。");
				}
			}

			// ⑥ 编译：与 vpk2patch 完全同一段。
			onStage?.Invoke("正在编译生效层…");
			var compile = await ModCompileService
				.CompileAsync(modSelector, modDirectory, context, onStage: onStage, cancellationToken: cancellationToken)
				.ConfigureAwait(false);
			if (!compile.Ok)
				return new Legacy2SectionalOutcome(false, compile.Error ?? "编译失败。", 1, materialGroups, notes);

			notes.AddRange(compile.Notes);
			return new Legacy2SectionalOutcome(true, null, 1, materialGroups, notes);
		}
		catch (Exception exception) when (exception is IOException
			or InvalidDataException
			or UnauthorizedAccessException
			or NotSupportedException
			or ArgumentException)
		{
			return Legacy2SectionalOutcome.Failed(exception.Message, notes);
		}
		finally
		{
			try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true); } catch { }
		}
	}

	/// <summary>把一组规划写成一个 patch 文件，返回它的路径（写不出来返回 <c>null</c>）。</summary>
	/// <param name="modDirectory">
	/// 来源 patch 所在的 Mod 目录。<see cref="SectionalPlannedSelection.SourcePatch"/> 记的是
	/// **文件名**（依据文件里就是这么存的），拼路径必须带上它，否则会按进程当前目录去找。
	/// </param>
	private static async ValueTask<string?> WriteGroupAsync(
		PatchSubsetWriter writer,
		string modDirectory,
		SectionalPlannedPatch group,
		string outputDirectory,
		CancellationToken cancellationToken)
	{
		// 一组里的 key 可能分散在多个来源 patch 上：按来源分组，先取第一个有货的当模板，
		// 再把全部选择一次性交给 WriteSelectedAsync（它支持跨来源）。
		var selections = group.Selections
			.Select(selection => new PatchSubsetSelection(
				Path.Combine(modDirectory, selection.SourcePatch),
				new AdaptationAssetKey(selection.Key.TypeId, selection.Key.FileId)))
			.ToArray();
		if (selections.Length == 0) return null;

		var template = selections[0].SourcePatchTocPath;
		if (!File.Exists(template)) return null;
		var result = await writer
			.WriteSelectedAsync(template, outputDirectory, selections, overwriteExisting: true, cancellationToken)
			.ConfigureAwait(false);
		var produced = result?.TocFilePath;
		return string.IsNullOrWhiteSpace(produced) || !File.Exists(produced) ? null : produced;
	}

	private static async Task<IReadOnlyDictionary<string, SectionalPatchContents>> ScanAsync(
		IReadOnlyList<string> patchFiles,
		CancellationToken cancellationToken)
	{
		var scanner = new PatchTocScanner();
		var scanned = new Dictionary<string, SectionalPatchContents>(StringComparer.OrdinalIgnoreCase);
		foreach (var path in patchFiles)
		{
			var entries = await scanner.ScanEntriesAsync(path, cancellationToken).ConfigureAwait(false);
			var keys = entries
				.Select(entry => new SectionalAssetRef(entry.AssetKey.TypeId, entry.AssetKey.FileId))
				.ToHashSet();
			// 键用**文件名**：依据文件里的 PatchFiles 和规划里的 SourcePatch 都是文件名，
			// 用完整路径做键会让"来源里没有白名单内的 unit"这种假失败出现。
			scanned[Path.GetFileName(path)] = new SectionalPatchContents(keys);
		}

		return scanned;
	}

	/// <summary>从 <c>&lt;archive&gt;.patch_N</c> 里取 archive（16 位十六进制）。</summary>
	private static string ArchiveHexOf(string patchPath)
	{
		var name = Path.GetFileName(patchPath);
		var marker = name.LastIndexOf(".patch_", StringComparison.OrdinalIgnoreCase);
		if (marker <= 0) return name;
		var archive = name[..marker];
		return ulong.TryParse(archive, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ? archive : name;
	}

	private static void MoveAs(string source, string destination)
	{
		foreach (var suffix in new[] { "", ".gpu_resources", ".stream" })
			if (File.Exists(source + suffix)) File.Move(source + suffix, destination + suffix, overwrite: true);
	}

	private static void CopyIfExists(string fromDirectory, string toDirectory, string fileName)
	{
		var source = Path.Combine(fromDirectory, fileName);
		if (!File.Exists(source)) return;
		File.Copy(source, Path.Combine(toDirectory, fileName), overwrite: true);
	}
}
