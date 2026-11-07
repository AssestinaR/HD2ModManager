using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using HD2ModManager.ViewModels;
using HD2ModManager.Views;

namespace UiSnapshot;

internal sealed class Scenario
{
    public required string Name { get; init; }
    public required Func<FrameworkElement> Build { get; init; }
    public Action<FrameworkElement>? Prepare { get; init; }
    // 在布局稳定之后、截图之前执行；可以驱动动画并 PumpFor 到指定时刻。
    public Action<RenderHost>? Settle { get; init; }
    public double Width { get; init; } = 1200;
    public double Height { get; init; } = 720;
    public Brush Background { get; init; } = Brushes.White;
}

internal static class ScenarioCatalog
{
    private static readonly Dictionary<string, Func<Scenario>> Factories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["modlist"] = () => ModListScenario.Build("modlist", ModListScenario.Variant.Rest),
        ["modlist-hover"] = () => ModListScenario.Build("modlist-hover", ModListScenario.Variant.Hover),
        ["modlist-selected"] = () => ModListScenario.Build("modlist-selected", ModListScenario.Variant.Selected),
        ["modlist-checkbox"] = () => ModListScenario.Build("modlist-checkbox", ModListScenario.Variant.Checkbox),
        ["modlist-empty"] = () => ModListScenario.Build("modlist-empty", ModListScenario.Variant.Empty),
        ["modlist-empty-state"] = () => ModListScenario.Build("modlist-empty-state", ModListScenario.Variant.EmptyState),
        ["modlist-empty-search"] = () => ModListScenario.Build("modlist-empty-search", ModListScenario.Variant.EmptySearch),
        ["modlist-empty-search-derived"] = () => ModListScenario.Build("modlist-empty-search-derived", ModListScenario.Variant.EmptySearchDerived),
        ["rowaction-probe"] = () => ModListScenario.BuildRowActionProbe("rowaction-probe"),
        ["relationship-drop-probe"] = () => ModListScenario.BuildRelationshipDropProbe("relationship-drop-probe"),
        ["cards"] = () => CardScenario.Build("cards", midInsert: false),
        ["cards-insert"] = () => CardScenario.Build("cards-insert", midInsert: true),
        ["cards-hide"] = () => CardScenario.BuildHide("cards-hide"),
        ["cards-swap"] = () => CardScenario.BuildSwap("cards-swap"),
        ["moddetails"] = () => ModDetailsScenario.Build("moddetails", vpk: false),
        ["moddetails-vpk"] = () => ModDetailsScenario.Build("moddetails-vpk", vpk: true),
        ["nav"] = () => NavScenario.Build("nav", 1200),
        ["nav-narrow"] = () => NavScenario.Build("nav-narrow", 520),
        ["nav-transition"] = () => NavScenario.BuildTransition("nav-transition"),
        ["wheel-probe"] = () => WheelProbeScenario.Build("wheel-probe"),
        ["wheel-probe-fixed"] = () => WheelProbeScenario.Build("wheel-probe-fixed", wheelScrollEnabled: true),
        ["scroll-step-probe"] = () => ScrollStepProbeScenario.Build("scroll-step-probe"),
        ["empty-mock"] = () => EmptyStateScenario.Build("empty-mock"),
        ["capsule-mock"] = () => CapsuleMockScenario.Build("capsule-mock"),
        ["cluster"] = () => ClusterScenario.Build("cluster", expanded: false),
        ["cluster-expanded"] = () => ClusterScenario.Build("cluster-expanded", expanded: true),
        ["cluster-many"] = () => ClusterScenario.Build("cluster-many", expanded: true, many: true),
        ["cluster-many-focus"] = () => ClusterScenario.Build("cluster-many-focus", expanded: true, many: true, focus: 3),
        ["cluster-all-labels"] = () => ClusterScenario.BuildAllLabels("cluster-all-labels", many: false),
        ["cluster-all-labels-by-click"] = () => ClusterScenario.BuildAllLabelsByClick("cluster-all-labels-by-click"),
        ["cluster-all-labels-many"] = () => ClusterScenario.BuildAllLabels("cluster-all-labels-many", many: true),
        ["cluster-labels"] = () => ClusterScenario.BuildLabelLengths("cluster-labels", "生成替换"),
        ["cluster-labels-max"] = () => ClusterScenario.BuildLabelLengths("cluster-labels-max", "批量生成"),
        ["app-update"] = () => AppUpdateScenario.Build("app-update", AppUpdateScenario.Mode.Available),
        ["app-update-ready"] = () => AppUpdateScenario.Build("app-update-ready", AppUpdateScenario.Mode.Ready),
        ["app-update-latest"] = () => AppUpdateScenario.Build("app-update-latest", AppUpdateScenario.Mode.Latest),
        ["app-update-dev"] = () => AppUpdateScenario.Build("app-update-dev", AppUpdateScenario.Mode.Dev),
        ["app-update-error"] = () => AppUpdateScenario.Build("app-update-error", AppUpdateScenario.Mode.Error),
        ["tree"] = () => TreeScenario.Build("tree"),
        ["tree-selected"] = () => TreeScenario.BuildSelected("tree-selected"),
        ["tree-keys"] = () => TreeScenario.BuildKeys("tree-keys"),
        ["tree-fan"] = () => TreeScenario.BuildFan("tree-fan"),
        ["tree-fan-settled"] = () => TreeScenario.BuildFanSettled("tree-fan-settled"),
    };

    public static IEnumerable<string> Names => Factories.Keys;

    public static Scenario Resolve(string name)
        => Factories.TryGetValue(name, out var factory)
            ? factory()
            : throw new ArgumentException($"Unknown scenario '{name}'. Run 'UiSnapshot list' to see the available names.");

    public static Brush Resource(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Magenta;
}
