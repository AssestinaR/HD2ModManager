using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using HD2ModManager.Services;

// 作用：以单次 Reset 通知替换列表内容，避免大量逐项 CollectionChanged 消息阻塞 UI 线程。
namespace HD2ModManager.ViewModels
{
    public enum ListTransitionKind
    {
        Automatic,
        Refresh,
        Filter,
        Reorder,
        Insert,
        Remove,
        Replace,
    }

    public sealed record ListTransitionBatch(
        long Version,
        ListTransitionKind Kind,
        int OldCount,
        int NewCount,
        bool Animate = true);

    public interface IListTransitionNotifier
    {
        event EventHandler<ListTransitionBatch>? TransitionStarting;
    }

    public sealed class BulkObservableCollection<T> : ObservableCollection<T>, IListTransitionNotifier
    {
        private bool _suppressNotifications;
        private readonly Func<T, string?>? _transitionKey;
        private long _transitionVersion;

        public BulkObservableCollection(Func<T, string?>? transitionKey = null)
        {
            _transitionKey = transitionKey;
        }

        public event EventHandler<ListTransitionBatch>? TransitionStarting;

        public void ReplaceWith(IEnumerable<T> items, ListTransitionKind kind = ListTransitionKind.Automatic)
        {
            ArgumentNullException.ThrowIfNull(items);
            var replacement = items as IReadOnlyList<T> ?? items.ToList();
            var oldCount = Count;
            var resolvedKind = ResolveTransitionKind(replacement, kind);
            UiPerformanceDiagnostics.RecordBurst(
                "List.ReplaceWith",
                $"itemType={typeof(T).Name}; kind={resolvedKind}; old={oldCount}; new={replacement.Count}",
                threshold: 12);
            using var perf = UiPerformanceDiagnostics.Measure(
                "List.ReplaceWith",
                thresholdMilliseconds: 25,
                details: () => $"itemType={typeof(T).Name}; kind={resolvedKind}; old={oldCount}; new={replacement.Count}");
            var batch = new ListTransitionBatch(
                Interlocked.Increment(ref _transitionVersion),
                resolvedKind,
                oldCount,
                replacement.Count,
                Animate: resolvedKind != ListTransitionKind.Refresh);
            TransitionStarting?.Invoke(this, batch);
            _suppressNotifications = true;
            try
            {
                Items.Clear();
                foreach (var item in replacement) Items.Add(item);
            }
            finally
            {
                _suppressNotifications = false;
            }
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            if (!_suppressNotifications) base.OnCollectionChanged(e);
        }

        private ListTransitionKind ResolveTransitionKind(IReadOnlyList<T> replacement, ListTransitionKind requestedKind)
        {
            if (requestedKind != ListTransitionKind.Automatic || _transitionKey is null) return requestedKind == ListTransitionKind.Automatic ? ListTransitionKind.Refresh : requestedKind;

            var before = Items.Select(_transitionKey).Where(key => !string.IsNullOrWhiteSpace(key)).ToArray();
            var after = replacement.Select(_transitionKey).Where(key => !string.IsNullOrWhiteSpace(key)).ToArray();
            if (before.Length != Count || after.Length != replacement.Count) return ListTransitionKind.Replace;
            if (before.SequenceEqual(after, StringComparer.OrdinalIgnoreCase)) return ListTransitionKind.Refresh;

            var beforeKeys = before.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var afterKeys = after.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (beforeKeys.SetEquals(afterKeys)) return ListTransitionKind.Reorder;
            if (beforeKeys.IsSubsetOf(afterKeys)) return ListTransitionKind.Insert;
            if (afterKeys.IsSubsetOf(beforeKeys)) return ListTransitionKind.Remove;
            return ListTransitionKind.Replace;
        }
    }
}
