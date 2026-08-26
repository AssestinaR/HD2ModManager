using System.IO;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModManager.Services;
using Microsoft.Win32;

namespace HD2ModManager.ViewModels;

// Purpose: Hosts non-destructive material split and merge candidate selection, output, and progress in the workspace.
public sealed class MaterialPackagingPageViewModel : PageViewModel
{
    private readonly ModNode source;
    private readonly string modsRootDirectory;
    private readonly IMaterialPackagingApplicationService packaging;
    private readonly ModLibraryService library;
    private readonly NotificationService? notifications;
    private readonly bool requireAllExternalMaterials;
    private readonly ModMaterialPackagingState? initialPackagingState;
    private readonly MaterialDeliveryFacts? initialDeliveryFacts;
    private string outputDirectory = SettingsService.GetResolvedDefaultExportPath();
    private MaterialPackageCandidateViewModel? selectedCandidate;
    private bool importToLibrary;
    private bool onlyReferencedAssets = true;
    private bool replaceExistingMaterials = true;
    private bool candidatesLoaded;
    private bool isRunning;
    private string state;

    public string SourceName => source.Metadata.Name;
    public bool RequiresCandidate { get; }
    public string OperationTitle { get; }
    public string Explanation { get; }
    public IReadOnlyList<MaterialPackageCandidateViewModel> Candidates { get; private set; } = Array.Empty<MaterialPackageCandidateViewModel>();
    public IReadOnlyList<MaterialPackageCandidateViewModel> CandidateOptions => Candidates.Count != 0
        ? Candidates
        : [MaterialPackageCandidateViewModel.Placeholder(candidatesLoaded ? "没有找到可用材质包" : "正在读取材质包候选...")];
    public MaterialPackageCandidateViewModel? SelectedCandidate
    {
        get => selectedCandidate;
        set
        {
            if (!SetField(ref selectedCandidate, value)) return;
            GenerateCommand.RaiseCanExecuteChanged();
        }
    }
    public string OutputDirectory { get => outputDirectory; set { if (SetField(ref outputDirectory, value)) GenerateCommand.RaiseCanExecuteChanged(); } }
    public bool ImportToLibrary { get => importToLibrary; set => SetField(ref importToLibrary, value); }
    public bool OnlyReferencedAssets { get => onlyReferencedAssets; set => SetField(ref onlyReferencedAssets, value); }
    public bool ReplaceExistingMaterials { get => replaceExistingMaterials; set => SetField(ref replaceExistingMaterials, value); }
    public bool IsRunning { get => isRunning; private set { if (SetField(ref isRunning, value)) GenerateCommand.RaiseCanExecuteChanged(); } }
    public string State { get => state; private set => SetField(ref state, value); }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand GenerateCommand { get; }

    public MaterialPackagingPageViewModel(
        ModNode source,
        string modsRootDirectory,
        IMaterialPackagingApplicationService packaging,
        ModLibraryService library,
        NotificationService? notifications,
        bool splitEmbeddedMaterials,
        bool requireAllExternalMaterials = false,
        ModMaterialPackagingState? initialPackagingState = null,
        MaterialDeliveryFacts? initialDeliveryFacts = null)
    {
        this.source = source;
        this.modsRootDirectory = modsRootDirectory;
        this.packaging = packaging;
        this.library = library;
        this.notifications = notifications;
        this.requireAllExternalMaterials = requireAllExternalMaterials;
        this.initialPackagingState = initialPackagingState;
        this.initialDeliveryFacts = initialDeliveryFacts;
        RequiresCandidate = !splitEmbeddedMaterials;
        OperationTitle = splitEmbeddedMaterials ? "拆分内嵌材质" : requireAllExternalMaterials ? "嵌入外部材质" : "替换内嵌材质";
        Explanation = splitEmbeddedMaterials
            ? "将内嵌 Material/Texture 拆分为独立输出；不会修改源 Mod 或当前配置。"
            : "候选按精确 Material AssetKey 匹配；确认后生成独立候选，不会修改源 Mod 或当前配置。";
        state = RequiresCandidate
            ? initialDeliveryFacts is null ? "正在读取 Mod 库中的材质候选…" : "正在复核信息中心提供的材质候选…"
            : initialPackagingState is null ? "选择输出方式后即可开始。" : "已复用详情页材质检查结果；写出前仍会进行最终复核。";
        Title = OperationTitle;
        BrowseCommand = new RelayCommand(_ => Browse());
        GenerateCommand = new RelayCommand(async _ => await GenerateAsync(), _ => CanGenerate());
        if (RequiresCandidate)
        {
            SelectedCandidate = CandidateOptions[0];
            _ = LoadCandidatesAsync();
        }
    }

