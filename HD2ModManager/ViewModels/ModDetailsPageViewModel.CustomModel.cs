using System.IO;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Services;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;

namespace HD2ModManager.ViewModels;

/// <summary>
/// 「生成sectional版」：把来源（主 Mod + 递归内的 legacy 子级）的 patch 平铺进一个新条目，
/// 写下转换依据，导入后**立刻**跑一次 sectional 转换与编译。
///
/// <para>
/// <b>与旧实现的区别。</b>旧实现调 <c>CustomModelConversionService.GenerateAsync</c> 生成一条
/// 新的子集 patch，再把它搬进 <c>sectional/</c>——根上什么都不留，于是那个条目永远不可重构建。
/// 现在按四段链路走：导入只放来源（根上平铺），转换才产出 <c>sectional/</c>，
/// 与 vpk2patch 完全同形。
/// </para>
///
/// <para>
/// <b>"递归"与"只认 legacy"不是二选一。</b>从主 Mod 开始逐级向下走，但每一步只把**根上有 patch**
/// 的条目收进来源集——非 legacy 的子级（vpk2patch / sectional）本来就不属于这个来源集，
/// 被筛掉是定义使然，不是遗漏。
/// </para>
/// </summary>
public partial class ModDetailsPageViewModel
{
	private async Task OpenCustomModelConversionAsync()
	{
		if (!TryGetCurrentNode(out var source)
			|| System.Windows.Application.Current?.MainWindow?.DataContext is not ShellViewModel shell) return;
		var converter = new CustomModelConversionService(_paths, _library.InformationReader);
		var sourceNodes = CollectSectionalSources(source);

		async Task<ModEquipmentSourceFacts> ReadFactsAsync(ModNode node, CancellationToken token = default)
		{
			var id = Guid.NewGuid();
			try
			{
				return await _equipmentSourceFactsReader.ReadAsync(node, _library.ModsRootDirectory,
					new ModInformationRequest(ModInformationKind.AssetInventory, "CustomModelConversion",
						Context: ModInformationRequestContext.Create(ModInformationCacheScope.None, id, operationName: "CustomModelConversion"))
					{ Property = ModInformationPropertyKind.UnitPartMapping }, token);
			}
			finally { _equipmentSourceFactsReader.ClearOperation(id); }
		}

		var bar = new CustomModelConversionBottomBarViewModel(Name, async operation =>
		{
			// "名称为空 / 没选来源"已由底栏自己的 GenerateAsync 拦下并写进状态行；
			// 这里只剩一条它看不到的事实：整条来源链上一个 patch 都没有。
			if (sourceNodes.Count == 0)
			{
				operation.Status = "这个 Mod 与它的子级里都没有来源 patch（都不是 legacy 类），没有可转换的东西。";
				return;
			}
			var task = _backgroundTasks?.Enqueue(BackgroundTaskKind.Other, "生成sectional版", operation.OutputDirectory,
				origin: "Mod 详情",
				userVisibleReason: "把来源 patch 平铺进新条目，再按 sectional 转换出 7 槽 unit 组与材质组。",
				canCancel: true, presentation: BackgroundTaskPresentation.Foreground);
			task?.MarkRunning("正在检查来源并保存编辑");
			var token = task?.CancellationToken ?? CancellationToken.None;
			var workspaceLocked = false;
			try
			{
				await shell.FlushArmorRegistryWorkspaceAsync();
				workspaceLocked = true;
				var name = operation.Name;
				var output = operation.OutputDirectory;
				var gameData = SettingsService.GetGameDataFolder();

				// ① 来源（已按 legacy 过滤好的整条链）。
				task?.UpdateStage("正在收集来源条目");
				var candidates = sourceNodes.Select(ToCandidate).ToArray();
				if (candidates.Length == 0)
					throw new InvalidOperationException("没有可用的来源：来源条目根上都没有 patch。");

				// ② 干净资产白名单：选中护甲/头盔的 unit → 它们引用的材质 → 那些材质引用的贴图。
				//
				// 引用关系要看**整条来源链**的 patch：选中的护甲可能来自某个子级，
				// 只读主 Mod 的 patch 会算不出它的材质闭包。
				task?.UpdateStage("正在整理护甲与头盔范围");
				var patchPaths = new List<string>();
				foreach (var node in sourceNodes)
				{
					var facts = await ReadFactsAsync(node, token);
					patchPaths.AddRange(facts.SourcePatchTocPaths);
				}

				var intentUnits = operation.SelectedSources
					.SelectMany(entry => entry.Parts)
					.Where(part => !part.IsCullingMesh)
					.Select(part => new AdaptationAssetKey(part.UnitAssetKey.TypeId, part.UnitAssetKey.FileId))
					.Distinct()
					.ToArray();

				// 装备目录只列 kit 槽位上的 unit（实测某套护甲只有 9 个），而系列里的装饰件
				// （翅膀 / 鞋子 / 上衣…）是同一个 patch 里另外的 unit —— 它们是这套护甲的一部分，
				// 只按意图集会全部丢掉（2026-11-10 线上：11 个来源只剩 9 个材质组 + 1 个 unit 组）。
				var unitKeys = await SectionalWhitelistBuilder
					.ExpandIntentUnitsAsync(intentUnits, patchPaths, token)
					.ConfigureAwait(true);

				var whitelist = await SectionalWhitelistBuilder
					.BuildFromPatchesAsync(unitKeys, patchPaths, token)
					.ConfigureAwait(true);
				var whitelistError = SectionalWhitelistBuilder.Describe(whitelist);
				if (whitelistError is not null) throw new InvalidOperationException(whitelistError);

				// ③ 建条目目录 → 平铺拷来源 → 写转换依据。
				task?.UpdateStage("正在把来源 patch 拷进新条目");
				var safeName = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
				var modDirectory = Path.Combine(output, $"{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}");
				Directory.CreateDirectory(modDirectory);
				var assembly = await SectionalSourceAssembler.AssembleAsync(
					modDirectory,
					candidates,
					ToSelection(operation.SelectedArmor),
					ToSelection(operation.SelectedHelmet),
					whitelist,
					token).ConfigureAwait(true);
				if (!assembly.Ok)
					throw new InvalidOperationException(assembly.Error ?? "装配来源失败。");
				task?.SetOutputArtifacts(modDirectory, null);

				// ④ 导入：导入之后目录才算"库里的条目"，路径也可能被规范化，所以后面一律用库里的路径。
				task?.UpdateStage("正在导入新 Mod");
				var imported = await new ImportService(_library, informationCenter: _library.InformationCenter,
					onError: message => operation.Status = "已生成，但导入失败：" + message)
					.ImportPathAsync(modDirectory, token);
				if (imported.Count == 0)
					throw new InvalidOperationException("新 Mod 导入失败；请查看输出目录后重试。");

				var imports = imported
					.Select(guid => Guid.TryParse(guid, out var id) ? new ModNodeId(id) : (ModNodeId?)null)
					.Where(id => id is not null)
					.Select(id => id!.Value)
					.Where(id => _library.Snapshot.Nodes.ContainsKey(id))
					.ToArray();
				if (imports.Length == 0)
					throw new InvalidOperationException("导入成功但找不到库里的节点，无法继续转换。");
				var importedNode = _library.Snapshot.Nodes[imports[0]];
				var importedDirectory = Path.Combine(_library.ModsRootDirectory, importedNode.RelativePath);

				// ⑤ 立刻转换 + 编译（与 vpk 导入后的自动重构建对称）。
				task?.UpdateStage("正在转换并编译：按 7 槽拆分 unit 组");
				var converted = await Legacy2SectionalConverter.RunAsync(
					importedNode.Id.Value.ToString("N"),
					importedDirectory,
					new VpkHostContext(gameData, SettingsService.CreateStoragePaths()),
					stage => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => task?.UpdateStage(stage)),
					token).ConfigureAwait(true);
				if (!converted.Ok)
					throw new InvalidOperationException(converted.Error ?? "转换失败。");

				operation.Status = $"已生成 {converted.MaterialGroups} 个材质组 · 1 个 unit 组"
					+ (assembly.Notes.Count == 0 ? string.Empty : $"（{string.Join("；", assembly.Notes)}）");
				await _library.RefreshAssetSummariesAsync(token);
				task?.MarkCompleted();
				_notifications?.Show(operation.Status, NotificationLevel.Info, TimeSpan.FromSeconds(12));
			}
			catch (OperationCanceledException) { task?.MarkCanceled(); throw; }
			catch (Exception exception) { task?.MarkFailed(exception.Message); throw; }
			finally { if (workspaceLocked) shell.EndArmorRegistryWorkspaceEdit(); }
		});
		shell.OpenCustomModelConversion(bar);
		try
		{
			if (!await HasCurrentGameDataIndexAsync())
				throw new InvalidOperationException("请先在设置页重建游戏资产索引。");
			if (sourceNodes.Count == 0)
				throw new InvalidOperationException("这个 Mod 与它的子级里都没有来源 patch（都不是 legacy 类）。");

			// 候选表覆盖**整条来源链**：主 Mod 与每个 legacy 子级各读一次事实再合并。
			// 只读主 Mod 的话，护甲/头盔拆在子级里的形态根本进不了选择表。
			var catalog = new List<EquipmentUnitCatalogEntry>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var node in sourceNodes)
			{
				var facts = await ReadFactsAsync(node);
				foreach (var entry in await converter.InspectAsync(facts))
				{
					// 同一个 archive 只留一份：不同来源可能都报同一件护甲（共用 unit 的情况）。
					if (!seen.Add(entry.ArchiveId)) continue;
					catalog.Add(entry);
				}
			}

