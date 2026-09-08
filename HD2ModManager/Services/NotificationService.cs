using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;

namespace HD2ModManager.Services
{
    public enum NotificationLevel { Info, Warning, Error }

    public class NotificationItem : INotifyPropertyChanged
    {
        private string _message = string.Empty;
        private NotificationLevel _level = NotificationLevel.Info;
        public string Message { get => _message; set => SetField(ref _message, value); }
        public NotificationLevel Level { get => _level; set => SetField(ref _level, value); }
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        private DateTime _updatedAt = DateTime.UtcNow;
        private int _occurrenceCount = 1;
        private bool _isUnread = true;
        private bool _isAcknowledged;
        private int _attentionViewCount;
        public DateTime UpdatedAt { get => _updatedAt; private set => SetField(ref _updatedAt, value); }
        public int OccurrenceCount { get => _occurrenceCount; private set => SetField(ref _occurrenceCount, value); }
        public TimeSpan? Duration { get; set; } = TimeSpan.FromSeconds(6);
        public bool IsUnread { get => _isUnread; set => SetField(ref _isUnread, value); }
        public bool IsAcknowledged { get => _isAcknowledged; private set => SetField(ref _isAcknowledged, value); }
        public int AttentionViewCount { get => _attentionViewCount; private set => SetField(ref _attentionViewCount, value); }
        public string? Source { get; init; }
        public string? AggregationKey { get; init; }
        internal string? Tag { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        internal void RecordRepeat(DateTime timestamp)
        {
            OccurrenceCount++;
            UpdatedAt = timestamp;
            IsUnread = true;
            ResetAcknowledgement();
        }

        internal void Update(string message, NotificationLevel level, TimeSpan? duration, DateTime timestamp)
        {
            Message = message;
            Level = level;
            Duration = duration;
            UpdatedAt = timestamp;
            IsUnread = true;
            ResetAcknowledgement();
        }

        internal void RecordAttentionView()
        {
            if (IsAcknowledged) return;
            AttentionViewCount++;
            if (AttentionViewCount >= 2) Acknowledge();
        }

        internal void Acknowledge()
        {
            IsAcknowledged = true;
            IsUnread = false;
        }

        private void ResetAcknowledgement()
        {
            IsAcknowledged = false;
            AttentionViewCount = 0;
        }
    }

    public class NotificationService
    {
        private readonly ObservableCollection<NotificationItem> _items = new();
        private readonly ObservableCollection<NotificationItem> _history = new();
        public ReadOnlyObservableCollection<NotificationItem> Items { get; }
        public ReadOnlyObservableCollection<NotificationItem> History { get; }
        public event EventHandler? Changed;
        public event EventHandler<NotificationItem>? Published;

        public NotificationService()
        {
            Items = new ReadOnlyObservableCollection<NotificationItem>(_items);
            History = new ReadOnlyObservableCollection<NotificationItem>(_history);
        }

        public void Show(string message, NotificationLevel level = NotificationLevel.Info, TimeSpan? duration = null, string? source = null, string? aggregationKey = null)
        {
            UiPerformanceDiagnostics.RecordBurst(
                "Notification.Request",
                $"level={level}; source={(string.IsNullOrWhiteSpace(source) ? "none" : "set")}",
                threshold: 12);
            RunOnUi(() =>
            {
                var now = DateTime.UtcNow;
                var item = FindRecentDuplicate(message, level, source, aggregationKey, now);
                if (item is not null)
                {
                    item.RecordRepeat(now);
                    if (!_items.Contains(item)) _items.Add(item);
                    Published?.Invoke(this, item);
                    RaiseChanged();
                    return;
                }

                item = new NotificationItem
                {
                    Message = message,
                    Level = level,
                    Duration = duration ?? TimeSpan.FromSeconds(6),
                    Source = source,
                    AggregationKey = aggregationKey,
                };
                _items.Add(item);
                _history.Add(item);
                TrimHistory();
                if (item.Duration.HasValue) _ = AutoDismissAsync(item);
                Published?.Invoke(this, item);
                RaiseChanged();
            });
        }

