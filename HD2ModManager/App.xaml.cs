using System.Globalization;
using System.Windows;
using HD2ModManager.Services;

namespace HD2ModManager
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 启动时把设置里的三项 lua 相关项同步到静态字段：部署阶段读静态字段，不读磁盘。
            // 必须**三项都同步**——只同步护甲映射模式的话，物理模式会停在默认的「关闭」，
            // 于是设置页显示"已开启"、部署却不带物理运行时（2026-11-01 实机踩过：
            // 一局下来既没有物理，日志也没被创建）。
            Services.SettingsService.ApplyToLuaDeployment();
            // 建包用的全局几何参数（头部缩放比例）走同一套：Core 的重构建只读静态字段，
            // 启动这一次不同步，用户改过的值不会生效，而日志里看不出任何异常。
            Services.SettingsService.ApplyToVpkBuild();
            ApplyLocalization();
            DispatcherUnhandledException += (_, args) =>
            {
                LogService.Error($"Unhandled UI exception: {args.Exception}");
                MessageBox.Show($"管理器遇到未处理错误，详情已写入 logs：{args.Exception.Message}", "HD2 Mod Manager", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
            // 命令行程序的位置随布局而变（开发构建在管理器旁边、发布包在 bin\ 下）。
            // 启动时记一条：VPK 导入或换甲失败时，日志里能直接看出当时找的是哪个位置。
            var cli = HD2ModCore.Infrastructure.Hd2ModCliLocator.TryResolve();
            LogService.Info(cli is null
                ? $"命令行程序未找到；已查找：{HD2ModCore.Infrastructure.Hd2ModCliLocator.DescribeSearchedLocations()}"
                : $"命令行程序：{cli}");
            UiPerformanceDiagnostics.Start();
            // App.xaml uses StartupUri, so the base implementation creates the
            // first window. Start diagnostics before it so the initial page load
            // and startup synchronization are included in the trace.
            // 采集默认关闭（HD2_UI_DIAG=1 打开），打开后写 logs/trace_*.log。
            base.OnStartup(e);

            // 外观映射的档位在这一版变了（真映射停用，选项改成 关闭 / 独占 boot / 外置加载器兼容）。
            // 如果存档里是旧口径，告诉用户一声——他们的设置含义变了，但不应丢功能。
            // 必须等主窗口 Loaded 之后再弹：OnStartup 阶段 MainWindow 还没显示，
            // AppDialog 拿不到 owner，弹出来会被主窗口盖住。
            if (SettingsService.LastMappingModeWasMigrated)
            {
                var caption = "外观映射设置已转换";
                var text =
                    "游戏这一版改了护甲注册表文件的校验方式：写过的文件一进游戏就会闪退。"
                    + "因此「真映射」这一档已停用，外观映射改为三档：关闭 / 独占 boot / 外置加载器兼容。\n\n"
                    + "你原来的设置已经自动换算到新口径，当前实际生效的是："
                    + HD2ModCore.Domain.ArmorMappingModes.DisplayName(HD2ModCore.Infrastructure.LuaDeploymentService.MappingMode)
                    + "。映射内容没有丢失。\n\n"
                    + "如果这不是你想要的那一档，去设置页的 lua 卡片里改。";
                Dispatcher.InvokeAsync(
                    () => Views.AppDialog.Show(MainWindow, text, caption, MessageBoxButton.OK, MessageBoxImage.Information),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            UiPerformanceDiagnostics.Stop();
            AppUpdateService.Instance?.Dispose();
            SettingsService.FlushAsync().GetAwaiter().GetResult();
            base.OnExit(e);
        }

        private static void ApplyLocalization()
        {
            var lang = HD2ModManager.Services.SettingsService.GetLanguage() ?? System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag;
            var culture = new CultureInfo(lang);
            HD2ModManager.Resources.Strings.Culture = culture;
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(
                    System.Windows.Markup.XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
        }
    }
}
