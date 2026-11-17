using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HD2ModCore.Infrastructure.Sectional;

namespace HD2ModManager.Views
{
    // Purpose: Interactive resolution dialog when a merged sectional Unit exceeds Helldivers 2's 128 MiB single-buffer D3D12 block ceiling.
    public sealed class MergeOverflowDialogWindow : Window
    {
        private readonly SectionalMergeOverflowContext _context;
        private readonly List<(SectionalMergeOverflowGroupItem Group, CheckBox Box)> _rows = [];
        private readonly TextBlock _projectionText;
        private readonly Button _disableAndMergeButton;

        public SectionalMergeOverflowDecision Decision { get; private set; }
            = new(SectionalMergeOverflowAction.Cancel, []);

        public MergeOverflowDialogWindow(SectionalMergeOverflowContext context, Window? owner = null)
        {
            _context = context;
            Title = "合并模型超出游戏显存上限（128 MiB）";
            SizeToContent = SizeToContent.WidthAndHeight;
            MinWidth = 480;
            MaxWidth = 600;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = FindBrush("SurfaceCard", Brushes.White);

            var parent = owner ?? Application.Current?.MainWindow;
            if (parent is { IsLoaded: true } && !ReferenceEquals(parent, this))
                Owner = parent;
            else
                WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var root = new Grid { Margin = new Thickness(20, 18, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Row 0: Header with warning icon and explanation
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (Application.Current?.TryFindResource("DialogIcon.Warning") is Geometry warningGeometry)
            {
                header.Children.Add(new Path
                {
                    Data = warningGeometry,
                    Stroke = FindBrush("StatusWarning", Brushes.DarkOrange),
                    StrokeThickness = 1.4,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Width = 28,
                    Height = 28,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 14, 0),
                });
            }

            var headerStack = new StackPanel();
            headerStack.Children.Add(new TextBlock
            {
                Text = $"「{context.ModDisplayName}」合并后的单个网格流超出 128 MiB",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = FindBrush("TextPrimary", Brushes.Black),
                TextWrapping = TextWrapping.Wrap,
            });
            var subtitleText = context.Groups.Count != 0
                ? $"合并槽「{context.MergedSlotDisplayName}」达到 {SectionalMergeOverflowAnalyzer.FormatMiB(context.ActualBufferBytes)}，超过游戏 D3D12 单块上限 {SectionalMergeOverflowAnalyzer.FormatMiB(context.LimitBytes)}，直接进游戏会导致闪退。\n已自动预选取消多余互斥/重复组件（保留勾选 = 启用，取消勾选 = 关闭并写回配置）："
                : $"合并槽「{context.MergedSlotDisplayName}」达到 {SectionalMergeOverflowAnalyzer.FormatMiB(context.ActualBufferBytes)}，超过游戏 D3D12 单块上限 {SectionalMergeOverflowAnalyzer.FormatMiB(context.LimitBytes)}，直接进游戏会导致闪退。\n该模组没有可单独开关的组件组，请退回非合并模式编译。";
            headerStack.Children.Add(new TextBlock
            {
                Text = subtitleText,
                FontSize = 13,
                LineHeight = 20,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = FindBrush("TextSecondary", Brushes.DimGray),
                TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetColumn(headerStack, 1);
            header.Children.Add(headerStack);
            root.Children.Add(header);

            // Row 1: Component checklist box
            if (context.Groups.Count != 0)
            {
                var listStack = new StackPanel();
                Grid.SetIsSharedSizeScope(listStack, true);
                foreach (var group in context.Groups)
                {
                    var row = BuildGroupRow(group);
                    listStack.Children.Add(row);
                }

                var listContainer = new Border
                {
                    Margin = new Thickness(0, 14, 0, 0),
                    Padding = new Thickness(10, 6, 10, 6),
                    Background = FindBrush("SurfaceMuted", Brushes.WhiteSmoke),
                    BorderBrush = FindBrush("StrokeSubtle", Brushes.LightGray),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Child = new ScrollViewer
                    {
                        Content = listStack,
                        MaxHeight = 240,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    },
                };
                Grid.SetRow(listContainer, 1);
                root.Children.Add(listContainer);
            }

            // Row 2: Live projected buffer size banner
            _projectionText = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetRow(_projectionText, 2);
            root.Children.Add(_projectionText);

            // Row 3: Optional note when fallback to unmerged is unavailable (exclusive armor mode)
            if (!context.CanFallbackToUnmerged)
            {
                var exclusiveNote = new TextBlock
                {
                    Text = "提示：当前为目标护甲专属合并模式，无独立多槽素体可退；请关闭多余组件，或取消后改用「自定义素体」。",
                    FontSize = 12,
                    Margin = new Thickness(0, 6, 0, 0),
                    Foreground = FindBrush("TextSecondary", Brushes.DimGray),
                    TextWrapping = TextWrapping.Wrap,
                };
                Grid.SetRow(exclusiveNote, 3);
                root.Children.Add(exclusiveNote);
            }

            // Row 4: Action buttons
            var buttonBar = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };

            _disableAndMergeButton = CreateButton("一键关闭未勾选项并继续合并", primary: true, cancel: false);
            _disableAndMergeButton.Click += (_, _) =>
            {
                var disabledIds = _rows
                    .Where(item => item.Box.IsChecked != true)
                    .Select(item => item.Group.GroupId)
                    .ToArray();
                Decision = new SectionalMergeOverflowDecision(SectionalMergeOverflowAction.DisableSelectedGroups, disabledIds);
                DialogResult = true;
            };
            buttonBar.Children.Add(_disableAndMergeButton);

            var fallbackButton = CreateButton("保留全部，退回非合并模式", primary: context.Groups.Count == 0 && context.CanFallbackToUnmerged, cancel: false);
            fallbackButton.IsEnabled = context.CanFallbackToUnmerged;
            fallbackButton.Click += (_, _) =>
            {
                Decision = new SectionalMergeOverflowDecision(SectionalMergeOverflowAction.FallbackToUnmerged, []);
                DialogResult = true;
            };
            buttonBar.Children.Add(fallbackButton);

            var cancelButton = CreateButton("取消", primary: false, cancel: true);
            cancelButton.Click += (_, _) =>
            {
                Decision = new SectionalMergeOverflowDecision(SectionalMergeOverflowAction.Cancel, []);
                DialogResult = false;
            };
            buttonBar.Children.Add(cancelButton);

            Grid.SetRow(buttonBar, 4);
            root.Children.Add(buttonBar);

            Content = root;
            KeyDown += (_, args) =>
            {
                if (args.Key == Key.Escape)
                {
                    Decision = new SectionalMergeOverflowDecision(SectionalMergeOverflowAction.Cancel, []);
                    DialogResult = false;
                }
            };

            RefreshProjection();
        }

        public static ValueTask<SectionalMergeOverflowDecision> PromptAsync(SectionalMergeOverflowContext context)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                var fallback = context.CanFallbackToUnmerged
                    ? SectionalMergeOverflowAction.FallbackToUnmerged
                    : SectionalMergeOverflowAction.Cancel;
                return ValueTask.FromResult(new SectionalMergeOverflowDecision(fallback, []));
            }

            if (dispatcher.CheckAccess())
                return ValueTask.FromResult(ShowCore(context));

            return new ValueTask<SectionalMergeOverflowDecision>(dispatcher.InvokeAsync(() => ShowCore(context)).Task);
        }

