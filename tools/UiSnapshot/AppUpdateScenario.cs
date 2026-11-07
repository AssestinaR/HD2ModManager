using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using HD2ModCore.Updates;
using HD2ModManager.Services;
using HD2ModManager.ViewModels;
using HD2ModManager.Views;

namespace UiSnapshot;

// 作用：渲染设置页「程序更新」卡片的几种状态（有新版本 / 已下载待安装 / 已是最新 / 开发构建）。
// 用假的网络响应驱动真实的 AppUpdater，所以显示的是真实的状态机输出；不联网、不动程序目录。
internal static class AppUpdateScenario
{
    public enum Mode { Available, Ready, Latest, Dev, Error }

    private sealed class Host : IAppUpdateHost, IDisposable
    {
        public AppUpdater Updater { get; }
        public InstalledPackage? Package { get; }
        public event Action? Changed { add { } remove { } }
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hd2-ui-update-" + Guid.NewGuid().ToString("N"));

        public Host(Mode mode)
        {
            Package = mode == Mode.Dev ? null : new InstalledPackage("1.5.0", "complete");
            var zip = new byte[4096];
            var name = ReleasePicker.PackageName("1.6.0", "complete");
            var sums = $"{Hex(zip)}  {name}\n";
            var notes = "## 新增\n- 设置页新增「程序更新」：启动时自动检查，一键下载安装\n- 部署失败的提示改成看得懂的摘要\n\n## 修复\n- 一键修复在资产索引已是最新时不再整库重建";
            var latest = mode == Mode.Latest ? "1.5.0" : "1.6.0";
            var json = JsonSerializer.Serialize(new[]
            {
                new
                {
                    tag_name = "v" + latest, name = "v" + latest, body = notes, draft = false, prerelease = false,
                    html_url = "https://github.com/AssestinaR/HD2ModManager/releases/tag/v" + latest, published_at = "2026-11-07T08:00:00Z",
                    assets = new[]
                    {
                        new { name, browser_download_url = "http://127.0.0.1:1/dl/" + name, digest = "sha256:" + Hex(zip), size = (long)zip.Length },
                        new { name = "SHA256SUMS", browser_download_url = "http://127.0.0.1:1/dl/SHA256SUMS", digest = "sha256:" + Hex(Encoding.UTF8.GetBytes(sums)), size = (long)sums.Length },
                    },
                },
            });
            FetchFn raw = (url, _, _) =>
            {
                var path = new Uri(url).AbsolutePath;
                if (mode == Mode.Error) throw new HttpRequestException("offline");
                HttpContent? content = path switch
                {
                    "/releases" => new StringContent(json, Encoding.UTF8, "application/json"),
                    var p when p == "/dl/" + name => new ByteArrayContent(zip),
                    "/dl/SHA256SUMS" => new StringContent(sums),
                    _ => null,
                };
                return Task.FromResult(content is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            };
            Updater = new AppUpdater(new AppUpdaterOptions
            {
                Current = "1.5.0", Repo = "AssestinaR/HD2ModManager", ConfigDir = Path.Combine(_dir, "config"), RunDir = Path.Combine(_dir, "run"),
                Variant = mode == Mode.Dev ? null : "complete", Installer = new NoopInstaller(), Raw = raw, Feed = "http://127.0.0.1:1/releases",
                FirstCheckDelay = TimeSpan.FromDays(1),
            });
            Task.Run(async () =>
            {
                await Updater.CheckAsync(manual: true);
                if (mode == Mode.Ready) await Updater.DownloadAsync();
            }).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            Updater.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private static string Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    private sealed class NoopInstaller : IUpdateInstaller
    {
        public bool IsIdle => true;
        public void Install(InstallRequest request) { }
    }

    public static Scenario Build(string name, Mode mode) => new()
    {
        Name = name,
        Width = 860,
        Height = 760,
        Build = () =>
        {
            var host = new Host(mode);
            return new AppUpdateCard { DataContext = new AppUpdateSectionViewModel(host), Margin = new System.Windows.Thickness(16) };
        },
    };
}
