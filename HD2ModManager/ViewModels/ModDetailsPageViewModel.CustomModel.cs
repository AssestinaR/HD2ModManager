using System.IO;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Services;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;

namespace HD2ModManager.ViewModels;

/// <summary>
/// 「生成sectional版」：把来源（主 Mod + 递归子级）的 patch 平铺进一个新条目，写下转换依据，
/// 导入后**立刻**跑一次 sectional 转换与编译。
///
/// <para>
/// <b>与旧实现的区别。</b>旧实现调 <c>CustomModelConversionService.GenerateAsync</c> 生成一条
/// 新的子集 patch，再把它搬进 <c>sectional/</c>——根上什么都不留，于是那个条目永远不可重构建。
/// 现在按四段链路走：导入只放来源（根上平铺），转换才产出 <c>sectional/</c>，
/// 与 vpk2patch 完全同形。
/// </para>
/// </summary>
public partial class ModDetailsPageViewModel
{
	private async Task OpenCustomModelConversionAsync()
	{
		if (!TryGetCurrentNode(out var source)
			|| System.Windows.Application.Current?.MainWindow?.DataContext is not ShellViewModel shell) return;
		ModEquipmentSourceFacts? facts = null;
		var converter = new CustomModelConversionService(_paths, _library.InformationReader);
		async Task<ModEquipmentSourceFacts> ReadFactsAsync(CancellationToken token = default)
		{
			var id = Guid.NewGuid();
			try
			{
				return await _equipmentSourceFactsReader.ReadAsync(source, _library.ModsRootDirectory,
					new ModInformationRequest(ModInformationKind.AssetInventory, "CustomModelConversion",
						Context: ModInformationRequestContext.Create(ModInformationCacheScope.None, id, operationName: "CustomModelConversion"))
					{ Property = ModInformationPropertyKind.UnitPartMapping }, token);
			}
			finally { _equipmentSourceFactsReader.ClearOperation(id); }
		}
		var bar = new CustomModelConversionBottomBarViewModel(Name, async operation =>
		{
			if (facts is null || operation.SelectedSources.Count == 0) return;
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

				// ① 来源：当前条目 + 递归子级。必须是 legacy（patch 在根上），否则拷过去只会得到空条目。
				task?.UpdateStage("正在收集来源条目");
				var sources = CollectSectionalSources(source);
				if (sources.Count == 0)
					throw new InvalidOperationException("没有可用的来源：来源条目根上都没有 patch。");

				// ② 干净资产白名单：选中护甲/头盔的 unit → 它们引用的材质 → 那些材质引用的贴图。
				task?.UpdateStage("正在整理护甲与头盔范围");
				var freshFacts = await ReadFactsAsync(token);
				var unitKeys = operation.SelectedSources
					.SelectMany(entry => entry.Parts)
					.Where(part => !part.IsCullingMesh)
					.Select(part => part.UnitAssetKey)
					.Distinct()
					.ToArray();
				var whitelist = await SectionalWhitelistBuilder
					.BuildFromPatchesAsync(
						unitKeys.Select(key => new AdaptationAssetKey(key.TypeId, key.FileId)).ToArray(),
						freshFacts.SourcePatchTocPaths,
						token)
					.ConfigureAwait(true);
				var whitelistError = SectionalWhitelistBuilder.Describe(whitelist);
				if (whitelistError is not null) throw new InvalidOperationException(whitelistError);

				// ③ 建条目目录 → 平铺拷来源 → 写转换依据。
				task?.UpdateStage("正在把来源 patch 拷进新条目");
				var safeName = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
				var modDirectory = Path.Combine(output, $"{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}");
				Directory.CreateDirectory(modDirectory);
				var assembly = SectionalSourceAssembler.Assemble(
					modDirectory,
					sources,
					ToSelection(operation.SelectedArmor),
					ToSelection(operation.SelectedHelmet),
					whitelist);
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
				var node = _library.Snapshot.Nodes[imports[0]];
				var importedDirectory = Path.Combine(_library.ModsRootDirectory, node.RelativePath);

				// ⑤ 立刻转换 + 编译（与 vpk 导入后的自动重构建对称）。
				task?.UpdateStage("正在转换并编译：按 7 槽拆分 unit 组");
				var converted = await Legacy2SectionalConverter.RunAsync(
					node.Id.Value.ToString("N"),
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
			facts = await ReadFactsAsync();
			bar.SetSources(await converter.InspectAsync(facts));
		}
		catch (Exception exception) { bar.Status = "来源无效：" + exception.Message; }
	}

	/// <summary>底栏里的"不包含"是 <c>Source == null</c>；转成依据文件里的选择记录。</summary>
	private static SectionalSourceSelection? ToSelection(CustomModelSourceOption? option)
		=> option?.Source is { } entry && !string.IsNullOrWhiteSpace(entry.ArchiveId)
			? new SectionalSourceSelection(entry.ArchiveId, entry.DisplayName)
			: null;

	/// <summary>
	/// 收集来源：当前条目 + 它的递归子级。
	/// <para>
	/// <b>为什么子级也要收。</b>一个主体的外观常常拆在主 Mod 与若干子级里；只拷主 Mod 的话，
	/// 产物会缺掉子级贡献的 unit 与材质，而用户在选择时看着的是"整个系列"。
	/// </para>
	/// </summary>
	private IReadOnlyList<SectionalSourceCandidate> CollectSectionalSources(ModNode host)
	{
		var root = _library.ModsRootDirectory;
		var ordered = new List<(ModNode Node, string Name)>
		{
			(host, string.IsNullOrWhiteSpace(host.Metadata.Name) ? host.RelativePath : host.Metadata.Name),
		};
		var graph = OptionRelationshipGraph.Create(_library.Snapshot);
		foreach (var child in graph.GetDescendantOptions(host.Id))
			ordered.Add((child, string.IsNullOrWhiteSpace(child.Metadata.Name) ? child.RelativePath : child.Metadata.Name));

		var candidates = new List<SectionalSourceCandidate>(ordered.Count);
		foreach (var (node, name) in ordered)
		{
			var directory = Path.Combine(root, node.RelativePath);
			if (!Directory.Exists(directory)) continue;
			// 只认 legacy：来源 patch 必须在根上（转换产物与 vpk2patch 的 patch 在 payload 里，
			// 拷根目录只会得到空条目）。
			if (SectionalSplitter.EnumeratePatchFiles(directory).Count == 0) continue;
			candidates.Add(new SectionalSourceCandidate(node.Id.Value.ToString("N"), name, directory));
		}

		return candidates;
	}
}
