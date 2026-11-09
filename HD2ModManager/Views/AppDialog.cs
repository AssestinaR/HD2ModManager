using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HD2ModManager.Views
{
    // Purpose: App-themed replacement for System.Windows.MessageBox with the same call shape.
    // Uses shared vector icons and button styles from Themes/Controls.xaml; safe to call from any thread.
    public static class AppDialog
    {
        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
            => Show(null, messageBoxText, caption, button, icon);

        public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption, MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return MessageBox.Show(messageBoxText, caption, button, icon);
            if (!dispatcher.CheckAccess())
                return dispatcher.Invoke(() => ShowCore(owner, messageBoxText, caption, button, icon));
            return ShowCore(owner, messageBoxText, caption, button, icon);
        }

        private static MessageBoxResult ShowCore(Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage image)
        {
            var result = DefaultCancelResult(buttons);
            var window = new Window
            {
                Title = caption,
                SizeToContent = SizeToContent.WidthAndHeight,
                MinWidth = 360,
                MaxWidth = 520,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brush("SurfaceCard", Brushes.White),
            };
            var parent = owner ?? Application.Current?.MainWindow;
            if (parent is { IsLoaded: true } && !ReferenceEquals(parent, window)) window.Owner = parent;
            else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var root = new Grid { Margin = new Thickness(20, 18, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var iconKey = IconKey(image);
            if (iconKey is not null)
            {
                body.Children.Add(new Path
                {
                    Data = Application.Current?.TryFindResource(iconKey) as Geometry,
                    Stroke = Brush(IconBrushKey(image), Brushes.Gray),
                    StrokeThickness = 1.3,
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
            var message = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextPrimary", Brushes.Black),
                FontSize = 14,
                LineHeight = 21,
            };
            var scroller = new ScrollViewer
            {
                Content = message,
                MaxHeight = 380,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            Grid.SetColumn(scroller, 1);
            body.Children.Add(scroller);
            root.Children.Add(body);

            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            Grid.SetRow(bar, 1);
            void AddButton(string label, MessageBoxResult value, bool primary, bool cancel)
            {
                var item = new Button { Content = label, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0), IsDefault = primary, IsCancel = cancel };
                if (Application.Current?.TryFindResource(primary ? "AppPrimaryButton" : "AppSecondaryButton") is Style style) item.Style = style;
                item.Click += (_, _) => { result = value; window.Close(); };
                bar.Children.Add(item);
            }
            switch (buttons)
            {
                case MessageBoxButton.OK:
                    AddButton("确定", MessageBoxResult.OK, true, true);
                    break;
                case MessageBoxButton.OKCancel:
                    AddButton("确定", MessageBoxResult.OK, true, false);
                    AddButton("取消", MessageBoxResult.Cancel, false, true);
                    break;
                case MessageBoxButton.YesNo:
                    AddButton("是", MessageBoxResult.Yes, true, false);
                    AddButton("否", MessageBoxResult.No, false, true);
                    break;
                default:
                    AddButton("是", MessageBoxResult.Yes, true, false);
                    AddButton("否", MessageBoxResult.No, false, false);
                    AddButton("取消", MessageBoxResult.Cancel, false, true);
                    break;
            }
            root.Children.Add(bar);
            window.Content = root;
            window.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && buttons == MessageBoxButton.YesNo) { result = MessageBoxResult.No; window.Close(); }
            };
            window.ShowDialog();
            return result;
        }

        private static MessageBoxResult DefaultCancelResult(MessageBoxButton buttons) => buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel,
        };

        private static string? IconKey(MessageBoxImage image) => image switch
        {
            MessageBoxImage.Error => "DialogIcon.Error",
            MessageBoxImage.Warning => "DialogIcon.Warning",
            MessageBoxImage.Question => "DialogIcon.Question",
            MessageBoxImage.Information => "DialogIcon.Info",
            _ => null,
        };

        private static string IconBrushKey(MessageBoxImage image) => image switch
        {
            MessageBoxImage.Error => "StatusDanger",
            MessageBoxImage.Warning => "StatusWarning",
            _ => "StatusInfo",
        };

        private static Brush Brush(string key, Brush fallback)
            => Application.Current?.TryFindResource(key) as Brush ?? fallback;
    }
}
