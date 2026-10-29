using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HD2ModManager.Controls.EmptyState;
using HD2ModManager.Controls.Lists;
using HD2ModManager.ViewModels;
using HD2ModManager.Views;

namespace UiSnapshot;

internal static class ModListScenario
{
    internal enum Variant
    {
        Rest,
        Hover,
        Selected,
        Checkbox,
        Empty,
        EmptyState,
        EmptySearch,
        EmptySearchDerived
    }

    public static Scenario Build(string name, Variant variant)
    {
        var rows = variant is Variant.Empty or Variant.EmptyState or Variant.EmptySearch or Variant.EmptySearchDerived
            ? new List<FakeRow>()
            : FakeRow.Sample();
        if (variant == Variant.Selected)
        {
            rows[1].IsSelected = true;
            rows[4].IsSelected = true;
        }

        var profileLike = variant == Variant.Checkbox;
        var panel = new ModListPanel
        {
            HeaderTitle = profileLike ? "配置文件（放在这边的mod才会启用）" : "模组库",
            HeaderSummary = profileLike
                ? "显示 6 / 6 个 Mod · 活动配置：配置文件（放在这边的mod才会启用） · 正在编辑活动配置"
                : variant is Variant.EmptySearch or Variant.EmptySearchDerived ? "显示 0 / 33 个 Mod" : "显示 12 / 33 个 Mod",
            EmptyMessage = "没有可显示的 Mod。",
            ItemsSource = rows,
            SelectionPolicy = ModListSelectionPolicy.Multiple,
            ShowSelectionCheckbox = profileLike,
            RowActions = profileLike
                ? ModListRowAction.Details | ModListRowAction.MoveUp | ModListRowAction.MoveDown |
                  ModListRowAction.RemoveFromProfile | ModListRowAction.DeleteFromLibrary
                : ModListRowAction.Rename | ModListRowAction.Details | ModListRowAction.AddToProfile |
                  ModListRowAction.ToggleDecoration | ModListRowAction.OpenFolder | ModListRowAction.DeleteFromLibrary,
            AllowInternalReorder = profileLike
        };

        if (variant == Variant.EmptyState)
        {
            panel.EmptyState = new EmptyStatePresentation(
                "模组库是空的",
                "把 Mod 压缩包或文件夹拖进窗口，或用右下角菜单里的「导入 Mod」。",
                "导入 Mod",
                new AlwaysCommand(),
                EmptyStateIcon.Inbox);
        }
        else if (variant == Variant.EmptySearch)
        {
            panel.SearchText = "zzz";
            panel.EmptyState = new EmptyStatePresentation(
                "没有匹配的 Mod",
                "试试更短的关键词，或清除搜索回到全部 Mod。",
                "清除搜索",
                new AlwaysCommand(),
                EmptyStateIcon.Search);
        }
        else if (variant == Variant.EmptySearchDerived)
        {
            // 宿主没有给 EmptyState：面板应当自己派生"没有匹配"态，
            // 因为搜索框是它自己的控件，它知道自己正被使用。
            panel.SearchText = "zzz";
        }

        return new Scenario
        {
            Name = name,
            Build = () => Wrap(panel),
            Prepare = variant switch
            {
                Variant.Hover => root => ApplyHover(root, 2),
                _ => null
            }
        };
    }