    private async Task LoadCandidatesAsync()
    {
        try
        {
            LogService.Info($"{OperationTitle}候选读取开始：Mod={SourceName}，节点={source.Id.Value:N}，复用信息中心事实={initialDeliveryFacts is not null}。");
            notifications?.Show("集成材质：正在读取材质候选和依赖闭包…", NotificationLevel.Info, TimeSpan.FromSeconds(30));
            IReadOnlyList<MaterialPackageCandidate> candidates;
            // External-material embedding uses delivery facts; embedded-material replacement
            // must match every Unit -> Material edge, including materials already present.
            if (initialDeliveryFacts is not null && requireAllExternalMaterials)
            {
                // 这里复用信息中心派生出的交付候选；GenerateAsync 仍会做最终 Payload 校验。
                candidates = initialDeliveryFacts.Candidates
                    .Where(candidate => library.Snapshot.Nodes.ContainsKey(candidate.NodeId))
                    .Select(candidate => new MaterialPackageCandidate(
                        candidate.NodeId,
                        candidate.Name,
                        candidate.IsComplete,
                        candidate.CoveredMaterialCount,
                        0,
                        candidate.MissingTextureCount,
                        candidate.IsComplete ? Array.Empty<string>() : new[] { "信息中心判断材质闭包不完整，写出前将再次验证。" }))
                    .ToArray();
            }
            else
            {
                candidates = await packaging.FindCandidatesAsync(source, library.Snapshot.Nodes.Values.ToArray(), modsRootDirectory, requireAllExternalMaterials);
            }
            Candidates = candidates.Select(candidate => new MaterialPackageCandidateViewModel(candidate)).ToArray();
            candidatesLoaded = true;
            SelectedCandidate = CandidateOptions[0];
            State = Candidates.Count == 0 ? "Mod 库中没有引用目标所需 Material 的材质包。" : $"找到 {Candidates.Count} 个候选，请选择要集成的材质包。";
            notifications?.Show(Candidates.Count == 0 ? "集成材质：没有找到可用候选。" : $"集成材质：已找到 {Candidates.Count} 个候选，请选择后生成。", NotificationLevel.Info, TimeSpan.FromSeconds(10));
            LogService.Info($"{OperationTitle}候选读取完成：Mod={SourceName}，候选数={Candidates.Count}，可用数={Candidates.Count(candidate => candidate.IsCompatible)}。");
            OnPropertyChanged(nameof(Candidates));
            OnPropertyChanged(nameof(CandidateOptions));
            GenerateCommand.RaiseCanExecuteChanged();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            candidatesLoaded = true;
            Candidates = Array.Empty<MaterialPackageCandidateViewModel>();
            State = $"读取材质候选失败：{exception.Message}";
            SelectedCandidate = MaterialPackageCandidateViewModel.Placeholder("读取材质包候选失败");
            OnPropertyChanged(nameof(Candidates));
            OnPropertyChanged(nameof(CandidateOptions));
            LogService.Error($"{OperationTitle}候选读取异常：Mod={SourceName}，错误={exception}");
            notifications?.Show(State, NotificationLevel.Error, TimeSpan.FromSeconds(12));
        }
    }

    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "选择材质候选输出根目录", Multiselect = false, InitialDirectory = OutputDirectory };
        if (dialog.ShowDialog() == true) OutputDirectory = dialog.FolderName;
    }

    private bool CanGenerate() => !IsRunning
        && !string.IsNullOrWhiteSpace(OutputDirectory)
        && (!RequiresCandidate || SelectedCandidate is { IsPlaceholder: false });

    private async Task GenerateAsync()
    {
        var importResultToLibrary = ImportToLibrary;
        var root = importResultToLibrary
            ? Path.Combine(Path.GetTempPath(), "HD2ModManager", "MaterialPackaging", Guid.NewGuid().ToString("N"))
            : BuildDestinationDirectory();
        IsRunning = true;
        State = "正在验证 Payload 与资源图并生成材质候选…";
        LogService.Info($"{OperationTitle}开始：Mod={SourceName}，节点={source.Id.Value:N}，输出={root}，导入库={importResultToLibrary}。");
        notifications?.Show(
            RequiresCandidate
                ? "集成材质：正在进行最终 Payload 验证并写出材质候选…"
                : "拆分材质：正在验证 Payload 与资源图并写出模型包、材质包…",
            NotificationLevel.Info,
            TimeSpan.FromSeconds(30));
        try
        {
            MaterialPackagingOperationResult result;
            if (!RequiresCandidate)
            {
                // InspectAsync is intentionally repeated here as a final payload-level safety check.
                result = await packaging.SplitAsync(source, modsRootDirectory, root);
            }
            else
            {
                if (SelectedCandidate is null || !library.Snapshot.Nodes.TryGetValue(SelectedCandidate.NodeId, out var candidate)) return;
                result = await packaging.MergeAsync(source, candidate, modsRootDirectory, root, requireAllExternalMaterials, OnlyReferencedAssets, ReplaceExistingMaterials);
            }

            if (!result.IsSuccessful)
            {
                State = $"生成失败：{string.Join("；", result.Issues.Select(issue => issue.Message).Take(3))}";
                LogService.Error($"{OperationTitle}失败：Mod={SourceName}，输出={root}，问题={string.Join(" | ", result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"))}");
                notifications?.Show(State, NotificationLevel.Error, TimeSpan.FromSeconds(12));
                return;
            }
            if (importResultToLibrary)
            {
                var importer = new ImportService(library, informationCenter: library.InformationCenter);
                foreach (var directory in result.OutputDirectories) await importer.ImportPathAsync(directory, default);
            }
            State = $"完成：{result.AssetCount} 个资源，{result.GraphEdgeCount} 条引用。{(importResultToLibrary ? "已导入 Mod 库。" : $"输出：{root}")}";
            LogService.Info($"{OperationTitle}完成：Mod={SourceName}，资源={result.AssetCount}，引用={result.GraphEdgeCount}，输出={root}，导入库={importResultToLibrary}。");
            notifications?.Show("材质候选生成完成。", NotificationLevel.Info, TimeSpan.FromSeconds(8));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException)
        {
            State = $"生成材质候选失败：{exception.Message}";
            LogService.Error($"{OperationTitle}异常：Mod={SourceName}，输出={root}，错误={exception}");
            notifications?.Show(State, NotificationLevel.Error, TimeSpan.FromSeconds(12));
        }
        finally
        {
            if (importResultToLibrary)
                TryDeleteTemporaryDirectory(root);
            IsRunning = false;
        }
    }

    private string BuildDestinationDirectory()
    {
        var candidateName = SelectedCandidate is null ? string.Empty : $"-{SelectedCandidate.Name}";
        return Path.Combine(OutputDirectory, $"{Sanitize(SourceName)}{Sanitize(candidateName)}+{DateTime.Now:yyyyMMdd-HHmmssfff}+{Sanitize(OperationTitle)}");
    }

    private static string Sanitize(string value) => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();

    private static void TryDeleteTemporaryDirectory(string directory)
    {
        try
        {
            var temporaryRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "HD2ModManager", "MaterialPackaging"));
            var fullDirectory = Path.GetFullPath(directory);
            var relative = Path.GetRelativePath(temporaryRoot, fullDirectory);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return;
            if (Directory.Exists(fullDirectory)) Directory.Delete(fullDirectory, recursive: true);
            if (Directory.Exists(temporaryRoot) && !Directory.EnumerateFileSystemEntries(temporaryRoot).Any())
                Directory.Delete(temporaryRoot, recursive: false);
            var managerRoot = Directory.GetParent(temporaryRoot)?.FullName;
            if (managerRoot is not null && Directory.Exists(managerRoot) && !Directory.EnumerateFileSystemEntries(managerRoot).Any())
                Directory.Delete(managerRoot, recursive: false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class MaterialPackageCandidateViewModel
{
    private MaterialPackageCandidateViewModel(MaterialPackageCandidate? candidate, string placeholderText = "")
    {
        Candidate = candidate;
        Name = candidate?.Name ?? placeholderText;
    }

    public MaterialPackageCandidateViewModel(MaterialPackageCandidate candidate) : this(candidate, string.Empty) { }
    public MaterialPackageCandidate? Candidate { get; }
    public bool IsPlaceholder => Candidate is null;
    public ModNodeId NodeId => Candidate?.NodeId ?? default;
    public string Name { get; }
    public bool IsCompatible => Candidate?.IsCompatible == true;
    public int MatchingMaterialCount => Candidate?.MatchingMaterialCount ?? 0;
    public int MissingMaterialCount => Candidate?.MissingMaterialCount ?? 0;
    public int MissingTextureCount => Candidate?.MissingTextureCount ?? 0;
    public string BlockerSummary => Candidate is null || Candidate.Blockers.Count == 0 ? string.Empty : string.Join("；", Candidate.Blockers);
    public string StatusText => Candidate is null ? string.Empty : Candidate.Blockers.Count == 0 ? "可集成" : "可集成（依赖由用户负责）";
    public string StatusBrush => Candidate is null || Candidate.Blockers.Count == 0 ? "#237A3B" : "#B06A00";
    public static MaterialPackageCandidateViewModel Placeholder(string text) => new(null, text);
}
