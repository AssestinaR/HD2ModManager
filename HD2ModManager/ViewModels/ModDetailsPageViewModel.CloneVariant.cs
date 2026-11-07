using System.IO;
using System.Windows.Input;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

// 「复制为变体」：把一个 sectional / vpk2patch 类 Mod 复制成独立命名空间的一份副本，
// 于是两套外观能同时在场。
//
// 逻辑全在 ModCloneService 里（CLI 的 `mod clone` 走同一份实现）。这里只做三件事：
// 取一个没被用过的变体名、把过程挂进后台任务让用户看得见、复制完重新同步库。
public partial class ModDetailsPageViewModel
{
	private RelayCommand? _cloneVariantCommand;

	public ICommand CloneVariantCommand =>
		_cloneVariantCommand ??= new RelayCommand(async _ => await CloneVariantAsync(), _ => CanCloneVariant);

	/// <summary>只有 sectional / vpk2patch 类 Mod 能复制：其余类型没有"命名空间家族"这回事。</summary>
	public bool CanCloneVariant
	{
		get
		{
			if (!TryGetCurrentNode(out var node)) return false;
			var directory = Path.Combine(_library.ModsRootDirectory, node.RelativePath);
			if (!Directory.Exists(directory)) return false;
			var kind = new ModStorageKindResolver(new PatchFileNameParser()).Resolve(directory);
			return kind is ModStorageKind.Sectional or ModStorageKind.Vpk2Patch;
		}
	}

	private async Task CloneVariantAsync()
	{
		if (!TryGetCurrentNode(out var node)) return;
		var libraryRoot = _library.ModsRootDirectory;
		var sourceDirectory = Path.Combine(libraryRoot, node.RelativePath);
		if (!Directory.Exists(sourceDirectory)) return;

		var variant = ModCloneService.NextVariantName(libraryRoot, node.Metadata.Name);
		var task = _backgroundTasks?.Enqueue(
			BackgroundTaskKind.Other, "复制为变体", sourceDirectory,
			origin: "Mod 详情", userVisibleReason: "复制来源并用新命名空间重新转换，两套外观可以同时部署。",
			canCancel: true, presentation: BackgroundTaskPresentation.Foreground);
		task?.MarkRunning($"正在复制为「{variant}」…");

		try
		{
			var result = await ModCloneService.CloneAsync(
				libraryRoot,
				_paths.AppRootDirectory,
				SettingsService.GetGameDataFolder(),
				sourceDirectory,
				node.Metadata.Name,
				node.RelativePath,
				variant,
				stage => task?.UpdateStage(stage),
				task?.CancellationToken ?? CancellationToken.None).ConfigureAwait(true);

			if (!result.Ok)
			{
				task?.MarkFailed(result.Error ?? "复制失败。");
				return;
			}

			// 副本已经在库目录里了，同步一次把它登记成节点。
			await _library.SynchronizeAsync().ConfigureAwait(true);
			task?.UpdateStage($"已复制为「{result.CloneName}」：两套外观的材质 id 不同，可以同时启用。");
			task?.MarkCompleted();
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or InvalidOperationException)
		{
			task?.MarkFailed(exception.Message);
		}
	}
}
