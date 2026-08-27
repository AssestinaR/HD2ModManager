using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
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
        public string Title => _task?.Name ?? _notification?.Message ?? string.Empty;
        public string Detail => _task is null
            ? BuildNotificationDetail(_notification!)
            : string.IsNullOrWhiteSpace(_task.Stage) || string.Equals(_task.Stage, _task.StatusText, StringComparison.Ordinal)
                ? _task.StatusText
                : $"{_task.StatusText} · {_task.Stage}";
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
        public bool CanAcknowledge => _notification?.IsAcknowledged == false
            || _task is { IsActive: true, IsFocusDismissed: false }
            || _task is { Status: BackgroundTaskStatus.Failed, IsAcknowledged: false };
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

        private static string BuildNotificationDetail(NotificationItem notification)
        {
            var source = string.IsNullOrWhiteSpace(notification.Source) ? string.Empty : $"{notification.Source} · ";
            var count = notification.OccurrenceCount > 1 ? $" · 重复 {notification.OccurrenceCount} 次" : string.Empty;
            return $"{source}{notification.UpdatedAt.ToLocalTime():HH:mm:ss}{count}";
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
        private readonly Dispatcher? _dispatcher;
        private readonly DispatcherTimer? _transientToastTimer;
        private MessageCenterItem? _activityToastItem;
        private MessageCenterItem? _attentionToastItem;
        private int _refreshQueued;

        public ReadOnlyObservableCollection<MessageCenterItem> ActiveTasks { get; }
        public ReadOnlyObservableCollection<MessageCenterItem> AttentionItems { get; }
        public ReadOnlyObservableCollection<MessageCenterItem> RecentNotifications { get; }
        public MessageCenterItem? ActivityToastItem => _activityToastItem;
        public MessageCenterItem? AttentionToastItem => _attentionToastItem;
        public bool HasActivityToast => _activityToastItem is not null;
        public bool HasAttentionToast => _attentionToastItem is not null;
        public int RunningTaskCount => _activeTasks.Count;
        public int AttentionCount => _tasks.Tasks.Count(task => task.Status == BackgroundTaskStatus.Failed && !task.IsAcknowledged && !task.IsInformationCenter)
            + _notifications.History.Count(item => item.Level is NotificationLevel.Warning or NotificationLevel.Error && !item.IsAcknowledged);
        public int RecentUnreadCount => _tasks.Tasks.Count(task => !task.IsActive && task.IsUnread && !task.IsInformationCenter)
            + _notifications.History.Count(item => item.IsUnread);
        public int UnreadCount => _tasks.Tasks.Count(task => !task.IsActive && task.IsUnread && !task.IsInformationCenter)
            + _notifications.History.Count(item => item.IsUnread);
        public int SuppressedUnreadCount => Math.Max(0, UnreadCount - (_attentionToastItem?.IsUnread == true ? 1 : 0));
        public event EventHandler? Changed;

        public MessageCenterService(NotificationService notifications, BackgroundTaskService tasks)
        {
            _notifications = notifications;
            _tasks = tasks;
            _dispatcher = Application.Current?.Dispatcher;
            ActiveTasks = new ReadOnlyObservableCollection<MessageCenterItem>(_activeTasks);
            AttentionItems = new ReadOnlyObservableCollection<MessageCenterItem>(_attentionItems);
            RecentNotifications = new ReadOnlyObservableCollection<MessageCenterItem>(_recentNotifications);
            _notifications.Changed += (_, _) => Refresh();
            _tasks.Changed += (_, args) => { if (args.RequiresProjectionRefresh) Refresh(); };
            if (_dispatcher is not null)
            {
                _transientToastTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
                _transientToastTimer.Tick += (_, _) =>
                {
                    _transientToastTimer.Stop();
                    Refresh();
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
            Replace(_activeTasks, _tasks.Tasks
                .Where(task => task.IsActive && !task.IsInformationCenter)
                .OrderByDescending(task => task.StartedAt ?? task.CreatedAt)
                .Select(task => new MessageCenterItem(task)));

            Replace(_attentionItems, _tasks.Tasks
                .Where(task => task.Status == BackgroundTaskStatus.Failed && !task.IsAcknowledged && !task.IsInformationCenter)
                .Select(task => new MessageCenterItem(task))
                .Concat(_notifications.History
                    .Where(item => item.Level is NotificationLevel.Warning or NotificationLevel.Error && !item.IsAcknowledged)
                    .Select(item => new MessageCenterItem(item)))
                .OrderByDescending(item => item.OccurredAt)
                .Take(40));

            Replace(_recentNotifications, _tasks.Tasks
                .Where(task => !task.IsInformationCenter && (task.Status is BackgroundTaskStatus.Completed or BackgroundTaskStatus.Canceled || task.Status == BackgroundTaskStatus.Failed && task.IsAcknowledged))
                .Select(task => new MessageCenterItem(task))
                .Concat(_notifications.History
                    .Where(item => item.Level == NotificationLevel.Info || item.IsAcknowledged)
                    .Select(item => new MessageCenterItem(item)))
                .OrderByDescending(item => item.OccurredAt)
                .Take(80)
                );

            var now = DateTime.UtcNow;
            var activityTask = _tasks.Tasks
                .Where(task => task.IsActive && task.IsForeground && !task.IsInformationCenter && !task.IsFocusDismissed)
                .OrderByDescending(task => task.StartedAt ?? task.CreatedAt)
                .FirstOrDefault();
            SetToastTask(ref _activityToastItem, activityTask);

            var attention = SelectAttentionToast(now);
            SetToast(ref _attentionToastItem, attention);
            ScheduleTransientToastRefresh(now);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void MarkAllRead()
        {
            _notifications.MarkAllRead();
            foreach (var task in _tasks.Tasks.Where(task => !task.IsActive && !task.IsInformationCenter)) task.MarkRead();
            Refresh();
        }

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
            if (item is null || !item.CanAcknowledge) return;
            if (item.Notification is { } notification) _notifications.Acknowledge(notification);
            else if (item.Task is { IsActive: true } task) task.DismissFromFocus();
            else item.Task?.Acknowledge();
        }

        private MessageCenterItem? SelectAttentionToast(DateTime now)
        {
            var persistent = _tasks.Tasks
                .Where(task => task.Status == BackgroundTaskStatus.Failed && !task.IsAcknowledged && !task.IsInformationCenter)
                .Select(task => ToastSource.ForTask(task, priority: 3))
                .Concat(_notifications.History
                    .Where(item => item.Level is NotificationLevel.Warning or NotificationLevel.Error && !item.IsAcknowledged)
                    .Select(item => ToastSource.ForNotification(item, item.Level == NotificationLevel.Error ? 3 : 2)))
                .OrderByDescending(source => source.Priority)
                .ThenByDescending(source => source.OccurredAt)
                .FirstOrDefault();
            if (persistent is not null) return persistent.ToItem();

            var terminal = _tasks.Tasks
                .Where(task => task.IsForeground && task.Status == BackgroundTaskStatus.Completed && task.IsUnread && !task.IsInformationCenter)
                .Where(task => task.FinishedAt is { } finished && now - finished <= TimeSpan.FromSeconds(4))
                .OrderByDescending(task => task.FinishedAt)
                .FirstOrDefault();
            if (terminal is not null) return new MessageCenterItem(terminal);

            var info = _notifications.Items
                .Where(item => item.Level == NotificationLevel.Info && item.IsUnread && !item.IsAcknowledged && now - item.UpdatedAt <= TimeSpan.FromSeconds(4))
                .OrderByDescending(item => item.UpdatedAt)
                .FirstOrDefault();
            return info is null ? null : new MessageCenterItem(info);
        }

        private void ScheduleTransientToastRefresh(DateTime now)
        {
            if (_transientToastTimer is null) return;
            var expires = _tasks.Tasks
                .Where(task => task.IsForeground && task.Status == BackgroundTaskStatus.Completed && task.IsUnread && task.FinishedAt is not null)
                .Select(task => task.FinishedAt!.Value + TimeSpan.FromSeconds(4))
                .Concat(_notifications.Items
                    .Where(item => item.Level == NotificationLevel.Info && item.IsUnread && !item.IsAcknowledged)
                    .Select(item => item.UpdatedAt + TimeSpan.FromSeconds(4)))
                .Where(expiry => expiry > now)
                .OrderBy(expiry => expiry)
                .FirstOrDefault();
            if (expires == default)
            {
                _transientToastTimer.Stop();
                return;
            }

            _transientToastTimer.Stop();
            _transientToastTimer.Interval = expires - now < TimeSpan.FromMilliseconds(100)
                ? TimeSpan.FromMilliseconds(100)
                : expires - now;
            _transientToastTimer.Start();
        }

        private static void SetToastTask(ref MessageCenterItem? destination, BackgroundTaskItem? task)
        {
            if (ReferenceEquals(destination?.Task, task)) return;
            destination?.Dispose();
            destination = task is null ? null : new MessageCenterItem(task);
        }

        private static void SetToast(ref MessageCenterItem? destination, MessageCenterItem? next)
        {
            var isSameTask = ReferenceEquals(destination?.Task, next?.Task);
            var isSameNotification = ReferenceEquals(destination?.Notification, next?.Notification);
            if (isSameTask || isSameNotification)
            {
                next?.Dispose();
                return;
            }

            destination?.Dispose();
            destination = next;
        }

        private sealed record ToastSource(BackgroundTaskItem? Task, NotificationItem? Notification, int Priority, DateTime OccurredAt)
        {
            public static ToastSource ForTask(BackgroundTaskItem task, int priority)
                => new(task, null, priority, task.FinishedAt ?? task.StartedAt ?? task.CreatedAt);

            public static ToastSource ForNotification(NotificationItem notification, int priority)
                => new(null, notification, priority, notification.UpdatedAt);

            public MessageCenterItem ToItem()
                => Task is not null ? new MessageCenterItem(Task) : new MessageCenterItem(Notification!);
        }

        private static void Replace(ObservableCollection<MessageCenterItem> destination, IEnumerable<MessageCenterItem> source)
        {
            foreach (var item in destination) item.Dispose();
            destination.Clear();
            foreach (var item in source) destination.Add(item);
        }
    }
}
