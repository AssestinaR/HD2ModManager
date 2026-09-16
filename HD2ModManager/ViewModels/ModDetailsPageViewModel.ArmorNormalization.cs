using System.IO;
using System.Windows;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

public partial class ModDetailsPageViewModel
{
    private bool _armorNormalizationRunning;
    private bool CanNormalizeArmor => !_disposed && CanUsePatchTools && !_armorNormalizationRunning && TryGetCurrentNode(out _);

    private void OpenArmorNormalization()
    {
        if (!CanNormalizeArmor) return;
        if (System.Windows.Application.Current?.MainWindow?.DataContext is not ShellViewModel shell) return;
        shell.OpenSameKeyRebuild(new SameKeyRebuildBottomBarViewModel(
            NormalizeArmorAsync, "选择规整护甲 patch 包输出根目录"));
    }

    private async Task NormalizeArmorAsync(string outputRootDirectory, bool importToLibrary)
    {
        if (!CanNormalizeArmor || !TryGetCurrentNode(out var source)) return;
        _armorNormalizationRunning = true;
        NormalizeArmorCommand.RaiseCanExecuteChanged();
        BackgroundTaskItem? task = null;
        string? stagingDirectory = null;
        string? destination = null;
        try
        {
            var outputRoot = Path.GetFullPath(outputRootDirectory);
            var gameData = SettingsService.GetGameDataFolder();
            var gameRoot = Directory.GetParent(Path.GetFullPath(gameData))?.FullName ?? gameData;
            if (IsNormalizationPathInside(outputRoot, _library.ModsRootDirectory)
                || IsNormalizationPathInside(outputRoot, gameRoot))
                throw new InvalidOperationException("请选择 Mod 库和游戏目录以外的输出位置。");
            if (!await HasCurrentGameDataIndexAsync())
                throw new InvalidOperationException("请在设置页重建资产索引。");

            task = _backgroundTasks?.Enqueue(
                BackgroundTaskKind.RepairMods, "规整护甲 patch 包", "护甲规整", "Mod 详情",
                "生成独立的规整护甲 Mod。", canCancel: true,
                presentation: BackgroundTaskPresentation.Foreground);
            task?.MarkRunning("正在检查护甲部件与模型复用");
            var cancellationToken = task?.CancellationToken ?? CancellationToken.None;
            await using var reader = CoreServices.CreateModInformationReader();
            var service = CoreServices.CreateDirectArmorNormalizationService(_paths, reader);
            var inspection = await Task.Run(() => service.InspectAsync(
                source, _library.ModsRootDirectory, gameData, cancellationToken).AsTask());
            if (!inspection.IsReady)
                throw new InvalidOperationException(inspection.Issues.Count == 0
                    ? "当前 Mod 未检测到可规整的护甲。"
                    : string.Join(Environment.NewLine, inspection.Issues.Select(issue => issue.Message)));

            var warnings = inspection.Armors.SelectMany(armor => armor.Warnings)
                .Concat(inspection.Issues.Where(issue => issue.Severity == CoreIssueSeverity.Warning).Select(issue => issue.Message))
                .Distinct().ToArray();
            if (warnings.Length != 0 && MessageBox.Show(
                System.Windows.Application.Current.MainWindow,
                "规整按同一 patch 内的护甲使用相同模型处理；非承载护甲部件将隐藏。" + Environment.NewLine
                + string.Join(Environment.NewLine, warnings) + Environment.NewLine + "是否继续生成？",
                "护甲规整警告", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                task?.MarkCanceled();
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(outputRoot);
            destination = Path.Combine(outputRoot, $"{SanitizeFileName(source.Metadata.Name)}+{DateTime.Now:yyyyMMdd-HHmmssfff}+护甲规整");
            stagingDirectory = Path.Combine(outputRoot, ".armor-normalize-" + Guid.NewGuid().ToString("N"));
            var sourceDirectory = Path.GetFullPath(Path.Combine(_library.ModsRootDirectory, source.RelativePath));
            var progress = new Progress<ArmorNormalizationProgress>(value =>
                task?.MarkRunning($"{value.Stage}（{value.Completed}/{value.Total}）"));
            task?.MarkRunning("正在生成规整护甲 patch 包");
            var result = await Task.Run(() => service.ExecuteAsync(
                source, sourceDirectory, stagingDirectory, gameData, cancellationToken, progress).AsTask());
            if (!result.IsSuccessful || result.ProcessedPatches == 0 || result.Issues.Any(issue => issue.Severity == CoreIssueSeverity.Error))
                throw new InvalidOperationException("规整失败：" + string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingDirectory, destination);
            stagingDirectory = null;
            task?.SetOutputArtifacts(destination, null);
            if (importToLibrary)
            {
                task?.MarkRunning("正在导入并替换当前 Mod");
                await _library.ReplaceStoredFilesAsync(source.Id, destination, CancellationToken.None);
            }
            task?.MarkCompleted();
            var summary = $"护甲规整完成：Patch {result.ProcessedPatches}，重建 Unit {result.RebuiltUnits}，隐藏 Unit {result.HiddenUnits}。";
            LogService.Info($"{summary} 输出={destination}，自动导入={importToLibrary}。");
            _notifications?.Show(importToLibrary ? "护甲规整完成，已导入并替换当前 Mod。" : summary, NotificationLevel.Info, TimeSpan.FromSeconds(10));
            var outputWarnings = result.Issues.Where(issue => issue.Severity == CoreIssueSeverity.Warning)
                .Select(issue => issue.Message).Except(warnings).ToArray();
            if (outputWarnings.Length != 0)
                MessageBox.Show(System.Windows.Application.Current.MainWindow, string.Join(Environment.NewLine, outputWarnings),
                    "护甲规整完成，有兼容提醒", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            task?.MarkCanceled();
        }
        catch (Exception exception)
        {
            task?.MarkFailed(exception.Message);
            LogService.Error($"护甲规整失败：输出={destination}，错误={exception}");
            MessageBox.Show(System.Windows.Application.Current.MainWindow, exception.Message,
                "护甲规整失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // Only this operation's freshly created staging directory may be removed.
            if (stagingDirectory is not null && Directory.Exists(stagingDirectory)
                && IsNormalizationPathInside(stagingDirectory, Path.GetFullPath(outputRootDirectory))
                && Path.GetFileName(stagingDirectory).StartsWith(".armor-normalize-", StringComparison.Ordinal))
            {
                try { Directory.Delete(stagingDirectory, recursive: true); }
                catch (Exception exception) { LogService.Error($"清理规整暂存目录失败：{exception.Message}"); }
            }
            _armorNormalizationRunning = false;
            NormalizeArmorCommand.RaiseCanExecuteChanged();
        }
    }

    private static bool IsNormalizationPathInside(string path, string root)
    {
        var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(candidate, boundary, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
