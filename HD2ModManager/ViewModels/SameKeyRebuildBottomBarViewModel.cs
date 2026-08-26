using System.IO;
using HD2ModManager.Services;
using Microsoft.Win32;

namespace HD2ModManager.ViewModels;

public sealed class SameKeyRebuildBottomBarViewModel : BaseViewModel
{
    private readonly Func<string, bool, Task> generate;
    private string outputDirectory = SettingsService.GetResolvedDefaultExportPath();
    private bool importToLibrary;
    private bool isRunning;

    public SameKeyRebuildBottomBarViewModel(Func<string, bool, Task> generate)
    {
        this.generate = generate ?? throw new ArgumentNullException(nameof(generate));
        BrowseCommand = new RelayCommand(_ => Browse());
        GenerateCommand = new RelayCommand(async _ => await GenerateAsync(), _ => !IsRunning && !string.IsNullOrWhiteSpace(OutputDirectory));
    }

    public string OutputDirectory { get => outputDirectory; set { if (SetField(ref outputDirectory, value)) GenerateCommand.RaiseCanExecuteChanged(); } }
    public bool ImportToLibrary { get => importToLibrary; set => SetField(ref importToLibrary, value); }
    public bool IsRunning { get => isRunning; private set { if (SetField(ref isRunning, value)) GenerateCommand.RaiseCanExecuteChanged(); } }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand GenerateCommand { get; }

    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "选择修复 patch 输出根目录", Multiselect = false, InitialDirectory = OutputDirectory };
        if (dialog.ShowDialog() == true) OutputDirectory = dialog.FolderName;
    }

    private async Task GenerateAsync()
    {
        IsRunning = true;
        try { await generate(OutputDirectory, ImportToLibrary); }
        finally { IsRunning = false; }
    }
}

public enum SameKeyRebuildBottomBarRowKind
{
    Output,
    Options
}

public sealed record SameKeyRebuildBottomBarRowViewModel(
    SameKeyRebuildBottomBarViewModel Operation,
    SameKeyRebuildBottomBarRowKind Kind)
{
    public bool IsOutput => Kind == SameKeyRebuildBottomBarRowKind.Output;
    public bool IsOptions => Kind == SameKeyRebuildBottomBarRowKind.Options;
}
