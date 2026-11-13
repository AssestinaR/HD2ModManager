using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HD2ModCore.Infrastructure.KitInstance;

namespace HD2ModManager.Views
{
    // 作用：「转专属」的选目标套 + 预览窗口（规划 §6.1 第 1、2 步）。
    // 左边列出候选套（不能转的灰着，并带原因），右边是选中那一套的预览：搬什么、藏什么、改什么、哪些做不到隔离。
    // 点「转成专属」才会关窗并返回选中的目标；窗口本身不写任何文件。
    // Purpose: Candidate-kit picker and preview for converting a mod to a kit-exclusive one. Writes nothing itself.
    public sealed class KitInstanceConversionWindow : Window
    {
        private readonly ListBox _list;
        private readonly TextBox _preview;
        private readonly Button _confirm;
        private readonly IReadOnlyList<KitConversionCandidate> _candidates;

        public KitConversionCandidate? Chosen { get; private set; }

        public KitInstanceConversionWindow(string modName, IReadOnlyList<KitConversionCandidate> candidates)
        {
            _candidates = candidates;
            Title = "转专属";
            Width = 760;
            Height = 520;
            MinWidth = 560;
            MinHeight = 360;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = Application.Current?.MainWindow;

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var header = new TextBlock
            {
                Text = $"把「{modName}」改成只在某一套护甲身上生效。选一个目标套，右边会预览具体改动；确认前不会写任何文件。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            };
            Grid.SetColumnSpan(header, 2);
            grid.Children.Add(header);

            _list = new ListBox { Margin = new Thickness(0, 0, 10, 0) };
            foreach (var candidate in candidates)
            {
                _list.Items.Add(new ListBoxItem
                {
                    Content = candidate.CanConvert ? candidate.Label : $"{candidate.Label}（不能转）",
                    IsEnabled = true,
                    Opacity = candidate.CanConvert ? 1.0 : 0.55,
                    ToolTip = candidate.CanConvert ? null : candidate.Plan.FailureReason,
                    Tag = candidate,
                });
            }
            Grid.SetRow(_list, 1);
            grid.Children.Add(_list);

            _preview = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                AcceptsReturn = true,
            };
            Grid.SetRow(_preview, 1);
            Grid.SetColumn(_preview, 1);
            grid.Children.Add(_preview);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            _confirm = new Button { Content = "转成专属", Padding = new Thickness(16, 6, 16, 6), IsEnabled = false };
            if (Application.Current?.TryFindResource("AppPrimaryButton") is Style primary) _confirm.Style = primary;
            _confirm.Click += (_, _) =>
            {
                Chosen = SelectedCandidate();
                if (Chosen is { CanConvert: true }) DialogResult = true;
            };
            var cancel = new Button { Content = "取消", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 6, 16, 6), IsCancel = true };
            if (Application.Current?.TryFindResource("AppSecondaryButton") is Style secondary) cancel.Style = secondary;
            buttons.Children.Add(_confirm);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 2);
            Grid.SetColumnSpan(buttons, 2);
            grid.Children.Add(buttons);
            Content = grid;

            _list.SelectionChanged += (_, _) => ShowPreview();
            var first = candidates.ToList().FindIndex(candidate => candidate.CanConvert);
            _list.SelectedIndex = first >= 0 ? first : (candidates.Count > 0 ? 0 : -1);
        }

        private KitConversionCandidate? SelectedCandidate()
            => (_list.SelectedItem as ListBoxItem)?.Tag as KitConversionCandidate;

        private void ShowPreview()
        {
            var candidate = SelectedCandidate();
            _confirm.IsEnabled = candidate is { CanConvert: true };
            _preview.Text = candidate is null
                ? string.Empty
                : string.Join(System.Environment.NewLine + System.Environment.NewLine, KitConversionPlanner.DescribePreview(candidate.Plan))
                    + (candidate.CanConvert
                        ? System.Environment.NewLine + System.Environment.NewLine + "确认后会在这个 Mod 的 Overwrite 目录里生成改过的 patch（原文件不动），并记下一条隐藏规则；下次部署后生效，可随时撤销。"
                        : string.Empty);
        }
    }
}