    // 探针：行内动作从"声明"回到"事件"这条链必须是活的——按钮在 ItemsControl 里，
    // 它的 DataContext 是动作项，行对象只能从容器上取。这里真的点一次按钮，把
    // 事件里拿到的行类型、动作、按钮数写进表头，让截图自己证明。
    public static Scenario BuildRowActionProbe(string name)
    {
        var rows = FakeRow.Sample();
        var panel = new ModListPanel
        {
            HeaderTitle = "行内动作探针",
            HeaderSummary = "PROBE: not run",
            ItemsSource = rows,
            SelectionPolicy = ModListSelectionPolicy.Multiple,
            RowActions = ModListRowAction.Rename | ModListRowAction.Details | ModListRowAction.ArmorAssign |
                         ModListRowAction.AddToProfile |
                         ModListRowAction.ToggleDecoration | ModListRowAction.OpenFolder | ModListRowAction.DeleteFromLibrary,
            // 宿主覆盖文案这条链也要证明：护甲映射页就是靠它把"解除"改成"解除视觉对象"。
            ArmorAssignToolTip = "覆盖文案-加入视觉来源",
        };

        string? received = null;
        panel.RowActionInvoked += (_, e) => received = $"{e.Item?.GetType().Name}/{e.Action}";

        return new Scenario
        {
            Name = name,
            Build = () => Wrap(panel),
            Settle = host =>
            {
                host.Pump(2);
                var actionPanel = VisualTree.All<Border>(host.Content)
                    .FirstOrDefault(border => border.Name == "ActionPanel");
                var buttons = actionPanel is null
                    ? new List<Button>()
                    : VisualTree.All<Button>(actionPanel).ToList();
                buttons.FirstOrDefault()?.RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                host.Pump(2);

                // 第二段：搜索不变式。查询非空 → 搜索框展开且派生出空态；
                // 点一下搜索开关收起 → 查询必须同时被清空，不能留下"看不见的过滤"。
                panel.SearchText = "zzz";
                host.Pump(2);
                var searchBox = VisualTree.Named<TextBox>(host.Content, "HeaderSearchBox");
                var openedOnDemand = searchBox?.Visibility == Visibility.Visible;
                var derivedState = panel.EffectiveEmptyState is EmptyStatePresentation derived
                    ? derived.Title
                    : "null";

                var headerCard = VisualTree.Named<Border>(host.Content, "HeaderCard");
                var toggle = headerCard is null ? null : VisualTree.All<Button>(headerCard).FirstOrDefault();
                toggle?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                host.Pump(2);
                var clearedOnHide = string.IsNullOrEmpty(panel.SearchText)
                    && panel.EffectiveEmptyState is null;

                panel.HeaderTitle = "行内动作探针 · " + string.Join(" | ", buttons.Select(button => button.ToolTip));
                panel.HeaderSummary =
                    $"PROBE: {received ?? "no click received"} · buttons={buttons.Count} " +
                    $"· searchAutoOpen={openedOnDemand} · derived='{derivedState}' · clearedOnHide={clearedOnHide}";
            }
        };
    }

    // 探针：关系拖放整条链。面板自己就是目标，所以"声明参与哪种操作 → 显示叠层 →
    // 命中判定 → 放下事件"可以在离屏环境里逐项断言，不需要真的拖动鼠标。
    public static Scenario BuildRelationshipDropProbe(string name)
    {
        var sourcePanel = new ModListPanel
        {
            HeaderTitle = "源（拖出）",
            HeaderSummary = "RelationshipDragOperation = SetAsOption",
            ItemsSource = FakeRow.Sample(),
            SelectionPolicy = ModListSelectionPolicy.Multiple,
            RelationshipDragOperation = ModRelationshipDragOperation.SetAsOption,
        };

        var targetPanel = new ModListPanel
        {
            HeaderTitle = "目标（拖入）",
            HeaderSummary = "PROBE: not run",
            ItemsSource = FakeRow.Sample(),
            SelectionPolicy = ModListSelectionPolicy.Multiple,
            AcceptsRelationshipDrop = ModRelationshipDragOperation.SetAsOption,
            RelationshipDropHintFactory = payload => new ModListDropHint(
                "探针：释放以设为选项",
                $"共 {payload.SelectedKeys.Count} 项"),
        };

        string? dropped = null;
        targetPanel.RelationshipDropRequested += (_, e) => dropped = $"{e.Operation}/{e.SelectedKeys.Count}";

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(sourcePanel);
        Grid.SetColumn(targetPanel, 1);
        grid.Children.Add(targetPanel);

        return new Scenario
        {
            Name = name,
            Build = () => Wrap(grid),
            Settle = host =>
            {
                host.Pump(2);
                var payload = new ModRelationshipDragPayload(
                    ModRelationshipDragOperation.SetAsOption,
                    ["row-00", "row-01"],
                    null);
                var target = (IModRelationshipDropTarget)targetPanel;

                var accepted = target.CanAcceptRelationshipDrop(payload);
                target.SetRelationshipDropOverlay(payload, true);
                // 叠层是淡入的，离屏渲染必须真的把动画时钟推过去，否则截到的是 Opacity≈0。
                host.PumpFor(260);

                var overlay = VisualTree.All<DropHintOverlay>(targetPanel)
                    .FirstOrDefault(candidate => candidate.Visibility == Visibility.Visible);
                var hint = overlay?.Hint as ModListDropHint;

                var insidePoint = targetPanel.PointToScreen(
                    new Point(targetPanel.ActualWidth / 2, targetPanel.ActualHeight / 2));
                var outsidePoint = sourcePanel.PointToScreen(
                    new Point(sourcePanel.ActualWidth / 2, sourcePanel.ActualHeight / 2));
                var hitsInside = target.ContainsScreenPoint(insidePoint);
                var hitsOutside = target.ContainsScreenPoint(outsidePoint);

                target.RaiseRelationshipDropRequested(payload);

                // 源和目标可以是同一个面板（库页拖自己的行去建关系）。这时必须把自己
                // 排除掉，否则会在自己身上弹叠层、松手还会真的执行。
                var selfExcluded = ((IModRelationshipDropTarget)targetPanel).ContainsElement(targetPanel);

                targetPanel.HeaderTitle = "目标（拖入）· " + (hint?.Title ?? "no hint");
                targetPanel.HeaderSummary =
                    $"PROBE: accepted={accepted} · hitsInside={hitsInside} · hitsOutside={hitsOutside} " +
                    $"· selfExcluded={selfExcluded} · dropped={dropped ?? "none"} · detail='{hint?.Detail}'";
            }
        };
    }

