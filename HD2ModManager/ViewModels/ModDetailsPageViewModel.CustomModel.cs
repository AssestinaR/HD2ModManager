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
				var import = operation.ImportToLibrary;
				operation.Status = "正在复制可见 Unit 并生成新 ID，不修改来源 Mod…";
				// The bottom bar can stay open while the source Mod changes.
				var freshFacts = await ReadFactsAsync(token);
				var gameData = SettingsService.GetGameDataFolder();
				var result = await Task.Run(() => converter.GenerateAsync(freshFacts, selected, name, output, gameData, token, progress), token);
				task?.SetOutputArtifacts(result.OutputDirectory, result.DefinitionPath);
				await shell.ReloadArmorRegistryWorkspaceAsync();
				operation.Status = $"已生成 {result.Model.Pieces?.Count} 个组件：{result.OutputDirectory}";
				if (import)
				{
					task?.UpdateStage("正在导入新 Mod");
					task?.UpdateProgress(null);
					var importer = new ImportService(_library, informationCenter: _library.InformationCenter,
						onError: message => operation.Status = "已生成，但导入失败：" + message);
					var imported = await importer.ImportPathAsync(result.OutputDirectory, token);
					if (imported.Count == 0)
						throw new InvalidOperationException("Patch 已生成且素体已登记，但新 Mod 导入失败；请手动导入输出目录。");
					operation.Status += " · 已作为新 Mod 导入";
				}
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
}
