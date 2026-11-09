using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using HD2ModCore.Updates;
using HD2ModManager.Services;
using HD2ModManager.Views;

namespace HD2ModManager.ViewModels
{
    // 作用：设置页「程序更新」卡片的视图模型：显示版本与更新说明，检查 / 下载 / 安装 / 跳过，
    // 自动检查与自动安装开关，下载来源（直连 / 自动镜像 / 优先镜像）与自定义镜像。状态都来自 AppUpdater 的快照。
    // Purpose: View model of the settings "Program update" card; all state comes from the AppUpdater snapshot.
    public sealed class AppUpdateSectionViewModel : BaseViewModel, IDisposable
    {
        private readonly IAppUpdateHost _service;
        private string? _offerKey;
        private string? _dismissedOfferKey;
        private string _customMirrorText;
        private string _customMirrorError = string.Empty;
        private string _lastActionError = string.Empty;

        private readonly AsyncRelayCommand _check;
        private readonly RelayCommand _download;
        private readonly RelayCommand _cancel;
        private readonly AsyncRelayCommand _install;
        private readonly RelayCommand _toggleSkip;

        public System.Windows.Input.ICommand CheckCommand => _check;
        public System.Windows.Input.ICommand DownloadCommand => _download;
        public System.Windows.Input.ICommand CancelCommand => _cancel;
        public System.Windows.Input.ICommand InstallCommand => _install;
        public System.Windows.Input.ICommand ToggleSkipCommand => _toggleSkip;
        public System.Windows.Input.ICommand OpenReleasePageCommand { get; }

        public AppUpdateSectionViewModel(IAppUpdateHost service)
        {
            _service = service;
            _customMirrorText = service.Updater.View().Prefs.CustomMirror;
            _check = new AsyncRelayCommand(_ => RunAsync(() => _service.Updater.CheckAsync(manual: true)), _ => CanCheck);
            _download = new RelayCommand(_ => Run(() => _service.Updater.StartDownload()), _ => CanDownload);
            _cancel = new RelayCommand(_ => _service.Updater.Cancel(), _ => IsDownloading);
            _install = new AsyncRelayCommand(_ => InstallAsync(), _ => CanInstallNow);
            _toggleSkip = new RelayCommand(_ => ToggleSkip(), _ => Release is not null);
            OpenReleasePageCommand = new RelayCommand(OpenReleasePage);
            _service.Changed += OnServiceChanged;
        }

        public void Dispose() => _service.Changed -= OnServiceChanged;

        private AppUpdateView View => _service.Updater.View();
        private AppRelease? Release => View.Check.Phase == CheckPhase.Available ? View.Check.Release : null;

        // ---- 展示 ----

        public string CurrentVersionText
        {
            get
            {
                var package = _service.Package;
                var kind = package is null ? "开发构建" : package.Variant switch
                {
                    "complete" => "完整包",
                    "requires-dotnet" => "需要 .NET 的精简包",
                    "compat-complete" => "兼容版（完整包）",
                    "compat-requires-dotnet" => "兼容版（需要 .NET）",
                    _ => package.Variant,
                };
                return $"当前版本 {View.Current} · {kind}";
            }
        }

        public string StatusText
        {
            get
            {
                if (!string.IsNullOrEmpty(_lastActionError)) return _lastActionError;
                var v = View;
                var download = v.Download;
                if (download.Phase == DownloadPhase.Installing) return "正在安装：管理器即将关闭并自动重启……";
                if (download.Phase == DownloadPhase.Error && download.Code is { } dc) return $"更新失败：{AppUpdateText.Error(dc)}";
                if (download.Phase == DownloadPhase.Ready) return $"新版本 {download.Version} 已下载并校验通过，可以安装";
                if (download.Phase == DownloadPhase.Downloading) return $"正在下载 {download.Version}……";
                return v.Check.Phase switch
                {
                    CheckPhase.Checking => "正在检查更新……",
                    CheckPhase.Latest => $"已是最新版本（{v.Check.At?.ToLocalTime():MM-dd HH:mm} 检查）",
                    CheckPhase.Available => $"发现新版本 {v.Check.Release?.Version}",
                    CheckPhase.Error => $"检查更新失败：{(v.Check.Code is { } c ? AppUpdateText.Error(c) : "未知错误")}",
                    _ => "尚未检查更新",
                };
            }
        }

