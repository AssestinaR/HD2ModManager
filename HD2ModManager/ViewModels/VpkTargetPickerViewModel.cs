using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using HD2ModCore.Infrastructure.KitInstance;

namespace HD2ModManager.ViewModels
{
    // 作用：下拉里的一项（带勾选状态）。专属多选模式下 IsChecked 才有意义。
    // Purpose: One row of the target picker with its checked state (multi-select mode only).
    public sealed class VpkTargetChoice : BaseViewModel
    {
        private bool _isChecked;

        public VpkTargetChoice(VpkTargetArmorOption option) { Option = option; }

        public VpkTargetArmorOption Option { get; }

        /// <summary>专属模式下「不可」的项禁用、不能勾；普通模式恒可选。</summary>
        public bool IsEnabled => Option.ExclusiveAvailable;

        public bool IsChecked
        {
            get => _isChecked;
            set => SetField(ref _isChecked, value);
        }

        /// <summary>行里显示的文字：名字（编号）+ 标注 + 状态。</summary>
        public string RowText => Option.DisplayName;

        /// <summary>输入框里标签的文字。</summary>
        public string ChipText => Option.ItemId is { } id
            ? $"{Option.StoredName}（{id.ToString(CultureInfo.InvariantCulture)}）" + (Option.Tag is null ? string.Empty : " · " + Option.Tag)
            : Option.DisplayName;
    }

    // 作用：目标护甲下拉的状态——可输入搜索；普通模式单选，专属模式多选（勾选框 + 标签 + 全选）。
    // Purpose: State for the searchable target picker (single-select normally, element-ui style multi-select in exclusive mode).
    public sealed class VpkTargetPickerViewModel : BaseViewModel
    {
        private IReadOnlyList<VpkTargetChoice> _choices = [];
        private string _searchText = string.Empty;
        private bool _isMultiple;
        private bool _isOpen;
        private VpkTargetChoice? _selected;
        private bool _suppress;

        public VpkTargetPickerViewModel()
        {
            Chips = [];
            View = CollectionViewSource.GetDefaultView(Array.Empty<VpkTargetChoice>());
            ToggleCommand = new RelayCommand(parameter =>
            {
                if (parameter is not VpkTargetChoice choice || !choice.IsEnabled) return;
                if (_isMultiple) choice.IsChecked = !choice.IsChecked;
                else Selected = choice;
                if (!_isMultiple) IsOpen = false;
            });
            RemoveChipCommand = new RelayCommand(parameter =>
            {
                if (parameter is VpkTargetChoice choice) choice.IsChecked = false;
            });
            SelectAllCommand = new RelayCommand(_ => SelectAll());
            ClearCommand = new RelayCommand(_ => { foreach (var choice in _choices) choice.IsChecked = false; });
        }

        /// <summary>选中集合变了（多选勾选、标签去掉、单选换项）。</summary>
        public event EventHandler? SelectionChanged;

        public ObservableCollection<VpkTargetChoice> Chips { get; }
        public ICollectionView View { get; private set; }
        public RelayCommand ToggleCommand { get; }
        public RelayCommand RemoveChipCommand { get; }
        public RelayCommand SelectAllCommand { get; }
        public RelayCommand ClearCommand { get; }

        public bool IsMultiple
        {
            get => _isMultiple;
            private set { if (SetField(ref _isMultiple, value)) OnPropertyChanged(nameof(IsSingle)); }
        }

        public bool IsSingle => !_isMultiple;

        public bool IsOpen
        {
            get => _isOpen;
            set
            {
                if (!SetField(ref _isOpen, value)) return;
                if (!value) SearchText = string.Empty;
                OnPropertyChanged(nameof(ShowSingleText));
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (!SetField(ref _searchText, value ?? string.Empty)) return;
                View.Refresh();
                OnPropertyChanged(nameof(ShowSingleText));
                OnPropertyChanged(nameof(HasNoMatch));
                if (!string.IsNullOrEmpty(_searchText)) IsOpen = true;
            }
        }

        /// <summary>搜不到任何项时给一句话，而不是一块空白。</summary>
        public bool HasNoMatch => View.IsEmpty && _choices.Count != 0;

        /// <summary>单选且没在输入时，输入框里显示当前选中项的名字。</summary>
        public bool ShowSingleText => IsSingle && string.IsNullOrEmpty(_searchText) && _selected is not null;

        public string SingleText => _selected?.RowText ?? string.Empty;

        public bool HasChips => Chips.Count != 0;

        /// <summary>多选时的占位提示：没勾任何项时显示。</summary>
        public bool ShowPlaceholder => IsMultiple ? Chips.Count == 0 && string.IsNullOrEmpty(_searchText) : _selected is null && string.IsNullOrEmpty(_searchText);

