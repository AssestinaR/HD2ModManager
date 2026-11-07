using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace HD2ModCore.Updates;

/// <summary>已校验的更新包（交给安装器的内容）。</summary>
public sealed record InstallRequest(string File, string Sha256, string Version);

public interface IUpdateInstaller
{
	/// <summary>当前是否适合自动安装（例如没有部署/导入任务在跑）。手动安装不看这个。</summary>
	bool IsIdle { get; }

	/// <summary>解压校验并交接给外部脚本，随后让程序退出。失败抛 <see cref="UpdateException"/>。</summary>
	void Install(InstallRequest request);
}

/// <summary>随包发布的标记文件 update-package.json：这个安装是哪个版本、哪种发布包。开发构建没有它，所以不支持应用内安装。</summary>
public sealed record InstalledPackage(string Version, string Variant)
{
	public const string FileName = "update-package.json";
	public static readonly string[] Variants = { "complete", "requires-dotnet", "compat-complete", "compat-requires-dotnet" };

	public static InstalledPackage? Read(string appDir)
	{
		try
		{
			var path = Path.Combine(appDir, FileName);
			if (!File.Exists(path)) return null;
			using var doc = JsonDocument.Parse(File.ReadAllText(path));
			var version = AppVersion.Normalize(doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null);
			var variant = doc.RootElement.TryGetProperty("variant", out var p) ? p.GetString() : null;
			return version is not null && variant is not null && Variants.Contains(variant) ? new InstalledPackage(version, variant) : null;
		}
		catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
	}
}

/// <summary>上一次应用更新的结果（脚本写下，下次启动读出并展示）。</summary>
public sealed record UpdateApplyResult(bool Ok, string Version, string Message)
{
	public static string ResultPath(string runDir) => Path.Combine(runDir, "result.json");

	public static UpdateApplyResult? Take(string runDir)
	{
		var path = ResultPath(runDir);
		try
		{
			if (!File.Exists(path)) return null;
			using var doc = JsonDocument.Parse(File.ReadAllText(path));
			var root = doc.RootElement;
			var r = new UpdateApplyResult(
				root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
				root.TryGetProperty("version", out var v) ? v.GetString() ?? string.Empty : string.Empty,
				root.TryGetProperty("message", out var m) ? m.GetString() ?? string.Empty : string.Empty);
			File.Delete(path);
			return r;
		}
		catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
	}
}

// 作用：把已校验的 zip 解压到暂存目录，写出替换脚本并启动它，然后让管理器退出；脚本等旧进程退出后
// 备份并覆盖随包文件（bin 整体替换，其余逐文件覆盖，用户数据一律不动），失败自动回滚，最后重启管理器。
// Purpose: Extracts the verified zip to a stage folder, starts a replacement script and exits the manager. The
// script waits for the old process, backs up and overlays shipped files (bin is replaced as a whole; user data is
// never touched), rolls back on failure and restarts the manager.
public sealed class ZipUpdateInstaller : IUpdateInstaller
{
	private readonly string _appDir;
	private readonly string _runDir;
	private readonly string _exe;
	private readonly Func<bool> _requestExit;
	private readonly Func<bool> _idle;
	private readonly Func<ProcessStartInfo, Action?> _start;

	/// <param name="requestExit">
	/// 让程序退出并等待结果：true 表示程序确定在退出（窗口的保存流程没有否决），false 表示没能退出（保存失败、超时等）。
	/// </param>
	/// <param name="start">
	/// 启动替换脚本。返回「取消」动作（用于在程序没能退出时杀掉还在等待的脚本）；返回 null 表示没能启动。
	/// </param>
	public ZipUpdateInstaller(string appDir, string runDir, string exe, Func<bool> requestExit, Func<bool>? idle = null, Func<ProcessStartInfo, Action?>? start = null)
	{
		_appDir = appDir.TrimEnd('\\', '/');
		_runDir = runDir;
		_exe = exe;
		_requestExit = requestExit;
		_idle = idle ?? (() => true);
		_start = start ?? StartHelper;
	}