        public bool HasRelease => Release is not null;
        public string ReleaseTitle => Release is { } r ? (string.IsNullOrWhiteSpace(r.Name) ? r.Version : r.Name) + (r.Prerelease ? "（预发布）" : string.Empty) : string.Empty;
        public string ReleaseDate => Release?.PublishedAt is { } at ? $"发布于 {at.ToLocalTime():yyyy-MM-dd}" : string.Empty;
        public string ReleaseNotes => Release is { } r && !string.IsNullOrWhiteSpace(r.Notes) ? r.Notes : "（这个版本没有填写更新说明）";
        public bool IsSkipped => Release is { } r && View.Prefs.Skipped == r.Version;
        public string SkipText => IsSkipped ? "取消跳过" : "跳过此版本";

        public bool ShowBlockedNote => HasRelease && !View.CanInstall;
        public string BlockedNote => View.InstallBlockedReason == "dev-build"
            ? "这是开发构建，不能应用内更新。请到 Release 页面下载发布包。"
            : "当前环境不能应用内更新，请到 Release 页面下载发布包。";

        public bool IsDownloading => View.Download.Phase == DownloadPhase.Downloading;
        public bool ShowProgress => IsDownloading;
        public double ProgressValue => View.Download is { Phase: DownloadPhase.Downloading, Total: > 0 } d ? Math.Min(100d, d.Received * 100d / d.Total!.Value) : 0;
        public bool ProgressIndeterminate => View.Download is { Phase: DownloadPhase.Downloading, Total: null or 0 };
        public string ProgressText => View.Download is { Phase: DownloadPhase.Downloading } d
            ? (d.Total is > 0 ? $"已下载 {AppUpdateText.Bytes(d.Received)} / {AppUpdateText.Bytes(d.Total.Value)}" : $"已下载 {AppUpdateText.Bytes(d.Received)}")
            : string.Empty;

        public bool IsReady => View.Download.Phase == DownloadPhase.Ready && View.Download.Version == Release?.Version;
        public string DigestNote => (View.Download.DigestSource ?? View.Check.DigestSource) == DigestSource.Mirror
            ? "校验信息来自镜像（官方页面暂时取不到）。更新包仍会用 SHA-256 核对，但镜像给出的校验值本身无法由官方交叉验证。"
            : string.Empty;
        public bool HasDigestNote => DigestNote.Length > 0;

        public bool CanCheck => View.Check.Phase != CheckPhase.Checking && View.Download.Phase is not (DownloadPhase.Downloading or DownloadPhase.Installing);
        public bool CanDownload => HasRelease && View.CanInstall && View.Download.Phase is DownloadPhase.None or DownloadPhase.Error;
        public bool CanInstallNow => IsReady && View.CanInstall;
        public bool ShowDownloadButton => HasRelease && View.CanInstall && !IsDownloading && !IsReady && View.Download.Phase != DownloadPhase.Installing;
        public string DownloadButtonText => View.Download.Phase == DownloadPhase.Error ? "重试下载" : "下载更新";
        public bool ShowReleasePageButton => HasRelease;

        // ---- 设置 ----

        public bool AutoCheck
        {
            get => View.Prefs.AutoCheck;
            set { _service.Updater.SetPrefs(autoCheck: value); OnPropertyChanged(); }
        }

        public bool AutoUpdate
        {
            get => View.Prefs.AutoUpdate;
            set { _service.Updater.SetPrefs(autoUpdate: value); OnPropertyChanged(); }
        }

