namespace HD2ModManager.ViewModels
{
    public enum CategoryChipKind { BuiltIn, Pending, Custom }

    // 作用：一个可筛选的分类（键、名字、数量、选中态）。
    // 同一个对象在多次 Rebuild 之间复用，只更新数字/选中态/名字——搜索框下面那排按钮
    // 会按这个集合原地对齐，复用对象才能保住按钮上的焦点。
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

        public int Count { get => _count; set => SetField(ref _count, value); }
        public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }
        public string Label { get => _label; set => SetField(ref _label, value); }
    }
}