	public bool IsIdle => _idle();

	/// <summary>更新包只允许带这些位置的文件；其余（settings.json、mods、config、backups、data\indexes …）一律拒绝。</summary>
	public static readonly string[] AllowedFiles = { "HD2ModManager.exe", "README.md", "hd2mod.cmd", InstalledPackage.FileName };
	public static readonly string[] AllowedDirectories = { "bin", "third_party", "hd2mod-docs", @"data\resources" };

	/// <summary>相对路径（任一分隔符）是否在更新包允许的范围内。</summary>
	public static bool IsAllowedPackagePath(string relative)
	{
		var path = relative.Replace('/', '\\').TrimStart('\\');
		if (path.Split('\\').Any(segment => segment is ".." or "." or "")) return false;
		if (AllowedFiles.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase))) return true;
		return AllowedDirectories.Any(d => path.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase));
	}

	private static Action? StartHelper(ProcessStartInfo psi)
	{
		var process = Process.Start(psi);
		if (process is null) return null;
		return () =>
		{
			try { if (!process.HasExited) process.Kill(true); }
			catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
		};
	}

	public string ScriptPath => Path.Combine(_runDir, "apply-update.ps1");

	public void Install(InstallRequest request)
	{
		var stage = Path.Combine(_runDir, "stage-" + request.Version);
		try
		{
			Extract(request, stage);
			File.WriteAllText(ScriptPath, ScriptText, new UTF8Encoding(true));
			var psi = new ProcessStartInfo("powershell.exe")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden,
				WorkingDirectory = _runDir,
			};
			foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ScriptPath,
				"-ProcessId", Environment.ProcessId.ToString(), "-AppDir", _appDir, "-Stage", stage, "-Exe", _exe,
				"-Version", request.Version, "-ResultFile", UpdateApplyResult.ResultPath(_runDir), "-LogFile", Path.Combine(_appDir, "logs", "app-update.log") })
				psi.ArgumentList.Add(a);
			// 先启动脚本并确认它真的起来了；它此时只是在等本进程退出，不会碰任何文件。
			var cancel = _start(psi) ?? throw new UpdateException(UpdateErrorCode.Failed, "Could not start the update script");
			bool exiting;
			try { exiting = _requestExit(); }
			catch { cancel(); throw; }
			if (!exiting)
			{
				// 窗口的保存流程否决了关闭（或超时）：旧实例还活着，把还在等待的脚本撤掉，更新留待重试。
				cancel();
				throw new UpdateException(UpdateErrorCode.Failed, "The manager did not close (saving was refused or timed out); the update was not applied");
			}
		}
		catch (UpdateException) { TryDeleteDir(stage); throw; }
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
		{
			TryDeleteDir(stage);
			throw new UpdateException(UpdateErrorCode.Failed, "Installing the update failed: " + e.Message, e);
		}
	}

	/// <summary>解压并校验：条目不得越出目录；必须带 HD2ModManager.exe 和 update-package.json，且版本与安装变体匹配；磁盘要放得下。</summary>
	internal void Extract(InstallRequest request, string stage)
	{
		var installed = InstalledPackage.Read(_appDir) ?? throw new UpdateException(UpdateErrorCode.NotInstallable, "This build cannot install updates");
		TryDeleteDir(stage);
		Directory.CreateDirectory(stage);
		var root = Path.GetFullPath(stage).TrimEnd('\\') + "\\";
		try
		{
			using var zip = ZipFile.OpenRead(request.File);
			long total = 0;
			foreach (var e in zip.Entries) total += e.Length;
			var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_runDir))!).AvailableFreeSpace;
			if (free < total * 5 / 2) throw new UpdateException(UpdateErrorCode.DiskSpace, "Not enough free disk space to unpack the update");
			foreach (var e in zip.Entries)
			{
				var isDirectory = e.FullName.EndsWith('/') || e.FullName.EndsWith('\\');
				if (!isDirectory && !IsAllowedPackagePath(e.FullName)) throw new UpdateException(UpdateErrorCode.BadPackage, $"Package contains a path outside the shipped files: {e.FullName}");
				var target = Path.GetFullPath(Path.Combine(stage, e.FullName));
				if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new UpdateException(UpdateErrorCode.BadPackage, "Package entry escapes the target folder");
				if (isDirectory) { Directory.CreateDirectory(target); continue; }
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				e.ExtractToFile(target, true);
			}
		}
		catch (InvalidDataException e) { throw new UpdateException(UpdateErrorCode.BadPackage, "The update package is not a valid zip", e); }
		if (!File.Exists(Path.Combine(stage, "HD2ModManager.exe"))) throw new UpdateException(UpdateErrorCode.BadPackage, "Package has no HD2ModManager.exe");
		var marker = Path.Combine(stage, InstalledPackage.FileName);
		var staged = File.Exists(marker) ? InstalledPackage.Read(stage) : null;
		if (staged is null || staged.Version != request.Version) throw new UpdateException(UpdateErrorCode.BadPackage, "Package marker does not match the announced version");
		if (staged.Variant != installed.Variant) throw new UpdateException(UpdateErrorCode.BadPackage, "Package variant differs from the installed one");
	}

	/// <summary>启动时清理上次的暂存与脚本（更新已应用或已放弃）。</summary>
	public static void CleanupStage(string runDir)
	{
		try
		{
			if (!Directory.Exists(runDir)) return;
			foreach (var d in Directory.EnumerateDirectories(runDir, "stage-*")) TryDeleteDir(d); // backup-* 不动：回滚不完整时它是唯一的恢复副本
			foreach (var f in new[] { "apply-update.ps1" }) { try { File.Delete(Path.Combine(runDir, f)); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private static void TryDeleteDir(string path)
	{
		try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}

	internal const string ScriptText = """
param([int]$ProcessId, [string]$AppDir, [string]$Stage, [string]$Exe, [string]$Version, [string]$ResultFile, [string]$LogFile)
$ErrorActionPreference = 'Stop'
$AppDir = $AppDir.TrimEnd('\')
$Stage = $Stage.TrimEnd('\')
$Backup = Join-Path (Split-Path -Parent $Stage) ("backup-" + $Version + "-" + (Get-Date -Format 'yyyyMMddHHmmssfff'))
$swapped = $false
$created = @()
$rolledBack = $false
$restart = $false

function Log($m) {
    try {
        New-Item -ItemType Directory -Path (Split-Path -Parent $LogFile) -Force | Out-Null
        Add-Content -LiteralPath $LogFile -Value ('{0} {1}' -f (Get-Date -Format s), $m) -Encoding UTF8
    } catch { }
}
function Result($ok, $msg) {
    try {
        New-Item -ItemType Directory -Path (Split-Path -Parent $ResultFile) -Force | Out-Null
        (@{ ok = $ok; version = $Version; message = $msg } | ConvertTo-Json) | Set-Content -LiteralPath $ResultFile -Encoding UTF8
    } catch { }
}
function Rel($root, $full) { $full.Substring($root.Length + 1) }

try {
    Log "update ${Version}: waiting for process $ProcessId to exit"
    $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($p -and -not $p.WaitForExit(90000)) { throw 'The old manager did not exit in time.' }
    # From here on the old instance is gone, so a restart can never produce a second running copy.
    $restart = $true

    # Command-line tools started from bin\ must be gone as well, or their files cannot be replaced.
    $deadline = (Get-Date).AddSeconds(30)
    while ($true) {
        $busy = @(Get-Process | Where-Object { try { $_.Id -ne $PID -and $_.Path -and $_.Path.StartsWith("$AppDir\", [StringComparison]::OrdinalIgnoreCase) } catch { $false } })
        if ($busy.Count -eq 0) { break }
        if ((Get-Date) -gt $deadline) { throw ('Files are in use by: ' + (($busy | Select-Object -First 3 -ExpandProperty ProcessName) -join ', ')) }
        Start-Sleep -Milliseconds 500
    }

    $files = @(Get-ChildItem -LiteralPath $Stage -Recurse -File | ForEach-Object { Rel $Stage $_.FullName })
    if ($files.Count -eq 0) { throw 'The staged update is empty.' }
    # Only shipped files may be replaced. The app already refuses other paths; this is the second, independent check.
    $allowedFiles = @('HD2ModManager.exe', 'README.md', 'hd2mod.cmd', 'update-package.json')
    $allowedDirs = @('bin\', 'third_party\', 'hd2mod-docs\', 'data\resources\')
    foreach ($rel in $files) {
        $ok = $allowedFiles -contains $rel
        foreach ($d in $allowedDirs) { if ($rel.StartsWith($d, [StringComparison]::OrdinalIgnoreCase)) { $ok = $true } }
        if (-not $ok) { throw "The update contains a path outside the shipped files: $rel" }
    }

    if (Test-Path -LiteralPath $Backup) { Remove-Item -LiteralPath $Backup -Recurse -Force }
    New-Item -ItemType Directory -Path $Backup -Force | Out-Null
    foreach ($rel in $files) {
        if ($rel.StartsWith('bin\', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $dest = Join-Path $AppDir $rel
        if (Test-Path -LiteralPath $dest) {
            $b = Join-Path $Backup $rel
            New-Item -ItemType Directory -Path (Split-Path -Parent $b) -Force | Out-Null
            Copy-Item -LiteralPath $dest -Destination $b -Force
        }
    }

    $swapped = $true
    $binDir = Join-Path $AppDir 'bin'
    if (Test-Path -LiteralPath $binDir) { Move-Item -LiteralPath $binDir -Destination (Join-Path $Backup 'bin.old') }
    foreach ($rel in $files) {
        $dest = Join-Path $AppDir $rel
        if (-not (Test-Path -LiteralPath $dest)) { $created += $rel }
        New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $Stage $rel) -Destination $dest -Force
    }
    foreach ($rel in $files) {
        if ((Get-Item -LiteralPath (Join-Path $AppDir $rel)).Length -ne (Get-Item -LiteralPath (Join-Path $Stage $rel)).Length) { throw "Verification failed: $rel" }
    }
    Log "update ${Version} applied ($($files.Count) files)"
    Result $true ''
    Remove-Item -LiteralPath $Stage -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $Backup -Recurse -Force -ErrorAction SilentlyContinue
}
catch {
    $reason = "$_"
    Log "update ${Version} failed: ${reason}"
    $note = ''
    if ($swapped) {
        try {
            $binDir = Join-Path $AppDir 'bin'
            $old = Join-Path $Backup 'bin.old'
            if (Test-Path -LiteralPath $old) {
                if (Test-Path -LiteralPath $binDir) { Remove-Item -LiteralPath $binDir -Recurse -Force }
                Move-Item -LiteralPath $old -Destination $binDir
            }
            foreach ($rel in $created) { Remove-Item -LiteralPath (Join-Path $AppDir $rel) -Force -ErrorAction SilentlyContinue }
            foreach ($f in @(Get-ChildItem -LiteralPath $Backup -Recurse -File)) {
                $rel = Rel $Backup $f.FullName
                Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $AppDir $rel) -Force
            }
            Log 'rolled back'
            $rolledBack = $true
        } catch {
            $note = " Rollback was incomplete; a backup of the previous files is kept in $Backup. Reinstall the manager from the release page if it does not start."
            Log "rollback failed: $_"
        }
    } else { $rolledBack = $true }
    if ($rolledBack) {
        Remove-Item -LiteralPath $Stage -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $Backup -Recurse -Force -ErrorAction SilentlyContinue
    }
    Result $false ($reason + $note)
}
finally {
    if ($restart) {
        try { Start-Process -FilePath $Exe -WorkingDirectory $AppDir } catch { Log "restart failed: $_" }
    }
}
""";
}
