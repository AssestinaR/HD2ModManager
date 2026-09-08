using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;

namespace HD2ModManager.Services
{
    // Purpose: Provides stable message-center sections. Active work is deliberately
    // separate from ordinary notifications so status cannot be displaced by chatter.
    public sealed class MessageCenterItem : INotifyPropertyChanged, IDisposable
    {
        private readonly NotificationItem? _notification;
        private readonly BackgroundTaskItem? _task;
        private bool _isExpanded;

        public MessageCenterItem(NotificationItem notification)
        {
            _notification = notification;
            _notification.PropertyChanged += OnSourcePropertyChanged;
        }

        public MessageCenterItem(BackgroundTaskItem task)
        {
            _task = task;
            _task.PropertyChanged += OnSourcePropertyChanged;
        }

        public DateTime OccurredAt => _notification?.UpdatedAt ?? _task?.FinishedAt ?? _task?.StartedAt ?? _task?.CreatedAt ?? DateTime.UtcNow;
        public string ItemId => _task?.Id ?? _notification?.Id ?? string.Empty;
        public string Title => _task?.Name ?? _notification?.Message ?? string.Empty;
        public string Detail => _task is null
            ? BuildNotificationDetail(_notification!)
            : BuildTaskDetail(_task);
        public string ExpandedDetail => _task is null ? _notification?.Message ?? string.Empty : CopyText;
        public bool IsExpanded { get => _isExpanded; private set { if (_isExpanded == value) return; _isExpanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); } }
        public NotificationLevel Level => _notification?.Level ?? _task?.Status switch
        {
            BackgroundTaskStatus.Failed => NotificationLevel.Error,
            _ => NotificationLevel.Info,
        };
        public bool IsTask => _task is not null;
        public bool IsUnread => _notification?.IsUnread == true || _task?.IsUnread == true;
        public bool CanCancel => _task?.CanCancel == true;
        public bool CanRetry => _task?.CanRetry == true;
        public bool HasProgress => _task?.HasProgress == true;
        public double Progress => _task?.Progress ?? 0d;
        public bool HasReport => _task?.HasReport == true;
        public bool HasOutputDirectory => _task?.HasOutputDirectory == true;
        public bool IsAcknowledged => _notification?.IsAcknowledged == true || _task?.IsAcknowledged == true;
        public bool CanDismiss => _notification is not null || _task is { IsActive: false };
        public BackgroundTaskItem? Task => _task;
        internal NotificationItem? Notification => _notification;
        public string CopyText => _task is null
            ? string.IsNullOrWhiteSpace(Detail) ? Title : $"{Title}{Environment.NewLine}{Detail}"
            : string.Join(Environment.NewLine, new[]
            {
                $"标题：{Title}", $"来源：{_task.Origin}", $"状态：{_task.StatusText}", $"阶段：{_task.Stage}",
                $"创建时间：{_task.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}", $"开始时间：{_task.StartedAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
                $"完成时间：{_task.FinishedAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss}", $"详情：{_task.Detail}", $"错误：{_task.Error}",
                $"输出目录：{_task.OutputDirectory}", $"报告：{_task.ReportPath}",
            });

        public void ToggleExpanded() => IsExpanded = !IsExpanded;

        private static string BuildNotificationDetail(NotificationItem notification)
        {
            var parts = new System.Collections.Generic.List<string>
            {
                notification.UpdatedAt.ToLocalTime().ToString("HH:mm:ss")
            };
            if (!string.IsNullOrWhiteSpace(notification.Source)) parts.Add(notification.Source!);
            var count = notification.OccurrenceCount > 1 ? $" · 重复 {notification.OccurrenceCount} 次" : string.Empty;
            if (count.Length != 0) parts.Add(count.Trim(' ', '·'));
            return string.Join(" · ", parts);
        }