        private static SectionalMergeOverflowDecision ShowCore(SectionalMergeOverflowContext context)
        {
            var window = new MergeOverflowDialogWindow(context);
            _ = window.ShowDialog();
            return window.Decision;
        }

        private UIElement BuildGroupRow(SectionalMergeOverflowGroupItem group)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "SizeCol" });

            var box = new CheckBox
            {
                IsChecked = group.RecommendedKeepEnabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            box.Checked += (_, _) => RefreshProjection();
            box.Unchecked += (_, _) => RefreshProjection();
            _rows.Add((group, box));
            row.Children.Add(box);

            var label = new TextBlock
            {
                Text = group.DisplayName,
                ToolTip = group.GroupName,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = FindBrush("TextPrimary", Brushes.Black),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Cursor = Cursors.Hand,
            };
            label.MouseLeftButtonUp += (_, _) => box.IsChecked = box.IsChecked != true;
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            if (!string.IsNullOrWhiteSpace(group.ConflictLabel))
            {
                var isExplicit = group.ConflictKind == SectionalOverflowConflictKind.ExplicitExclusive;
                var badgeBrush = FindBrush(isExplicit ? "StatusDanger" : "StatusWarning", Brushes.DarkOrange);
                var badge = new Border
                {
                    Margin = new Thickness(8, 0, 0, 0),
                    Padding = new Thickness(6, 1, 6, 1),
                    BorderBrush = badgeBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = group.ConflictLabel,
                        FontSize = 11.5,
                        Foreground = badgeBrush,
                    },
                };
                Grid.SetColumn(badge, 2);
                row.Children.Add(badge);
            }

            var sizeLabel = new TextBlock
            {
                Text = $"~{SectionalMergeOverflowAnalyzer.FormatMiB(group.EstimatedBytes)}",
                FontSize = 12.5,
                Margin = new Thickness(10, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = FindBrush("TextSecondary", Brushes.DimGray),
            };
            Grid.SetColumn(sizeLabel, 3);
            row.Children.Add(sizeLabel);

            return row;
        }

        private void RefreshProjection()
        {
            if (_context.Groups.Count == 0)
            {
                _projectionText.Text = $"预计合并单流：{SectionalMergeOverflowAnalyzer.FormatMiB(_context.ActualBufferBytes)} / {SectionalMergeOverflowAnalyzer.FormatMiB(_context.LimitBytes)}（超出上限）";
                _projectionText.Foreground = FindBrush("StatusDanger", Brushes.Red);
                _disableAndMergeButton.IsEnabled = false;
                return;
            }

            var keptIds = _rows
                .Where(item => item.Box.IsChecked == true)
                .Select(item => item.Group.GroupId)
                .ToHashSet(System.StringComparer.Ordinal);
            var disabledCount = _rows.Count - keptIds.Count;
            var projected = SectionalMergeOverflowAnalyzer.ProjectRemainingBytes(_context, keptIds);
            var safe = projected < _context.LimitBytes;

            if (disabledCount == 0)
            {
                _projectionText.Text = $"当前未关闭任何组件 · 预计合并单流：{SectionalMergeOverflowAnalyzer.FormatMiB(projected)} / {SectionalMergeOverflowAnalyzer.FormatMiB(_context.LimitBytes)}（超限，无法继续合并）";
                _projectionText.Foreground = FindBrush("StatusDanger", Brushes.Red);
            }
            else if (!safe)
            {
                _projectionText.Text = $"已选关闭 {disabledCount} 个组件 · 预计合并单流：{SectionalMergeOverflowAnalyzer.FormatMiB(projected)} / {SectionalMergeOverflowAnalyzer.FormatMiB(_context.LimitBytes)}（仍超限，请再取消勾选或退回非合并模式）";
                _projectionText.Foreground = FindBrush("StatusDanger", Brushes.Red);
            }
            else
            {
                _projectionText.Text = $"已选关闭 {disabledCount} 个组件 · 预计合并单流：{SectionalMergeOverflowAnalyzer.FormatMiB(projected)} / {SectionalMergeOverflowAnalyzer.FormatMiB(_context.LimitBytes)}（安全）";
                _projectionText.Foreground = FindBrush("StatusSuccess", Brushes.Green);
            }

            _disableAndMergeButton.IsEnabled = disabledCount > 0 && safe;
        }

        private static Button CreateButton(string label, bool primary, bool cancel)
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 88,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(8, 4, 0, 0),
                IsDefault = primary,
                IsCancel = cancel,
            };
            if (Application.Current?.TryFindResource(primary ? "AppPrimaryButton" : "AppSecondaryButton") is Style style)
                button.Style = style;
            return button;
        }

        private static Brush FindBrush(string key, Brush fallback)
            => Application.Current?.TryFindResource(key) as Brush ?? fallback;
    }
}
