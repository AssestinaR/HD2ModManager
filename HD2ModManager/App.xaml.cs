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
            // Do not auto-compile a temporary diagnostic protocol or overwrite
            // user state while a recovery journal is pending.
            var recovery = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "data", "diagnostics", "armor", "active.json");
            if (System.IO.File.Exists(recovery))
            {
                try
                {
                    if (new System.IO.FileInfo(recovery).Length > 256 * 1024)
                        throw new System.IO.InvalidDataException();
                    using var journal = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(recovery));
                    if (!journal.RootElement.TryGetProperty("completed", out var completed) || !completed.GetBoolean())
                    {
                        MessageBox.Show("检测到尚未恢复的护甲诊断。请先完全退出游戏，再双击管理器目录中的 Armor-Diagnostics.cmd，按提示恢复原配置。\n\n为避免覆盖诊断状态，本次不启动管理器。",
                            "护甲诊断待恢复", MessageBoxButton.OK, MessageBoxImage.Warning);
                        Shutdown();
                        return;
                    }
                }
                catch
                {
                    MessageBox.Show("护甲诊断恢复记录无法读取。为避免覆盖原配置，本次停止启动。请保留 data/diagnostics/armor 目录并联系支持人员。",
                        "护甲诊断恢复记录异常", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Shutdown();
                    return;
                }
            }
            ApplyLocalization();
            DispatcherUnhandledException += (_, args) =>
            {
                LogService.Error($"Unhandled UI exception: {args.Exception}");
                MessageBox.Show($"管理器遇到未处理错误，详情已写入 logs：{args.Exception.Message}", "HD2 Mod Manager", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
            UiPerformanceDiagnostics.Start();
            // App.xaml uses StartupUri, so the base implementation creates the
            // first window. Start diagnostics before it so the initial page load
            // and startup synchronization are included in the trace.
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            UiPerformanceDiagnostics.Stop();
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
