using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace HD2ModManager.Views
{
    /// <summary>
    /// 分类弹窗的开合状态（行内"设置分类"、选择栏"设置分类"、芯片条"设置分类"共用一套）。
    ///
    /// 为什么需要它：这三个入口原来是"每次点击都新建一个 Popup 再打开"，于是再点同一个按钮
    /// 不会收起——旧的那个被 StaysOpen=false 关掉，新的又立刻打开，看着像点了没反应。
    /// 这里只保留"当前打开的那一个"：同一锚点再点就是收起，换锚点才换位置。
    ///
    /// 外部点击由我们自己接：Popup.StaysOpen 保持 true，改为在宿主窗口上监听 PreviewMouseDown。
    /// 这样"点锚点"和"点别处"能分开处理——点锚点要顺带把紧随其后的 Click 抑制掉，
    /// 否则用户的手感是"关掉又打开"。
    /// </summary>
    internal static class CategoryPopup
    {
        // 上一个弹窗就是因为"点了这个锚点"才关掉的：它后面那次 Click 不该再打开。
        private static readonly TimeSpan SuppressWindow = TimeSpan.FromMilliseconds(500);

        private static Popup? _open;
        private static FrameworkElement? _anchor;
        private static Window? _owner;
        private static FrameworkElement? _closedByAnchorClick;
        private static long _closedAt;

        internal static bool IsOpenFor(FrameworkElement anchor)
            => _open is { IsOpen: true } && ReferenceEquals(_anchor, anchor);

        /// <summary>打开/收起：同一个锚点第二次调用就是收起。</summary>
        internal static void Toggle(
            FrameworkElement anchor,
            PlacementMode placement,
            double horizontalOffset,
            double verticalOffset,
            Func<FrameworkElement> content)
        {
            ArgumentNullException.ThrowIfNull(anchor);
            ArgumentNullException.ThrowIfNull(content);

            if (IsOpenFor(anchor))
            {
                Close();
                return;
            }

            // 这次 Click 是"刚才点锚点关掉弹窗"的余波，不该再打开。
            if (ReferenceEquals(_closedByAnchorClick, anchor)
                && Environment.TickCount64 - _closedAt <= (long)SuppressWindow.TotalMilliseconds)
            {
                _closedByAnchorClick = null;
                return;
            }

            Close();

            var popup = new Popup
            {
                PlacementTarget = anchor,
                Placement = placement,
                HorizontalOffset = horizontalOffset,
                VerticalOffset = verticalOffset,
                // 自己处理外部点击，见类注释。
                StaysOpen = true,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
            };
            popup.Child = content();
            popup.Closed += (_, _) =>
            {
                if (ReferenceEquals(_open, popup)) Detach();
            };

            _open = popup;
            _anchor = anchor;
            _owner = Window.GetWindow(anchor);
            if (_owner is not null)
            {
                _owner.PreviewMouseDown += OnOwnerPreviewMouseDown;
                // 切到别的窗口（Alt+Tab、点任务栏）也算失焦。StaysOpen 只覆盖应用内的点击。
                _owner.Deactivated += OnOwnerDeactivated;
            }

            popup.IsOpen = true;
        }

        internal static void Close()
        {
            var popup = _open;
            Detach();
            if (popup is not null) popup.IsOpen = false;
        }

        private static void Detach()
        {
            if (_owner is not null)
            {
                _owner.PreviewMouseDown -= OnOwnerPreviewMouseDown;
                _owner.Deactivated -= OnOwnerDeactivated;
                _owner = null;
            }

            _open = null;
            _anchor = null;
        }

        private static void OnOwnerPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_open is null) return;

            // 点回锚点：关掉，并记下"这次关闭是锚点造成的"，抑制紧随其后的 Click。
            if (_anchor is not null && IsWithin(e.OriginalSource as DependencyObject, _anchor))
            {
                _closedByAnchorClick = _anchor;
                _closedAt = Environment.TickCount64;
            }

            Close();
        }

        private static void OnOwnerDeactivated(object? sender, EventArgs e) => Close();

        private static bool IsWithin(DependencyObject? node, DependencyObject target)
        {
            for (var current = node; current is not null; current = VisualTreeWalk.Parent(current))
            {
                if (ReferenceEquals(current, target)) return true;
            }

            return false;
        }
    }
}