        private static string BuildTaskDetail(BackgroundTaskItem task)
        {
            var timestamp = (task.FinishedAt ?? task.StartedAt ?? task.CreatedAt)
                .ToLocalTime()
                .ToString("HH:mm:ss");
            var state = string.IsNullOrWhiteSpace(task.Stage) || string.Equals(task.Stage, task.StatusText, StringComparison.Ordinal)
                ? task.StatusText
                : $"{task.StatusText} · {task.Stage}";
            return $"{timestamp} · {state}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Dispose()
        {
            if (_notification is not null) _notification.PropertyChanged -= OnSourcePropertyChanged;
            if (_task is not null) _task.PropertyChanged -= OnSourcePropertyChanged;
        }

        private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public sealed class MessageCenterService
    {
        private readonly NotificationService _notifications;
        private readonly BackgroundTaskService _tasks;
        private readonly ObservableCollection<MessageCenterItem> _activeTasks = new();
        private readonly ObservableCollection<MessageCenterItem> _attentionItems = new();
        private readonly ObservableCollection<MessageCenterItem> _recentNotifications = new();
        private readonly ObservableCollection<MessageCenterItem> _messages = new();
        private readonly Queue<MessageCenterItem> _transientToastQueue = new();
        private readonly Dictionary<string, BackgroundTaskStatus> _observedTaskStatuses = new(StringComparer.Ordinal);
        private readonly Dispatcher? _dispatcher;
        private readonly DispatcherTimer? _transientToastTimer;
        private MessageCenterItem? _currentTransientToast;
        private readonly HashSet<string> _activeTaskToastIds = new(StringComparer.Ordinal);
        private int _refreshQueued;
        private readonly Dictionary<string, MessageCenterItem> _itemCache = new(StringComparer.Ordinal);

        public ReadOnlyObservableCollection<MessageCenterItem> ActiveTasks { get; }
        public ReadOnlyObservableCollection<MessageCenterItem> AttentionItems { get; }
        public ReadOnlyObservableCollection<MessageCenterItem> RecentNotifications { get; }
        public ReadOnlyObservableCollection<MessageCenterItem> Messages { get; }
        public MessageCenterItem? CurrentTransientToast => _currentTransientToast;
        public ReadOnlyObservableCollection<MessageCenterItem> ActiveTaskToasts { get; }
        public bool HasActiveTaskToasts => _activeTasks.Count > 0;
        public bool HasTransientToast => _currentTransientToast is not null;
        public int RunningTaskCount => _activeTasks.Count;
        public int AttentionCount => _tasks.Tasks.Count(task => task.Status == BackgroundTaskStatus.Failed && !task.IsAcknowledged && !task.IsInformationCenter)
            + _notifications.History.Count(item => item.Level is NotificationLevel.Warning or NotificationLevel.Error && !item.IsAcknowledged);
        public int RecentUnreadCount => _tasks.Tasks.Count(task => !task.IsActive && task.IsUnread && !task.IsInformationCenter)
            + _notifications.History.Count(item => item.IsUnread);
        public int UnreadCount => _tasks.Tasks.Count(task => !task.IsActive && task.IsUnread && !task.IsInformationCenter)
            + _notifications.History.Count(item => item.IsUnread);
        public int SuppressedUnreadCount => UnreadCount;
        public event EventHandler? Changed;

        public MessageCenterService(NotificationService notifications, BackgroundTaskService tasks)
        {
            _notifications = notifications;
            _tasks = tasks;
            _dispatcher = Application.Current?.Dispatcher;
            ActiveTasks = new ReadOnlyObservableCollection<MessageCenterItem>(_activeTasks);
            AttentionItems = new ReadOnlyObservableCollection<MessageCenterItem>(_attentionItems);
            RecentNotifications = new ReadOnlyObservableCollection<MessageCenterItem>(_recentNotifications);
            Messages = new ReadOnlyObservableCollection<MessageCenterItem>(_messages);
            ActiveTaskToasts = new ReadOnlyObservableCollection<MessageCenterItem>(_activeTasks);
            _notifications.Changed += (_, _) => Refresh();
            _notifications.Published += (_, item) => EnqueueTransientToast(item);
            _tasks.Changed += (_, args) => { if (args.RequiresProjectionRefresh) Refresh(); };
            if (_dispatcher is not null)
            {
                _transientToastTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
                _transientToastTimer.Tick += (_, _) =>
                {
                    _transientToastTimer.Stop();
                    AdvanceTransientToast();
                };
            }
            Refresh();
        }

        public void Refresh()
        {
            if (_dispatcher is null || _dispatcher.CheckAccess())
            {
                RefreshCore();
                return;
            }

            if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
                _ = _dispatcher.InvokeAsync(() => { Interlocked.Exchange(ref _refreshQueued, 0); RefreshCore(); }, DispatcherPriority.DataBind);
        }

        private void RefreshCore()
        {
            using var perf = UiPerformanceDiagnostics.Measure(
                "MessageCenter.Refresh",
                thresholdMilliseconds: 8,
                details: () => $"tasks={_tasks.Tasks.Count}; notifications={_notifications.History.Count}; active={_activeTasks.Count}; attention={_attentionItems.Count}; recent={_recentNotifications.Count}");
            foreach (var task in _tasks.Tasks.Where(task => !task.IsInformationCenter))
            {
                if (_observedTaskStatuses.TryGetValue(task.Id, out var previousStatus))
                {
                    if (previousStatus != task.Status && !task.IsActive)
                        EnqueueTransientToast(GetItem(task));
                }
                _observedTaskStatuses[task.Id] = task.Status;
            }

            Replace(_activeTasks, _tasks.Tasks
                .Where(task => task.IsActive && !task.IsInformationCenter)
                .OrderByDescending(task => task.StartedAt ?? task.CreatedAt)
                .Select(GetItem));

            Replace(_messages, _tasks.Tasks
                .Where(task => !task.IsInformationCenter)
                .Select(GetItem)
                .Concat(_notifications.History.Select(GetItem))
                .OrderByDescending(item => item.OccurredAt));

            Replace(_attentionItems, _tasks.Tasks
                .Where(task => task.Status == BackgroundTaskStatus.Failed && !task.IsAcknowledged && !task.IsInformationCenter)
                .Select(GetItem)
                .Concat(_notifications.History
                    .Where(item => item.Level is NotificationLevel.Warning or NotificationLevel.Error && !item.IsAcknowledged)
                    .Select(GetItem))
                .OrderByDescending(item => item.OccurredAt)
                .Take(40));

            Replace(_recentNotifications, _tasks.Tasks
                .Where(task => !task.IsInformationCenter && (task.Status is BackgroundTaskStatus.Completed or BackgroundTaskStatus.Canceled || task.Status == BackgroundTaskStatus.Failed && task.IsAcknowledged))
                .Select(GetItem)
                .Concat(_notifications.History
                    .Where(item => item.Level == NotificationLevel.Info || item.IsAcknowledged)
                    .Select(GetItem))
                .OrderByDescending(item => item.OccurredAt)
                .Take(80)
                );

            var liveIds = _tasks.Tasks.Select(task => task.Id)
                .Concat(_notifications.History.Select(notification => notification.Id))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var stale in _itemCache.Keys.Where(id => !liveIds.Contains(id)).ToList())
            {
                _itemCache[stale].Dispose();
                _itemCache.Remove(stale);
            }

            var activeTaskIds = _tasks.Tasks.Where(task => task.IsActive && task.IsForeground && !task.IsInformationCenter)
                .Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
            _activeTaskToastIds.Clear();
            foreach (var id in activeTaskIds) _activeTaskToastIds.Add(id);
            Replace(_activeTasks, _tasks.Tasks.Where(task => activeTaskIds.Contains(task.Id)).Select(GetItem));
            if (_currentTransientToast is not null && !_messages.Contains(_currentTransientToast)) AdvanceTransientToast();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void EnqueueTransientToast(NotificationItem notification)
        {
            EnqueueTransientToast(GetItem(notification));
        }

        private void EnqueueTransientToast(MessageCenterItem item)
        {
            _transientToastQueue.Enqueue(item);
            if (_currentTransientToast is null) AdvanceTransientToast();
            else Changed?.Invoke(this, EventArgs.Empty);
        }

        public void ClearTransientToasts()
        {
            _transientToastQueue.Clear();
            _currentTransientToast = null;
            _transientToastTimer?.Stop();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void AdvanceTransientToast()
        {
            _currentTransientToast = _transientToastQueue.Count == 0 ? null : _transientToastQueue.Dequeue();
            if (_currentTransientToast is not null && _transientToastTimer is not null)
            {
                _transientToastTimer.Stop();
                _transientToastTimer.Interval = TimeSpan.FromSeconds(4);
                _transientToastTimer.Start();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void MarkAllRead()
        {
            _notifications.MarkAllRead();
            foreach (var task in _tasks.Tasks.Where(task => !task.IsActive && !task.IsInformationCenter)) task.MarkRead();
            Refresh();
        }

        public void MarkCurrentMessagesRead()
        {
            var notificationSnapshot = _notifications.History.ToList();
            var taskSnapshot = _tasks.Tasks.Where(task => !task.IsInformationCenter).ToList();
            _notifications.MarkRead(notificationSnapshot);
            foreach (var task in taskSnapshot.Where(task => !task.IsActive)) task.MarkRead();
            Refresh();
        }

        public void DismissRead()
        {
            _notifications.DismissRead();
            foreach (var task in _tasks.Tasks.Where(task => !task.IsInformationCenter && !task.IsActive && !task.IsUnread).ToList())
                _tasks.Remove(task);
            Refresh();
        }

        public void ClearTransientToastsAndRefresh() => ClearTransientToasts();

        public void MarkAttentionViewed()
        {
            _notifications.MarkAttentionViewed();
            foreach (var task in _tasks.Tasks.Where(task => task.Status == BackgroundTaskStatus.Failed && !task.IsAcknowledged && !task.IsInformationCenter))
            {
                task.RecordAttentionView();
            }
        }

        public void Acknowledge(MessageCenterItem item)
        {
            if (item is null || !item.CanDismiss) return;
            if (item.Notification is { } notification) _notifications.Dismiss(notification);
            else if (item.Task is { IsActive: false } task) _tasks.Remove(task);
        }

        private static void Replace(ObservableCollection<MessageCenterItem> destination, IEnumerable<MessageCenterItem> source)
        {
            destination.Clear();
            foreach (var item in source) destination.Add(item);
        }

        private MessageCenterItem GetItem(BackgroundTaskItem task)
            => _itemCache.TryGetValue(task.Id, out var item) ? item : AddItem(task.Id, new MessageCenterItem(task));

        private MessageCenterItem GetItem(NotificationItem notification)
            => _itemCache.TryGetValue(notification.Id, out var item) ? item : AddItem(notification.Id, new MessageCenterItem(notification));

        private MessageCenterItem AddItem(string id, MessageCenterItem item)
        {
            _itemCache[id] = item;
            return item;
        }
    }
}
