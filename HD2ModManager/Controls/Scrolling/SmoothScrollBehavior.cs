using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace HD2ModManager.Controls.Scrolling;

// Purpose: Adds wheel-only smooth scrolling to an explicit ScrollViewer while preserving nested-scroll boundaries.
public static class SmoothScrollBehavior
{
	private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
		"State", typeof(SmoothScrollState), typeof(SmoothScrollBehavior), new PropertyMetadata(null));

	public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
		"IsEnabled", typeof(bool), typeof(SmoothScrollBehavior), new PropertyMetadata(false, OnIsEnabledChanged));

	public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
	public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

	// All programmatic scrolling that belongs to an interaction should come
	// through the same state machine as wheel scrolling.  Calling
	// ScrollToVerticalOffset directly from a drag timer fights the render-loop
	// interpolation below and produces the characteristic "one frame, then
	// jump" behavior on a busy virtualized list.
	public static bool ScrollBy(ScrollViewer? viewer, double offsetDelta)
	{
		if (viewer is null || Math.Abs(offsetDelta) < 0.01 || viewer.ScrollableHeight <= 0)
			return false;
		if (viewer.GetValue(StateProperty) is not SmoothScrollState state)
			return false;

		state.Queue(offsetDelta);
		return true;
	}

	public static void Stop(ScrollViewer? viewer)
	{
		if (viewer?.GetValue(StateProperty) is SmoothScrollState state)
			state.StopAtCurrentOffset();
	}

	private static void OnIsEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
	{
		if (target is not ScrollViewer viewer) return;
		if ((bool)args.NewValue)
		{
			if (viewer.GetValue(StateProperty) is null)
				viewer.SetValue(StateProperty, new SmoothScrollState(viewer));
			return;
		}

		if (viewer.GetValue(StateProperty) is SmoothScrollState state)
		{
			state.Dispose();
			viewer.ClearValue(StateProperty);
		}
	}

	private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (e.Handled || sender is not ScrollViewer viewer || e.Delta == 0) return;
		// 有控件在认领滚轮（例如下拉展开的 ComboBox），页面让位。
		if (WheelScroll.IsClaimedByAncestor(e.OriginalSource as DependencyObject)) return;
		var direction = Math.Sign(-e.Delta);
		var nested = FindNearestEnabledViewer(e.OriginalSource as DependencyObject);
		if (nested is not null && !ReferenceEquals(nested, viewer) && CanScroll(nested, direction)) return;
		if (!CanScroll(viewer, direction)) return;

		if (viewer.GetValue(StateProperty) is SmoothScrollState state)
		{
			state.Queue(-(e.Delta / 120d) * 84d);
			e.Handled = true;
		}
	}

	private static bool CanScroll(ScrollViewer viewer, int direction)
		=> viewer.ScrollableHeight > 0
			&& (direction < 0 ? viewer.VerticalOffset > 0.5 : viewer.VerticalOffset < viewer.ScrollableHeight - 0.5);

	private static ScrollViewer? FindNearestEnabledViewer(DependencyObject? source)
	{
		for (var current = source; current is not null; current = GetParent(current))
		{
			if (current is ScrollViewer viewer && GetIsEnabled(viewer)) return viewer;
		}
		return null;
	}

	private static DependencyObject? GetParent(DependencyObject current)
		=> current is Visual or Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);

	private sealed class SmoothScrollState : IDisposable
	{
		private readonly ScrollViewer _viewer;
		private double _target;
		private DateTime _lastFrameUtc;
		private double? _expectedOffset;

		public SmoothScrollState(ScrollViewer viewer)
		{
			_viewer = viewer;
			_viewer.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel), handledEventsToo: false);
			_viewer.ScrollChanged += OnScrollChanged;
		}

		public void Queue(double offsetDelta)
		{
			if (Math.Abs(offsetDelta) < 0.01) return;
			if (_lastFrameUtc == default) _target = _viewer.VerticalOffset;
			_target = Math.Clamp(_target + offsetDelta, 0, _viewer.ScrollableHeight);
			if (_lastFrameUtc != default) return;
			_lastFrameUtc = DateTime.UtcNow;
			CompositionTarget.Rendering += OnRendering;
		}

		private void OnRendering(object? sender, EventArgs args)
		{
			var now = DateTime.UtcNow;
			var elapsedMilliseconds = Math.Clamp((now - _lastFrameUtc).TotalMilliseconds, 1, 50);
			_lastFrameUtc = now;
			var current = _viewer.VerticalOffset;
			var distance = _target - current;
			if (Math.Abs(distance) < 0.2)
			{
				Apply(_target);
				Stop();
				return;
			}

			var factor = 1 - Math.Exp(-elapsedMilliseconds / 70d);
			Apply(Math.Clamp(current + (distance * factor), 0, _viewer.ScrollableHeight));
		}

		private void Apply(double offset)
		{
			_expectedOffset = offset;
			_viewer.ScrollToVerticalOffset(offset);
		}

		private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
		{
			// Extent and viewport changes are not user scrolling and must not cancel inertia.
			if (Math.Abs(e.VerticalChange) < 0.01)
				return;

			if (_expectedOffset is { } expected && Math.Abs(e.VerticalOffset - expected) < 0.75)
			{
				_expectedOffset = null;
				return;
			}

			if (_lastFrameUtc != default) Stop();
		}

		private void Stop()
		{
			if (_lastFrameUtc != default) CompositionTarget.Rendering -= OnRendering;
			_target = 0;
			_lastFrameUtc = default;
			_expectedOffset = null;
		}

		public void StopAtCurrentOffset() => Stop();

		public void Dispose()
		{
			Stop();
			_viewer.RemoveHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel));
			_viewer.ScrollChanged -= OnScrollChanged;
		}
	}
}
