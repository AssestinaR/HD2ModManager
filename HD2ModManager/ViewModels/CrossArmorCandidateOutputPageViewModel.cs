using System.IO;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Services;
using HD2ModManager.Views;
using HD2ModCore.Application;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace HD2ModManager.ViewModels
{
    // Purpose: Runs isolated cross-armor candidate generation and adapts its progress to the Manager task center.
    // 作用：在跨护甲双槽工作区右侧收集候选输出参数并执行隔离写出。
    public sealed class CrossArmorCandidateOutputPageViewModel : PageViewModel
    {
        private readonly CrossArmorTransferPlanWindowViewModel _plan;
        private readonly NotificationService _notifications;
        private readonly BackgroundTaskService _backgroundTasks;
        private string _outputDirectory = Path.Combine(AppContext.BaseDirectory, "Output");
        private bool _isGenerating;
        private string _state = "先在左侧确认计划；候选将写入输出目录下自动命名的子文件夹。";

        public string OutputDirectory
        {
            get => _outputDirectory;
            set
            {
                if (!SetField(ref _outputDirectory, value)) return;
                OnPropertyChanged(nameof(CanGenerate));
                GenerateCommand.RaiseCanExecuteChanged();
            }
        }
        public bool IsGenerating { get => _isGenerating; private set { if (SetField(ref _isGenerating, value)) GenerateCommand.RaiseCanExecuteChanged(); } }
        public string State { get => _state; private set => SetField(ref _state, value); }
        public bool CanGenerate => !IsGenerating && _plan.CanGenerateCandidate && !string.IsNullOrWhiteSpace(OutputDirectory);
        public RelayCommand GenerateCommand { get; }

        public CrossArmorCandidateOutputPageViewModel(CrossArmorTransferPlanWindowViewModel plan, NotificationService notifications, BackgroundTaskService backgroundTasks)
        {
            Title = "验证候选输出";
            _plan = plan;
            _notifications = notifications;
            _backgroundTasks = backgroundTasks;
            GenerateCommand = new RelayCommand(async _ => await GenerateAsync(), _ => CanGenerate);
            _plan.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(CanGenerate));
                GenerateCommand.RaiseCanExecuteChanged();
            };
        }

        public void SetOutputDirectory(string directory)
        {
            OutputDirectory = directory;
            OnPropertyChanged(nameof(CanGenerate));
            GenerateCommand.RaiseCanExecuteChanged();
        }

        private async Task GenerateAsync()
        {
            var plan = _plan.GetCurrentPlan();
            if (plan is null || !plan.CanContinue || string.IsNullOrWhiteSpace(OutputDirectory)) return;
            var candidateDirectory = BuildCandidateDirectory(plan);
            var operationId = Guid.NewGuid();
            var task = _backgroundTasks.Enqueue(
                BackgroundTaskKind.Other,
                "生成跨护甲验证候选",
                candidateDirectory,
                origin: "跨护甲工作区",
                userVisibleReason: "重建目标 Unit 并写出隔离验证候选。",
                suggestedAction: "完成后按输出目录中的报告验收。",
                canCancel: true);
            var uiContext = SynchronizationContext.Current
                ?? (Application.Current?.Dispatcher is { } dispatcher
                    ? new DispatcherSynchronizationContext(dispatcher)
                    : new SynchronizationContext());
            var bridge = new OperationProgressBridge(new BackgroundTaskOperationTarget(task), operationId, uiContext);
            long sequence = 0;

            IsGenerating = true;
			// Cross-armor production deliberately uses direct source Unit reuse.  The
			// generic Canonical route remains owned by Same-key reconstruction and
			// other Patch workflows, not this production entry point.
			const bool directReuse = true;
			State = "正在直接复用来源 Unit 并写出验证候选。";
			LogService.Info($"替换护甲生成开始：源Patch={_plan.SourcePatchTocPath}，输出={candidateDirectory}，目标数={plan.SelectedTargets.Count}，自动隐藏其余部件={_plan.AutoHideUnmappedTargetUnits}，快速复用来源Unit={directReuse}。");
            _plan.CandidateGenerationRunning = true;
            try
            {
				await _plan.EnsurePreparedSourceEntriesAsync(task.CancellationToken).ConfigureAwait(false);
				bridge.Apply(new OperationProgressEvent(operationId, OperationKind.CrossArmorTransfer, OperationStage.Preparing, OperationState.Started, sequence: sequence++));
				var progress = new InlineProgress<CrossArmorTransferProgress>(update =>
					bridge.Apply(update.ToOperationProgressEvent(operationId, sequence: Interlocked.Increment(ref sequence))));
                var request = new CrossArmorTransferCandidateRequest(
                    _plan.SourcePatchTocPath,
                    _plan.GameDataDirectory,
                    candidateDirectory,
                    plan,
                    CrossArmorMaterialBindingMode.PreserveSourceReferences,
                    _plan.PreparedSourceEntries,
                    progress,
                    AutoHideUnmappedTargetUnits: _plan.AutoHideUnmappedTargetUnits,
					DirectSourceUnitReuse: directReuse);
				// The orchestrator owns Patch staging and validation; this production
				// request explicitly selects DirectSourceUnitReuse. Unit-local LOD
				// dependencies are carried by the source Unit closure.
				var result = await Task.Run(() => CoreServices.CreateCanonicalCrossArmorOrchestrator(_plan.InformationReader)
                    .ExecuteAsync(request, task.CancellationToken).AsTask(), task.CancellationToken);
                var presentation = CrossArmorCandidateResultPresenter.Map(result);
                if (presentation.IsFailure)
                {
                    task.SetOutputArtifacts(result.OutputDirectory, result.ReportPath);
                    var firstIssue = result.Issues.FirstOrDefault();
                    bridge.Apply(new OperationProgressEvent(operationId, OperationKind.CrossArmorTransfer, OperationStage.Failed, OperationState.Failed,
                        message: firstIssue?.Message ?? "候选生成失败",
                        issueCode: firstIssue?.Code,
                        sequence: sequence++));
                    State = $"生成失败：{string.Join("；", result.Issues.Select(issue => issue.Message).Take(3))}";
                    LogService.Error($"替换护甲生成失败：输出={candidateDirectory}，问题={string.Join(" | ", result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"))}");
                    task.MarkFailed(firstIssue?.Message ?? "候选生成失败");
                    return;
                }

                bridge.Apply(new OperationProgressEvent(operationId, OperationKind.CrossArmorTransfer, OperationStage.Completed, OperationState.Completed, message: presentation.StatusText, sequence: sequence++));
                task.SetOutputArtifacts(result.OutputDirectory, result.ReportPath);
                task.MarkCompleted();
				const string routeName = "快速来源 Unit 验证候选";
                State = result.HasWarnings
                    ? $"{routeName}已提交，但报告不完整/有告警：{candidateDirectory}。Unit {result.OutputUnitCount}；报告：{result.ReportPath ?? "未生成"}"
                    : $"{routeName}已生成到：{candidateDirectory}。Unit {result.OutputUnitCount}；替换 mesh {result.ReplacementMeshCount}；极小化 mesh {result.MinifiedMeshCount}。报告：{result.ReportPath}";
                LogService.Info($"替换护甲生成完成：输出={candidateDirectory}，Unit={result.OutputUnitCount}，替换Mesh={result.ReplacementMeshCount}，极小化Mesh={result.MinifiedMeshCount}，报告={result.ReportPath}。");
            }
            catch (OperationCanceledException)
            {
                bridge.Apply(new OperationProgressEvent(operationId, OperationKind.CrossArmorTransfer, OperationStage.Canceled, OperationState.Canceled, message: "生成已取消", sequence: sequence++));
                task.MarkCanceled();
                State = "生成已取消。";
                LogService.Info($"替换护甲生成已取消：输出={candidateDirectory}。");
            }
            catch (Exception exception)
            {
                task.SetOutputArtifacts(candidateDirectory, Path.Combine(candidateDirectory, "canonical-report.md"));
                bridge.Apply(new OperationProgressEvent(operationId, OperationKind.CrossArmorTransfer, OperationStage.Failed, OperationState.Failed, message: exception.Message, sequence: sequence++));
                task.MarkFailed(exception.Message);
                State = $"生成候选失败：{exception.Message}";
                LogService.Error($"替换护甲生成异常：输出={candidateDirectory}，错误={exception}");
            }
            finally
            {
                _plan.CandidateGenerationRunning = false;
                IsGenerating = false;
            }
        }

        private string BuildCandidateDirectory(CrossArmorTransferPlan plan)
        {
            var sourceName = SanitizeDirectoryName(plan.SelectedSource?.DisplayName ?? "未知来源");
            var armorTargets = plan.SelectedTargets.Where(target => string.Equals(target.Category, "Armor", StringComparison.OrdinalIgnoreCase)).ToArray();
            var targetName = armorTargets.Length == 0
                ? "无护甲目标"
                : SanitizeDirectoryName(armorTargets[0].DisplayName) + (armorTargets.Length > 1 ? $"以及更多的{armorTargets.Length - 1}个" : string.Empty);
            return Path.Combine(OutputDirectory, $"{sourceName}+{DateTime.Now:yyyyMMdd-HHmmss}+替换{targetName}");
        }

        private static string SanitizeDirectoryName(string value)
        {
            var invalidCharacters = Path.GetInvalidFileNameChars();
            var safe = new string(value.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray()).Trim();
            return string.IsNullOrWhiteSpace(safe) ? "未命名" : safe;
        }
    }
}
