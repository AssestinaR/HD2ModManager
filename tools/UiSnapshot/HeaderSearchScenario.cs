using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using HD2ModManager.Controls.Lists;
using HD2ModManager.ViewModels;
using HD2ModManager.Views;

namespace UiSnapshot;

// 标题卡改版后的三种静态状态：标题态、搜索态（空）、搜索态（有标签）。
// 用来确认两件事：卡片高度没变（32 + 22），以及标签条溢出时整行不跟着长高。
internal static class HeaderSearchScenario
{
    internal enum Variant
    {
        Title,
        SearchEmpty,
        SearchTags,
        SearchNarrow
    }

    public static Scenario Build(string name, Variant variant)
    {
        var width = variant == Variant.SearchNarrow ? 560.0 : 900.0;

        // 摘要类事实在快照里只需要"有一个值"，分类筛选则靠 CategoryFilter 自己的键集合。
        var selected = variant == Variant.SearchTags;
        var filters = new ObservableCollection<ModListSearchFilter>
        {
            new("outdated", "过时", () => selected, _ => { }, "只看与当前游戏版本不兼容的 Mod"),
            new("options", "含选项", () => selected, _ => { }, "把作为「选项」的 Mod 也列出来"),
        };

        ModListPanel? panel = null;
        var category = new ModCategoryFilter(_ => null, () => { });

        panel = new ModListPanel
        {
            Width = width,
            Height = 320,
            HeaderTitle = "模组库",
            HeaderSummary = "显示 12 / 33 个 Mod",
            ItemsSource = new List<FakeRow>(),
            SelectionPolicy = ModListSelectionPolicy.Multiple,
            RowActions = ModListRowAction.Rename | ModListRowAction.Details,
            CategoryFilter = category,
            SearchFilters = filters,
        };

        return new Scenario
        {
            Name = name,
            Width = width + 48,
            Height = 360,
            Build = () => new Border
            {
                Background = ScenarioCatalog.Resource("SurfaceCanvas"),
                Padding = new Thickness(12),
                Child = panel
            },
            Prepare = variant == Variant.Title
                ? null
                : _ =>
                {
                    // 设一个有内容的查询就会进搜索态（和真实页面一致：查询非空 ⟺ 搜索框可见）。
                    if (variant != Variant.SearchEmpty) panel.SearchText = "健壮";
                    else panel.SearchText = "健";

                    if (variant != Variant.SearchTags) return;

                    // 分类标签：走真实的多选集合，胶囊上的名字由 CategoryFilter.LabelOf 给。
                    category.Toggle("Armor");
                    category.Toggle("Cape");
                    // 自由词标签：这里直接往投影里加一枚（真实路径是"输入后按空格"）。
                    panel.SearchTags.Add(new ModListSearchTag("t:大衣", "大衣", ModListSearchTagKind.Text, null));
                    panel.SearchTags.Add(new ModListSearchTag("t:尾巴", "尾巴", ModListSearchTagKind.Text, null));
                }
        };
    }
}
