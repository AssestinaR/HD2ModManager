using System.Collections.ObjectModel;
using System.ComponentModel;
using HD2ModManager.Controls.Nav;

namespace HD2ModManager.ViewModels;

// 作用：把顶栏按钮变成"声明"。
//
// 这里只回答"有哪些按钮、顺序、可见性、哪个是激活的、有没有角标"，
// 不回答"长什么样、放在哪、怎么动"——那些在 NavBarHost 里。
// 新增一个顶栏入口 = 这里加一行 + 一个图标枚举值，不需要动 MainWindow.xaml。
public partial class ShellViewModel
{
    private ObservableCollection<NavItemDescriptor>? _leadingNavItems;
    private ObservableCollection<NavItemDescriptor>? _trailingNavItems;

    public ObservableCollection<NavItemDescriptor> LeadingNavItems
    {
        get
        {
            EnsureNav();
            return _leadingNavItems!;
        }
    }

    public ObservableCollection<NavItemDescriptor> TrailingNavItems
    {
        get
        {
            EnsureNav();
            return _trailingNavItems!;
        }
    }

    private void EnsureNav()
    {
        if (_leadingNavItems is not null) return;

        _leadingNavItems =
        [
            new NavItemDescriptor("nav.home", "首页", NavIconKey.Home, ShowHomeCommand)
        ];

        _trailingNavItems =
        [
            new NavItemDescriptor("nav.armor", "护甲映射", NavIconKey.ArmorRegistry, ShowArmorRegistryCommand),
            new NavItemDescriptor("nav.settings", "设置", NavIconKey.Settings, ShowSettingsCommand)
        ];

        PropertyChanged += OnNavStateChanged;
        SyncNav();
    }

    private void OnNavStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsHomeActive)
            or nameof(IsArmorRegistryActive)
            or nameof(IsSettingsActive))
        {
            SyncNav();
        }
    }

    private void SyncNav()
    {
        if (_leadingNavItems is null || _trailingNavItems is null) return;

        _leadingNavItems[0].IsActive = IsHomeActive;
        _trailingNavItems[0].IsActive = IsArmorRegistryActive;
        _trailingNavItems[1].IsActive = IsSettingsActive;
    }
}
