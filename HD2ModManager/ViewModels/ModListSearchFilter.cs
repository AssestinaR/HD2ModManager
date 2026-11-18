using System;

namespace HD2ModManager.ViewModels
{
    // 作用：宿主页面贡献给搜索态标签条的一个"可切换筛选"。
    //
    // 为什么让宿主提供而不是在面板里写死：过时、含选项、Unit 命中这些条件属于页面自己的领域，
    // 面板不该知道它们的含义。面板只负责"读 IsSelected 决定要不要高亮、点一下调 setSelected"，
    // 状态仍然只有宿主那一份（不复制，避免标签与实际筛选漂移）。
    //
    // 语义约定：**选中 = 这个条件生效**。面板不提供反向文案（旧版「显示过时 ↔ 显示全部」
    // 那种），取消选中就回到默认视图。
    public sealed class ModListSearchFilter : BaseViewModel
    {
        private readonly Func<bool> _isSelected;
        private readonly Action<bool> _setSelected;
        private readonly Func<string?>? _toolTip;

        public ModListSearchFilter(string key, string label, Func<bool> isSelected, Action<bool> setSelected, string? toolTip = null)
            : this(key, label, isSelected, setSelected, toolTip is null ? null : () => toolTip)
        {
        }

        // 带数量的提示（"护甲 · 12 项"）要跟着筛选结果变，所以允许传一个取值函数。
        public ModListSearchFilter(string key, string label, Func<bool> isSelected, Action<bool> setSelected, Func<string?>? toolTip)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            Label = label ?? throw new ArgumentNullException(nameof(label));
            _isSelected = isSelected ?? throw new ArgumentNullException(nameof(isSelected));
            _setSelected = setSelected ?? throw new ArgumentNullException(nameof(setSelected));
            _toolTip = toolTip;
            ToolTip = toolTip?.Invoke();
            ToggleCommand = new RelayCommand(_ => Toggle());
        }

        public string Key { get; }

        public string Label { get; }

        public string? ToolTip { get; private set; }

        public bool IsSelected => _isSelected();

        public RelayCommand ToggleCommand { get; }

        public void Toggle() => _setSelected(!_isSelected());

        /// <summary>清空（收起搜索框时用）。本来就没选中就不动，避免无谓刷新。</summary>
        public void Clear()
        {
            if (_isSelected()) _setSelected(false);
        }

        /// <summary>宿主改完状态后通知界面重画选中态（面板在每次切换后调用）。</summary>
        public void RefreshSelection()
        {
            ToolTip = _toolTip?.Invoke();
            OnPropertyChanged(nameof(IsSelected));
        }
    }
}
