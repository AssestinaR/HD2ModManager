using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace HD2ModManager.ViewModels
{
    public sealed class SelectionChangedEventArgs : EventArgs
    {
        public SelectionChangedEventArgs(
            string? previousScope,
            IReadOnlyList<string> previousSelectedIds,
            string? scope,
            IReadOnlyList<string> selectedIds)
        {
            PreviousScope = previousScope;
            PreviousSelectedIds = previousSelectedIds;
            Scope = scope;
            SelectedIds = selectedIds;
        }

        public string? PreviousScope { get; }
        public IReadOnlyList<string> PreviousSelectedIds { get; }
        public string? Scope { get; }
        public IReadOnlyList<string> SelectedIds { get; }

        public IReadOnlyList<string> GetCurrentIds(string targetScope)
            => string.Equals(Scope, targetScope, StringComparison.OrdinalIgnoreCase)
                ? SelectedIds
                : Array.Empty<string>();

        public IReadOnlyList<string> GetChangedIds(string targetScope)
        {
            var previous = string.Equals(PreviousScope, targetScope, StringComparison.OrdinalIgnoreCase)
                ? PreviousSelectedIds
                : Array.Empty<string>();
            var current = GetCurrentIds(targetScope);
            return SelectionSetOperations.SymmetricDifference(previous, current);
        }
    }

    // 护甲映射页的选择作用域。集中在这里是因为**底栏的动作由它决定**：
    // 同一批右侧选中的护甲，"来源列表里"是「转为视觉对象」，
    // "某个来源的目标列表里"是「注册为映射目标」——这是两种完全不同的操作，
    // 以前它们共用一个作用域，于是多选永远只会变成视觉来源（见
    // docs/armor-registry-page-perf-20261027.md §4 D1）。
    internal static class ArmorRegistryScopes
    {
        public const string Available = "ArmorRegistry:Available";
        public const string AvailableAsTarget = "ArmorRegistry:AvailableAsTarget";
        public const string Providers = "ArmorRegistry:Providers";
        public const string Targets = "ArmorRegistry:Targets";

        public static bool IsAvailable(string? scope)
            => string.Equals(scope, Available, StringComparison.Ordinal)
                || string.Equals(scope, AvailableAsTarget, StringComparison.Ordinal);
    }

    // 作用：维护跨页面唯一的临时选择上下文，供底部悬浮操作栏使用。
    public sealed class SelectionCoordinator : BaseViewModel
    {
        private string? _scope;
        private readonly ObservableCollection<string> _selectedIds = new();

        public event EventHandler<SelectionChangedEventArgs>? SelectionChanged;

        public string? Scope
        {
            get => _scope;
            private set
            {
                if (SetField(ref _scope, value))
                {
                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(Summary));
                }
            }
        }

        public IReadOnlyList<string> SelectedIds => _selectedIds;
        public bool HasSelection => _selectedIds.Count > 0;
        public string Summary => Scope switch
        {
            "Library" => $"已选择 {_selectedIds.Count} 个库内 Mod",
            "Profile" => $"已选择 {_selectedIds.Count} 个配置项",
            var scope when scope?.StartsWith("DecorationHost:", StringComparison.Ordinal) == true => $"已选择 {_selectedIds.Count} 个附加 Mod",
            _ => $"已选择 {_selectedIds.Count} 项",
        };

        public void Replace(string scope, IEnumerable<string> ids)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(scope);
            ArgumentNullException.ThrowIfNull(ids);

            var nextIds = ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var nextScope = nextIds.Length == 0 ? null : scope;
            var previousIds = _selectedIds.ToArray();
            if (string.Equals(_scope, nextScope, StringComparison.Ordinal)
                && previousIds.SequenceEqual(nextIds, StringComparer.OrdinalIgnoreCase))
                return;

            var previousScope = _scope;
            Scope = nextScope;
            _selectedIds.Clear();
            foreach (var id in nextIds) _selectedIds.Add(id);
            NotifySelectionChanged(previousScope, previousIds);
        }

        public void Clear()
        {
            if (_scope is null && _selectedIds.Count == 0) return;
            var previousScope = _scope;
            var previousIds = _selectedIds.ToArray();
            _selectedIds.Clear();
            Scope = null;
            NotifySelectionChanged(previousScope, previousIds);
        }

        private void NotifySelectionChanged(string? previousScope, IReadOnlyList<string> previousIds)
        {
            OnPropertyChanged(nameof(SelectedIds));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(Summary));
            SelectionChanged?.Invoke(this, new SelectionChangedEventArgs(
                previousScope,
                previousIds,
                Scope,
                _selectedIds.ToArray()));
        }
    }

    internal static class SelectionSetOperations
    {
        public static IReadOnlyList<string> SymmetricDifference(
            IEnumerable<string> previous,
            IEnumerable<string> current)
        {
            var previousSet = previous.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var currentSet = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return previousSet
                .Concat(currentSet)
                .Where(id => previousSet.Contains(id) != currentSet.Contains(id))
                .ToArray();
        }
    }
}