        /// <summary>0=默认直连、失败后询问；1=直连失败自动换镜像；2=优先走镜像。</summary>
        public int DownloadSourceChoice
        {
            get => (int)View.Prefs.DownloadSource;
            set { _service.Updater.SetPrefs(source: (HD2ModCore.Updates.DownloadSourceMode)value); OnPropertyChanged(); OnPropertyChanged(nameof(ShowMirrorPicker)); }
        }

        public string SourceHint => View.Prefs.DownloadSource switch
        {
            HD2ModCore.Updates.DownloadSourceMode.Auto => "先连 GitHub；直连失败后自动改用镜像，自动检查也会这样做。",
            HD2ModCore.Updates.DownloadSourceMode.Mirror => "总是通过下面选的镜像下载。",
            _ => "先连 GitHub；连不上时弹窗询问是否改用镜像。",
        };

        public sealed record MirrorOption(string Id, string Label);

        /// <summary>「优先使用镜像」可选的镜像：内置的按推荐度排序，自定义的（如果有）放最后。</summary>
        public System.Collections.Generic.IReadOnlyList<MirrorOption> MirrorOptions
        {
            get
            {
                var list = UpdateMirrors.All.Select((m, i) => new MirrorOption(m.Id, i == 0 ? $"{m.Host}（推荐）" : i == 1 ? $"{m.Host}（备用）" : m.Host)).ToList();
                if (UpdateMirrors.Custom(View.Prefs.CustomMirror) is { } own) list.Add(new MirrorOption(own.Id, $"{own.Host}（你自己设置的镜像）"));
                return list;
            }
        }

        public string MirrorRiskNote => "镜像由第三方运营。通过镜像下载时，文件仍会校验 SHA-256：会先尝试从 GitHub 取官方校验值，GitHub 连不上时才用镜像提供的校验值，界面会标出「校验信息来自镜像」。";

        public bool ShowMirrorPicker => View.Prefs.DownloadSource == HD2ModCore.Updates.DownloadSourceMode.Mirror;

        public string MirrorId
        {
            get => string.IsNullOrEmpty(View.Prefs.MirrorId) ? UpdateMirrors.All[0].Id : View.Prefs.MirrorId;
            set { _service.Updater.SetPrefs(mirrorId: value); OnPropertyChanged(); }
        }

        public string CustomMirror
        {
            get => _customMirrorText;
            set
            {
                _customMirrorText = value ?? string.Empty;
                var normalized = UpdateMirrors.NormalizeCustom(_customMirrorText);
                if (normalized is null)
                {
                    CustomMirrorError = "镜像地址无效：只接受 https 开头的公网域名（不能是 IP、localhost 或内网名，不带账号、端口、参数）。";
                }
                else
                {
                    CustomMirrorError = string.Empty;
                    _service.Updater.SetPrefs(customMirror: normalized);
                    OnPropertyChanged(nameof(MirrorOptions));
                    OnPropertyChanged(nameof(MirrorId));
                }
                OnPropertyChanged();
            }
        }

        public string CustomMirrorError
        {
            get => _customMirrorError;
            private set { if (SetField(ref _customMirrorError, value)) OnPropertyChanged(nameof(HasCustomMirrorError)); }
        }

        public bool HasCustomMirrorError => _customMirrorError.Length > 0;

        // ---- 动作 ----

