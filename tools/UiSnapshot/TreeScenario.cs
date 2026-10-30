using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HD2ModManager.Controls.Trees;
using HD2ModManager.Views;

namespace UiSnapshot;

// 扁平化树的快照：行外观用控件自带的默认模板（与原来的"简单树"逐项对齐），
// 验证缩进/竖线/固定行高，以及展开时"子行从父行滑出来"的扑克牌过渡。
internal static class TreeScenario
{
    /// <summary>
    /// 演示节点：实现控件要求的契约，外加默认行模板会读的那几个展示属性。
    /// 属性名与 WardrobeTreeNodeViewModel 一致，所以那份 VM 接进来时不用改模板。
    /// </summary>
    private sealed class DemoNode : ITreeSurfaceNode
    {
        private readonly List<DemoNode> kids = new();
        private int index;

        public DemoNode(string label, string? subtitle = null, string? pieceType = null)
        {
            Label = label;
            Subtitle = subtitle;
            PieceTypeLabel = pieceType;
        }

        public string Label { get; }
        public string? Subtitle { get; }
        public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
        public string? PieceTypeLabel { get; }
        public bool HasPieceTypeLabel => !string.IsNullOrEmpty(PieceTypeLabel);
        public bool IsMixed { get; set; }
        public bool SharedAcrossBodies { get; set; }
        public bool IsChecked { get; set; }
        public bool IsSelected { get; set; }

        public DemoNode Add(params DemoNode[] children)
        {
            kids.AddRange(children);
            return this;
        }

        public void Number(ref int counter)
        {
            index = ++counter;
            foreach (var kid in kids) kid.Number(ref counter);
        }

        public IReadOnlyList<ITreeSurfaceNode> Children => kids;
        public bool IsExpanded { get; set; }
        public int Depth { get; set; }
        public bool IsAlternateRow { get; set; }
        public int ZIndex { get; set; }
        public string ListKey => $"demo-{index}";
    }

    private static DemoNode BuildTree()
    {
        var helmet01 = new DemoNode("helmet_01", "6 section", "unit").Add(
            new DemoNode("section 0"),
            new DemoNode("section 1"),
            new DemoNode("section 2"));
        ((DemoNode)helmet01.Children[0]).IsChecked = true;

        var helmet = new DemoNode("头盔", "slot", "helmet").Add(
            helmet01,
            new DemoNode("helmet_02", "4 section", "unit").Add(
                new DemoNode("section 0"),
                new DemoNode("section 1")));

        var chest01 = new DemoNode("chest_01", "9 section", "unit").Add(
            new DemoNode("section 0"),
            new DemoNode("section 1"));
        chest01.IsMixed = true;

        var chest = new DemoNode("胸部", "slot", "chest").Add(
            chest01,
            new DemoNode("chest_02", "5 section", "unit"));

        var hip = new DemoNode("胯部", "slot", "hip")
            .Add(new DemoNode("hip_01", "7 section", "unit"));

        var sturdy = new DemoNode("健壮", "bodyType").Add(helmet, chest, hip);

        // 两种身形共用的 unit：树里靠锁链标记提示。
        var slenderHelmet = new DemoNode("helmet_01", "6 section", "unit");
        slenderHelmet.SharedAcrossBodies = true;
        var slender = new DemoNode("纤细", "bodyType")
            .Add(new DemoNode("头盔", "slot", "helmet").Add(slenderHelmet));

        var tree = new DemoNode("RE-2310 护甲", "armour").Add(sturdy, slender);
        var counter = 0;
        tree.Number(ref counter);
        return tree;
    }

    private static DemoNode? Find(DemoNode root, string label)
    {
        if (root.Label == label) return root;
        foreach (var kid in root.Children.Cast<DemoNode>())
        {
            if (Find(kid, label) is { } hit) return hit;
        }

        return null;
    }

    /// <summary>建一棵已展开到"头盔/胸部"层的树，并把控件摆进一张卡片里。</summary>
    private static (TreeSurface Surface, DemoNode Root) BuildSurface()
    {
        var root = BuildTree();
        // 胯部故意留着不展开，给展开动画用。
        root.IsExpanded = true;
        var sturdy = (DemoNode)root.Children[0];
        sturdy.IsExpanded = true;
        ((DemoNode)sturdy.Children[0]).IsExpanded = true;
        ((DemoNode)sturdy.Children[1]).IsExpanded = true;
        ((DemoNode)sturdy.Children[0].Children[0]).IsExpanded = true;
        ((DemoNode)root.Children[1]).IsExpanded = true;

        var surface = new TreeSurface
        {
            Roots = new[] { root },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        return (surface, root);
    }

    private static FrameworkElement Frame(TreeSurface surface)
    {
        var card = new Border
        {
            Background = ScenarioCatalog.Resource("SurfaceCard"),
            BorderBrush = ScenarioCatalog.Resource("StrokeSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            Child = surface,
        };

        return new Border
        {
            Background = ScenarioCatalog.Resource("SurfaceCanvas"),
            Padding = new Thickness(16),
            Child = card,
        };
    }

    public static Scenario Build(string name)
    {
        var (surface, _) = BuildSurface();
        return new Scenario
        {
            Name = name,
            Width = 620,
            Height = 520,
            Build = () => Frame(surface),
        };
    }

    /// <summary>展开一个节点，逐帧记录位移收敛情况。</summary>
    public static Scenario BuildFan(string name)
    {
        var (surface, root) = BuildSurface();
        return new Scenario
        {
            Name = name,
            Width = 620,
            Height = 520,
            Build = () => Frame(surface),
            Settle = host =>
            {
                // 首批布局 + Loaded 优先级上排队的工作各需要一轮。
                host.PumpFor(40);
                host.PumpFor(40);

                var target = Find(root, "胯部");
                if (target is null) return;
                surface.Toggle(target);

                // 逐帧看位移怎么收敛：第一帧应该还贴着父行，最后一帧归零。
                for (var frame = 0; frame < 8; frame++)
                {
                    host.PumpFor(1);
                    Dump($"fan{frame}", host.Content);
                }
            },
        };
    }

    /// <summary>展开并让动画走完：验证落位后的行距与竖线。</summary>
    public static Scenario BuildFanSettled(string name)
    {
        var (surface, root) = BuildSurface();
        return new Scenario
        {
            Name = name,
            Width = 620,
            Height = 520,
            Build = () => Frame(surface),
            Settle = host =>
            {
                host.PumpFor(40);
                host.PumpFor(40);
                var target = Find(root, "胯部");
                if (target is null) return;
                surface.Toggle(target);
                host.PumpFor(40);
                host.PumpFor(400);
            },
        };
    }

    /// <summary>把每一行的不透明度与位移打出来，用来核对"从父行出发"是否真的发生了。</summary>
    private static void Dump(string tag, DependencyObject root)
    {
        foreach (var presenter in VisualTree.All<ContentPresenter>(root))
        {
            if (presenter.DataContext is not DemoNode node) continue;
            var delta = (presenter.RenderTransform as TranslateTransform)?.Y ?? 0d;
            Console.WriteLine(
                $"[{tag}] {node.Label,-12} depth={node.Depth} opacity={presenter.Opacity:0.00} dy={delta,7:0.0}");
        }
    }
}
