using System.Windows;
using System.Windows.Controls;
using HD2ModCore.Infrastructure.KitInstance;

namespace HD2ModManager.Views
{
    // 作用：「专属切换」确认前的预览窗口（P4a，规划 §4.1 第 3 条）。
    // 列出每个模型部位落到哪个独有部件、会被隐藏的原版部件、被取代的独有部件外观提示、头盔怎么处理。
    // 点「确认专属切换」才返回 true；窗口本身不写任何文件。
    // Purpose: Confirmation preview for exclusive (per-instance) armor switching. Writes nothing itself.
    public sealed class KitExclusivePreviewWindow : Window
    {
        public KitExclusivePreviewWindow(string modName, KitExclusivePreview preview, string? confirmDisabledReason = null)
        {
            Title = "专属切换";
            Width = 760;
            Height = 560;
            MinWidth = 560;
            MinHeight = 380;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = Application.Current?.MainWindow;

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            grid.Children.Add(new TextBlock
            {
                Text = $"把「{modName}」专属切换到 {preview.Title}。确认前不会写任何文件。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });

            var text = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                AcceptsReturn = true,
                Text = string.Join(System.Environment.NewLine, preview.Lines),
            };
            Grid.SetRow(text, 1);
            grid.Children.Add(text);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var confirm = new Button { Content = "确认专属切换", Padding = new Thickness(16, 6, 16, 6) };
            if (Application.Current?.TryFindResource("AppPrimaryButton") is Style primary) confirm.Style = primary;
            confirm.Click += (_, _) => DialogResult = true;
            if (confirmDisabledReason is not null) confirm.IsEnabled = false;
            var cancel = new Button { Content = "取消", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 6, 16, 6), IsCancel = true };
            if (Application.Current?.TryFindResource("AppSecondaryButton") is Style secondary) cancel.Style = secondary;
            if (confirmDisabledReason is not null)
                buttons.Children.Add(new TextBlock { Text = confirmDisabledReason, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Foreground = System.Windows.Media.Brushes.Firebrick });
            buttons.Children.Add(confirm);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);
            Content = grid;
        }
    }
}