        public VpkTargetChoice? Selected
        {
            get => _selected;
            set
            {
                if (!SetField(ref _selected, value)) return;
                OnPropertyChanged(nameof(SingleText));
                OnPropertyChanged(nameof(ShowSingleText));
                OnPropertyChanged(nameof(ShowPlaceholder));
                if (!_suppress) SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public IReadOnlyList<VpkTargetChoice> Choices => _choices;

        /// <summary>已勾选的 item_id（升序），多选模式用。</summary>
        public IReadOnlyList<uint> CheckedItemIds
            => _choices.Where(choice => choice.IsChecked && choice.Option.ItemId is not null)
                .Select(choice => choice.Option.ItemId!.Value).Distinct().Order().ToArray();

        /// <summary>
        /// 换一批候选。<paramref name="multiple"/> 为真走多选（<paramref name="checkedIds"/> 预勾），否则单选（<paramref name="selected"/> 选中）。
        /// </summary>
        public void SetOptions(
            IReadOnlyList<VpkTargetArmorOption> options, bool multiple, IEnumerable<uint>? checkedIds, VpkTargetArmorOption? selected)
        {
            _suppress = true;
            try
            {
                foreach (var old in _choices) old.PropertyChanged -= OnChoiceChanged;
                Chips.Clear();
                var list = options.Select(option => new VpkTargetChoice(option)).ToArray();
                _choices = list;
                IsMultiple = multiple;
                var wanted = checkedIds?.ToHashSet() ?? [];
                foreach (var choice in list)
                {
                    choice.PropertyChanged += OnChoiceChanged;
                    if (multiple && choice.Option.ItemId is { } id && wanted.Contains(id) && choice.IsEnabled)
                    {
                        choice.IsChecked = true;
                        Chips.Add(choice);
                    }
                }

                var view = new ListCollectionView(list) { Filter = Matches };
                View = view;
                _selected = multiple ? null : list.FirstOrDefault(choice => selected is not null && Same(choice.Option, selected));
                _searchText = string.Empty;
                OnPropertyChanged(nameof(Choices));
                OnPropertyChanged(nameof(View));
                OnPropertyChanged(nameof(Selected));
                OnPropertyChanged(nameof(SearchText));
                OnPropertyChanged(nameof(SingleText));
                OnPropertyChanged(nameof(ShowSingleText));
                OnPropertyChanged(nameof(HasChips));
                OnPropertyChanged(nameof(ShowPlaceholder));
                OnPropertyChanged(nameof(HasNoMatch));
            }
            finally
            {
                _suppress = false;
            }
        }

        /// <summary>程序化改选中项（不触发 <see cref="SelectionChanged"/>）。</summary>
        public void SelectSingle(VpkTargetArmorOption? option)
        {
            _suppress = true;
            try { Selected = option is null ? null : _choices.FirstOrDefault(choice => Same(choice.Option, option)); }
            finally { _suppress = false; }
        }

        private static bool Same(VpkTargetArmorOption left, VpkTargetArmorOption right)
            => left.ItemId is not null || right.ItemId is not null
                ? left.ItemId == right.ItemId
                : string.Equals(left.ArchiveId, right.ArchiveId, StringComparison.OrdinalIgnoreCase);

        private bool Matches(object item)
        {
            if (item is not VpkTargetChoice choice) return false;
            if (string.IsNullOrWhiteSpace(_searchText)) return true;
            var option = choice.Option;
            var text = option.StoredName + " " + (option.ItemId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
                + " " + (option.Tag ?? string.Empty) + " " + option.ArchiveId;
            return _searchText.Split([' ', '\t', '　'], StringSplitOptions.RemoveEmptyEntries)
                .All(token => text.Contains(token, StringComparison.CurrentCultureIgnoreCase));
        }

        private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(VpkTargetChoice.IsChecked) || sender is not VpkTargetChoice choice) return;
            if (choice.IsChecked) { if (!Chips.Contains(choice)) Chips.Add(choice); }
            else Chips.Remove(choice);
            OnPropertyChanged(nameof(HasChips));
            OnPropertyChanged(nameof(ShowPlaceholder));
            if (!_suppress) SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>「全选」：勾上所有可用的玩家护甲（同名的玩家版本全勾），跳过禁用项和非玩家记录。</summary>
        private void SelectAll()
        {
            if (!_isMultiple) return;
            var ids = KitExclusiveSelection.SelectAll(_choices
                .Where(choice => choice.Option.ItemId is not null)
                .Select(choice => new KitExclusiveOption(
                    choice.Option.ItemId!.Value, choice.Option.ArchiveId, choice.Option.StoredName,
                    choice.Option.ExclusiveAvailable, string.Empty,
                    NonPlayerLabel: choice.Option.IsNonPlayer ? choice.Option.Tag : null))).ToHashSet();
            foreach (var choice in _choices)
                if (choice.Option.ItemId is { } id && ids.Contains(id)) choice.IsChecked = true;
        }
    }
}
