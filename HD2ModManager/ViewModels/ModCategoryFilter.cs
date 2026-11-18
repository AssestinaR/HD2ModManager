using System.Collections.ObjectModel;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels
{
    // 分类键：内置分类用枚举名（"Armor"…），"待分类"用 "None"，用户自建分类用 "c:<id>"。
    internal static class CategoryKey
    {
        public const string Pending = "None";
        private const string CustomPrefix = "c:";

        public static string Of(ModCategory category) => category.ToString();
        public static string OfCustom(string id) => CustomPrefix + id;
        public static bool IsCustom(string key) => key.StartsWith(CustomPrefix, StringComparison.Ordinal);
        public static string CustomId(string key) => key[CustomPrefix.Length..];
        public static bool TryParseBuiltIn(string key, out ModCategory category)
            => Enum.TryParse(key, out category) && category != ModCategory.None && ModCategoryClassifier.Ordered.Contains(category);
    }

    // 作用：分类的用户数据——自建分类与"手动归类"覆盖，两个列表页共用一份，持久化在设置里。
    internal static class CategoryStore
    {
        public const int MaxNameLength = 12;
        private static readonly object Gate = new();
        private static List<CustomCategoryRecord>? _custom;
        private static Dictionary<string, string>? _overrides;
        public static event Action? Changed;

        private static void EnsureLoaded()
        {
            if (_custom is not null) return;
            var (custom, overrides) = SettingsService.GetCategoryUserData();
            _custom = custom.ToList();
            _overrides = new Dictionary<string, string>(overrides, StringComparer.OrdinalIgnoreCase);
        }

        public static IReadOnlyList<CustomCategoryRecord> Custom
        {
            get { lock (Gate) { EnsureLoaded(); return _custom!.ToArray(); } }
        }

        // 手动归类的目标；目标是已删除的自建分类时视为没有覆盖。
        public static string? GetOverride(string guid)
        {
            lock (Gate)
            {
                EnsureLoaded();
                if (!_overrides!.TryGetValue(guid, out var key)) return null;
                if (CategoryKey.IsCustom(key)) return _custom!.Any(item => item.Id == CategoryKey.CustomId(key)) ? key : null;
                return CategoryKey.TryParseBuiltIn(key, out _) ? key : null;
            }
        }

        // 名字不能为空、不能超长、不能和内置分类或其他自建分类重名。excludeId 用于重命名时排除自己。
        public static string? ValidateName(string? name, string? excludeId = null)
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            if (trimmed.Length > MaxNameLength) return null;
            if (ModCategoryClassifier.Ordered.Any(category => string.Equals(ModCategoryClassifier.DisplayName(category), trimmed, StringComparison.OrdinalIgnoreCase))) return null;
            if (string.Equals(trimmed, "全部", StringComparison.Ordinal) || string.Equals(trimmed, "待分类", StringComparison.Ordinal)) return null;
            lock (Gate)
            {
                EnsureLoaded();
                if (_custom!.Any(item => item.Id != excludeId && string.Equals(item.Name, trimmed, StringComparison.OrdinalIgnoreCase))) return null;
            }
            return trimmed;
        }

        public static string? AddCustom(string? name)
        {
            var valid = ValidateName(name);
            if (valid is null) return null;
            string key;
            lock (Gate)
            {
                EnsureLoaded();
                var id = Guid.NewGuid().ToString("N")[..8];
                _custom!.Add(new CustomCategoryRecord(id, valid));
                key = CategoryKey.OfCustom(id);
                Save();
            }
            Changed?.Invoke();
            return key;
        }

        public static bool Rename(string key, string? name)
        {
            if (!CategoryKey.IsCustom(key)) return false;
            var id = CategoryKey.CustomId(key);
            var valid = ValidateName(name, id);
            if (valid is null) return false;
            lock (Gate)
            {
                EnsureLoaded();
                var index = _custom!.FindIndex(item => item.Id == id);
                if (index < 0) return false;
                if (_custom[index].Name == valid) return true;
                _custom[index] = _custom[index] with { Name = valid };
                Save();
            }
            Changed?.Invoke();
            return true;
        }

        public static void Delete(string key)
        {
            if (!CategoryKey.IsCustom(key)) return;
            var id = CategoryKey.CustomId(key);
            lock (Gate)
            {
                EnsureLoaded();
                if (_custom!.RemoveAll(item => item.Id == id) == 0) return;
                foreach (var guid in _overrides!.Where(pair => pair.Value == key).Select(pair => pair.Key).ToArray())
                    _overrides!.Remove(guid);
                Save();
            }
            Changed?.Invoke();
        }

        // key 为 null：撤销手动归类，回到自动分类。
        public static void Assign(IEnumerable<string> guids, string? key)
        {
            var changed = false;
            lock (Gate)
            {
                EnsureLoaded();
                foreach (var guid in guids)
                {
                    if (key is null) changed |= _overrides!.Remove(guid);
                    else if (!_overrides!.TryGetValue(guid, out var current) || current != key) { _overrides[guid] = key; changed = true; }
                }
                if (changed) Save();
            }
            if (changed) Changed?.Invoke();
        }

        private static void Save() => SettingsService.SetCategoryUserData(_custom!, _overrides!);
    }

    // 作用：模组库页与配置页共用的"分类筛选"：分类缓存、多选键集合、命中判断，
    // 以及手动归类 / 自建分类的编辑入口。
    //
    // 多选（并集）是标签式搜索的直接产物：每个选中的分类是搜索框里的一枚标签，
    // 摘掉标签就是取消选中。这里不再有"整体开关"——分类永远可用，没选任何分类就等于不筛。
    public sealed class ModCategoryFilter : BaseViewModel, IDisposable
    {
        private readonly Func<string, ModAssetSummary?> _summaryOf;
        private readonly Action _onSelectionChanged;
        // 自动分类只在资产摘要对象变化时重算。
        private readonly Dictionary<string, (ModAssetSummary? Summary, ModCategory Category)> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ModCategorySelection _selection = new();
        // 上一次 Rebuild 统计芯片时各 Mod 的分类签名，用来判断"资产摘要后来才到、分类变了"。
        private readonly Dictionary<string, string> _counted = new(StringComparer.OrdinalIgnoreCase);

        public ModCategoryFilter(Func<string, ModAssetSummary?> summaryOf, Action onSelectionChanged)
        {
            _summaryOf = summaryOf ?? throw new ArgumentNullException(nameof(summaryOf));
            _onSelectionChanged = onSelectionChanged ?? throw new ArgumentNullException(nameof(onSelectionChanged));
            CategoryStore.Changed += OnStoreChanged;
        }

        /// <summary>选中的分类键（并集语义）。只读投影：改它请走 <see cref="Toggle"/> / <see cref="ClearSelection"/>。</summary>
        public IReadOnlyList<string> SelectedKeys => _selection.Keys;

        /// <summary>是否正在按分类筛选。为 false 时列表是全集。</summary>
        public bool IsActive => _selection.IsActive;

        /// <summary>选择变了（点标签 / 清空 / 自建分类被删）。**不会**触发宿主刷新，只让界面重新投影。</summary>
        public event Action? SelectionChanged;

        public bool Toggle(string key)
        {
            if (!_selection.Toggle(key)) return false;
            NotifySelectionChanged(refreshHost: true);
            return true;
        }

        public bool ClearSelection()
        {
            if (!_selection.Clear()) return false;
            NotifySelectionChanged(refreshHost: true);
            return true;
        }

        public bool IsSelected(string key) => _selection.Contains(key);

        private void NotifySelectionChanged(bool refreshHost)
        {
            OnPropertyChanged(nameof(IsActive));
            foreach (var chip in Chips) chip.IsSelected = _selection.Contains(chip.Key);
            SelectionChanged?.Invoke();
            if (refreshHost) _onSelectionChanged();
        }

        // 分类的用户数据变了（新建/改名/删除/手动归类）：自建分类被删就摘键，然后整体刷新。
        private void OnStoreChanged()
        {
            if (PruneDeletedCustomKeys()) OnPropertyChanged(nameof(IsActive));
            SelectionChanged?.Invoke();
            _onSelectionChanged();
        }

        // 只摘"已经不存在的自建分类"。内置分类即使这一刻 0 个 Mod 也不摘：那会悄悄删掉
        // 用户刚点出来的标签，而且列表是不是空的本来就该由列表自己说。
        private bool PruneDeletedCustomKeys()
        {
            var known = ModCategoryClassifier.Ordered.Select(CategoryKey.Of)
                .Append(CategoryKey.Pending)
                .Concat(CategoryStore.Custom.Select(item => CategoryKey.OfCustom(item.Id)));
            return _selection.PruneKnown(known);
        }

        public void Dispose()
        {
            CategoryStore.Changed -= OnStoreChanged;
        }

        public ObservableCollection<LibraryCategoryChip> Chips { get; } = new();

        // ---- 分类计算 ----

        private ModCategory GetAutoCategory(string guid)
        {
            var summary = _summaryOf(guid);
            if (_cache.TryGetValue(guid, out var cached) && ReferenceEquals(cached.Summary, summary)) return cached.Category;
            var category = ModCategoryClassifier.Classify(summary);
            _cache[guid] = (summary, category);
            return category;
        }

        // 一个 Mod 当前属于哪些分类键：手动归类优先（只属于那一个），否则按自动分类。
        public IReadOnlyList<string> GetKeys(string guid)
        {
            if (CategoryStore.GetOverride(guid) is { } overridden) return new[] { overridden };
            var category = GetAutoCategory(guid);
            if (category == ModCategory.None) return new[] { CategoryKey.Pending };
            var keys = new List<string>(2);
            foreach (var single in ModCategoryClassifier.Ordered)
                if ((category & single) != 0) keys.Add(CategoryKey.Of(single));
            return keys;
        }

        public bool Matches(string guid) => _selection.Matches(GetKeys(guid));

        // 资产摘要是后台异步算出来的：刚打开时多数 Mod 还是"待分类"，摘要到了才有真实分类。
        // 宿主在内容刷新时问一句：已统计过的 Mod 里有没有谁的分类变了；变了就该整体刷新一次芯片。
        // guids 为 null 表示"全部已统计的 Mod"。
        public bool HasClassificationChanged(IEnumerable<string>? guids)
        {
            foreach (var guid in guids ?? _counted.Keys.ToArray())
            {
                if (_counted.TryGetValue(guid, out var counted) && counted != Signature(guid)) return true;
            }
            return false;
        }

        private string Signature(string guid) => string.Join('|', GetKeys(guid));

        public void Rebuild(IReadOnlyCollection<string> candidateGuids)
        {
            _counted.Clear();

            var counts = new Dictionary<string, int>();
            foreach (var guid in candidateGuids)
            {
                var keys = GetKeys(guid);
                _counted[guid] = string.Join('|', keys);
                foreach (var key in keys) counts[key] = counts.GetValueOrDefault(key) + 1;
            }

            if (PruneDeletedCustomKeys())
            {
                OnPropertyChanged(nameof(IsActive));
                SelectionChanged?.Invoke();
            }

            var custom = CategoryStore.Custom;
            var desired = new List<LibraryCategoryChip>();
            foreach (var category in ModCategoryClassifier.Ordered)
            {
                var key = CategoryKey.Of(category);
                Chip(desired, key, ModCategoryClassifier.DisplayName(category), CategoryChipKind.BuiltIn, counts.GetValueOrDefault(key));
            }
            if (counts.GetValueOrDefault(CategoryKey.Pending) > 0)
                Chip(desired, CategoryKey.Pending, "待分类", CategoryChipKind.Pending, counts[CategoryKey.Pending]);
            foreach (var item in custom)
            {
                var key = CategoryKey.OfCustom(item.Id);
                Chip(desired, key, item.Name, CategoryChipKind.Custom, counts.GetValueOrDefault(key));
            }
            Reconcile(desired);
            OnPropertyChanged(nameof(IsActive));
        }

        private void Chip(List<LibraryCategoryChip> into, string key, string label, CategoryChipKind kind, int count)
        {
            var existing = Chips.FirstOrDefault(chip => chip.Kind == kind && chip.Key == key);
            var selected = _selection.Contains(key);
            if (existing is null)
            {
                into.Add(new LibraryCategoryChip(key, label, kind, count, selected));
                return;
            }
            existing.Count = count;
            existing.IsSelected = selected;
            existing.Label = label;
            into.Add(existing);
        }

        // 原地对齐芯片集合：已有的对象保留（保住输入焦点和动画状态），只增删/挪动有差异的部分。
        private void Reconcile(List<LibraryCategoryChip> desired)
        {
            for (var i = Chips.Count - 1; i >= 0; i--)
                if (!desired.Contains(Chips[i])) Chips.RemoveAt(i);
            for (var i = 0; i < desired.Count; i++)
            {
                var current = Chips.IndexOf(desired[i]);
                if (current < 0) Chips.Insert(i, desired[i]);
                else if (current != i) Chips.Move(current, i);
            }
        }

        /// <summary>一个分类键现在叫什么（给搜索框里的标签用）。找不到就退回键本身。</summary>
        public string LabelOf(string key)
        {
            var chip = Chips.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            if (chip is not null) return chip.Label;
            if (CategoryKey.TryParseBuiltIn(key, out var category)) return ModCategoryClassifier.DisplayName(category);
            if (string.Equals(key, CategoryKey.Pending, StringComparison.Ordinal)) return "待分类";
            if (CategoryKey.IsCustom(key))
            {
                var id = CategoryKey.CustomId(key);
                return CategoryStore.Custom.FirstOrDefault(item => item.Id == id)?.Name ?? "已删除的分类";
            }
            return key;
        }

        // ---- 手动归类 ----

        // 菜单里可选的归类目标：内置分类 + 自建分类。"待分类"不是真分类，不可选。
        public IReadOnlyList<(string Key, string Label)> AssignTargets
        {
            get
            {
                var list = new List<(string, string)>();
                foreach (var category in ModCategoryClassifier.Ordered)
                    list.Add((CategoryKey.Of(category), ModCategoryClassifier.DisplayName(category)));
                foreach (var item in CategoryStore.Custom)
                    list.Add((CategoryKey.OfCustom(item.Id), item.Name));
                return list;
            }
        }

        public bool AllInCategory(IEnumerable<string> guids, string key) => guids.All(guid => GetKeys(guid).Contains(key));
        public bool AnyOverridden(IEnumerable<string> guids) => guids.Any(guid => CategoryStore.GetOverride(guid) is not null);
        public void Assign(IEnumerable<string> guids, string? key) => CategoryStore.Assign(guids, key);

        // 弹窗里"创建并放入"：建好自建分类，并把这些 Mod 归进去。名字无效返回 false。
        public bool CreateAndAssign(string? name, IReadOnlyList<string> guids)
        {
            var key = CategoryStore.AddCustom(name);
            if (key is null) return false;
            CategoryStore.Assign(guids, key);
            return true;
        }

        // ---- 自建分类的管理（由"设置分类"弹窗调用）----

        public IReadOnlyList<CustomCategoryRecord> CustomCategories => CategoryStore.Custom;
        public int CountOf(string key) => Chips.FirstOrDefault(chip => chip.Key == key && chip.Kind == CategoryChipKind.Custom)?.Count ?? 0;
        public bool Create(string? name) => CategoryStore.AddCustom(name) is not null;
        public bool Rename(string key, string? name) => CategoryStore.Rename(key, name);
        // 删除后里面的 Mod 回到自动分类（覆盖记录随之清掉）。
        public void Delete(string key) => CategoryStore.Delete(key);
    }
}
