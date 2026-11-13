using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.KitInstance;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

// 作用：详情页「共用部件」卡片上的「转专属」与「撤销专属」（按实例隐藏 P3）。
// 业务全在 KitConversionService：这里只负责取节点目录、弹选择/确认窗口、提示结果、刷新卡片。
//   - 转专属：预览（后台）→ 选目标套并看预览（窗口）→ 写派生 patch 与记录（后台）→ 提示「下次部署后生效」
//   - 撤销专属：确认 → 删记录与派生 patch → 提示
// 已转专属的 Mod 卡片显示「已转专属：<目标套>」与撤销按钮，不再显示「会影响 N 套」的污染结论。
public partial class ModDetailsPageViewModel
{
    private bool _kitConvertBusy;
    private KitInstanceWorkspaceRecord? _kitInstanceRecord;
    private RelayCommand? _convertKitInstanceCommand;
    private RelayCommand? _undoKitInstanceCommand;

    public bool IsKitInstanceConverted => _kitInstanceRecord is not null;
    public bool ShowKitConvertButton => ShowKitPollutionCard && !IsKitInstanceConverted && KitPollutionIsPolluted;
    public bool ShowKitUndoButton => IsKitInstanceConverted;

    private bool _kitPollutionIsPolluted;
    private bool KitPollutionIsPolluted => _kitPollutionIsPolluted;

    public RelayCommand ConvertKitInstanceCommand
        => _convertKitInstanceCommand ??= new RelayCommand(async _ => await ConvertKitInstanceAsync(), _ => !_disposed && !_kitConvertBusy);

    public RelayCommand UndoKitInstanceCommand
        => _undoKitInstanceCommand ??= new RelayCommand(async _ => await UndoKitInstanceAsync(), _ => !_disposed && !_kitConvertBusy);

    private KitPollutionService GetKitPollutionService() => _kitPollutionService ??= new KitPollutionService(_paths);

    private void SetKitConvertState(KitInstanceWorkspaceRecord? record, bool polluted)
    {
        _kitInstanceRecord = record;
        _kitPollutionIsPolluted = polluted;
        OnPropertyChanged(nameof(IsKitInstanceConverted));
        OnPropertyChanged(nameof(ShowKitConvertButton));
        OnPropertyChanged(nameof(ShowKitUndoButton));
    }

    private void RaiseKitConvertCommands()
    {
        _convertKitInstanceCommand?.RaiseCanExecuteChanged();
        _undoKitInstanceCommand?.RaiseCanExecuteChanged();
    }

    // 已转专属时卡片显示的内容（RefreshKitPollutionAsync 先查记录，有记录就不再做污染分析）。
    internal static string BuildKitConvertedHeadline(KitInstanceWorkspaceRecord record)
    {
        var name = string.IsNullOrWhiteSpace(record.TargetName) ? "未命名护甲" : record.TargetName;
        return $"已转专属：{name}（{record.TargetItemId}）";
    }

    internal static string BuildKitConvertedNote(KitInstanceWorkspaceRecord record)
    {
        var lines = new System.Collections.Generic.List<string>
        {
            $"这个 Mod 只在穿着这套护甲的角色身上生效，其它护甲不受影响；该角色身上被替换的 {record.Hide.Count} 个原版部件会被隐藏。",
        };
        if (record.DerivedFiles is null)
            lines.Add("（这是一条手写的记录，没有派生 patch；撤销只会删掉记录。）");
        lines.Add("转专属与撤销都在下次部署后生效。");
        return string.Join(Environment.NewLine, lines);
    }

