using System.IO;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

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
			var task = _backgroundTasks?.Enqueue(BackgroundTaskKind.Other, "一键转为自定义素体", operation.OutputDirectory,
				origin: "Mod 详情", userVisibleReason: "复制可见组件并生成自定义 Unit ID。",
				canCancel: true, presentation: BackgroundTaskPresentation.Foreground);
			task?.MarkRunning("正在检查来源并保存编辑");
			var token = task?.CancellationToken ?? CancellationToken.None;
			var workspaceLocked = false;
			var progress = new Progress<CrossArmorTransferProgress>(update =>
			{
				task?.UpdateStage(update.StageText);
				task?.UpdateProgress(update.Total > 0 ? (double)update.Completed / update.Total : null);
			});
			try
			{
				await shell.FlushArmorRegistryWorkspaceAsync();
				workspaceLocked = true;
				var selected = operation.SelectedSources.Select(s => s.ArchiveId).ToArray();
				var name = operation.Name;
				var output = operation.OutputDirectory;
				operation.Status = "正在复制可见 Unit 并生成新 ID，不修改来源 Mod…";
				// The bottom bar can stay open while the source Mod changes.
				var freshFacts = await ReadFactsAsync(token);
				var gameData = SettingsService.GetGameDataFolder();
				var result = await Task.Run(() => converter.GenerateAsync(freshFacts, selected, name, output, gameData, token, progress), token);
				task?.SetOutputArtifacts(result.OutputDirectory, result.DefinitionPath);

				// 拆件：把换甲产物按「7 标准槽 × 身形」重排，让每块几何都能在游戏里单独开关。
				//
				// 只拆**装着几何的那个 patch**：真实 Mod 目录常常不止一个 patch
				// （实测 18 个分件 Mod 里 4 个有 patch_0 + patch_1），但几何集中在一个里面，
				// patch_1 那种只有几 KB，是附属小文件。覆盖它们会把不该动的成员打乱。
				task?.UpdateStage("正在拆件：按 7 槽 × 身形重排 section 组");
				task?.UpdateProgress(null);
				var split = await SectionalSplitter
					.ExecuteAsync(result.OutputDirectory, gameData, result.OutputDirectory, token)
					.ConfigureAwait(true);
				if (!split.IsValid)
				{
					throw new InvalidOperationException(
						"拆件失败，已保留已生成的产物但不会导入；请查看 " + result.OutputDirectory
						+ (split.Warnings.Count == 0 ? string.Empty : "：" + string.Join("；", split.Warnings)));
				}

				await shell.ReloadArmorRegistryWorkspaceAsync();
				var mergedSlots = split.Plan.Targets.Count(target => target.DonorUnitIds.Count != 0);
				// 合并把 donor unit 并进了宿主，但素体定义还是按老 unit 记的 ——
				// 不剪的话护甲映射页会列出"选了什么都不反应"的组件（悬空引用）。
				var compaction = await CustomModelSectionalCompaction
					.PruneMergedPiecesAsync(
						CoreServices.CreateRuntimeArmorRegistryService(SettingsService.CreateStoragePaths()),
						result.OutputDirectory,
						[.. split.Sections.Units.Select(unit => unit.FileId)],
						token)
					.ConfigureAwait(true);
				operation.Status = $"已生成 {compaction.RemainingPieceCount} 个组件 · {split.Groups.Groups.Count} 个 section 组"
					+ (mergedSlots == 0
						? "（每块一块，无需合并）"
						: $"（{mergedSlots} 个槽发生过合并，剪掉 {compaction.RemovedPieceCount} 个悬空组件）");

				// **必须导入成 Mod，没有"只生成不导入"这条路。**
				//
				// 素体不是独立对象，它是某个 Mod 的产物：那套 Unit 只有跟着那个 Mod 部署才有意义，
				// 而"归属"的唯一事实来源就是 Mod 目录里的 `custom-model.json`。不导入就等于
				// 留下一具没有归属的素体 —— 护甲映射页里挂着一个，选它什么都不发生。
				// 所以这里无条件导入；导入失败就把刚登记的素体收回去，并把原因报出来，
				// 绝不留下"登记了但没有 Mod"的半状态。
				task?.UpdateStage("正在导入新 Mod");
				task?.UpdateProgress(null);
				var imported = await new ImportService(_library, informationCenter: _library.InformationCenter,
					onError: message => operation.Status = "已生成，但导入失败：" + message)
					.ImportPathAsync(result.OutputDirectory, token);
				if (imported.Count == 0)
				{
					var reason = await RollBackRegistrationAsync(result.Model.Key);
					throw new InvalidOperationException(
						"新 Mod 导入失败，已撤销这次素体登记；请查看输出目录后重试。"
						+ (reason is null ? string.Empty : $"（撤销也失败了：{reason}）"));
				}

				operation.Status += " · 已作为新 Mod 导入（素体依附于它，删除 Mod 会一并删除）";
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

	/// <summary>
	/// 撤销刚登记的素体（导入失败时的回滚）。返回 <c>null</c> 表示撤干净了，否则是失败原因。
	/// <para>
	/// <b>为什么要回滚。</b>登记发生在生成流程里，而"成为某个 Mod 的产物"要等导入完成。
	/// 导入失败还留着记录，用户就会在护甲映射页看到一个选了什么都没反应的素体 ——
	/// 那种"看着正常但不生效"的半状态比直接失败更难查。
	/// </para>
	/// </summary>
	private static async Task<string?> RollBackRegistrationAsync(string modelKey)
	{
		try
		{
			var ownership = new CustomModelOwnershipService(
				CoreServices.CreateRuntimeArmorRegistryService(SettingsService.CreateStoragePaths()),
				SettingsService.CreateStoragePaths().ModsDirectory);
			await ownership.RemoveAsync(modelKey).ConfigureAwait(false);
			return null;
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or InvalidOperationException)
		{
			LogService.Warn($"撤销素体登记失败（{modelKey}）：{exception.Message}");
			return exception.Message;
		}
	}
}
