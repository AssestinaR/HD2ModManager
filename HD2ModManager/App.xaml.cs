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
