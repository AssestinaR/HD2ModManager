using System.Windows;
using System.Windows.Media;

namespace HD2ModManager.Views;

/// <summary>
/// The two cross-page relationship gestures supported by the Manager.
/// Keeping the operation in the drag payload prevents a target page from
/// guessing whether a drop means "attach" or "release".
/// </summary>
public enum ModRelationshipDragOperation
{
    None,
    SetAsOption,
    RemoveOptionParent,
}

public sealed record ModRelationshipDragPayload(
    ModRelationshipDragOperation Operation,
    IReadOnlyList<string> SelectedKeys,
    string? SourceHostId);

internal sealed class ModRelationshipDropEventArgs(ModRelationshipDragPayload payload) : EventArgs
{
    public ModRelationshipDragOperation Operation { get; } = payload.Operation;
    public IReadOnlyList<string> SelectedKeys { get; } = payload.SelectedKeys;
    public string? SourceHostId { get; } = payload.SourceHostId;
    public ModRelationshipDragPayload Payload { get; } = payload;
}

/// <summary>
/// Implemented by a page-level drop surface. Targets own their visual overlay
/// and raise the semantic event only after the pointer is released inside them.
/// </summary>
public interface IModRelationshipDropTarget
{
    bool CanAcceptRelationshipDrop(ModRelationshipDragPayload payload);
    bool ContainsScreenPoint(Point screenPoint);
    bool ContainsElement(DependencyObject element);
    void SetRelationshipDropOverlay(ModRelationshipDragPayload payload, bool visible);
    void RaiseRelationshipDropRequested(ModRelationshipDragPayload payload);
}

/// <summary>
/// Coordinates cross-page relationship drags without using WPF's global
/// DataObject drag pipeline. The source list keeps mouse capture, while this
/// coordinator makes registered page overlays visible and hit-tests them by
/// screen coordinates at release time.
/// </summary>
internal static class ModRelationshipDragCoordinator
{
    private static readonly List<WeakReference<IModRelationshipDropTarget>> Targets = [];
    private static ModListPanel? _source;
    private static ModRelationshipDragPayload? _payload;
    private static IReadOnlyList<IModRelationshipDropTarget> _activeTargets = Array.Empty<IModRelationshipDropTarget>();

    public static void RegisterTarget(IModRelationshipDropTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        PruneDeadTargets();
        if (Targets.Any(reference => reference.TryGetTarget(out var current) && ReferenceEquals(current, target)))
            return;
        Targets.Add(new WeakReference<IModRelationshipDropTarget>(target));
    }

    public static void UnregisterTarget(IModRelationshipDropTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        for (var index = Targets.Count - 1; index >= 0; index--)
        {
            if (!Targets[index].TryGetTarget(out var current) || ReferenceEquals(current, target))
                Targets.RemoveAt(index);
        }

        if (_activeTargets.Contains(target) && _payload is { } payload)
            target.SetRelationshipDropOverlay(payload, visible: false);
        _activeTargets = _activeTargets.Where(current => !ReferenceEquals(current, target)).ToArray();
        if (_activeTargets.Count == 0)
            Clear();
    }

    public static bool TryBegin(
        ModListPanel source,
        ModRelationshipDragOperation operation,
        IReadOnlyList<string> selectedKeys,
        string? sourceHostId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selectedKeys);

        // Every new pointer gesture invalidates a possibly interrupted prior
        // session, even when its eventual payload turns out to be unusable.
        CancelActive();
        if (operation == ModRelationshipDragOperation.None || selectedKeys.Count == 0)
            return false;

        // A new pointer gesture must own the singleton session. The previous
        // implementation only cancelled when the previous source happened to
        // be the same list, which could leave another page's overlay visible
        // after an interrupted capture or a rapid page switch.
        var payload = new ModRelationshipDragPayload(
            operation,
            selectedKeys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            string.IsNullOrWhiteSpace(sourceHostId) ? null : sourceHostId.Trim());
        if (payload.SelectedKeys.Count == 0)
            return false;

        var targets = GetLiveTargets()
            .Where(target => !target.ContainsElement(source))
            .Where(target => target.CanAcceptRelationshipDrop(payload))
            .ToArray();
        if (targets.Length == 0)
            return false;

        _source = source;
        _payload = payload;
        _activeTargets = targets;
        foreach (var target in targets)
            target.SetRelationshipDropOverlay(payload, visible: true);
        return true;
    }

    public static bool Complete(ModListPanel source, Point screenPoint)
    {
        if (!ReferenceEquals(_source, source) || _payload is null)
            return false;

        var payload = _payload;
        var target = _activeTargets
            .Where(candidate => candidate.CanAcceptRelationshipDrop(payload))
            .FirstOrDefault(candidate => candidate.ContainsScreenPoint(screenPoint));
        foreach (var activeTarget in _activeTargets)
            activeTarget.SetRelationshipDropOverlay(payload, visible: false);

        if (target is null)
        {
            Clear();
            return false;
        }

        // Clear the session before raising the event. The page may immediately
        // refresh/unload itself as part of the relationship transaction.
        Clear();
        target.RaiseRelationshipDropRequested(payload);
        return true;
    }

    public static void Cancel(ModListPanel source)
    {
        if (!ReferenceEquals(_source, source))
            return;

        CancelActive();
    }

    internal static bool IsVisuallyActive(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement visual
                && (visual.Visibility != Visibility.Visible || visual.Opacity <= 0.01))
                return false;
        }

        return true;
    }

    private static IReadOnlyList<IModRelationshipDropTarget> GetLiveTargets()
    {
        PruneDeadTargets();
        return Targets
            .Select(reference => reference.TryGetTarget(out var target) ? target : null)
            .Where(target => target is not null)
            .Cast<IModRelationshipDropTarget>()
            .ToArray();
    }

    private static void PruneDeadTargets()
    {
        for (var index = Targets.Count - 1; index >= 0; index--)
        {
            if (!Targets[index].TryGetTarget(out _))
                Targets.RemoveAt(index);
        }
    }

    private static void Clear()
    {
        _source = null;
        _payload = null;
        _activeTargets = Array.Empty<IModRelationshipDropTarget>();
    }

    private static void CancelActive()
    {
        if (_payload is { } payload)
        {
            foreach (var target in _activeTargets)
                target.SetRelationshipDropOverlay(payload, visible: false);
        }

        Clear();
    }
}
