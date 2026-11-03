using System.IO;
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
				await shell.ReloadArmorRegistryWorkspaceAsync();
				operation.Status = $"已生成 {result.Model.Pieces?.Count} 个组件：{result.OutputDirectory}";

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
