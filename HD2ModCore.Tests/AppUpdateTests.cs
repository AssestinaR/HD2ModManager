using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModCore.Updates;

namespace HD2ModCore.Tests;

// 作用：验证应用内更新的各环节：版本比较、Release 挑选、校验和、GitHub 页面解析、镜像规范化、
// 下载（续传/摘要）、更新主流程、更新包解压校验。
// Purpose: Covers the in-app update pipeline end to end with fake network responses.
public sealed class AppUpdateTests : IDisposable
{
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "hd2-update-tests-" + Guid.NewGuid().ToString("N"));

	public AppUpdateTests() => Directory.CreateDirectory(_dir);

	public void Dispose()
	{
		try { Directory.Delete(_dir, true); } catch (IOException) { }
	}

	private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

	// ---- 版本 ----

	[Theory]
	[InlineData("1.5.0", "1.4.9", 1)]
	[InlineData("1.4", "1.4.0", 0)]
	[InlineData("1.5.0-beta.2", "1.5.0-beta.10", -1)]
	[InlineData("1.5.0-beta.1", "1.5.0", -1)]
	[InlineData("1.5.0-rc", "1.5.0-beta", 1)]
	[InlineData("junk", "1.0.0", -1)]
	public void Compare_FollowsSemVerPrecedence(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(AppVersion.Compare(a, b)));

	[Theory]
	[InlineData("v1.4", "1.4.0")]
	[InlineData("1.5.0+abc", "1.5.0")]
	[InlineData("x1.5", null)]
	[InlineData("", null)]
	public void FromTag_NormalizesOrRejects(string tag, string? expected) => Assert.Equal(expected, AppVersion.FromTag(tag));

	// ---- 挑选 ----

	private static GithubRelease Rel(string tag, bool pre = false, bool draft = false, params ReleaseAsset[] assets)
		=> new(tag, tag, "notes", draft, pre, "https://github.com/o/r/releases/tag/" + tag, null, assets);

	private static ReleaseAsset Asset(string name, string? digest = null) => new(name, "https://github.com/o/r/releases/download/x/" + name, digest);

	[Fact]
	public void Pick_StableIgnoresPrereleaseAndDraft_AndFindsPackageForVariant()
	{
		var pkg = Asset(ReleasePicker.PackageName("1.6.0", "complete"), "sha256:" + new string('a', 64));
		var other = Asset(ReleasePicker.PackageName("1.6.0", "requires-dotnet"));
		var sums = Asset("SHA256SUMS");
		var list = new[]
		{
			Rel("v1.7.0-beta.1", pre: true), Rel("v1.8.0", draft: true),
			Rel("v1.6.0", assets: new[] { other, pkg, sums }), Rel("v1.5.0"),
		};
		var c = ReleasePicker.Pick(list, "1.5.0", _ => true, "complete");
		Assert.NotNull(c);
		Assert.Equal("1.6.0", c!.Release.Version);
		Assert.Equal(pkg, c.Package);
		Assert.Equal(sums, c.Sums);
		Assert.Null(ReleasePicker.Pick(list, "1.6.0", _ => true, "complete"));
	}

	[Fact]
	public void Pick_PrereleaseUserMovesToNewerPrerelease_AndAssetUrlFilterApplies()
	{
		var list = new[] { Rel("v1.5.0-beta.3", pre: true, assets: new[] { Asset("SHA256SUMS") }) };
		Assert.Equal("1.5.0-beta.3", ReleasePicker.Pick(list, "1.5.0-beta.1", _ => true, null)!.Release.Version);
		Assert.Null(ReleasePicker.Pick(list, "1.5.0-beta.1", _ => false, null)!.Sums);
	}

	[Fact]
	public void SumFor_FindsNameInSha256sumFormat()
	{
		var h = new string('B', 64);
		var text = $"{new string('0', 64)}  other.zip\r\n{h} *HD2ModManager-1.0.0-win-x64-complete.zip\r\n";
		Assert.Equal(h.ToLowerInvariant(), ChecksumFile.SumFor(text, "HD2ModManager-1.0.0-win-x64-complete.zip"));
		Assert.Null(ChecksumFile.SumFor(text, "missing.zip"));
	}

	// ---- GitHub 页面解析 ----

	[Fact]
	public void ParseReleasesAtom_ReadsTagsNotesAndSkipsBadTags()
	{
		var xml = """
<feed><entry><link rel="alternate" href="https://github.com/o/r/releases/tag/v1.6.0"/><title>v1.6.0 &amp; more</title>
<updated>2026-11-01T00:00:00Z</updated><content type="html">&lt;h2&gt;New&lt;/h2&gt;&lt;ul&gt;&lt;li&gt;a &amp;amp; b&lt;/li&gt;&lt;/ul&gt;</content></entry>
<entry><link href="https://github.com/o/r/releases/tag/bad%2Ftag"/></entry></feed>
""";
		var list = GithubFeed.ParseReleasesAtom(xml, "o/r");
		var r = Assert.Single(list);
		Assert.Equal("v1.6.0", r.TagName);
		Assert.Equal("v1.6.0 & more", r.Name);
		Assert.Contains("- a & b", r.Body);
		Assert.NotNull(r.PublishedAt);
	}

	[Fact]
	public void ParseExpandedAssets_ReadsDigestsInBothAttributeOrders()
	{
		var d1 = "sha256:" + new string('1', 64);
		var d2 = "sha256:" + new string('2', 64);
		var html = $"""
<a href="/o/r/releases/download/v1/a.zip">a</a><clipboard-copy aria-label="Copy to clipboard digest for a.zip" value="{d1}"></clipboard-copy>
<a href="/o/r/releases/download/v1/SHA256SUMS">s</a><clipboard-copy value="{d2}" aria-label="Copy to clipboard digest for SHA256SUMS"></clipboard-copy>
<a href="/o/r/releases/download/v1/nodigest.txt">n</a>
""";
		var assets = GithubFeed.ParseExpandedAssets(html, "o/r", "v1");
		Assert.Equal(3, assets.Count);
		Assert.Equal(d1, assets.Single(a => a.Name == "a.zip").Digest);
		Assert.Equal(d2, assets.Single(a => a.Name == "SHA256SUMS").Digest);
		Assert.Null(assets.Single(a => a.Name == "nodigest.txt").Digest);
	}

	// ---- 镜像 ----

	[Theory]
	[InlineData("https://mirror.example.com", "https://mirror.example.com/")]
	[InlineData("https://mirror.example.com/gh", "https://mirror.example.com/gh/")]
	[InlineData("", "")]
	[InlineData("http://mirror.example.com/", null)]
	[InlineData("https://127.0.0.1/", null)]
	[InlineData("https://localhost/", null)]
	[InlineData("https://nas.local/", null)]
	[InlineData("https://intranet/", null)]
	[InlineData("https://user:pw@mirror.example.com/", null)]
	[InlineData("https://mirror.example.com/?x=1", null)]
	[InlineData("https://mirror.example.com:8443/", null)]
	public void NormalizeCustom_AcceptsOnlyPublicHttpsNames(string raw, string? expected) => Assert.Equal(expected, UpdateMirrors.NormalizeCustom(raw));

	[Fact]
	public async Task MirrorWrap_RefusesNonGithubAndFollowsPathAbsoluteRedirects()
	{
		var seen = new List<string>();
		FetchFn raw = (url, _, _) =>
		{
			seen.Add(url);
			if (seen.Count == 1)
			{
				var redirect = new HttpResponseMessage(HttpStatusCode.Found);
				redirect.Headers.Location = new Uri("/https://github.com/o/r/final", UriKind.Relative);
				return Task.FromResult(redirect);
			}
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
		};
		var mirror = UpdateMirrors.All[0];
		var wrapped = UpdateMirrors.Wrap(mirror, raw);
		using var ok = await wrapped("https://github.com/o/r/x", null, default);
		Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
		Assert.Equal(new[] { "https://ghfast.top/https://github.com/o/r/x", "https://ghfast.top/https://github.com/o/r/final" }, seen);
		await Assert.ThrowsAsync<InvalidOperationException>(() => wrapped("https://evil.example.com/x", null, default));
	}

	// ---- 假网络 ----

	private sealed class FakeNet
	{
		public readonly Dictionary<string, byte[]> Files = new();
		public readonly List<(string Url, string? Range)> Requests = new();
		public int FailFirst;

		public FetchFn Fn => (url, headers, ct) =>
		{
			var range = headers is not null && headers.TryGetValue("Range", out var r) ? r : null;
			Requests.Add((url, range));
			if (FailFirst > 0 && url.EndsWith(".zip", StringComparison.Ordinal)) { FailFirst--; throw new HttpRequestException("boom"); }
			var path = new Uri(url).AbsolutePath;
			if (!Files.TryGetValue(path, out var data)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
			if (range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
			{
				var from = int.Parse(range[6..].TrimEnd('-'));
				var part = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data[from..]) };
				part.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, data.Length - 1, data.Length);
				return Task.FromResult(part);
			}
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
		};
	}

	private static readonly DownloadOptions FastDownload = new() { RetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero }, StallTimeout = TimeSpan.FromSeconds(5) };

	[Fact]
	public async Task Downloader_ResumesFromPartAndVerifiesDigest()
	{
		var data = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
		var net = new FakeNet();
		net.Files["/f.zip"] = data;
		var file = Path.Combine(_dir, "f.zip");
		var asset = new ReleaseAsset("f.zip", "http://127.0.0.1/f.zip", "sha256:" + Sha(data), data.Length);
		File.WriteAllBytes(file + ".part", data[..2000]);
		File.WriteAllText(file + ".part.json", $"{Sha(data)}|{data.Length}|f.zip");

		var sha = await UpdateDownloader.DownloadAsync(net.Fn, asset, file, default, FastDownload);

		Assert.Equal(Sha(data), sha);
		Assert.Equal(data, File.ReadAllBytes(file));
		Assert.Equal("bytes=2000-", net.Requests.Single().Range);
		Assert.False(File.Exists(file + ".part"));
	}

	[Fact]
	public async Task Downloader_RetriesNetworkFailure_AndRejectsWrongDigestWithoutLeavingFile()
	{
		var data = new byte[100];
		var net = new FakeNet { FailFirst = 1 };
		net.Files["/f.zip"] = data;
		var good = new ReleaseAsset("f.zip", "http://127.0.0.1/f.zip", "sha256:" + Sha(data), 100);
		await UpdateDownloader.DownloadAsync(net.Fn, good, Path.Combine(_dir, "f.zip"), default, FastDownload);
		Assert.Equal(2, net.Requests.Count);

		var bad = good with { Digest = "sha256:" + new string('0', 64) };
		var ex = await Assert.ThrowsAsync<UpdateException>(() => UpdateDownloader.DownloadAsync(net.Fn, bad, Path.Combine(_dir, "g.zip"), default, FastDownload));
		Assert.Equal(UpdateErrorCode.DigestMismatch, ex.Code);
		Assert.False(File.Exists(Path.Combine(_dir, "g.zip")));
		Assert.False(File.Exists(Path.Combine(_dir, "g.zip.part")));

		var none = await Assert.ThrowsAsync<UpdateException>(() => UpdateDownloader.DownloadAsync(net.Fn, good with { Digest = null }, Path.Combine(_dir, "h.zip"), default, FastDownload));
		Assert.Equal(UpdateErrorCode.NoDigest, none.Code);
	}

	// ---- 更新主流程 ----

	private const string Feed = "http://127.0.0.1:1/releases";

	private static byte[] MakePackage(string version, string variant)
	{
		using var ms = new MemoryStream();
		using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
		{
			void Add(string name, string text) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(text); }
			Add("HD2ModManager.exe", "exe");
			Add("README.md", "readme");
			Add("bin/hd2mod.exe", "cli");
			Add(InstalledPackage.FileName, JsonSerializer.Serialize(new { version, variant }));
		}
		return ms.ToArray();
	}

	private sealed class RecordingInstaller : IUpdateInstaller
	{
		public bool IsIdle { get; set; } = true;
		public InstallRequest? Request;
		public void Install(InstallRequest request) => Request = request;
	}

	private static string ReleasesJson(string version, string pkgName, string pkgDigest, string sumsDigest, long pkgSize, string notes = "更新说明") => JsonSerializer.Serialize(new[]
	{
		new
		{
			tag_name = "v" + version, name = "v" + version, body = notes, draft = false, prerelease = false,
			html_url = "https://github.com/o/r/releases/tag/v" + version, published_at = "2026-11-01T00:00:00Z",
			assets = new[]
			{
				new { name = pkgName, browser_download_url = "http://127.0.0.1:1/dl/" + pkgName, digest = (string?)pkgDigest, size = pkgSize },
				new { name = "SHA256SUMS", browser_download_url = "http://127.0.0.1:1/dl/SHA256SUMS", digest = (string?)sumsDigest, size = 0L },
			},
		},
	});

	private AppUpdater NewUpdater(FakeNet net, IUpdateInstaller? installer, string? variant = "complete", string current = "1.5.0")
		=> new(new AppUpdaterOptions
		{
			Current = current, Repo = "o/r", ConfigDir = Path.Combine(_dir, "config"), RunDir = Path.Combine(_dir, "run"),
			Variant = variant, Installer = installer, Raw = net.Fn, Feed = Feed, Download = FastDownload,
			FirstCheckDelay = TimeSpan.FromDays(1),
		});

	private (FakeNet Net, byte[] Zip, string Name) ReleaseWorld(string version = "1.6.0", string variant = "complete", string? sumsOverride = null)
	{
		var zip = MakePackage(version, variant);
		var name = ReleasePicker.PackageName(version, variant);
		var sums = sumsOverride ?? $"{Sha(zip)}  {name}\n";
		var net = new FakeNet();
		net.Files["/releases"] = Encoding.UTF8.GetBytes(ReleasesJson(version, name, "sha256:" + Sha(zip), "sha256:" + Sha(Encoding.UTF8.GetBytes(sums)), zip.Length));
		net.Files["/dl/" + name] = zip;
		net.Files["/dl/SHA256SUMS"] = Encoding.UTF8.GetBytes(sums);
		return (net, zip, name);
	}

	[Fact]
	public async Task Updater_CheckDownloadInstall_HappyPath()
	{
		var (net, zip, _) = ReleaseWorld();
		var installer = new RecordingInstaller();
		using var updater = NewUpdater(net, installer);

		await updater.CheckAsync(manual: true);
		var v = updater.View();
		Assert.Equal(CheckPhase.Available, v.Check.Phase);
		Assert.Equal("1.6.0", v.Check.Release!.Version);
		Assert.Equal("更新说明", v.Check.Release.Notes);
		Assert.True(v.CanInstall);

		await updater.DownloadAsync(manual: true);
		Assert.Equal(DownloadPhase.Ready, updater.View().Download.Phase);

		await updater.InstallAsync();
		Assert.Equal(DownloadPhase.Installing, updater.View().Download.Phase);
		Assert.Equal("1.6.0", installer.Request!.Version);
		Assert.Equal(Sha(zip), installer.Request.Sha256);
		Assert.Equal(zip, File.ReadAllBytes(installer.Request.File));
	}

	[Fact]
	public async Task Updater_NoNewerVersion_ReportsLatest()
	{
		var (net, _, _) = ReleaseWorld("1.5.0");
		using var updater = NewUpdater(net, new RecordingInstaller());
		await updater.CheckAsync();
		Assert.Equal(CheckPhase.Latest, updater.View().Check.Phase);
	}

	[Fact]
	public async Task Updater_SumsDisagreeWithAssetDigest_RefusesDownload()
	{
		var (net, _, name) = ReleaseWorld(sumsOverride: new string('f', 64) + "  HD2ModManager-1.6.0-win-x64-complete.zip\n");
		// SHA256SUMS 的摘要要与 GitHub 给的一致才信；这里故意不一致。
		using var updater = NewUpdater(net, new RecordingInstaller());
		await updater.CheckAsync();
		var ex = await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadAsync());
		Assert.Equal(UpdateErrorCode.DigestMismatch, ex.Code);
		Assert.Equal(DownloadPhase.Error, updater.View().Download.Phase);
		Assert.False(File.Exists(Path.Combine(_dir, "run", name)));
	}

	[Fact]
	public async Task Updater_DevBuildWithoutVariant_OffersButNeverInstalls()
	{
		var (net, _, _) = ReleaseWorld();
		using var updater = NewUpdater(net, new RecordingInstaller(), variant: null);
		await updater.CheckAsync();
		var v = updater.View();
		Assert.Equal(CheckPhase.Available, v.Check.Phase);
		Assert.False(v.CanInstall);
		var ex = await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadAsync());
		Assert.Equal(UpdateErrorCode.NotInstallable, ex.Code);
	}

	[Fact]
	public async Task Updater_AutoUpdate_InstallsOnlyWhenIdleAndNotSkipped()
	{
		var (net, _, _) = ReleaseWorld();
		var installer = new RecordingInstaller { IsIdle = false };
		using var updater = NewUpdater(net, installer);
		updater.SetPrefs(autoUpdate: true);
		await updater.CheckAsync();
		Assert.Equal(DownloadPhase.Ready, updater.View().Download.Phase); // 下载了，但程序忙，不自动安装
		Assert.Null(installer.Request);

		installer.IsIdle = true;
		updater.SetPrefs(skipped: "1.6.0");
		await updater.InstallAsync(automatic: true);
		Assert.Null(installer.Request); // 已跳过的版本不会自动安装
	}

	[Fact]
	public async Task Updater_ManualCheckNetworkFailure_OffersMirror()
	{
		var net = new FakeNet(); // 没有 /releases → 404 → Http 错误；atom 兜底只对官方源
		using var updater = NewUpdater(net, new RecordingInstaller());
		await updater.CheckAsync(manual: true);
		var v = updater.View();
		Assert.Equal(CheckPhase.Error, v.Check.Phase);
		Assert.True(v.Check.Offer);
		await updater.CheckAsync(manual: false);
		Assert.False(updater.View().Check.Offer);
	}

	[Fact]
	public void Updater_RejectsNonLoopbackCustomFeed()
	{
		var net = new FakeNet();
		Assert.Throws<ArgumentException>(() => new AppUpdater(new AppUpdaterOptions
		{
			Current = "1.0.0", Repo = "o/r", ConfigDir = _dir, RunDir = _dir, Raw = net.Fn, Feed = "https://evil.example.com/releases",
		}));
	}

	// ---- 安装器：解压校验 ----

	private ZipUpdateInstaller NewInstaller(string variant, out string appDir, Action? exit = null, Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process?>? start = null)
	{
		appDir = Path.Combine(_dir, "app");
		Directory.CreateDirectory(appDir);
		File.WriteAllText(Path.Combine(appDir, InstalledPackage.FileName), JsonSerializer.Serialize(new { version = "1.5.0", variant }));
		return new ZipUpdateInstaller(appDir, Path.Combine(_dir, "run"), Path.Combine(appDir, "HD2ModManager.exe"), exit ?? (() => { }), null, start ?? (_ => null));
	}

	private InstallRequest Pack(byte[] zip, string version = "1.6.0")
	{
		var file = Path.Combine(_dir, "pkg.zip");
		File.WriteAllBytes(file, zip);
		return new InstallRequest(file, Sha(zip), version);
	}

	[Fact]
	public void Installer_Extract_AcceptsMatchingPackage()
	{
		var installer = NewInstaller("complete", out _);
		var stage = Path.Combine(_dir, "stage");
		installer.Extract(Pack(MakePackage("1.6.0", "complete")), stage);
		Assert.True(File.Exists(Path.Combine(stage, "HD2ModManager.exe")));
		Assert.True(File.Exists(Path.Combine(stage, "bin", "hd2mod.exe")));
	}

	[Theory]
	[InlineData("1.7.0", "complete")]          // 标记里的版本与公告的不一致
	[InlineData("1.6.0", "requires-dotnet")]   // 变体与已安装的不一致
	public void Installer_Extract_RejectsMismatchedMarker(string markerVersion, string markerVariant)
	{
		var installer = NewInstaller("complete", out _);
		var ex = Assert.Throws<UpdateException>(() => installer.Extract(Pack(MakePackage(markerVersion, markerVariant)), Path.Combine(_dir, "stage")));
		Assert.Equal(UpdateErrorCode.BadPackage, ex.Code);
	}

	[Fact]
	public void Installer_Extract_RejectsZipSlipAndNonZip()
	{
		var installer = NewInstaller("complete", out _);
		using var ms = new MemoryStream();
		using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
		{
			using var w = new StreamWriter(zip.CreateEntry("../evil.txt").Open());
			w.Write("x");
		}
		var slip = Assert.Throws<UpdateException>(() => installer.Extract(Pack(ms.ToArray()), Path.Combine(_dir, "stage")));
		Assert.Equal(UpdateErrorCode.BadPackage, slip.Code);
		Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));

		var bad = Assert.Throws<UpdateException>(() => installer.Extract(Pack(Encoding.UTF8.GetBytes("not a zip")), Path.Combine(_dir, "stage2")));
		Assert.Equal(UpdateErrorCode.BadPackage, bad.Code);
	}

	[Fact]
	public void Installer_DevBuildWithoutMarker_IsNotInstallable()
	{
		var installer = new ZipUpdateInstaller(Path.Combine(_dir, "nomarker"), Path.Combine(_dir, "run"), "x.exe", () => { }, null, _ => null);
		Directory.CreateDirectory(Path.Combine(_dir, "nomarker"));
		var ex = Assert.Throws<UpdateException>(() => installer.Install(Pack(MakePackage("1.6.0", "complete"))));
		Assert.Equal(UpdateErrorCode.NotInstallable, ex.Code);
	}

	[Fact]
	public void Installer_Install_StartsScriptWithExpectedArgumentsThenExits()
	{
		var exited = false;
		System.Diagnostics.ProcessStartInfo? started = null;
		var installer = NewInstaller("complete", out var appDir, () => exited = true, psi => { started = psi; return System.Diagnostics.Process.GetCurrentProcess(); });
		installer.Install(Pack(MakePackage("1.6.0", "complete")));
		Assert.True(exited);
		Assert.Equal("powershell.exe", started!.FileName);
		Assert.Contains("-AppDir", started.ArgumentList);
		Assert.Equal(appDir, started.ArgumentList[started.ArgumentList.IndexOf("-AppDir") + 1]);
		Assert.True(File.Exists(installer.ScriptPath));
	}

	// 用真实的发布脚本产出的包验证解压（设置环境变量 HD2_UPDATE_REAL_ZIP 指向 complete 包时才运行，否则直接通过）。
	[Fact]
	public void Installer_Extract_AcceptsRealPublishedPackage_WhenProvided()
	{
		var zip = Environment.GetEnvironmentVariable("HD2_UPDATE_REAL_ZIP");
		if (string.IsNullOrEmpty(zip) || !File.Exists(zip)) return;
		var installer = NewInstaller("complete", out _);
		var stage = Path.Combine(_dir, "real-stage");
		installer.Extract(new InstallRequest(zip, Sha(File.ReadAllBytes(zip)), "1.5.0"), stage);
		Assert.True(File.Exists(Path.Combine(stage, "HD2ModManager.exe")));
		Assert.True(File.Exists(Path.Combine(stage, "bin", "hd2mod.exe")));
		Assert.NotEmpty(Directory.GetFiles(Path.Combine(stage, "bin", "dotnet", "host", "fxr"), "hostfxr.dll", SearchOption.AllDirectories));
		Assert.True(File.Exists(Path.Combine(stage, "data", "resources", "archivehashes.json")));
	}

	[Fact]
	public void Installer_ScriptStartFailure_ThrowsAndDoesNotExit()
	{
		var exited = false;
		var installer = NewInstaller("complete", out _, () => exited = true, _ => null);
		var ex = Assert.Throws<UpdateException>(() => installer.Install(Pack(MakePackage("1.6.0", "complete"))));
		Assert.Equal(UpdateErrorCode.Failed, ex.Code);
		Assert.False(exited);
	}
}
