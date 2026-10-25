using System.Windows;
using System.Windows.Controls;
using HD2ModManager.Views;

namespace UiSnapshot;

internal static class ModDetailsScenario
{
    public static Scenario Build(string name, bool vpk)
    {
        var stub = new ModDetailsStub
        {
            IsVpkMod = vpk,
            ShowVpkComponentCard = vpk,
            ShowPatchSettingsCard = !vpk,
            ShowUserStatusCard = true,
            ShowOutdatedCompatibility = true,
            IsModelOutdated = false,
            IsDecoration = false,
            CanShowDecorationPlan = !vpk,
            HasVpkComponentStatus = vpk
        };
        if (vpk) stub.VpkComponentStatus = "已读取 12 个 section，其中 3 个被关闭。";
        else stub.SelectedVpkTargetArmor = stub.VpkTargetArmors[0];

        var page = new ModDetailsPageView { DataContext = stub };

        return new Scenario
        {
            Name = name,
            Width = 1200,
            Height = 1180,
            Build = () => new Border
            {
                Background = ScenarioCatalog.Resource("SurfaceCanvas"),
                Padding = new Thickness(12),
                Child = page
            }
        };
    }
}
