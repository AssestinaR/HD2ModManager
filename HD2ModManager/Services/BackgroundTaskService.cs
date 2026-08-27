using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace HD2ModManager.Services
{
    // Purpose: Separates structural task changes from high-frequency live progress updates.
    public sealed class BackgroundTaskChangedEventArgs : EventArgs
    {
        public BackgroundTaskChangedEventArgs(bool requiresProjectionRefresh) => RequiresProjectionRefresh = requiresProjectionRefresh;
        public bool RequiresProjectionRefresh { get; }
    }

    // 作用：集中记录管理器后台任务，为状态页和任务详情窗口提供可观察数据。
    public enum BackgroundTaskStatus
    {
        Queued,
        Running,
        Completed,
        Failed,
        Canceled,
    }

    // Purpose: Separates background bookkeeping from work the user explicitly
    // started and is likely waiting for at the top status bar.
    public enum BackgroundTaskPresentation
    {
        Background,
        Foreground,
    }

    public enum BackgroundTaskKind
    {
        Import,
		AnalyzeImportedMod,
        RefreshLibrary,
        UpdateAssetMetadata,
        BuildAssetIndex,
		RepairMods,
        Deployment,
        DeactivateProfile,
        InformationCenter,
        Other,
    }

    public sealed class BackgroundTaskItem : INotifyPropertyChanged
    {
        private BackgroundTaskStatus _status;
        private string _stage = "等待执行";
        private string? _error;
        private double? _progress;
        private bool _isSelected;
        private string? _outputDirectory;
        private string? _reportPath;
        private bool _isAcknowledged;
        private bool _isUnread;
        private bool _isFocusDismissed;
        private int _attentionViewCount;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly bool _canCancel;

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public BackgroundTaskKind Kind { get; }
        public string Name { get; }
        public string? Detail { get; }
        public string Origin { get; }
        public string? UserVisibleReason { get; }
        public string? SuggestedAction { get; private set; }
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public DateTime? StartedAt { get; private set; }
        public DateTime? FinishedAt { get; private set; }
        public BackgroundTaskStatus Status { get => _status; private set => SetField(ref _status, value); }
        public string Stage { get => _stage; private set => SetField(ref _stage, value); }
        public string? Error { get => _error; private set => SetField(ref _error, value); }
        public double? Progress
        {
            get => _progress;
            private set
            {
                if (Equals(_progress, value)) return;
                _progress = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(HasProgress));
            }
        }
        public string ProgressText => Progress is null ? string.Empty : $"{Progress.Value:P0}";
        public bool HasProgress => Progress is not null;
        public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }
        public string? OutputDirectory { get => _outputDirectory; private set => SetField(ref _outputDirectory, value); }
        public string? ReportPath { get => _reportPath; private set => SetField(ref _reportPath, value); }
        public bool HasOutputDirectory => !string.IsNullOrWhiteSpace(OutputDirectory);
        public bool HasReport => !string.IsNullOrWhiteSpace(ReportPath);
        public bool IsAcknowledged { get => _isAcknowledged; private set => SetField(ref _isAcknowledged, value); }
        public bool IsUnread { get => _isUnread; private set => SetField(ref _isUnread, value); }
        public bool IsFocusDismissed { get => _isFocusDismissed; private set => SetField(ref _isFocusDismissed, value); }
        public int AttentionViewCount { get => _attentionViewCount; private set => SetField(ref _attentionViewCount, value); }
        public bool IsActive => Status is BackgroundTaskStatus.Queued or BackgroundTaskStatus.Running;
        public CancellationToken CancellationToken => _cancellation.Token;
        public bool CanCancel => _canCancel && !IsInformationCenter && IsActive;
        public bool CanRetry => !IsInformationCenter && Retry is not null && !IsActive;
        public bool IsInformationCenter { get; }
        public BackgroundTaskPresentation Presentation { get; }
        public bool IsForeground => Presentation == BackgroundTaskPresentation.Foreground;
        public Func<Task>? Retry { get; }
        public string StatusText => Status switch
        {
            BackgroundTaskStatus.Queued => "排队中",
            BackgroundTaskStatus.Running => "进行中",
            BackgroundTaskStatus.Completed => "已完成",
            BackgroundTaskStatus.Failed => "失败",
            BackgroundTaskStatus.Canceled => "已取消",
            _ => "未知",
        };
        public string KindIcon => Kind switch
        {
            BackgroundTaskKind.Import => "⇩",
			BackgroundTaskKind.AnalyzeImportedMod => "⌕",
            BackgroundTaskKind.RefreshLibrary => "⟳",
            BackgroundTaskKind.UpdateAssetMetadata => "◇",
            BackgroundTaskKind.BuildAssetIndex => "⌕",
			BackgroundTaskKind.RepairMods => "✦",
            BackgroundTaskKind.Deployment => "⇄",
            BackgroundTaskKind.DeactivateProfile => "■",
            _ => "•",
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        public BackgroundTaskItem(
            BackgroundTaskKind kind,
            string name,
            string? detail = null,
            string? origin = null,
            string? userVisibleReason = null,
            string? suggestedAction = null,
            Func<Task>? retry = null,
            bool canCancel = true,
            BackgroundTaskPresentation presentation = BackgroundTaskPresentation.Background)
        {
            Kind = kind;
            Name = name;
            Detail = detail;
            Origin = string.IsNullOrWhiteSpace(origin) ? "管理器" : origin;
            UserVisibleReason = userVisibleReason;
            SuggestedAction = suggestedAction;
            Retry = retry;
            IsInformationCenter = kind == BackgroundTaskKind.InformationCenter;
            Presentation = presentation;
            _canCancel = canCancel;
            _status = BackgroundTaskStatus.Queued;
        }

        public void MarkRunning(string stage)
        {
            if (!IsActive) return;
            IsAcknowledged = false;
            IsUnread = false;
            IsFocusDismissed = false;
            AttentionViewCount = 0;
            Status = BackgroundTaskStatus.Running;
            StartedAt = DateTime.UtcNow;
            UpdateStage(stage);
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanRetry));
        }

        public void UpdateStage(string stage)
        {
            if (!IsActive) return;
            Stage = string.IsNullOrWhiteSpace(stage) ? "处理中" : stage;
        }

        public void UpdateProgress(double? progress)
        {
            if (!IsActive) return;
            Progress = progress is null ? null : Math.Clamp(progress.Value, 0d, 1d);
        }

        public void SetSuggestedAction(string? suggestedAction)
        {
            SuggestedAction = suggestedAction;
            OnPropertyChanged(nameof(SuggestedAction));
        }

        public void SetOutputArtifacts(string? outputDirectory, string? reportPath)
        {
            OutputDirectory = !string.IsNullOrWhiteSpace(outputDirectory) && System.IO.Directory.Exists(outputDirectory)
                ? outputDirectory
                : null;
            ReportPath = !string.IsNullOrWhiteSpace(reportPath) && System.IO.File.Exists(reportPath)
                ? reportPath
                : null;
            OnPropertyChanged(nameof(HasOutputDirectory));
            OnPropertyChanged(nameof(HasReport));
        }

        public void MarkCompleted()
        {
            if (!IsActive) return;
            Status = BackgroundTaskStatus.Completed;
            FinishedAt = DateTime.UtcNow;
            Progress = 1d;
            Stage = "已完成";
            IsUnread = IsForeground;
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanRetry));
        }

        public void MarkFailed(string error)
        {
            if (!IsActive) return;
            IsAcknowledged = false;
            IsUnread = true;
            IsFocusDismissed = false;
            AttentionViewCount = 0;
            Status = BackgroundTaskStatus.Failed;
            Error = error;
            FinishedAt = DateTime.UtcNow;
            Stage = "处理失败";
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanRetry));
        }

        public void MarkCanceled()
        {
            if (!IsActive) return;
            Status = BackgroundTaskStatus.Canceled;
            FinishedAt = DateTime.UtcNow;
            Stage = "已取消";
            IsUnread = IsForeground;
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanRetry));
        }

        public void Cancel()
        {
            if (!IsActive) return;
            _cancellation.Cancel();
            if (Status == BackgroundTaskStatus.Queued)
            {
                MarkCanceled();
            }
            else
            {
                UpdateStage("正在取消");
            }
        }

        public void Acknowledge()
        {
            if (Status != BackgroundTaskStatus.Failed || IsAcknowledged) return;
            IsAcknowledged = true;
            IsUnread = false;
        }

        public void MarkRead()
        {
            if (IsActive || !IsUnread) return;
            IsUnread = false;
        }

        public void DismissFromFocus()
        {
            if (!IsActive || IsFocusDismissed) return;
            IsFocusDismissed = true;
        }

        public void RecordAttentionView()
        {
            if (Status != BackgroundTaskStatus.Failed || IsAcknowledged) return;
            AttentionViewCount++;
            if (AttentionViewCount >= 2) Acknowledge();
        }

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            OnPropertyChanged(name);
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class BackgroundTaskService
    {
        private readonly ObservableCollection<BackgroundTaskItem> _tasks = new();
        private readonly Dispatcher? _dispatcher = Application.Current?.Dispatcher;
        private int _changeQueued;
        private int _projectionRefreshPending;
        public ReadOnlyObservableCollection<BackgroundTaskItem> Tasks { get; }

        public BackgroundTaskService()
        {
            Tasks = new ReadOnlyObservableCollection<BackgroundTaskItem>(_tasks);
            _tasks.CollectionChanged += OnTasksChanged;
        }

        public event EventHandler<BackgroundTaskChangedEventArgs>? Changed;

        public BackgroundTaskItem Enqueue(
            BackgroundTaskKind kind,
            string name,
            string? detail = null,
            string? origin = null,
            string? userVisibleReason = null,
            string? suggestedAction = null,
            Func<Task>? retry = null,
            bool canCancel = true,
            BackgroundTaskPresentation presentation = BackgroundTaskPresentation.Background)
        {
            var task = new BackgroundTaskItem(kind, name, detail, origin, userVisibleReason, suggestedAction, retry, canCancel, presentation);
            RunOnUi(() => _tasks.Add(task));
            task.PropertyChanged += OnTaskPropertyChanged;
            return task;
        }

        public void RemoveCompleted()
        {
            RunOnUi(() =>
            {
                foreach (var task in _tasks.Where(t => t.Status is BackgroundTaskStatus.Completed or BackgroundTaskStatus.Failed or BackgroundTaskStatus.Canceled).ToList())
                {
                    task.PropertyChanged -= OnTaskPropertyChanged;
                    _tasks.Remove(task);
                }
            });
        }

        public int Count(BackgroundTaskStatus status) => _tasks.Count(t => t.Status == status);
        public int CountQueued => Count(BackgroundTaskStatus.Queued);
        public int CountRunning => Count(BackgroundTaskStatus.Running);
        public int CountCompleted => Count(BackgroundTaskStatus.Completed);
        public int CountFailed => Count(BackgroundTaskStatus.Failed);

        public ReadOnlyObservableCollection<BackgroundTaskItem> Snapshot => Tasks;

        private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueChanged(true);

        private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Live fields are already observed by MessageCenterItem. They do not require a second
            // dispatcher pass to rebuild task projections.
            if (RequiresProjectionRefresh(e.PropertyName)) QueueChanged(true);
        }

        private void QueueChanged(bool requiresProjectionRefresh)
        {
            if (requiresProjectionRefresh) Interlocked.Exchange(ref _projectionRefreshPending, 1);
            if (_dispatcher is null)
            {
                Changed?.Invoke(this, new BackgroundTaskChangedEventArgs(requiresProjectionRefresh));
                return;
            }

            if (Interlocked.Exchange(ref _changeQueued, 1) != 0) return;
            _ = _dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _changeQueued, 0);
                var projectionRefresh = Interlocked.Exchange(ref _projectionRefreshPending, 0) != 0;
                Changed?.Invoke(this, new BackgroundTaskChangedEventArgs(projectionRefresh));
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private static bool RequiresProjectionRefresh(string? propertyName)
            => propertyName is nameof(BackgroundTaskItem.Status)
                or nameof(BackgroundTaskItem.IsActive)
                or nameof(BackgroundTaskItem.StatusText)
                or nameof(BackgroundTaskItem.CanCancel)
                or nameof(BackgroundTaskItem.CanRetry)
                or nameof(BackgroundTaskItem.IsAcknowledged)
                or nameof(BackgroundTaskItem.IsUnread)
                or nameof(BackgroundTaskItem.IsFocusDismissed)
                or nameof(BackgroundTaskItem.HasReport)
                or nameof(BackgroundTaskItem.HasOutputDirectory);

        private static void RunOnUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            _ = dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
