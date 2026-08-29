using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using HD2ModManager.Enums;
using HD2ModManager.Services;
using HD2ModManager.ViewModels;
using HD2ModManager.Views;

namespace HD2ModManager
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private BottomBarLayoutSnapshot _pendingBottomBarLayout = BottomBarLayoutSnapshot.Empty;
        private BottomBarLayoutSnapshot _appliedBottomBarLayout = BottomBarLayoutSnapshot.Empty;
        private bool _bottomBarLayoutUpdateQueued;
        private SelectionActionBarPresentation? _selectionActionPresentation;
        private TemporaryEditorBarPresentation? _temporaryEditorPresentation;
        private bool _pageTransitionQueued;
        private bool _pageLayoutWasSplit;
        // Core navigation pages are revisited constantly. Keeping their actual
        // FrameworkElement instances avoids recreating an entire virtualized
        // Mod-list visual tree whenever the user switches between Home/Settings
        // and Library/Profile.
        private readonly Dictionary<object, FrameworkElement> _leftCorePageViewCache = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, FrameworkElement> _rightCorePageViewCache = new(ReferenceEqualityComparer.Instance);
        // A short-lived startup cache owns the first real Profile/Library views
        // before either has been navigated to. The views remain attached to an
        // invisible host so templates, bindings and virtualized ListBoxes can
        // complete their first layout without delaying Home navigation.
        private readonly Dictionary<object, FrameworkElement> _prewarmedCorePageViewCache = new(ReferenceEqualityComparer.Instance);
        private bool _workspaceVisualPrewarmQueued;
        private bool _workspaceVisualPrewarmStarted;
        private EventHandler? _workspaceVisualPrewarmRenderHandler;
        private int _workspaceVisualPrewarmFramesRemaining;
        [StructLayout(LayoutKind.Sequential)]
        struct MARGINS
        {
            public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        const int DWMWA_NCRENDERING_POLICY = 2; // DWMNCRP_ENABLED
        const int WM_GETMINMAXINFO = 0x0024;
        const int WM_NCHITTEST = 0x0084;
        const int HTTOP = 12;
        const uint MONITOR_DEFAULTTONEAREST = 2;

        public MainWindow()
        {
            DataContext = new ShellViewModel();
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            DataContextChanged += MainWindow_DataContextChanged;
            Closed += MainWindow_Closed;
            SourceInitialized += MainWindow_SourceInitialized;
            StateChanged += MainWindow_StateChanged; // 处理最大化时的阴影切换
            Title = HD2ModManager.Resources.Strings.App_Title;
            AllowDrop = true;
            DragOver += MainWindow_DragOver;
            Drop += MainWindow_Drop;
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            PreviewMouseDown += MainWindow_PreviewMouseDown;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Ignore drag when the click originates from interactive controls (tab headers or caption buttons)
            if (e.OriginalSource is DependencyObject d)
            {
                if (FindAncestor<TabControl>(d) != null || FindAncestor<Button>(d) != null)
                {
                    return; // let Tab/Buttons handle the click
                }
            }
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }
            else
            {
                RestoreFromMaximizedForDrag(e.GetPosition(this));
                DragMove();
            }
        }

        private void RestoreFromMaximizedForDrag(Point pointerPosition)
        {
            if (WindowState != WindowState.Maximized) return;

            var restoredBounds = RestoreBounds;
            var horizontalRatio = ActualWidth > 0 ? pointerPosition.X / ActualWidth : 0.5;
            var screenPointer = PointToScreen(pointerPosition);
            var dpi = VisualTreeHelper.GetDpi(this);

            WindowState = WindowState.Normal;
            Left = screenPointer.X / dpi.DpiScaleX - restoredBounds.Width * horizontalRatio;
            Top = screenPointer.Y / dpi.DpiScaleY - 16;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            using var perf = UiPerformanceDiagnostics.Measure(
                "MainWindow.Loaded",
                thresholdMilliseconds: 40,
                details: () => $"mode={(DataContext as ShellViewModel)?.CurrentMode}; split={(DataContext as ShellViewModel)?.ShowRightSlot}");
            SubscribeToShell(DataContext as ShellViewModel);
            BottomBarSurface.ContentSizeReady += BottomBarSurface_ContentSizeReady;
            InitializeBottomBarLayers();
            ShowInitialPage(LeftCurrentPageHost, (DataContext as ShellViewModel)?.LeftPage);
            ShowInitialPage(RightCurrentPageHost, (DataContext as ShellViewModel)?.RightPage);
            _pageLayoutWasSplit = (DataContext as ShellViewModel)?.ShowRightSlot == true;
            Dispatcher.BeginInvoke(UpdateWorkspaceNavigationIndicator, System.Windows.Threading.DispatcherPriority.Loaded);
            QueueWorkspaceVisualPrewarm();
        }

        private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            ClearWorkspaceVisualPrewarm();
            _leftCorePageViewCache.Clear();
            _rightCorePageViewCache.Clear();
            SubscribeToShell(e.NewValue as ShellViewModel);
            if (IsLoaded) QueueWorkspaceVisualPrewarm();
        }

        private async void MainWindow_Closed(object? sender, EventArgs e)
        {
            ClearWorkspaceVisualPrewarm();
            _leftCorePageViewCache.Clear();
            _rightCorePageViewCache.Clear();
            if (DataContext is ShellViewModel shell)
            {
                shell.PropertyChanged -= Shell_PropertyChanged;
                shell.BottomBar.LayoutChanged -= BottomBar_LayoutChanged;
                BottomBarSurface.ContentSizeReady -= BottomBarSurface_ContentSizeReady;
                await shell.DisposeAsync();
            }
        }

        private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is ShellViewModel { IsMessagePanelOpen: true } messageShell
                && e.OriginalSource is DependencyObject messageSource
                && !IsDescendantOf(messageSource, MessageCenterPanel)
                && !IsDescendantOf(messageSource, MessageNavigationButton))
            {
                messageShell.CloseMessagePanel();
            }
            if (DataContext is not ShellViewModel shell) return;
            if (shell.HasMaterialPackagingBottomBar || shell.HasSameKeyRebuildBottomBar)
            {
                if (e.OriginalSource is DependencyObject materialSource && IsDescendantOf(materialSource, BottomContextBar)) return;
                shell.DismissToolBottomBars();
                return;
            }
            if (!shell.BottomBar.HasTemporaryEditor) return;
            // ComboBox 的下拉项由独立 Popup 承载，选择项时焦点会暂时离开底栏视觉树；
            // 切换配置编辑器需要保留这段焦点特权，避免选择目标时底栏被提前收起。
            if (shell.BottomBar.IsProfileSwitchEditor) return;
            if (e.OriginalSource is DependencyObject source && IsDescendantOf(source, BottomContextBar)) return;
            // A selectable list row changes the shared selection on MouseUp.
            // Let that selection event close the editor so MouseDown does not
            // start a second bottom-bar transition for the same click.
            if (e.OriginalSource is DependencyObject rowSource
                && IsDescendantOfType<ListBoxItem>(rowSource)
                && !IsDescendantOfType<Button>(rowSource))
                return;
            shell.CancelBottomBarEdit();
        }

        private static bool IsDescendantOf(DependencyObject source, DependencyObject ancestor)
        {
            while (source != null)
            {
                if (ReferenceEquals(source, ancestor)) return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private static bool IsDescendantOfType<T>(DependencyObject source) where T : DependencyObject
        {
            while (source != null)
            {
                if (source is T) return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private void SubscribeToShell(ShellViewModel? shell)
        {
            if (shell == null) return;
            shell.PropertyChanged -= Shell_PropertyChanged;
            shell.PropertyChanged += Shell_PropertyChanged;
            shell.BottomBar.StructureChanged -= BottomBar_StructureChanged;
            shell.BottomBar.StructureChanged += BottomBar_StructureChanged;
            shell.BottomBar.LayoutChanged -= BottomBar_LayoutChanged;
            shell.BottomBar.LayoutChanged += BottomBar_LayoutChanged;
        }

        private void BottomBar_LayoutChanged(object? sender, BottomBarLayoutSnapshot snapshot)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => BottomBar_LayoutChanged(sender, snapshot), System.Windows.Threading.DispatcherPriority.Render);
                return;
            }
            QueueBottomBarLayout(snapshot);
        }

        private void BottomBar_StructureChanged(object? sender, EventArgs e)
        {
            if (DataContext is not ShellViewModel shell) return;
            _selectionActionPresentation ??= new SelectionActionBarPresentation(
                shell.BottomBar,
                shell.SelectionPrimaryCommand,
                shell.SelectionDeleteCommand,
                shell.SelectionDeleteFromLibraryCommand,
                shell.EnableSelectedDecorationsCommand,
                shell.DisableSelectedDecorationsCommand,
                shell.CancelSelectionCommand);
            _temporaryEditorPresentation ??= new TemporaryEditorBarPresentation(shell.BottomBar);

            if (shell.BottomBar.HasSelection)
                shell.BottomBar.SetSelectionActions(_selectionActionPresentation);
            else
                shell.BottomBar.ClearSelectionActions();

            if (shell.BottomBar.HasTemporaryEditor)
                shell.BottomBar.SetTemporaryEditor(_temporaryEditorPresentation);
            else
                shell.BottomBar.ClearTemporaryEditor();
            QueueBottomBarLayout(shell.BottomBar.Layout);
        }

        private void InitializeBottomBarLayers()
        {
            BottomBar_StructureChanged(this, EventArgs.Empty);
            if (_selectionActionPresentation is null || _temporaryEditorPresentation is null) return;
            BottomContextBar.Width = 0d;
            BottomContextBar.Height = 0d;
            BottomContextBar.Visibility = Visibility.Hidden;
            BottomBarSurface.Prepare(_selectionActionPresentation);
            BottomBarSurface.Prepare(_temporaryEditorPresentation);
            Dispatcher.BeginInvoke(() =>
            {
                if (DataContext is ShellViewModel { BottomBar.HasContent: false })
                    BottomContextBar.Visibility = Visibility.Collapsed;
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private int _bottomBarContainerAnimationVersion;

        private void BottomBarSurface_ContentSizeReady(object? sender, Size measuredSize)
            => ApplyBottomBarContainerSize(_appliedBottomBarLayout, measuredSize);

        private void QueueBottomBarLayout(BottomBarLayoutSnapshot snapshot)
        {
            _pendingBottomBarLayout = snapshot;
            if (_bottomBarLayoutUpdateQueued) return;
            _bottomBarLayoutUpdateQueued = true;
            Dispatcher.BeginInvoke(() =>
            {
                _bottomBarLayoutUpdateQueued = false;
                ApplyBottomBarLayout(_pendingBottomBarLayout);
            }, System.Windows.Threading.DispatcherPriority.Render);
        }

        private void ApplyBottomBarLayout(BottomBarLayoutSnapshot snapshot)
        {
            _appliedBottomBarLayout = snapshot;
            BottomBarSurface.Apply(snapshot);
        }

        private void ApplyBottomBarContainerSize(BottomBarLayoutSnapshot snapshot, Size measuredSize)
        {
            var padding = BottomContextBar.Padding;
            var border = BottomContextBar.BorderThickness;
            var targetWidth = snapshot.HasContent
                ? measuredSize.Width + padding.Left + padding.Right + border.Left + border.Right
                : 0d;
            var targetHeight = snapshot.HasContent
                ? snapshot.ContentHeight + padding.Top + padding.Bottom + border.Top + border.Bottom
                : 0d;
            var version = ++_bottomBarContainerAnimationVersion;
            var isAppearing = snapshot.HasContent && BottomContextBar.Visibility != Visibility.Visible;
            if (isAppearing)
            {
                // Do not let the first visible layout use Auto width from its parent.
                // The animation must start from a deterministic collapsed footprint.
                BottomContextBar.BeginAnimation(FrameworkElement.WidthProperty, null);
                BottomContextBar.BeginAnimation(FrameworkElement.HeightProperty, null);
                BottomContextBar.Width = 0d;
                BottomContextBar.Height = 0d;
                BottomContextBar.Visibility = Visibility.Visible;
            }
            AnimateContainerDimension(FrameworkElement.WidthProperty, targetWidth, 190, version, isAppearing ? 0d : null);
            AnimateContainerDimension(FrameworkElement.HeightProperty, targetHeight, 190, version, isAppearing ? 0d : null, () =>
            {
                if (!snapshot.HasContent && version == _bottomBarContainerAnimationVersion)
                    BottomContextBar.Visibility = Visibility.Collapsed;
            });
        }

        private void AnimateContainerDimension(DependencyProperty property, double target, int milliseconds, int version, double? explicitStart = null, Action? completed = null)
        {
            var current = explicitStart ?? (property == FrameworkElement.WidthProperty ? BottomContextBar.ActualWidth : BottomContextBar.ActualHeight);
            if (explicitStart is null && (double.IsNaN(current) || current <= 0)) current = (double)BottomContextBar.GetValue(property);
            if (double.IsNaN(current)) current = 0d;
            BottomContextBar.BeginAnimation(property, null);
            BottomContextBar.SetValue(property, current);
            var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop
            };
            animation.Completed += (_, _) =>
            {
                if (version != _bottomBarContainerAnimationVersion) return;
                BottomContextBar.SetValue(property, target);
                completed?.Invoke();
            };
            BottomContextBar.BeginAnimation(property, animation);
        }

        private void Shell_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not ShellViewModel shell) return;
            if (e.PropertyName == nameof(ShellViewModel.IsMessagePanelOpen))
            {
                AnimateMessagePanel(MessageCenterPanel, shell.IsMessagePanelOpen, 100, () => shell.IsMessagePanelOpen);
                return;
            }
            if (e.PropertyName == nameof(ShellViewModel.IsMessagePreviewOpen))
            {
                AnimateMessagePanel(MessagePreviewPanel, shell.IsMessagePreviewOpen, 50, () => shell.IsMessagePreviewOpen);
                return;
            }
            if (e.PropertyName == nameof(ShellViewModel.ShowRightSlot))
            {
                var targetIsSplit = shell.ShowRightSlot;
                if (!_pageLayoutWasSplit && targetIsSplit)
                    CaptureSinglePageForSplitTransition();
                _pageLayoutWasSplit = targetIsSplit;
                return;
            }
            if (e.PropertyName == nameof(ShellViewModel.LeftPage))
            {
                QueuePageTransitions();
            }
            else if (e.PropertyName == nameof(ShellViewModel.RightPage))
            {
                QueuePageTransitions();
            }
            else if (e.PropertyName is nameof(ShellViewModel.CurrentMode)
                or nameof(ShellViewModel.IsHomeActive)
                or nameof(ShellViewModel.IsProfileActive)
                or nameof(ShellViewModel.IsLibraryActive)
                or nameof(ShellViewModel.IsSplitActive)
                or nameof(ShellViewModel.IsSettingsActive))
            {
                Dispatcher.BeginInvoke(UpdateWorkspaceNavigationIndicator, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void AnimateMessagePanel(FrameworkElement panel, bool isOpen, double offset, Func<bool> isStillOpen)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (panel.RenderTransform is not TranslateTransform translate) return;
                panel.BeginAnimation(OpacityProperty, null);
                translate.BeginAnimation(TranslateTransform.YProperty, null);

                if (isOpen)
                {
                    panel.Visibility = Visibility.Visible;
                    panel.Opacity = 0;
                    translate.Y = -offset;
                    translate.BeginAnimation(TranslateTransform.YProperty, CreateMessageAnimation(-offset, 0, 190));
                    panel.BeginAnimation(OpacityProperty, CreateMessageAnimation(0, 1, 150));
                    return;
                }

                if (panel.Visibility != Visibility.Visible) return;
                var close = CreateMessageAnimation(panel.Opacity, 0, 150);
                close.Completed += (_, _) =>
                {
                    if (!isStillOpen()) panel.Visibility = Visibility.Collapsed;
                };
                translate.BeginAnimation(TranslateTransform.YProperty, CreateMessageAnimation(translate.Y, -offset, 190));
                panel.BeginAnimation(OpacityProperty, close);
            }, System.Windows.Threading.DispatcherPriority.Render);
        }

        private static DoubleAnimation CreateMessageAnimation(double from, double to, int milliseconds) => new(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        private void OnMessageListLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ListBox listBox) return;
            listBox.Items.CurrentChanged += (_, _) => ScrollMessagesToEnd(listBox);
            ScrollMessagesToEnd(listBox);
        }

        private void OnMessagePreviewClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && FindVisualParent<Button>(source) is not null) return;
            if (DataContext is ShellViewModel shell) shell.OpenMessagePanel();
            e.Handled = true;
        }

        private void OnMessageItemClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && FindVisualParent<Button>(source) is not null) return;
            if (sender is FrameworkElement element && element.DataContext is HD2ModManager.Services.MessageCenterItem item
                && DataContext is ShellViewModel shell) shell.CopyMessageCommand.Execute(item);
            e.Handled = true;
        }

        private static T? FindVisualParent<T>(DependencyObject source) where T : DependencyObject
        {
            var current = source;
            while (current is not null)
            {
                if (current is T match) return match;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private static void ScrollMessagesToEnd(ListBox listBox)
        {
            if (listBox.Items.Count == 0) return;
            listBox.Dispatcher.BeginInvoke(() => listBox.ScrollIntoView(listBox.Items[listBox.Items.Count - 1]), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void UpdateWorkspaceNavigationIndicator()
        {
            if (DataContext is not ShellViewModel shell || !WorkspaceNavigationHost.IsLoaded) return;

            FrameworkElement? target = shell.CurrentMode switch
            {
                WorkspaceMode.Home => HomeNavigationButton,
                WorkspaceMode.Settings => SettingsNavigationButton,
                WorkspaceMode.ProfileOnly or WorkspaceMode.LibraryOnly or WorkspaceMode.ProfileLibrarySplit => WorkspaceCapsuleGroup.GetActiveButton(),
                _ => null
            };
            if (target is null || target.ActualWidth <= 0 || target.ActualHeight <= 0) return;

            var targetPoint = target.TransformToAncestor(WorkspaceNavigationHost).Transform(new Point(0, 0));
            var targetHeight = target.ActualHeight;
            var targetRadius = targetHeight / 2d;
            var isInitialPlacement = WorkspaceNavigationIndicator.ActualWidth <= 0;

            var currentPoint = WorkspaceNavigationIndicator.TransformToAncestor(WorkspaceNavigationHost).Transform(new Point(0, 0));
            var currentWidth = WorkspaceNavigationIndicator.ActualWidth;
            var currentHeight = WorkspaceNavigationIndicator.ActualHeight;

            WorkspaceNavigationIndicator.BeginAnimation(WidthProperty, null);
            WorkspaceNavigationIndicator.BeginAnimation(HeightProperty, null);
            WorkspaceNavigationIndicator.BeginAnimation(Canvas.LeftProperty, null);
            WorkspaceNavigationIndicator.BeginAnimation(Canvas.TopProperty, null);
            WorkspaceNavigationIndicator.BeginAnimation(OpacityProperty, null);

            if (!isInitialPlacement)
            {
                WorkspaceNavigationIndicator.Width = currentWidth;
                WorkspaceNavigationIndicator.Height = currentHeight;
                Canvas.SetLeft(WorkspaceNavigationIndicator, currentPoint.X);
                Canvas.SetTop(WorkspaceNavigationIndicator, currentPoint.Y);
            }

            if (isInitialPlacement)
            {
                WorkspaceNavigationIndicator.Width = target.ActualWidth;
                WorkspaceNavigationIndicator.Height = targetHeight;
                WorkspaceNavigationIndicator.CornerRadius = new CornerRadius(targetRadius);
                Canvas.SetLeft(WorkspaceNavigationIndicator, targetPoint.X);
                Canvas.SetTop(WorkspaceNavigationIndicator, targetPoint.Y);
                WorkspaceNavigationIndicator.Opacity = 1;
                return;
            }

            var duration = TimeSpan.FromMilliseconds(240);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            WorkspaceNavigationIndicator.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(targetPoint.X, duration) { EasingFunction = easing });
            WorkspaceNavigationIndicator.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(targetPoint.Y, duration) { EasingFunction = easing });
            WorkspaceNavigationIndicator.BeginAnimation(WidthProperty, new DoubleAnimation(target.ActualWidth, duration) { EasingFunction = easing });
            WorkspaceNavigationIndicator.BeginAnimation(HeightProperty, new DoubleAnimation(targetHeight, duration) { EasingFunction = easing });
            WorkspaceNavigationIndicator.CornerRadius = new CornerRadius(targetRadius);
            WorkspaceNavigationIndicator.Opacity = 1;
        }

        private void ShowInitialPage(ContentControl host, object? page)
        {
            using var perf = UiPerformanceDiagnostics.Measure(
                "Page.InitialCreate",
                thresholdMilliseconds: 30,
                details: () => $"page={page?.GetType().Name ?? "null"}");
            host.Content = CreatePageContent(page, host);
            host.Opacity = 1;
        }

        private void QueueWorkspaceVisualPrewarm()
        {
            if (_workspaceVisualPrewarmQueued || _workspaceVisualPrewarmStarted || !IsLoaded) return;
            _workspaceVisualPrewarmQueued = true;
            _ = Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                new Action(BeginWorkspaceVisualPrewarm));
        }

        private void BeginWorkspaceVisualPrewarm()
        {
            _workspaceVisualPrewarmQueued = false;
            if (_workspaceVisualPrewarmStarted || !IsLoaded || DataContext is not ShellViewModel shell) return;

            _workspaceVisualPrewarmStarted = true;
            var pages = shell.GetOrCreateWorkspaceVisualPrewarmPages();
            using (UiPerformanceDiagnostics.Measure(
                       "WorkspaceVisualPrewarm.Profile",
                       thresholdMilliseconds: 30,
                       details: () => "host=prewarm-profile"))
            {
                PrewarmCorePageView(pages.Profile, PrewarmProfilePageHost);
            }

            // Build one real page per idle turn. This keeps Home responsive even
            // on cold WPF/JIT startup, while input still outranks the warmup.
            _ = Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                new Action(ContinueWorkspaceVisualPrewarm));
        }

        private void ContinueWorkspaceVisualPrewarm()
        {
            if (!IsLoaded || !_workspaceVisualPrewarmStarted || DataContext is not ShellViewModel shell) return;

            var pages = shell.GetOrCreateWorkspaceVisualPrewarmPages();
            using (UiPerformanceDiagnostics.Measure(
                       "WorkspaceVisualPrewarm.Library",
                       thresholdMilliseconds: 30,
                       details: () => "host=prewarm-library"))
            {
                PrewarmCorePageView(pages.Library, PrewarmLibraryPageHost);
            }

            AwaitWorkspaceVisualPrewarmFrames();
        }

        private void PrewarmCorePageView(object page, ContentControl host)
        {
            if (_prewarmedCorePageViewCache.ContainsKey(page)
                || _leftCorePageViewCache.ContainsKey(page)
                || _rightCorePageViewCache.ContainsKey(page))
                return;

            var view = CreateTemplatedPageView(page);
            if (view is null) return;
            _prewarmedCorePageViewCache.Add(page, view);
            host.Content = view;
        }

        private void AwaitWorkspaceVisualPrewarmFrames()
        {
            if (_workspaceVisualPrewarmRenderHandler is not null)
            {
                CompositionTarget.Rendering -= _workspaceVisualPrewarmRenderHandler;
                _workspaceVisualPrewarmRenderHandler = null;
            }

            _workspaceVisualPrewarmFramesRemaining = 2;
            EventHandler? handler = null;
            handler = (_, _) =>
            {
                if (--_workspaceVisualPrewarmFramesRemaining > 0) return;
                if (handler is not null) CompositionTarget.Rendering -= handler;
                if (ReferenceEquals(_workspaceVisualPrewarmRenderHandler, handler))
                    _workspaceVisualPrewarmRenderHandler = null;

                if (!IsLoaded || _prewarmedCorePageViewCache.Count == 0) return;
                LogService.Info($"工作区视觉预热完成：已布局真实配置页 / 模组库页，缓存视图={_prewarmedCorePageViewCache.Count}。");
            };
            _workspaceVisualPrewarmRenderHandler = handler;
            CompositionTarget.Rendering += handler;
        }

        private void ClearWorkspaceVisualPrewarm()
        {
            if (_workspaceVisualPrewarmRenderHandler is not null)
            {
                CompositionTarget.Rendering -= _workspaceVisualPrewarmRenderHandler;
                _workspaceVisualPrewarmRenderHandler = null;
            }

            _workspaceVisualPrewarmQueued = false;
            _workspaceVisualPrewarmStarted = false;
            _workspaceVisualPrewarmFramesRemaining = 0;
            if (PrewarmProfilePageHost is not null) PrewarmProfilePageHost.Content = null;
            if (PrewarmLibraryPageHost is not null) PrewarmLibraryPageHost.Content = null;
            _prewarmedCorePageViewCache.Clear();
        }

        private void DetachPrewarmedCorePage(FrameworkElement view)
        {
            if (ReferenceEquals(PrewarmProfilePageHost.Content, view)) PrewarmProfilePageHost.Content = null;
            if (ReferenceEquals(PrewarmLibraryPageHost.Content, view)) PrewarmLibraryPageHost.Content = null;
        }

        // Decoration planning is a full-workspace tool page. Host its concrete view
        // explicitly so it is not affected by implicit-template resolution during a
        // page transition. Core navigation pages use the same explicit creation
        // route, but are retained as real views so their ListBox containers and
        // scroll position survive ordinary workspace navigation.
        private object? CreatePageContent(object? page, ContentControl targetHost)
        {
            if (page is null) return null;

            var cache = GetCorePageViewCache(targetHost);
            if (ShouldCacheCorePage(page)
                && cache.TryGetValue(page, out var cached))
            {
                DetachCachedPageFromWorkspace(cached, targetHost);
                return cached;
            }

            if (ShouldCacheCorePage(page)
                && _prewarmedCorePageViewCache.Remove(page, out var prewarmed))
            {
                DetachPrewarmedCorePage(prewarmed);
                cache[page] = prewarmed;
                return prewarmed;
            }

            object? content = page switch
            {
                DecorationPlanPageViewModel plan => new DecorationPlanPageView { DataContext = plan },
                _ when ShouldCacheCorePage(page) => CreateTemplatedPageView(page) ?? page,
                _ => page,
            };

            if (ShouldCacheCorePage(page) && content is FrameworkElement view)
                cache[page] = view;

            return content;
        }

        private static bool ShouldCacheCorePage(object page)
            => page is HomePageViewModel
                or ProfilePageViewModel
                or LibraryPageViewModel
                or SettingsPageViewModel;

        private FrameworkElement? CreateTemplatedPageView(object page)
        {
            var templateKey = new DataTemplateKey(page.GetType());
            var template = TryFindResource(templateKey) as DataTemplate
                ?? Application.Current?.TryFindResource(templateKey) as DataTemplate;
            if (template?.LoadContent() is not FrameworkElement view) return null;
            view.DataContext = page;
            return view;
        }

        private Dictionary<object, FrameworkElement> GetCorePageViewCache(ContentControl host)
            => IsRightWorkspaceHost(host) ? _rightCorePageViewCache : _leftCorePageViewCache;

        private bool IsRightWorkspaceHost(ContentControl host)
            => ReferenceEquals(host, RightCurrentPageHost)
                || ReferenceEquals(host, RightPreviousPageHost)
                || ReferenceEquals(host, FindName("OverlayRightPageHost"));

        private void DetachCachedPageFromWorkspace(FrameworkElement view, ContentControl targetHost)
        {
            foreach (var host in EnumerateWorkspacePageHosts(IsRightWorkspaceHost(targetHost)))
            {
                if (!ReferenceEquals(host.Content, view)) continue;
                host.BeginAnimation(OpacityProperty, null);
                host.Content = null;
            }
        }

        private IEnumerable<ContentControl> EnumerateWorkspacePageHosts(bool rightSlot)
        {
            if (rightSlot)
            {
                yield return RightCurrentPageHost;
                yield return RightPreviousPageHost;
                if (FindName("OverlayRightPageHost") is ContentControl overlayRight) yield return overlayRight;
                yield break;
            }

            yield return LeftCurrentPageHost;
            yield return LeftPreviousPageHost;
            if (FindName("OverlayLeftPageHost") is ContentControl overlayLeft) yield return overlayLeft;
        }

        private static bool IsCurrentPageContent(ContentControl host, object? page)
            => ReferenceEquals(host.Content, page)
                || host.Content is FrameworkElement { DataContext: var dataContext }
                    && ReferenceEquals(dataContext, page);

        private void CaptureSinglePageForSplitTransition()
        {
            if (FindName("OverlayLeftPageHost") is not ContentControl overlayLeftPageHost) return;
            if (overlayLeftPageHost.Content is not null) return;
            var page = LeftCurrentPageHost.Content;
            if (page is null) return;

            var width = LeftCurrentPageHost.ActualWidth;
            var height = LeftCurrentPageHost.ActualHeight;
            if (width <= 0 || height <= 0) return;

            overlayLeftPageHost.BeginAnimation(OpacityProperty, null);
            overlayLeftPageHost.Content = page;
            overlayLeftPageHost.Width = width;
            overlayLeftPageHost.Height = height;
            Canvas.SetLeft(overlayLeftPageHost, 0);
            Canvas.SetTop(overlayLeftPageHost, 0);
            overlayLeftPageHost.Opacity = 1;
            LeftCurrentPageHost.Content = null;

            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
            {
                if (overlayLeftPageHost.Content is null) return;
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                fadeOut.Completed += (_, _) =>
                {
                    overlayLeftPageHost.BeginAnimation(OpacityProperty, null);
                    overlayLeftPageHost.Content = null;
                    overlayLeftPageHost.Width = double.NaN;
                    overlayLeftPageHost.Height = double.NaN;
                };
                overlayLeftPageHost.BeginAnimation(OpacityProperty, fadeOut);
            }));
        }

        private void QueuePageTransitions()
        {
            if (_pageTransitionQueued) return;
            _pageTransitionQueued = true;
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                _pageTransitionQueued = false;
                if (DataContext is not ShellViewModel shell) return;
                TransitionPage(LeftCurrentPageHost, LeftPreviousPageHost, shell.LeftPage);
                TransitionPage(RightCurrentPageHost, RightPreviousPageHost, shell.RightPage);
            }));
        }

        private void TransitionPage(ContentControl currentHost, ContentControl previousHost, object? nextPage)
        {
            if (IsCurrentPageContent(currentHost, nextPage)) return;

            using var perf = UiPerformanceDiagnostics.Measure(
                "Page.TransitionPrepare",
                thresholdMilliseconds: 30,
                details: () => $"page={nextPage?.GetType().Name ?? "null"}; host={currentHost.Name}");
            var nextContent = CreatePageContent(nextPage, currentHost);

            currentHost.BeginAnimation(OpacityProperty, null);
            previousHost.BeginAnimation(OpacityProperty, null);
            previousHost.Content = currentHost.Content;
            previousHost.Opacity = previousHost.Content is null ? 0 : 1;
            currentHost.Content = nextContent;
            currentHost.Opacity = 0;

            _ = currentHost.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
            {
                using var renderPerf = UiPerformanceDiagnostics.Measure(
                    "Page.TransitionRenderSetup",
                    thresholdMilliseconds: 20,
                    details: () => $"page={nextPage?.GetType().Name ?? "null"}; host={currentHost.Name}");
                var duration = TimeSpan.FromMilliseconds(200);
                var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
                currentHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, duration) { EasingFunction = easing });
                if (previousHost.Content is null) return;
                var fadeOut = new DoubleAnimation(0, duration) { EasingFunction = easing };
                fadeOut.Completed += (_, _) =>
                {
                    if (ReferenceEquals(currentHost.Content, nextContent)) previousHost.Content = null;
                };
                previousHost.BeginAnimation(OpacityProperty, fadeOut);
            }));
        }

        private static VisualMetrics CaptureVisualMetrics(DependencyObject root)
        {
            var metrics = new VisualMetrics();
            CountVisuals(root, metrics);
            return metrics;
        }

        private static void CountVisuals(DependencyObject current, VisualMetrics metrics)
        {
            if (current is null) return;
            metrics.VisualCount++;
            if (current is ListBox listBox)
            {
                metrics.ListBoxCount++;
                metrics.ListItemCount += listBox.Items.Count;
            }
            else if (current is Image)
            {
                metrics.ImageCount++;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < childCount; index++) CountVisuals(VisualTreeHelper.GetChild(current, index), metrics);
        }

        private sealed class VisualMetrics
        {
            public int VisualCount { get; set; }
            public int ListBoxCount { get; set; }
            public int ListItemCount { get; set; }
            public int ImageCount { get; set; }
        }

        private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void MaxButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WindowMessageHook);
            // 启用非客户区渲染（让DWM参与绘制，从而出现系统阴影）
            int val = 2; // DWMNCRP_ENABLED
            DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref val, sizeof(int));

            ApplyWindowChromeMetrics(isMaximized: WindowState == WindowState.Maximized);
            ApplyDwmShadowMargins(isMaximized: WindowState == WindowState.Maximized);
        }

        // 无边框窗口的最大化尺寸由工作区明确约束，避免覆盖任务栏或超出屏幕边界。
        private static IntPtr WindowMessageHook(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (msg == WM_NCHITTEST && hwnd != IntPtr.Zero && GetCursorPos(out var cursor) && GetWindowRect(hwnd, out var windowRect))
            {
                // 顶部标题栏覆盖了 WindowChrome 的默认命中区域时，仍保留 6px 原生拖拽调整边框。
                if (cursor.y >= windowRect.top && cursor.y < windowRect.top + 6)
                {
                    handled = true;
                    return new IntPtr(HTTOP);
                }
            }

            if (msg != WM_GETMINMAXINFO || lParam == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref monitorInfo))
            {
                return IntPtr.Zero;
            }

            var maxInfo = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var window = HwndSource.FromHwnd(hwnd)?.RootVisual as Window;
            if (window != null)
            {
                var dpi = VisualTreeHelper.GetDpi(window);
                maxInfo.ptMinTrackSize = new POINT
                {
                    x = (int)Math.Ceiling(window.MinWidth * dpi.DpiScaleX),
                    y = (int)Math.Ceiling(window.MinHeight * dpi.DpiScaleY)
                };
            }
            maxInfo.ptMaxPosition = new POINT
            {
                x = monitorInfo.rcWork.left - monitorInfo.rcMonitor.left,
                y = monitorInfo.rcWork.top - monitorInfo.rcMonitor.top
            };
            maxInfo.ptMaxSize = new POINT
            {
                x = monitorInfo.rcWork.right - monitorInfo.rcWork.left,
                y = monitorInfo.rcWork.bottom - monitorInfo.rcWork.top
            };
            Marshal.StructureToPtr(maxInfo, lParam, fDeleteOld: false);
            handled = true;
            return IntPtr.Zero;
        }

        // 最大化时不保留 WindowChrome 的调整边框，避免它与系统最大化边界叠加；普通状态恢复正常的拖拽边界。
        private void ApplyWindowChromeMetrics(bool isMaximized)
        {
            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome == null) return;

            chrome.ResizeBorderThickness = isMaximized
                ? new Thickness(0)
                : new Thickness(6);
        }

        // 根据最大化状态设置边距：正常时给极小边距，最大化时清零避免闪烁
        private void ApplyDwmShadowMargins(bool isMaximized)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            MARGINS m;
            if (isMaximized)
            {
                m = new MARGINS { cxLeftWidth = 0, cxRightWidth = 0, cyTopHeight = 0, cyBottomHeight = 0 };
            }
            else
            {
                // 给1像素，让DWM在非客户区绘制从而出现系统阴影
                m = new MARGINS { cxLeftWidth = 1, cxRightWidth = 1, cyTopHeight = 0, cyBottomHeight = 1 };
            }
            DwmExtendFrameIntoClientArea(hwnd, ref m);
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            ApplyWindowChromeMetrics(isMaximized: WindowState == WindowState.Maximized);
            ApplyDwmShadowMargins(isMaximized: WindowState == WindowState.Maximized);
        }

        private void MainWindow_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private async void MainWindow_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (DataContext is HD2ModManager.ViewModels.ShellViewModel shell)
            {
                await shell.ProcessImportQueueAsync(files);
            }
        }

        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                if (DataContext is HD2ModManager.ViewModels.ShellViewModel shell)
                {
                    if (shell.BottomBar.HasTemporaryEditor)
                    {
                        shell.CancelBottomBarEdit();
                    }
                    else if (shell.HasSelection)
                    {
                        shell.ClearTransientSelection();
                    }
                    else
                    {
                        shell.Navigate(WorkspaceMode.Home);
                    }
                    e.Handled = true;
                }
            }
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {

        }
    }
}
