using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HD2ModManager.Services;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views;

// The workspace owns two long-lived visual slots plus short-lived hidden hosts
// used to warm the first real Profile/Library views. Keeping that lifecycle in
// one place prevents MainWindow from mixing visual cache ownership with general
// window chrome and input handling.
internal sealed record WorkspacePageHosts(
    ContentControl LeftCurrent,
    ContentControl LeftPrevious,
    ContentControl RightCurrent,
    ContentControl RightPrevious,
    ContentControl OverlayLeft,
    ContentControl OverlayRight,
    ContentControl PrewarmProfile,
    ContentControl PrewarmLibrary);

internal sealed class WorkspacePagePresentationController : IDisposable
{
    private readonly Window _owner;
    private readonly WorkspacePageHosts _hosts;
    private readonly Dictionary<object, FrameworkElement> _leftCorePageViewCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, FrameworkElement> _rightCorePageViewCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, FrameworkElement> _prewarmedCorePageViewCache = new(ReferenceEqualityComparer.Instance);
    private bool _pageTransitionQueued;
    private bool _pageLayoutWasSplit;
    private bool _workspaceVisualPrewarmQueued;
    private bool _workspaceVisualPrewarmStarted;
    private EventHandler? _workspaceVisualPrewarmRenderHandler;
    private int _workspaceVisualPrewarmFramesRemaining;
    private bool _disposed;

