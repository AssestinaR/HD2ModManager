using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using HD2ModCore.Updates;

namespace HD2ModManager.Services
{
    // 作用：管理器自身更新的界面侧宿主：用程序目录与版本号创建 AppUpdater，把变化切回 UI 线程，
    // 在发现新版本、以及上次更新应用成功/失败时给出通知；安装时负责让窗口走正常的「保存后关闭」流程。
    // Purpose: UI-side host of the self-updater. Creates it from the app directory and version, marshals changes to the
    // UI thread, announces new versions and the outcome of the last applied update, and closes the main window
    // through its normal save-then-close path when an update is installed.
    /// <summary>设置页卡片依赖的最小接口：更新器、安装包信息和（UI 线程上的）变化通知。便于离屏渲染时换成假实现。</summary>
    public interface IAppUpdateHost
    {
        AppUpdater Updater { get; }
        InstalledPackage? Package { get; }
        event Action? Changed;
    }

    public sealed class AppUpdateService : IAppUpdateHost, IDisposable
    {
        /// <summary>检查更新的 GitHub 仓库（owner/name）。上游合并后改这一处即可。</summary>
        public const string Repository = "egoist74110/HD2ModManager";

        private readonly NotificationService _notifications;
        private readonly string _runDir;
        private string? _announcedVersion;

        public static AppUpdateService? Instance { get; private set; }

        public AppUpdater Updater { get; }

        /// <summary>当前安装的发布包信息；开发构建为 null。</summary>
        public InstalledPackage? Package { get; }

        /// <summary>始终在 UI 线程触发。</summary>
        public event Action? Changed;

        private AppUpdateService(NotificationService notifications, Func<bool> isIdle)
        {
            _notifications = notifications;
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _runDir = Path.Combine(baseDir, "cache", "app-update");
            Package = InstalledPackage.Read(baseDir);
            var exe = Path.Combine(baseDir, "HD2ModManager.exe");
            Updater = new AppUpdater(new AppUpdaterOptions
            {
                Current = AppVersion.Current(Assembly.GetEntryAssembly()),
                Repo = Repository,
                ConfigDir = Path.Combine(baseDir, "config"),
                RunDir = _runDir,
                Variant = Package?.Variant,
                Installer = new ZipUpdateInstaller(baseDir, _runDir, exe, RequestExit, isIdle),
            });
            Updater.Changed += OnUpdaterChanged;
        }

        public static AppUpdateService Initialize(NotificationService notifications, Func<bool> isIdle)
        {
            Instance?.Dispose();
            var service = new AppUpdateService(notifications, isIdle);
            Instance = service;
            service.AnnouncePreviousResult();
            service.Updater.Start();
            return service;
        }

        public string CurrentVersion => Updater.View().Current;

        private void AnnouncePreviousResult()
        {
            var result = UpdateApplyResult.Take(_runDir);
            if (result is null) return;
            if (result.Ok)
            {
                LogService.Info($"更新已应用：{result.Version}");
                _notifications.Show($"已更新到 {result.Version}", NotificationLevel.Info, TimeSpan.FromSeconds(8), "程序更新");
            }
            else
            {
                LogService.Error($"更新 {result.Version} 未完成，已回滚：{result.Message}");
                _notifications.Show(
                    $"更新到 {result.Version} 没有完成，已恢复为更新前的版本：{result.Message}。详情见 logs\\app-update.log。",
                    NotificationLevel.Error, TimeSpan.FromSeconds(30), "程序更新");
            }
        }

        private void OnUpdaterChanged()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            _ = dispatcher.InvokeAsync(() =>
            {
                AnnounceAvailable();
                Changed?.Invoke();
            });
        }

        private void AnnounceAvailable()
        {
            var view = Updater.View();
            var release = view.Check.Phase == CheckPhase.Available ? view.Check.Release : null;
            if (release is null || release.Version == _announcedVersion || view.Prefs.Skipped == release.Version) return;
            _announcedVersion = release.Version;
            _notifications.Show(
                $"发现新版本 {release.Version}（当前 {view.Current}）。到「设置 → 程序更新」查看更新说明并更新。",
                NotificationLevel.Info, TimeSpan.FromSeconds(10), "程序更新", "app-update-available");
        }

        /// <summary>
        /// 走主窗口的正常关闭流程（先保存再退出），并保证替换脚本只在程序确定要退出时才启动。
        /// 窗口的保存流程可以否决关闭（保存失败时会提示并留在原地）：那样旧实例还活着，脚本不能启动，
        /// 所以这里等应用的 Exit 事件——它触发才启动脚本；等不到就返回 false，更新回到「已下载」状态，可以重试。
        /// 本方法在后台线程上被调用并阻塞等待；窗口关闭本身在 UI 线程上进行。
        /// </summary>
        private static bool RequestExit(Action launch)
        {
            var app = Application.Current;
            if (app is null) { launch(); return true; }
            using var exiting = new ManualResetEventSlim();
            ExitEventHandler handler = (_, _) =>
            {
                try { launch(); }
                catch (Exception e) { LogService.Error($"启动更新脚本失败：{e.Message}"); }
                exiting.Set();
            };
            app.Dispatcher.Invoke(() =>
            {
                app.Exit += handler;
                if (app.MainWindow is { } window) window.Close();
                else app.Shutdown();
            });
            if (exiting.Wait(TimeSpan.FromSeconds(60))) return true;
            app.Dispatcher.Invoke(() => app.Exit -= handler);
            // Exit 恰好在超时前后触发的竞态：如果已经触发，脚本已启动，程序正在退出。
            return exiting.IsSet;
        }

        public void Dispose()
        {
            Updater.Changed -= OnUpdaterChanged;
            Updater.Dispose();
            if (ReferenceEquals(Instance, this)) Instance = null;
        }
    }

    internal static class AppUpdateText
    {
        public static string Error(UpdateErrorCode code) => code switch
        {
            UpdateErrorCode.Network => "连不上 GitHub（网络不通或被拦截）",
            UpdateErrorCode.RateLimited => "GitHub 访问次数受限，请稍后再试",
            UpdateErrorCode.Http => "GitHub 返回了异常响应",
            UpdateErrorCode.BadResponse => "返回的更新信息格式不对",
            UpdateErrorCode.NoPackage => "这个版本没有适合当前安装包类型的更新包，请到 Release 页面手动下载",
            UpdateErrorCode.NoDigest => "更新包缺少校验信息，已拒绝下载",
            UpdateErrorCode.DigestMismatch => "更新包校验未通过，已丢弃",
            UpdateErrorCode.Busy => "正在检查、下载或安装中",
            UpdateErrorCode.NotInstallable => "开发构建不支持应用内安装",
            UpdateErrorCode.NotReady => "更新包还没有下载",
            UpdateErrorCode.NothingNew => "没有可用的新版本",
            UpdateErrorCode.Cancelled => "已取消",
            UpdateErrorCode.Failed => "安装交接失败",
            UpdateErrorCode.DiskSpace => "磁盘空间不足",
            UpdateErrorCode.BadPackage => "更新包内容不符合要求，已拒绝安装",
            _ => code.ToString(),
        };

        public static string Bytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{bytes / 1024d:0} KB";
    }
}
