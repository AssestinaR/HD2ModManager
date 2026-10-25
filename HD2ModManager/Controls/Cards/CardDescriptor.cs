using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HD2ModManager.Controls.Cards;

// 卡片外壳的语义角色。逻辑层只声明"这是一张什么性质的卡"，
// 具体背景、描边、阴影、圆角全部由控件层决定。
public enum CardRole
{
    Static,
    Interactive,
    Elevated,
    Notice,
    Danger,
}

// 内容超出高度上限时怎么办。
public enum CardOverflow
{
    // 卡片内部滚动（默认，最不容易丢内容）
    Scroll,
    // 直接裁切
    Clip,
    // 不限制高度，让卡片长高
    Grow,
}

// 逻辑层递交的一张卡片。除了 Id/Kind 之外全部可变，
// 宿主监听 PropertyChanged 并据此做动画。
public sealed class CardDescriptor : INotifyPropertyChanged
{
    private string? _title;
    private object? _data;
    private object? _headerContent;
    private int _order;
    private bool _isVisible = true;
    private double _minHeight;
    private double _maxHeight = double.PositiveInfinity;
    private double? _preferredHeight;
    private CardOverflow _overflow = CardOverflow.Scroll;
    private CardRole _role = CardRole.Static;

    public CardDescriptor(string id, CardKind kind, object? data = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        Kind = kind;
        Data = data;
    }

    public string Id { get; }

    public CardKind Kind { get; }

    // 标题行。为 null 时整行不渲染（不是留白）。
    public string? Title { get => _title; set => Set(ref _title, value); }

    // 标题行右侧的动作区（例如"应用"按钮）。控件层只负责摆放。
    public object? HeaderContent { get => _headerContent; set => Set(ref _headerContent, value); }

    // 卡片内容。模板自己绑定，宿主不解释它。
    public object? Data { get => _data; set => Set(ref _data, value); }

    public int Order { get => _order; set => Set(ref _order, value); }

    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

    public double MinHeight { get => _minHeight; set => Set(ref _minHeight, value); }

    public double MaxHeight { get => _maxHeight; set => Set(ref _maxHeight, value); }

    // 为 null 表示高度自适应；有值时宿主会动画到这个高度。
    public double? PreferredHeight { get => _preferredHeight; set => Set(ref _preferredHeight, value); }

    public CardOverflow Overflow { get => _overflow; set => Set(ref _overflow, value); }

    public CardRole Role { get => _role; set => Set(ref _role, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

// 供 CardKind.KeyValueList 使用的最小行模型。
public sealed record CardKeyValue(string Label, string Value);

// 标题行右侧的动作（例如"应用"）。逻辑层只给文案与命令，
// 按钮长什么样由控件层的隐式模板决定。
public sealed record CardHeaderAction(string Text, System.Windows.Input.ICommand Command);
