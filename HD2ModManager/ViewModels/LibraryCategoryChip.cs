using System.Text;

namespace HD2ModManager.ViewModels
{
    public enum CategoryChipKind { All, BuiltIn, Pending, Custom, Add }

    // 作用：列表顶部分类条上的一个芯片。Key 是分类键（见 CategoryKey），All 的 Key 为空串。
    // 同一个芯片对象在多次 Rebuild 之间复用，只更新数字/选中态。
    public sealed class LibraryCategoryChip : BaseViewModel
    {
        private int _count;
        private bool _isSelected;
        private string _label;

        public LibraryCategoryChip(string key, string label, CategoryChipKind kind, int count, bool isSelected)
        {
            Key = key;
            Kind = kind;
            _label = label;
            _count = count;
            _isSelected = isSelected;
        }

        public string Key { get; }
        public CategoryChipKind Kind { get; }
        public bool IsCustom => Kind == CategoryChipKind.Custom;
        public bool IsAdd => Kind == CategoryChipKind.Add;

        public int Count { get => _count; set { if (SetField(ref _count, value)) OnPropertyChanged(nameof(Text)); } }
        public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }
        public string Label { get => _label; set { if (SetField(ref _label, value)) OnPropertyChanged(nameof(Text)); } }
        public string Text => Kind == CategoryChipKind.Add ? "设置分类" : new StringBuilder(Label).Append(' ').Append(Count).ToString();
    }
}
