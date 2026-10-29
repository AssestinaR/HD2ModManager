using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HD2ModManager.Controls.Actions;
using HD2ModManager.ViewModels;
using HD2ModManager.Views;

namespace UiSnapshot;

// 浮动操作簇（汉堡菜单）的快照。故意把列表垫在下面，好看出它盖住了什么。
internal static class ClusterScenario
{
    private static ObservableCollection<PageActionViewModel> ProfileActions() =>
    [
        new(PageActionKind.CreateProfile, "新建配置", new AlwaysCommand(), tone: PageActionTone.Primary, order: 10),
        new(PageActionKind.SwitchProfile, "切换当前配置", new AlwaysCommand(), order: 12),
        new(PageActionKind.ActivateProfile, "设为活动配置", new AlwaysCommand(), tone: PageActionTone.Primary, order: 15),
        new(PageActionKind.DeactivateProfile, "停用活动配置", new AlwaysCommand(), order: 16),
        new(PageActionKind.RenameProfile, "重命名配置", new AlwaysCommand(), order: 20),
        new(PageActionKind.RemoveProfile, "删除当前配置", new AlwaysCommand(), tone: PageActionTone.Danger, order: 30),
    ];

    // 14 个动作：用来证明"列高自适应 + 挤压"能装下详情页那种规模。
    private static ObservableCollection<PageActionViewModel> ManyActions() =>
    [
        new(PageActionKind.OpenFolder, "打开文件夹", new AlwaysCommand(), order: 10),
        new(PageActionKind.RenameProfile, "修改名称", new AlwaysCommand(), order: 11),
        new(PageActionKind.RefreshProfile, "修改图像", new AlwaysCommand(), order: 12),
        new(PageActionKind.Export, "修改备注", new AlwaysCommand(), order: 13),
        new(PageActionKind.ValidateLibrary, "详情表格", new AlwaysCommand(), order: 20),
        new(PageActionKind.Refresh, "修复 patch", new AlwaysCommand(), tone: PageActionTone.Primary, order: 30),
        new(PageActionKind.CreateLogicalMod, "生成替换其他护甲的模组", new AlwaysCommand(), order: 31),
        new(PageActionKind.OpenFolder, "规整护甲 patch 包", new AlwaysCommand(), order: 32),
        new(PageActionKind.Export, "Patch 资源重打包", new AlwaysCommand(), order: 33),
        new(PageActionKind.CreateLogicalMod, "批量生成装饰", new AlwaysCommand(), order: 40),
        new(PageActionKind.CreateLogicalMod, "生成装饰 Mod", new AlwaysCommand(), order: 41),
        new(PageActionKind.SwitchProfile, "头盔合并到盔甲", new AlwaysCommand(), order: 50),
        new(PageActionKind.DeactivateProfile, "关闭头盔合并", new AlwaysCommand(), order: 51),
        new(PageActionKind.RemoveProfile, "删除当前配置", new AlwaysCommand(), tone: PageActionTone.Danger, order: 90),
    ];

    // 抽屉宽度自适应：短文案停在最短宽度，中等文案按文字长，超长文案被 3 倍上限截断。
    private static ObservableCollection<PageActionViewModel> LabelLengthActions() =>
    [
        new(PageActionKind.OpenFolder, "打开文件夹", new AlwaysCommand(), order: 10),
        new(PageActionKind.CrossArmor, "生成替换其他护甲的模组", new AlwaysCommand(), order: 20),
        new(PageActionKind.BatchDecoration, "批量生成装饰（以主体 Mod）并写入活动配置的派生目录，同时刷新资产标签与兼容性报告", new AlwaysCommand(), order: 30),
    ];

    public static Scenario BuildLabelLengths(string name, string focusLabelPrefix = "生成替换")
    {
        var list = new ModListPanel
        {
            HeaderTitle = "抽屉宽度自适应",
            HeaderSummary = "最短 168 · 按文字 · 最长 504",
            ItemsSource = FakeRow.Sample(),
            SelectionPolicy = ModListSelectionPolicy.Multiple,
        };

        var cluster = new FloatingActionCluster
        {
            Actions = LabelLengthActions(),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(18),
        };

        var grid = new Grid();
        grid.Children.Add(list);
        grid.Children.Add(cluster);

        return new Scenario
        {
            Name = name,
            Build = () => new Border
            {
                Background = ScenarioCatalog.Resource("SurfaceCanvas"),
                Padding = new Thickness(12),
                Child = grid,
            },
            Settle = host =>
            {
                cluster.IsExpanded = true;
                host.PumpFor(60);
                host.PumpFor(420);

                // 按文案找目标按钮（视觉树顺序不等于数据顺序），聚焦它把对应长度的抽屉拍出来。
                VisualTree.All<Button>(cluster)
                    .FirstOrDefault(button => button.DataContext is PageActionViewModel action
                        && action.Label.StartsWith(focusLabelPrefix, StringComparison.Ordinal))
                    ?.Focus();
                host.PumpFor(300);
            },
        };
    }

    public static Scenario Build(string name, bool expanded, bool many = false, int focus = -1)
    {
        var actions = many ? ManyActions() : ProfileActions();

        var list = new ModListPanel
        {
            HeaderTitle = "配置文件",
            HeaderSummary = many ? "14 个动作 · 挤压到一列" : "显示 12 / 12 个 Mod",
            ItemsSource = FakeRow.Sample(),
            SelectionPolicy = ModListSelectionPolicy.Multiple,
        };

        var cluster = new FloatingActionCluster
        {
            Actions = actions,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(18),
        };

        var grid = new Grid();
        grid.Children.Add(list);
        grid.Children.Add(cluster);

        return new Scenario
        {
            Name = name,
            Build = () => new Border
            {
                Background = ScenarioCatalog.Resource("SurfaceCanvas"),
                Padding = new Thickness(12),
                Child = grid,
            },
            Settle = expanded
                ? host =>
                {
                    cluster.IsExpanded = true;
                    // 簇把"启动布局"排在 DispatcherPriority.Loaded，而 PumpFor 内部按
                    // Render 优先级泵（Loaded 比 Render 低），所以要分两轮泵。
                    host.PumpFor(60);
                    host.PumpFor(420);

                    if (focus >= 0)
                    {
                        // 用键盘焦点触发"放大 + 挤压"，这样焦点态在离屏环境也可复现。
                        var buttons = VisualTree.All<Button>(cluster).ToList();
                        if (focus < buttons.Count) buttons[focus].Focus();
                        host.PumpFor(60);
                        host.PumpFor(260);
                    }
                }
                : null,
        };
    }
}
