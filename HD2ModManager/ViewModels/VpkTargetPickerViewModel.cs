using System.ComponentModel;
using System.Windows.Data;

namespace HD2ModManager.ViewModels
{
    // 作用：下拉里的一项。
    // Purpose: One row of the target picker.
    public sealed class VpkTargetChoice
    {
        public VpkTargetChoice(VpkTargetArmorOption option) { Option = option; }

        public VpkTargetArmorOption Option { get; }

        /// <summary>行里显示的文字。</summary>
        public string RowText => Option.DisplayName;
    }

    // 作用：目标护甲下拉的状态——可输入搜索的单选。
    // 专属多选（勾选框 / 标签 / 全选）已撤：防污染只在护甲映射页做（规划 kit-instance-mapping-deploy-plan §15.3）。
    // Purpose: State for the searchable single-select target picker.
    public sealed class VpkTargetPickerViewModel : BaseViewModel
    {
        private IReadOnlyList<VpkTargetChoice> _choices = [];
        private string _searchText = string.Empty;
        private bool _isOpen;
        private VpkTargetChoice? _selected;
        private bool _suppress;

        public VpkTargetPickerViewModel()
        {
            View = CollectionViewSource.GetDefaultView(Array.Empty<VpkTargetChoice>());
            ToggleCommand = new RelayCommand(parameter =>
            {
                if (parameter is not VpkTargetChoice choice) return;
                Selected = choice;
                IsOpen = false;
            });
        }

        /// <summary>选中项变了（用户在下拉里换项；程序化改选不触发）。</summary>
        public event EventHandler? SelectionChanged;

        public ICollectionView View { get; private set; }
        public RelayCommand ToggleCommand { get; }

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
                OnPropertyChanged(nameof(ShowPlaceholder));
                OnPropertyChanged(nameof(HasNoMatch));
                if (!string.IsNullOrEmpty(_searchText)) IsOpen = true;
            }
        }

        /// <summary>搜不到任何项时给一句话，而不是一块空白。</summary>
        public bool HasNoMatch => View.IsEmpty && _choices.Count != 0;

        /// <summary>没在输入时，输入框里显示当前选中项的名字。</summary>
        public bool ShowSingleText => string.IsNullOrEmpty(_searchText) && _selected is not null;

        public string SingleText => _selected?.RowText ?? string.Empty;

        /// <summary>占位提示：没选任何项、也没在输入时显示。</summary>
        public bool ShowPlaceholder => _selected is null && string.IsNullOrEmpty(_searchText);

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

        /// <summary>换一批候选，<paramref name="selected"/> 选中（不触发 <see cref="SelectionChanged"/>）。</summary>
        public void SetOptions(IReadOnlyList<VpkTargetArmorOption> options, VpkTargetArmorOption? selected)
        {
            _suppress = true;
            try
            {
                var list = options.Select(option => new VpkTargetChoice(option)).ToArray();
                _choices = list;
                var view = new ListCollectionView(list) { Filter = Matches };
                View = view;
                _selected = list.FirstOrDefault(choice => selected is not null && Same(choice.Option, selected));
                _searchText = string.Empty;
                OnPropertyChanged(nameof(Choices));
                OnPropertyChanged(nameof(View));
                OnPropertyChanged(nameof(Selected));
                OnPropertyChanged(nameof(SearchText));
                OnPropertyChanged(nameof(SingleText));
                OnPropertyChanged(nameof(ShowSingleText));
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
            => string.Equals(left.ArchiveId, right.ArchiveId, StringComparison.OrdinalIgnoreCase);

        private bool Matches(object item)
        {
            if (item is not VpkTargetChoice choice) return false;
            if (string.IsNullOrWhiteSpace(_searchText)) return true;
            var option = choice.Option;
            var text = option.DisplayName + " " + option.ArchiveId;
            return _searchText.Split([' ', '\t', '　'], StringSplitOptions.RemoveEmptyEntries)
                .All(token => text.Contains(token, StringComparison.CurrentCultureIgnoreCase));
        }
    }
}
