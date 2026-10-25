using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HD2ModManager.Controls.Cards;

namespace UiSnapshot;

internal static class CardScenario
{
    public static Scenario Build(string name, bool midInsert)
    {
        // 故意乱序加入，用来验证宿主按 Order 排序。
        var cards = new ObservableCollection<CardDescriptor>
        {
            new("status", CardKind.KeyValueList, new[]
            {
                new CardKeyValue("部署模式", "软链接"),
                new CardKeyValue("已启用 Mod", "6"),
                new CardKeyValue("GameData", "索引过期"),
                new CardKeyValue("在线资产", "24 小时前检查"),
            })
            {
                Title = "当前状态",
                Order = 30
            },

            new("help", CardKind.Text,
                "导入的 Mod 会按目录结构生成对象树；每一个目录节点都是一个可管理的对象。" +
                "这个卡片只有外壳和标题，内容由 CardTemplate.Text 提供。")
            {
                Title = "说明",
                Order = 10
            },

            new("notice", CardKind.Notice, "游戏已更新，建议重建 GameData 索引。")
            {
                Order = 20,
                Role = CardRole.Notice
            },

            new("long", CardKind.Text, LongText())
            {
                Title = "受限高度（内部滚动）",
                Order = 40,
                MaxHeight = 140,
                PreferredHeight = 140,
                Overflow = CardOverflow.Scroll
            },

            new("missing", CardKind.ModHeader, null)
            {
                Title = "尚未提供模板的卡片",
                Order = 50
            },

            new("hidden", CardKind.Text, "这张卡 IsVisible=false，不应出现在画面里。")
            {
                Title = "隐藏卡",
                Order = 60,
                IsVisible = false
            }
        };

        var host = new CardStackHost { Cards = cards };

        return new Scenario
        {
            Name = name,
            Width = 1100,
            Height = 780,
            Build = () => new Border
            {
                Background = ScenarioCatalog.Resource("SurfaceCanvas"),
                Padding = new Thickness(12),
                Child = host
            },
            Settle = midInsert
                ? renderHost =>
                {
                    cards.Add(new CardDescriptor("inserted", CardKind.Notice, "这张卡是刚刚插入的，应当处于淡入中。")
                    {
                        Order = 15
                    });

                    // 按状态采样：等到真的出现"半透明"的卡片再截帧，
                    // 因为离屏环境的动画时钟推进速度与墙钟并不一致。
                    for (var attempt = 0; attempt < 60; attempt++)
                    {
                        renderHost.PumpFor(8);
                        if (AnyContainerFading(renderHost.Content, 0.15, 0.85)) break;
                    }
                }
                : null
        };
    }

    // 隐藏一张卡片、同一时刻让另一张出现：验证"上一轮还在播时到达的新增"不会丢掉淡入。
    public static Scenario BuildSwap(string name)
    {
        var scenario = Build(name, midInsert: false);
        return new Scenario
        {
            Name = scenario.Name,
            Width = scenario.Width,
            Height = scenario.Height,
            Build = scenario.Build,
            Settle = renderHost =>
            {
                var host = FindCardHost(renderHost.Content);
                if (host?.Cards is null) return;

                var all = host.Cards.OfType<CardDescriptor>().ToList();
                var hide = all.FirstOrDefault(c => c.Id == "help");
                var show = all.FirstOrDefault(c => c.Id == "hidden");
                if (hide is null || show is null) return;

                hide.IsVisible = false;
                renderHost.PumpFor(16);
                Console.WriteLine("[cards-swap] while playing: " + host.AnimationDiagnostics);

                // 这一下会落在上一轮动画的播放期内，只应该记成"待办新增"。
                show.IsVisible = true;
                Console.WriteLine("[cards-swap] queued show: " + host.AnimationDiagnostics);

                for (var attempt = 0; attempt < 80; attempt++)
                {
                    renderHost.PumpFor(8);
                    if (FindFadingContainerFor(renderHost.Content, "hidden", 0.15, 0.85) is not null) break;
                }

                Console.WriteLine("[cards-swap] sampled: " + host.AnimationDiagnostics);
            }
        };
    }

    private static ContentPresenter? FindFadingContainerFor(DependencyObject root, string id, double min, double max)
    {
        if (root is ContentPresenter presenter
            && presenter.DataContext is CardDescriptor card
            && card.Id == id
            && presenter.Opacity > min
            && presenter.Opacity < max)
        {
            return presenter;
        }

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            if (FindFadingContainerFor(System.Windows.Media.VisualTreeHelper.GetChild(root, index), id, min, max) is { } found)
                return found;
        }
        return null;
    }

    private static bool AnyContainerFading(DependencyObject root, double min, double max)
    {
        if (root is ContentPresenter presenter && presenter.Opacity > min && presenter.Opacity < max)
            return true;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            if (AnyContainerFading(System.Windows.Media.VisualTreeHelper.GetChild(root, index), min, max))
                return true;
        }
        return false;
    }

    // 隐藏一张中间卡片：验证"幽灵副本淡出 + 下方卡片合拢"。
    public static Scenario BuildHide(string name)
    {
        var scenario = Build(name, midInsert: false);
        return new Scenario
        {
            Name = scenario.Name,
            Width = scenario.Width,
            Height = scenario.Height,
            Build = scenario.Build,
            Settle = renderHost =>
            {
                var host = FindCardHost(renderHost.Content);
                if (host?.Cards is null) return;

                var overlay = FindByName<Canvas>(renderHost.Content, "TransitionOverlay");
                Console.WriteLine("[cards-hide] before: " + host.AnimationDiagnostics);

                foreach (var card in host.Cards.OfType<CardDescriptor>())
                {
                    if (card.Id != "help") continue;
                    card.IsVisible = false;
                    break;
                }

                Console.WriteLine("[cards-hide] after set: " + host.AnimationDiagnostics);

                // 同样按状态采样：等到幽灵副本进入"半透明"区间再截帧。
                var start = Environment.TickCount64;
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    renderHost.PumpFor(8);
                    if (overlay is not null && overlay.Children.OfType<UIElement>().Any(g => g.Opacity > 0.15 && g.Opacity < 0.85))
                        break;
                }

                Console.WriteLine("[cards-hide] sampled after " + (Environment.TickCount64 - start) + "ms: " + host.AnimationDiagnostics);
                Console.WriteLine("[cards-hide] overlay children = " + (overlay?.Children.Count ?? -1));
            }
        };
    }

    private static T? FindByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T match && match.Name == name) return match;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (FindByName<T>(child, name) is { } found) return found;
        }
        return null;
    }

    private static CardStackHost? FindCardHost(DependencyObject root)
    {
        if (root is CardStackHost host) return host;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (FindCardHost(child) is { } found) return found;
        }
        return null;
    }

    private static string LongText()
        => string.Join(Environment.NewLine, new[]
        {
            "第一段：这张卡片声明了 MaxHeight=140 与 PreferredHeight=140，超出部分应在卡片内部滚动，而不是把整页撑高。",
            "第二段：高度动画在 PreferredHeight 变化时触发；这里展示的是稳定状态。",
            "第三段：滚动条由 CardOverflow.Scroll 决定，Clip 与 Grow 会关掉滚动。",
            "第四段：用于确认 ScrollViewer 的 MaxHeight 绑定没有把外壳一起裁掉。",
            "第五段：内容再多也不应改变卡片外框的圆角与描边。",
            "第六段：结束。"
        });
}
