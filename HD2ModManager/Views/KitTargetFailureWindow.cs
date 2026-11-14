using System.Windows;
using System.Windows.Controls;
using HD2ModCore.Infrastructure.Sectional;

namespace HD2ModManager.Views
{
    /// <summary>专属多选编译失败时用户的选择。</summary>
    public enum KitTargetFailureChoice
    {
        /// <summary>回滚：什么都不写。</summary>
        Rollback = 0,

        /// <summary>继续，跳过失败的目标。</summary>
        ContinueSkip = 1,

        /// <summary>继续并记住：这个 Mod 以后再失败就自动跳过、不再问。</summary>
        ContinueAndRemember = 2,
    }

    // 作用：专属多选里有目标编译失败时弹出，让用户选「回滚 / 继续（跳过失败的）/ 继续并记住」。
    // Purpose: Three-way choice dialog when some exclusive targets fail to compile.
    public sealed class KitTargetFailureWindow : Window
    {
        public KitTargetFailureWindow(IReadOnlyList<SectionalTargetFailure> failures, int succeeded)
        {
            Title = "有目标编译失败";
            Width = 640;
            Height = 460;
            MinWidth = 480;
            MinHeight = 320;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = Application.Current?.MainWindow;

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(new TextBlock
            {
                Text = $"有 {failures.Count} 个目标没能编译，另外 {succeeded} 个目标没问题。尚未写任何文件，请选择：",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });

            var text = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                AcceptsReturn = true,
                Text = string.Join(System.Environment.NewLine + System.Environment.NewLine,
                    failures.Select(item => $"· {item.Title}{System.Environment.NewLine}  原因：{item.Reason}")),
            };
            Grid.SetRow(text, 1);
            grid.Children.Add(text);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            Button Make(string label, KitTargetFailureChoice result, bool primary)
            {
                var button = new Button { Content = label, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6) };
                if (Application.Current?.TryFindResource(primary ? "AppPrimaryButton" : "AppSecondaryButton") is Style style) button.Style = style;
                button.Click += (_, _) => { Choice = result; DialogResult = true; };
                return button;
            }

            buttons.Children.Add(Make("回滚（什么都不写）", KitTargetFailureChoice.Rollback, false));
            buttons.Children.Add(Make("继续，跳过失败的", KitTargetFailureChoice.ContinueSkip, true));
            buttons.Children.Add(Make("继续并记住（以后自动跳过）", KitTargetFailureChoice.ContinueAndRemember, false));
            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);
            Content = grid;
        }

        /// <summary>用户的选择；直接关窗口按回滚算。</summary>
        public KitTargetFailureChoice Choice { get; private set; } = KitTargetFailureChoice.Rollback;
    }
}