        private void OnServiceChanged()
        {
            foreach (var name in new[]
            {
                nameof(CurrentVersionText), nameof(StatusText), nameof(HasRelease), nameof(ReleaseTitle), nameof(ReleaseDate), nameof(ReleaseNotes),
                nameof(IsSkipped), nameof(SkipText), nameof(ShowBlockedNote), nameof(BlockedNote), nameof(IsDownloading), nameof(ShowProgress),
                nameof(ProgressValue), nameof(ProgressIndeterminate), nameof(ProgressText), nameof(IsReady), nameof(DigestNote), nameof(HasDigestNote),
                nameof(CanCheck), nameof(CanDownload), nameof(CanInstallNow), nameof(ShowDownloadButton), nameof(DownloadButtonText), nameof(ShowReleasePageButton),
                nameof(AutoCheck), nameof(AutoUpdate), nameof(DownloadSourceChoice), nameof(ShowMirrorPicker), nameof(MirrorId), nameof(SourceHint), nameof(MirrorOptions),
            }) OnPropertyChanged(name);
            _check.RaiseCanExecuteChanged();
            _download.RaiseCanExecuteChanged();
            _cancel.RaiseCanExecuteChanged();
            _install.RaiseCanExecuteChanged();
            _toggleSkip.RaiseCanExecuteChanged();
            OfferMirrorIfNeeded();
        }

        private async Task RunAsync(Func<Task> action)
        {
            _lastActionError = string.Empty;
            try { await action(); }
            catch (UpdateException e) { _lastActionError = $"操作失败：{AppUpdateText.Error(e.Code)}"; }
            OnServiceChanged();
        }

        private void Run(Action action)
        {
            _lastActionError = string.Empty;
            try { action(); }
            catch (UpdateException e) { _lastActionError = $"操作失败：{AppUpdateText.Error(e.Code)}"; }
            OnServiceChanged();
        }

        private async Task InstallAsync()
        {
            var release = Release;
            if (release is null) return;
            var busy = _service.Updater.View().Download.Phase == DownloadPhase.Installing;
            if (busy) return;
            var answer = HD2ModManager.Views.AppDialog.Show(
                $"将安装 {release.Version}。\n\n管理器会先保存并关闭，替换程序文件后自动重新启动；你的设置、Mod 库和配置不会被改动。\n如果正在部署或导入，请先等它们完成。\n\n现在安装吗？",
                "安装更新", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            await RunAsync(() => _service.Updater.InstallAsync());
        }

        private void ToggleSkip()
        {
            if (Release is not { } release) return;
            _service.Updater.SetPrefs(skipped: IsSkipped ? null : release.Version, clearSkipped: IsSkipped);
            OnServiceChanged();
        }

        private void OpenReleasePage()
        {
            var url = Release?.Url;
            if (string.IsNullOrEmpty(url)) url = View.ReleasesUrl;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                LogService.Warn($"打开 Release 页面失败：{e.Message}");
            }
        }

        /// <summary>用户主动发起的检查/下载因网络失败时，询问是否换镜像；同一次失败只问一次。</summary>
        private void OfferMirrorIfNeeded()
        {
            var v = View;
            UpdateErrorCode? code = null;
            string? key = null;
            Action<string>? retry = null;
            if (v.Check.Phase == CheckPhase.Error && v.Check.Offer && v.Check.Code is { } cc)
            {
                code = cc; key = $"check:{cc}:{v.Check.At:O}";
                retry = id => _ = RunAsync(() => _service.Updater.CheckAsync(manual: true, mirror: id));
            }
            else if (v.Download is { Phase: DownloadPhase.Error, Offer: true, Code: { } dc })
            {
                code = dc; key = $"download:{dc}:{v.Download.Version}";
                retry = id => Run(() => _service.Updater.StartDownload(id));
            }
            if (key is null || code is null || retry is null) { _offerKey = null; return; }
            if (key == _offerKey || key == _dismissedOfferKey) return;
            _offerKey = key;
            var offeredKey = key;
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                var window = new MirrorChoiceWindow(AppUpdateText.Error(code.Value), UpdateMirrors.Offered(View.Prefs.CustomMirror));
                if (window.ShowDialog() == true && window.Chosen is { } mirror)
                {
                    if (window.Remember) _service.Updater.SetPrefs(source: HD2ModCore.Updates.DownloadSourceMode.Auto);
                    retry(mirror.Id);
                }
                else _dismissedOfferKey = offeredKey;
            }));
        }
    }
}