			bar.SetSources(catalog
				.OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
				.ToArray());
		}
		catch (Exception exception) { bar.Status = "来源无效：" + exception.Message; }
	}

	/// <summary>底栏里的"不包含"是 <c>Source == null</c>；转成依据文件里的选择记录。</summary>
	private static SectionalSourceSelection? ToSelection(CustomModelSourceOption? option)
		=> option?.Source is { } entry && !string.IsNullOrWhiteSpace(entry.ArchiveId)
			? new SectionalSourceSelection(entry.ArchiveId, entry.DisplayName)
			: null;

	private SectionalSourceCandidate ToCandidate(ModNode node)
		=> new(
			node.Id.Value.ToString("N"),
			string.IsNullOrWhiteSpace(node.Metadata.Name) ? node.RelativePath : node.Metadata.Name,
			Path.Combine(_library.ModsRootDirectory, node.RelativePath));

	/// <summary>
	/// 收集来源：主条目 + 递归子级里**根上有 patch 的**那些（即 legacy 类）。
	/// <para>
	/// 递归与"只认 legacy"是同一个集合的两个约束，不是两条路：逐级向下走，边遇到非 legacy 的子级
	/// 就跳过——它不属于这个来源集，而不是"漏掉了"。
	/// </para>
	/// </summary>
	private IReadOnlyList<ModNode> CollectSectionalSources(ModNode host)
	{
		var root = _library.ModsRootDirectory;
		var ordered = new List<ModNode> { host };
		var graph = OptionRelationshipGraph.Create(_library.Snapshot);
		ordered.AddRange(graph.GetDescendantOptions(host.Id));

		var nodes = new List<ModNode>(ordered.Count);
		foreach (var node in ordered)
		{
			var directory = Path.Combine(root, node.RelativePath);
			if (!Directory.Exists(directory)) continue;
			// 根上没有 patch ⇒ 不是 legacy（来源 patch 在 payload 里或压根没有），不进入来源集。
			if (SectionalSplitter.EnumeratePatchFiles(directory).Count == 0) continue;
			nodes.Add(node);
		}

		return nodes;
	}
}