    private async Task ConvertKitInstanceAsync()
    {
        if (_kitConvertBusy || !TryGetCurrentNode(out var node)) return;
        _kitConvertBusy = true;
        RaiseKitConvertCommands();
        try
        {
            var service = new KitConversionService(GetKitPollutionService());
            var nodeDirectory = Path.GetFullPath(Path.Combine(_library.ModsRootDirectory, node.RelativePath));
            var gameData = SettingsService.GetGameDataFolder();
            var preview = await Task.Run(() => service.PreviewAsync(node.Id, nodeDirectory, gameData)).ConfigureAwait(true);
            var owner = System.Windows.Application.Current?.MainWindow;
            if (preview.Unavailable is not null)
            {
                HD2ModManager.Views.AppDialog.Show(owner, preview.Unavailable, "转专属", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var window = new HD2ModManager.Views.KitInstanceConversionWindow(
                string.IsNullOrWhiteSpace(node.Metadata.Name) ? node.RelativePath : node.Metadata.Name, preview.Candidates);
            if (window.ShowDialog() != true || window.Chosen is not { CanConvert: true } chosen) return;

            _notifications?.Show($"正在转专属：{chosen.Label}……", NotificationLevel.Info, TimeSpan.FromSeconds(5));
            var result = await Task.Run(() => service.ApplyAsync(
                node.Id, _library.ModsRootDirectory, nodeDirectory, chosen.ItemId, gameData)).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                LogService.Error($"转专属失败：{node.RelativePath} → {chosen.ItemId}：{result.Message}");
                HD2ModManager.Views.AppDialog.Show(owner, result.Message, "转专属失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            LogService.Info($"转专属完成：{node.RelativePath} → {chosen.ItemId}，写出 {string.Join("、", result.WrittenFiles)}。");
            _notifications?.Show($"已转为 {chosen.Label} 专属，下次部署后生效。", NotificationLevel.Info, TimeSpan.FromSeconds(10));
            // 先刷新卡片（读记录，很快），再失效缓存：卡片不能被失效缓存的耗时拖住。
            _ = RefreshKitPollutionAsync(node.Id, _detailContentFacts);
            await _library.InvalidateContentNodeAsync(node.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogService.Error($"转专属失败：{exception}");
            HD2ModManager.Views.AppDialog.Show(System.Windows.Application.Current?.MainWindow, exception.Message, "转专属失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _kitConvertBusy = false;
            RaiseKitConvertCommands();
        }
    }

    private async Task UndoKitInstanceAsync()
    {
        if (_kitConvertBusy || !TryGetCurrentNode(out var node) || _kitInstanceRecord is null) return;
        var owner = System.Windows.Application.Current?.MainWindow;
        if (HD2ModManager.Views.AppDialog.Show(
                owner,
                $"撤销后，这个 Mod 在下次部署后回到原样（重新全局生效），并删掉转专属生成的派生 patch 与隐藏规则。{Environment.NewLine}确定撤销吗？",
                "撤销专属", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _kitConvertBusy = true;
        RaiseKitConvertCommands();
        try
        {
            var nodeDirectory = Path.GetFullPath(Path.Combine(_library.ModsRootDirectory, node.RelativePath));
            var result = await Task.Run(() => KitConversionService.UndoAsync(node.Id, _library.ModsRootDirectory, nodeDirectory)).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                HD2ModManager.Views.AppDialog.Show(owner, result.Message, "撤销专属失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            LogService.Info($"撤销专属：{node.RelativePath}，{result.Message}");
            _notifications?.Show(result.Message, NotificationLevel.Info, TimeSpan.FromSeconds(10));
            // 立刻让卡片脱离「已转专属」（清掉记录状态与按钮），再在后台重新分析；不等缓存失效。
            SetKitPollution(true, "正在分析这个 Mod 会影响哪些护甲……", string.Empty, Array.Empty<string>());
            _ = RefreshKitPollutionAsync(node.Id, _detailContentFacts);
            await _library.InvalidateContentNodeAsync(node.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogService.Error($"撤销专属失败：{exception}");
            HD2ModManager.Views.AppDialog.Show(owner, exception.Message, "撤销专属失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _kitConvertBusy = false;
            RaiseKitConvertCommands();
        }
    }
}