        public void ShowOrUpdate(string key, string message, NotificationLevel level = NotificationLevel.Info, TimeSpan? duration = null, string? source = null)
        {
            UiPerformanceDiagnostics.RecordBurst("Notification.ShowOrUpdate", $"level={level}", threshold: 12);
            RunOnUi(() =>
            {
                var item = _history.FirstOrDefault(candidate => string.Equals(candidate.Tag, key, StringComparison.Ordinal));
                if (item is null)
                {
                    item = new NotificationItem { Message = message, Level = level, Duration = duration, Source = source, AggregationKey = key, Tag = key };
                    _items.Add(item);
                    _history.Add(item);
                    TrimHistory();
                    Published?.Invoke(this, item);
                    RaiseChanged();
                    return;
                }

                item.Update(message, level, duration, DateTime.UtcNow);
                if (!_items.Contains(item)) _items.Add(item);
                Published?.Invoke(this, item);
                RaiseChanged();
            });
        }

        public void MarkRead(IReadOnlyCollection<NotificationItem> snapshot)
        {
            RunOnUi(() =>
            {
                foreach (var item in snapshot.Where(_history.Contains)) item.IsUnread = false;
                RaiseChanged();
            });
        }

        public void MarkAllRead() => MarkRead(_history.ToList());

        public void Dismiss(NotificationItem item)
        {
            if (item is null) return;
            RunOnUi(() =>
            {
                _items.Remove(item);
                _history.Remove(item);
                RaiseChanged();
            });
        }

        public void DismissRead()
        {
            RunOnUi(() =>
            {
                var removed = _history.Where(item => !item.IsUnread).ToList();
                foreach (var item in removed)
                {
                    _items.Remove(item);
                    _history.Remove(item);
                }
                if (removed.Count != 0) RaiseChanged();
            });
        }

        public void Supersede(string aggregationKey)
        {
            if (string.IsNullOrWhiteSpace(aggregationKey)) return;
            RunOnUi(() =>
            {
                var removed = _history.Where(item => string.Equals(item.AggregationKey, aggregationKey, StringComparison.Ordinal)).ToList();
                foreach (var item in removed)
                {
                    _items.Remove(item);
                    _history.Remove(item);
                }
                if (removed.Count != 0) RaiseChanged();
            });
        }

        public void MarkAttentionViewed()
        {
            foreach (var item in _history.Where(item => item.Level is NotificationLevel.Warning or NotificationLevel.Error && !item.IsAcknowledged))
            {
                item.RecordAttentionView();
            }
            RaiseChanged();
        }

        public void Acknowledge(NotificationItem item)
        {
            if (item is null) return;
            RunOnUi(() =>
            {
                item.Acknowledge();
                RaiseChanged();
            });
        }

        private async Task AutoDismissAsync(NotificationItem item)
        {
            try
            {
                await Task.Delay(item.Duration!.Value);
                RunOnUi(() =>
                {
                    if (_items.Remove(item)) RaiseChanged();
                });
            }
            catch { }
        }

        public void Clear() => RunOnUi(() =>
        {
            if (_items.Count == 0) return;
            _items.Clear();
            RaiseChanged();
        });

        private void RaiseChanged()
        {
            UiPerformanceDiagnostics.RecordBurst(
                "Notification.Changed",
                $"visible={_items.Count}; history={_history.Count}",
                threshold: 12);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private NotificationItem? FindRecentDuplicate(string message, NotificationLevel level, string? source, string? aggregationKey, DateTime now)
            => _history.LastOrDefault(item => item.Level == level
                && string.Equals(item.Message, message, StringComparison.Ordinal)
                && string.Equals(item.Source, source, StringComparison.Ordinal)
            && string.Equals(item.AggregationKey, aggregationKey, StringComparison.Ordinal)
                && now - item.UpdatedAt <= TimeSpan.FromSeconds(10));

        private void TrimHistory()
        {
            const int historyLimit = 300;
            while (_history.Count > historyLimit)
            {
                var oldestInfo = _history.FirstOrDefault(item => item.Level == NotificationLevel.Info);
                _history.Remove(oldestInfo ?? _history[0]);
            }
        }

        private static void RunOnUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            _ = dispatcher.InvokeAsync(action);
        }
    }
}