    // Mirrors the workspace: a canvas-colored surface with the standard 12px gap.
    private static FrameworkElement Wrap(FrameworkElement content) => new Border
    {
        Background = ScenarioCatalog.Resource("SurfaceCanvas"),
        Padding = new Thickness(12),
        Child = content
    };

    // The hover state lives in a DataTemplate trigger, which cannot be driven
    // offscreen (IsMouseOver is read-only). Re-apply the same setters here.
    // The row-hover token falls back to the legacy interactive-hover brush so
    // this preview keeps working before/after the token is introduced.
    private static void ApplyHover(FrameworkElement root, int index)
    {
        var rows = new List<Border>();
        foreach (var border in VisualTree.All<Border>(root))
        {
            if (string.Equals(border.Name, "RowBack", StringComparison.Ordinal)) rows.Add(border);
        }

        if (rows.Count <= index) return;
        var row = rows[index];
        row.Background = Lookup("SurfaceRowHover", "SurfaceInteractiveHover");
        row.BorderBrush = Lookup("StrokeRowHover", "StrokeFocus");

        var actionPanel = VisualTree.Named<Border>(row, "ActionPanel");
        if (actionPanel is null) return;
        actionPanel.Opacity = 1;
        actionPanel.IsHitTestVisible = true;
    }

    private static Brush Lookup(string key, string fallback)
        => Application.Current?.TryFindResource(key) as Brush
           ?? Application.Current?.TryFindResource(fallback) as Brush
           ?? Brushes.Magenta;
}

// Minimal stand-in for the real row view models: only the presentation contract
// the shared panel binds to, plus the optional capability flags the template
// checks per row.
internal sealed class FakeRow : IModListSelectable, IModListInternalReorderable, IModListRowActionFilter, INotifyPropertyChanged
{
    private bool _isSelected;

