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
