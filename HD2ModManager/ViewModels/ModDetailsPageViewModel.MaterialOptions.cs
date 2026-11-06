using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using HD2ModCore.Infrastructure;

namespace HD2ModManager.ViewModels;

/// <summary>
/// 详情页的「材质」卡片：一个 Mod 自己的材质选项。
/// <para>
/// <b>为什么是一张列表卡而不是跳转的二级页。</b>这一步的操作就那几样——勾选、上移下移、
/// 改名、备注、配图、打开文件夹——条目内就能做完。为它开一个新页面只会让人多点两次。
/// </para>
/// <para>
/// <b>为什么没有删除。</b>一个材质选项就是主 Mod 里的一个 patch：它属于主 Mod 的一部分，
/// 不存在"移出列表"这个动作。能改的只有"启不启用、排第几、叫什么、备注是什么、配哪张图"。
/// </para>
/// </summary>
public partial class ModDetailsPageViewModel
{
    private string? _materialOptionsDirectory;
    private string? _materialOptionMessage;

    public ObservableCollection<MaterialOptionItemViewModel> MaterialOptions { get; } = [];

    public bool HasMaterialOptions => MaterialOptions.Count > 0;

    /// <summary>卡片可见性的唯一判据。</summary>
    public bool ShowMaterialOptionsCard => HasMaterialOptions;

    /// <summary>最近一次操作的结果，显示在卡片底部。</summary>
    public string? MaterialOptionMessage
    {
        get => _materialOptionMessage;
        private set
        {
            _materialOptionMessage = value;
            OnPropertyChanged(nameof(MaterialOptionMessage));
            OnPropertyChanged(nameof(HasMaterialOptionMessage));
        }
    }

    public bool HasMaterialOptionMessage => !string.IsNullOrWhiteSpace(_materialOptionMessage);

    /// <summary>重新读一遍材质选项。切换 Mod、完成转换、改完顺序之后调用。</summary>
    public void RefreshMaterialOptions()
    {
        MaterialOptions.Clear();
        _materialOptionsDirectory = null;
        if (TryGetCurrentNode(out var node))
        {
            var directory = Path.Combine(_library.ModsRootDirectory, node.RelativePath);
            if (Directory.Exists(directory))
            {
                _materialOptionsDirectory = directory;
                foreach (var option in MaterialOptionSettings.Read(directory))
                {
                    var item = new MaterialOptionItemViewModel(option, OnMaterialSelectionChanged) { ModDirectory = directory };
                    item.MoveRequested += OnMaterialOptionMoveRequested;
                    item.ImageRequested += OnMaterialOptionImageRequested;
                    item.OpenFolderRequested += OnMaterialOptionOpenFolderRequested;
                    MaterialOptions.Add(item);
                }
            }
        }

        OnPropertyChanged(nameof(HasMaterialOptions));
        OnPropertyChanged(nameof(ShowMaterialOptionsCard));
    }

    /// <summary>勾选变化：把启用集合写回去，再按它重建生效层。</summary>
    private void OnMaterialSelectionChanged()
    {
        var directory = _materialOptionsDirectory;
        if (directory is null) return;

        var enabled = MaterialOptions.Where(item => item.Enabled).Select(item => item.PatchFileName).ToArray();
        var writeError = MaterialOptionSettings.SetEnabled(directory, enabled);
        if (writeError is not null)
        {
            MaterialOptionMessage = $"启用集合没写进去：{writeError}";
            return;
        }

        // 生效层 = unit 层 + 所有启用的材质。物化器会把生效层里已有的换甲产物当 unit 层留住。
        var result = OverwriteMaterializer.Materialize(directory, new OverwriteSelection(enabled));
        MaterialOptionMessage = result.Succeeded
            ? $"已启用 {enabled.Length} 个材质；生效层已重写。未自动部署。"
            : $"材质已记录，但生效层没重建成功：{result.Error}";
    }

    /// <summary>上移/下移：只改列表与结构文件里的顺序，patch 文件一个都不动。</summary>
    private void OnMaterialOptionMoveRequested(object? sender, int delta)
    {
        if (sender is not MaterialOptionItemViewModel item) return;
        var index = MaterialOptions.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= MaterialOptions.Count) return;

        MaterialOptions.Move(index, target);
        PersistMaterialOptionOrder();
    }

    private void PersistMaterialOptionOrder()
    {
        var directory = _materialOptionsDirectory;
        if (directory is null) return;

        var ordered = MaterialOptions.Select(item => item.PatchIndex).ToArray();
        var error = MaterialOptionSettings.Move(directory, ordered);
        if (error is not null)
        {
            MaterialOptionMessage = $"顺序没写进去：{error}";
            return;
        }

        for (var i = 0; i < MaterialOptions.Count; i++) MaterialOptions[i].Order = i;
        // 列表顺序就是部署顺序（后部署的覆盖先部署的），所以改完要重建一次生效层；
        // 否则用户会看到"顺序改了、游戏里没变"。
        OnMaterialSelectionChanged();
        MaterialOptionMessage = "顺序已更新（写在结构文件里，patch 文件未改动）。未自动部署。";
    }

    private void OnMaterialOptionImageRequested(object? sender, EventArgs e)
    {
        if (sender is not MaterialOptionItemViewModel item || _materialOptionsDirectory is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择材质配图",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        var error = MaterialOptionSettings.SetImage(_materialOptionsDirectory, item.PatchIndex, dialog.FileName);
        if (error is not null)
        {
            MaterialOptionMessage = $"配图没换成功：{error}";
            return;
        }
        item.ImageFileName = MaterialOptionSettings
            .Read(_materialOptionsDirectory)
            .FirstOrDefault(option => option.PatchIndex == item.PatchIndex)?.ImageFileName;
        MaterialOptionMessage = "配图已更新。";
    }

    private void OnMaterialOptionOpenFolderRequested(object? sender, EventArgs e)
    {
        if (_materialOptionsDirectory is null) return;
        // 材质 patch 就在主 Mod 的基底目录里——"打开文件夹"打开的是那个目录，
        // 因为它本来就没有自己的文件夹。
        var directory = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(_materialOptionsDirectory)
            ?? _materialOptionsDirectory;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{directory}\"", UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MaterialOptionMessage = $"打不开文件夹：{exception.Message}";
        }
    }
}
