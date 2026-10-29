using System.Windows.Input;
using HD2ModManager.Controls.Actions;

namespace HD2ModManager.ViewModels
{
    // 作用：描述页面可注册到浮动操作区的标准动作项。
    // 只声明"是什么动作、什么时候可见、点了做什么"——图标、颜色、尺寸、位置全部由控件层决定。
    public sealed class PageActionViewModel : BaseViewModel
    {
        private bool _isVisible = true;
        private bool _isEnabled = true;
        private string _label;

        public PageActionViewModel(
            PageActionKind kind,
            string label,
            ICommand command,
            object? commandParameter = null,
            PageActionTone tone = PageActionTone.Neutral,
            object? expandedContent = null,
            double expandedWidth = 168d,
            int order = 0)
        {
            Kind = kind;
            _label = label;
            Command = command;
            CommandParameter = commandParameter;
            Tone = tone;
            ExpandedContent = expandedContent;
            ExpandedWidth = expandedWidth;
            Order = order;
        }

        public PageActionKind Kind { get; }
        // 可变：同一个动作在不同状态下文案不同（"临时将头盔合并到盔甲" / "取消将头盔合并到盔甲"）。
        public string Label
        {
            get => _label;
            set => SetField(ref _label, value);
        }
        public ICommand Command { get; }
        public object? CommandParameter { get; }
        public PageActionTone Tone { get; }
        // 需要内嵌控件（切换配置的下拉、重命名的输入框）时给这里。
        public object? ExpandedContent { get; }
        // 内嵌控件需要的宽度；这是唯一留在逻辑层的尺寸，因为它由内容决定。
        public double ExpandedWidth { get; }
        public int Order { get; }

        // 动作在某个页面状态下是否成立（例如详情页的 Patch 工具只对 Patch Mod 显示）。
        // 控件层按它过滤，不需要宿主增删集合。
        public bool IsVisible
        {
            get => _isVisible;
            set => SetField(ref _isVisible, value);
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetField(ref _isEnabled, value);
        }
    }
}