    public required string ListKey { get; init; }
    public string SelectionKey => ListKey;
    public bool IsDecoration { get; init; }
    public required ModListRowPresentation RowPresentation { get; init; }
    public bool CanInternalReorder { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? ImagePath { get; init; }
    public ModListRowAction HiddenActions { get; init; } = ModListRowAction.None;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static List<FakeRow> Sample()
    {
        var images = SampleImages.Ensure(12);
        var rows = new List<FakeRow>();
        var index = 0;

        FakeRow Add(string name, ModListRowPresentation presentation, bool decoration = false, bool reorderable = false, ModListRowAction hidden = ModListRowAction.None)
        {
            var row = new FakeRow
            {
                ListKey = "row-" + index.ToString("00"),
                Name = name,
                ImagePath = images.Count > 0 ? images[index % images.Count] : null,
                IsDecoration = decoration,
                CanInternalReorder = reorderable,
                HiddenActions = hidden,
                RowPresentation = presentation
            };
            index++;
            rows.Add(row);
            return row;
        }

        // 前两行分别演示两种行级抑制：不能加入配置 / 不是可附加对象。
        Add("【4.28】Chiffon-LUNALICE替换RE-2310",
            new ModListRowPresentation(ActivationLabel: "仅存储", ActivationTone: ModListRowTone.Muted,
                Note: "替换 RE-2310 全甲"),
            hidden: ModListRowAction.AddToProfile);

        Add("lunalice基础材质a1主体",
            new ModListRowPresentation(ActivationLabel: "已启用", ActivationTone: ModListRowTone.Success,
                OrderLabel: "配置顺序 6", Summary: "material · texture"),
            reorderable: true);

        Add("Bingus Shared Loader - v15",
            new ModListRowPresentation(ActivationLabel: "仅存储", ActivationTone: ModListRowTone.Muted,
                Summary: "包含 1 个选项", Note: "必须放在列表最底部"),
            hidden: ModListRowAction.ToggleDecoration);

        Add("RE-2310 头盔替换 · 猩红",
            new ModListRowPresentation(ActivationLabel: "仅存储", ActivationTone: ModListRowTone.Muted,
                WarningLabel: "模型过时", WarningTone: ModListRowTone.Danger,
                Summary: "helmet · RE-2310"));

        Add("lunalice基础材质b2服装色黑",
            new ModListRowPresentation(ActivationLabel: "已启用", ActivationTone: ModListRowTone.Success,
                WarningLabel: "缺材质", WarningTone: ModListRowTone.Danger,
                OrderLabel: "配置顺序 5", Summary: "material · texture"));

        Add("Night Vision HUD ReShade",
            new ModListRowPresentation(TypeLabel: "ReShade", TypeTone: ModListRowTone.Accent,
                ActivationLabel: "已启用", ActivationTone: ModListRowTone.Success, Summary: "reshade · postfx"));

        Add("ARES-7 披风包",
            new ModListRowPresentation(TypeLabel: "装饰", TypeTone: ModListRowTone.Muted,
                WarningLabel: "被覆盖", WarningTone: ModListRowTone.Warning,
                Summary: "cape · 3 件"), decoration: true);

        Add("【4.28】Chiffon-LUNALICE替换RE-2310（含头盔 / 披风 / 胸甲三件套）",
            new ModListRowPresentation(ActivationLabel: "仅存储", ActivationTone: ModListRowTone.Muted,
                Summary: "helmet · cape · armor", Note: "体积 428 MB"));

        Add("Steel Trooper 手臂材质",
            new ModListRowPresentation(ActivationLabel: "已启用", ActivationTone: ModListRowTone.Success,
                OrderLabel: "配置顺序 2", Summary: "material"), reorderable: true);

        Add("Minimal UI",
            new ModListRowPresentation(TypeLabel: "ReShade", TypeTone: ModListRowTone.Accent,
                ActivationLabel: "仅存储", ActivationTone: ModListRowTone.Muted, Note: "低配可用"));

        Add("Helmet Merge Test",
            new ModListRowPresentation(ActivationLabel: "已启用", ActivationTone: ModListRowTone.Success,
                WarningLabel: "模型过时", WarningTone: ModListRowTone.Danger,
                OrderLabel: "配置顺序 11"));

        Add("Cape Physics Fix",
            new ModListRowPresentation(ActivationLabel: "仅存储", ActivationTone: ModListRowTone.Muted,
                Summary: "包含 3 个选项"), decoration: true);

        return rows;
    }
}

// Generates distinct 96x96 thumbnails so the snapshot exercises the real image
// pipeline (decode-to-72px) instead of showing the same placeholder 12 times.
internal static class SampleImages
{
    private static readonly string CacheDirectory = Path.Combine(Path.GetTempPath(), "ui-snapshot-assets");

    public static List<string> Ensure(int count)
    {
        var paths = new List<string>();
        Directory.CreateDirectory(CacheDirectory);
        for (var index = 0; index < count; index++)
        {
            var path = Path.Combine(CacheDirectory, $"tile-{index:00}.png");
            paths.Add(path);
            if (File.Exists(path)) continue;
            WriteTile(path, index);
        }
        return paths;
    }

    private static void WriteTile(string path, int index)
    {
        var palette = new (byte R, byte G, byte B)[]
        {
            (0x2B, 0x3A, 0x55), (0x39, 0x4A, 0x63), (0x1E, 0x63, 0xD6), (0x23, 0x7A, 0x3B),
            (0x9A, 0x65, 0x00), (0xB3, 0x26, 0x1E), (0x4B, 0x55, 0x63), (0x62, 0x6B, 0x7A),
            (0x17, 0x57, 0xBF), (0x6D, 0x28, 0xD9), (0x0F, 0x76, 0x6E), (0x8A, 0x4B, 0x08)
        };
        var (r, g, b) = palette[index % palette.Length];

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = new SolidColorBrush(Color.FromRgb(r, g, b));
            background.Freeze();
            dc.DrawRectangle(background, null, new Rect(0, 0, 96, 96));
            var glow = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
            glow.Freeze();
            dc.DrawEllipse(glow, null, new Point(70, 26), 46, 46);
        }

        var bitmap = new RenderTargetBitmap(96, 96, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