    public WorkspacePagePresentationController(Window owner, WorkspacePageHosts hosts)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
    }

    public void ShowInitialPages(ShellViewModel? shell)
    {
        if (_disposed) return;
        ShowInitialPage(_hosts.LeftCurrent, shell?.LeftPage);
        ShowInitialPage(_hosts.RightCurrent, shell?.RightPage);
        _pageLayoutWasSplit = shell?.ShowRightSlot == true;
    }

    public void OnWorkspaceLayoutChanged(bool targetIsSplit)
    {
        if (_disposed) return;
        if (!_pageLayoutWasSplit && targetIsSplit)
            CaptureSinglePageForSplitTransition();
        _pageLayoutWasSplit = targetIsSplit;
    }

    public void QueuePageTransitions()
    {
        if (_disposed) return;
        if (_pageTransitionQueued) return;
        _pageTransitionQueued = true;
        _ = _owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _pageTransitionQueued = false;
            if (_disposed || _owner.DataContext is not ShellViewModel shell) return;
            TransitionPage(_hosts.LeftCurrent, _hosts.LeftPrevious, shell.LeftPage);
            TransitionPage(_hosts.RightCurrent, _hosts.RightPrevious, shell.RightPage);
        }));
    }

    public void QueueVisualPrewarm()
    {
        if (_disposed) return;
        if (_workspaceVisualPrewarmQueued || _workspaceVisualPrewarmStarted || !_owner.IsLoaded) return;
        _workspaceVisualPrewarmQueued = true;
        _ = _owner.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(BeginWorkspaceVisualPrewarm));
    }

    public void Reset()
    {
        if (_disposed) return;
        ClearWorkspaceVisualPrewarm();
        _leftCorePageViewCache.Clear();
        _rightCorePageViewCache.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearWorkspaceVisualPrewarm();
        _leftCorePageViewCache.Clear();
        _rightCorePageViewCache.Clear();
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

    private void BeginWorkspaceVisualPrewarm()
    {
        _workspaceVisualPrewarmQueued = false;
        if (_disposed || _workspaceVisualPrewarmStarted || !_owner.IsLoaded || _owner.DataContext is not ShellViewModel shell) return;

        _workspaceVisualPrewarmStarted = true;
        var pages = shell.GetOrCreateWorkspaceVisualPrewarmPages();
        using (UiPerformanceDiagnostics.Measure(
                   "WorkspaceVisualPrewarm.Profile",
                   thresholdMilliseconds: 30,
                   details: () => "host=prewarm-profile"))
        {
            PrewarmCorePageView(pages.Profile, _hosts.PrewarmProfile);
        }

        // Build one real page per idle turn. This keeps Home responsive even on
        // cold WPF/JIT startup, while input still outranks the warmup.
        _ = _owner.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(ContinueWorkspaceVisualPrewarm));
    }

    private void ContinueWorkspaceVisualPrewarm()
    {
        if (_disposed || !_owner.IsLoaded || !_workspaceVisualPrewarmStarted || _owner.DataContext is not ShellViewModel shell) return;

        var pages = shell.GetOrCreateWorkspaceVisualPrewarmPages();
        using (UiPerformanceDiagnostics.Measure(
                   "WorkspaceVisualPrewarm.Library",
                   thresholdMilliseconds: 30,
                   details: () => "host=prewarm-library"))
        {
            PrewarmCorePageView(pages.Library, _hosts.PrewarmLibrary);
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

            if (_disposed || !_owner.IsLoaded || _prewarmedCorePageViewCache.Count == 0) return;
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
        _hosts.PrewarmProfile.Content = null;
        _hosts.PrewarmLibrary.Content = null;
        _prewarmedCorePageViewCache.Clear();
    }

    private void DetachPrewarmedCorePage(FrameworkElement view)
    {
        if (ReferenceEquals(_hosts.PrewarmProfile.Content, view)) _hosts.PrewarmProfile.Content = null;
        if (ReferenceEquals(_hosts.PrewarmLibrary.Content, view)) _hosts.PrewarmLibrary.Content = null;
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
        var template = _owner.TryFindResource(templateKey) as DataTemplate
            ?? Application.Current?.TryFindResource(templateKey) as DataTemplate;
        if (template?.LoadContent() is not FrameworkElement view) return null;
        view.DataContext = page;
        return view;
    }

    private Dictionary<object, FrameworkElement> GetCorePageViewCache(ContentControl host)
        => IsRightWorkspaceHost(host) ? _rightCorePageViewCache : _leftCorePageViewCache;

    private bool IsRightWorkspaceHost(ContentControl host)
        => ReferenceEquals(host, _hosts.RightCurrent)
            || ReferenceEquals(host, _hosts.RightPrevious)
            || ReferenceEquals(host, _hosts.OverlayRight);

    private void DetachCachedPageFromWorkspace(FrameworkElement view, ContentControl targetHost)
    {
        foreach (var host in EnumerateWorkspacePageHosts(IsRightWorkspaceHost(targetHost)))
        {
            if (!ReferenceEquals(host.Content, view)) continue;
            host.BeginAnimation(UIElement.OpacityProperty, null);
            host.Content = null;
        }
    }

    private IEnumerable<ContentControl> EnumerateWorkspacePageHosts(bool rightSlot)
    {
        if (rightSlot)
        {
            yield return _hosts.RightCurrent;
            yield return _hosts.RightPrevious;
            yield return _hosts.OverlayRight;
            yield break;
        }

        yield return _hosts.LeftCurrent;
        yield return _hosts.LeftPrevious;
        yield return _hosts.OverlayLeft;
    }

    private static bool IsCurrentPageContent(ContentControl host, object? page)
        => ReferenceEquals(host.Content, page)
            || host.Content is FrameworkElement { DataContext: var dataContext }
                && ReferenceEquals(dataContext, page);

    private void CaptureSinglePageForSplitTransition()
    {
        if (_hosts.OverlayLeft.Content is not null) return;
        var page = _hosts.LeftCurrent.Content;
        if (page is null) return;

        var width = _hosts.LeftCurrent.ActualWidth;
        var height = _hosts.LeftCurrent.ActualHeight;
        if (width <= 0 || height <= 0) return;

        _hosts.OverlayLeft.BeginAnimation(UIElement.OpacityProperty, null);
        _hosts.OverlayLeft.Content = page;
        _hosts.OverlayLeft.Width = width;
        _hosts.OverlayLeft.Height = height;
        Canvas.SetLeft(_hosts.OverlayLeft, 0);
        Canvas.SetTop(_hosts.OverlayLeft, 0);
        _hosts.OverlayLeft.Opacity = 1;
        _hosts.LeftCurrent.Content = null;

        _ = _owner.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (_disposed || _hosts.OverlayLeft.Content is null) return;
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fadeOut.Completed += (_, _) =>
            {
                _hosts.OverlayLeft.BeginAnimation(UIElement.OpacityProperty, null);
                _hosts.OverlayLeft.Content = null;
                _hosts.OverlayLeft.Width = double.NaN;
                _hosts.OverlayLeft.Height = double.NaN;
            };
            _hosts.OverlayLeft.BeginAnimation(UIElement.OpacityProperty, fadeOut);
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

        currentHost.BeginAnimation(UIElement.OpacityProperty, null);
        previousHost.BeginAnimation(UIElement.OpacityProperty, null);
        previousHost.Content = currentHost.Content;
        previousHost.Opacity = previousHost.Content is null ? 0 : 1;
        currentHost.Content = nextContent;
        currentHost.Opacity = 0;

        _ = currentHost.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (_disposed) return;
            using var renderPerf = UiPerformanceDiagnostics.Measure(
                "Page.TransitionRenderSetup",
                thresholdMilliseconds: 20,
                details: () => $"page={nextPage?.GetType().Name ?? "null"}; host={currentHost.Name}");
            var duration = TimeSpan.FromMilliseconds(200);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            currentHost.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, duration) { EasingFunction = easing });
            if (previousHost.Content is null) return;
            var fadeOut = new DoubleAnimation(0, duration) { EasingFunction = easing };
            fadeOut.Completed += (_, _) =>
            {
                if (ReferenceEquals(currentHost.Content, nextContent)) previousHost.Content = null;
            };
            previousHost.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }));
    }

}
