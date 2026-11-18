using System;

namespace HD2ModManager.ViewModels
{
    // 标签的种类。三种在搜索框里长得不一样，因为它们撤销时的含义不一样：
    // - Filter：宿主的开关（过时 / 含选项 / Unit 命中…），撤销 = 那个开关复位；
    // - Category：分类筛选，撤销 = 从多选集合里摘掉；
    // - Text：用户自己敲出来的检索词，撤销 = 从自由标签里删掉。
    public enum ModListSearchTagKind
    {
        Filter,
        Category,
        Text,
    }

    // 作用：搜索框里的一枚标签（只读展示对象）。面板每次状态变化就重建整张表，
    // 所以这里不需要 INotifyPropertyChanged——重建比逐项同步更不容易漂移。
    public sealed class ModListSearchTag
    {
        public ModListSearchTag(string key, string label, ModListSearchTagKind kind, Action? remove)
        {
            Key = key;
            Label = label;
            Kind = kind;
            Remove = remove;
        }

        /// <summary>稳定键："f:过期" / "c:Armor" / "t:大衣"，面板据此复用时不会把同类项认错。</summary>
        public string Key { get; }

        public string Label { get; }

        public ModListSearchTagKind Kind { get; }

        /// <summary>撤销这一枚标签要做什么。null = 由面板自己处理（自由标签）。</summary>
        public Action? Remove { get; }

        public bool IsCategory => Kind == ModListSearchTagKind.Category;

        public bool IsText => Kind == ModListSearchTagKind.Text;

        public string? ToolTip => Kind switch
        {
            ModListSearchTagKind.Filter => $"{Label} · 点击移除",
            ModListSearchTagKind.Category => $"分类：{Label} · 点击移除",
            _ => $"关键词：{Label} · 点击移除",
        };
    }
}
