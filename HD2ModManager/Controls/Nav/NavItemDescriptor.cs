using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace HD2ModManager.Controls.Nav;

// 顶栏按钮的语义角色。逻辑层只说"这是个什么按钮"，
// 外观、间距、图标、激活态全部由控件层决定。
public enum NavItemRole
{
    Primary,
    Icon,
}

// 图标用枚举而不是字形：逻辑层写不出"一个 emoji"，
// 控件层负责把枚举映射成矢量 Path。
public enum NavIconKey
{
    None,
    Home,
    Profile,
    Library,
    Settings,
    ArmorRegistry,
}

// 逻辑层递交的一个顶栏按钮。
public sealed class NavItemDescriptor : INotifyPropertyChanged
{
    private string _label;
    private bool _isVisible = true;
    private bool _isActive;
    private string? _badge;
    private ICommand? _command;

    public NavItemDescriptor(string id, string label, NavIconKey iconKey, ICommand? command = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        _label = label;
        IconKey = iconKey;
        _command = command;
    }

    public string Id { get; }

    public NavIconKey IconKey { get; }

    public NavItemRole Role { get; init; } = NavItemRole.Primary;

    public string Label { get => _label; set => Set(ref _label, value); }

    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    // 角标文本（如未读消息数）。null/空 = 不显示角标。
    public string? Badge { get => _badge; set => Set(ref _badge, value); }

    public ICommand? Command { get => _command; set => Set(ref _command, value); }

    public bool HasBadge => !string.IsNullOrEmpty(_badge);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(Badge)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasBadge)));
    }
}

// 按 Role 选模板（NavItem.Primary / NavItem.Icon），与卡片宿主同一套约定。
// 找不到对应模板时回落到 Primary，避免新增角色时静默变成空白按钮。
public sealed class NavItemTemplateSelector : DataTemplateSelector
{
    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not NavItemDescriptor descriptor || container is not FrameworkElement element) return null;
        return element.TryFindResource("NavItem." + descriptor.Role) as DataTemplate
               ?? element.TryFindResource("NavItem.Primary") as DataTemplate;
    }
}

// NavIconKey → 应用级 Geometry 资源（NavIcon.Home 等）。
// 图标几何集中放在 Themes/Controls.xaml，模板里只写一次绑定，
// 激活态/非激活态两层共用同一段几何。
public sealed class NavIconGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not NavIconKey key || key == NavIconKey.None) return null;
        return System.Windows.Application.Current?.TryFindResource("NavIcon." + key) as Geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
