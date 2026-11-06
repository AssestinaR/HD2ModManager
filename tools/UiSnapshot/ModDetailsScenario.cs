using System.Windows;
using System.Linq;
using System.Windows.Controls;
using HD2ModManager.Views;

namespace UiSnapshot;

internal static class ModDetailsScenario
{
    public static Scenario Build(string name, bool vpk, bool materials = false)
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
            Height = materials ? 1980 : 1180,
            // 材质卡在页面最底部：先滚到底再拍，否则只能看到卡片标题。
            Settle = materials
                ? host =>
                {
                    var scroller = VisualTree.All<ScrollViewer>(host.Content).FirstOrDefault();
                    scroller?.ScrollToEnd();
                    host.PumpFor(60);
                    host.PumpFor(200);
                }
                : null,
            Build = () => new Border
            {
                Background = ScenarioCatalog.Resource("SurfaceCanvas"),
                Padding = new Thickness(12),
                Child = page
            }
        };
    }
}
