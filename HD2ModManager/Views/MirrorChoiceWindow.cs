using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using HD2ModCore.Updates;

namespace HD2ModManager.Views
{
    // 作用：用户主动检查/下载更新、因连不上 GitHub 失败时，问一下要不要换公共镜像重试。
    // 自动检查从不弹这个窗口。勾选「以后直连失败自动换镜像」会把下载来源改成自动。
    // Purpose: After a user-started check or download failed because GitHub could not be reached, asks which public
    // mirror to retry through. Automatic checks never open it.
    public sealed class MirrorChoiceWindow : Window
    {
        private readonly CheckBox _remember;

        public Mirror? Chosen { get; private set; }

        public bool Remember => _remember.IsChecked == true;

        public MirrorChoiceWindow(string reason, IReadOnlyList<Mirror> mirrors)
        {
            Title = "换镜像重试";
            SizeToContent = SizeToContent.WidthAndHeight;
            MaxWidth = 460;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = Application.Current?.MainWindow;

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock
            {
                Text = $"检查或下载更新失败：{reason}。\n可以改走公共 GitHub 镜像重试（下载内容仍会用 SHA-256 校验，镜像无法替换更新包）。",
                TextWrapping = TextWrapping.Wrap,
            });
            foreach (var mirror in mirrors)
            {
                var button = new Button { Content = $"通过 {mirror.Host} 重试", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
                if (Application.Current?.TryFindResource("AppPrimaryButton") is Style primary) button.Style = primary;
                var chosen = mirror;
                button.Click += (_, _) => { Chosen = chosen; DialogResult = true; };
                panel.Children.Add(button);
            }
            _remember = new CheckBox { Content = "以后直连失败时自动换镜像", Margin = new Thickness(0, 14, 0, 0) };
            panel.Children.Add(_remember);
            var cancel = new Button { Content = "不换了", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 6, 12, 6), IsCancel = true };
            if (Application.Current?.TryFindResource("AppSecondaryButton") is Style secondary) cancel.Style = secondary;
            panel.Children.Add(cancel);
            Content = panel;
        }
    }
}
