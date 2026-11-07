# Dev helper: keep HD2ModManager running and auto-rebuild + restart it when source code changes.
#
#   tools\watch-hd2.cmd [Debug|Release] [-SettleSeconds 4]
#
# How it works
#   * A FileSystemWatcher marks the tree dirty on any change to .cs/.xaml/.csproj/.props/.targets/.resx
#     (bin/obj/.git/.vs/outputs/docs and *.Tests projects are ignored).
#   * Once the last change is older than -SettleSeconds (default 4s), the app this script launched is
#     stopped, the project is rebuilt, and the app is started again. The settle window means a burst of
#     edits (a multi-file change) causes ONE rebuild instead of one per file.
#   * If the build fails the old app is NOT restarted; the error is printed and the script waits for the
#     next change.
#   * Ctrl+C stops the watcher and the app this script launched.
# The app's in-memory state is lost on every restart (it is a full process restart, not hot reload).
param(
    [string]$Configuration = 'Release',
    [int]$SettleSeconds = 4,
    [switch]$NoLaunch   # build/watch only; do not start the app (used for testing the watcher itself)
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'HD2ModManager\HD2ModManager.csproj'
$exe  = Join-Path $repo "HD2ModManager\bin\$Configuration\net10.0-windows\HD2ModManager.exe"

if (-not $NoLaunch -and (Get-Process -Name 'HD2ModManager' -ErrorAction SilentlyContinue)) {
    Write-Host "HD2ModManager is already running. Close it first, then start the watcher." -ForegroundColor Yellow
    exit 2
}

$state = [hashtable]::Synchronized(@{ Last = [datetime]::MinValue; Dirty = $false; Reason = '' })
$script:app = $null

function Stop-App {
    if ($script:app -and -not $script:app.HasExited) {
        Write-Host "[$(Get-Date -Format HH:mm:ss)] stopping app (PID $($script:app.Id))"
        $null = $script:app.CloseMainWindow()
        if (-not $script:app.WaitForExit(4000)) { $script:app.Kill(); $script:app.WaitForExit() }
    }
    $script:app = $null
}

function Build-And-Start {
    Write-Host "[$(Get-Date -Format HH:mm:ss)] building ($Configuration) ..."
    & dotnet build $proj -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[$(Get-Date -Format HH:mm:ss)] build FAILED - app not restarted; waiting for the next change." -ForegroundColor Red
        return
    }
    if ($NoLaunch) { Write-Host "[$(Get-Date -Format HH:mm:ss)] build ok (-NoLaunch)"; return }
    $script:app = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
    Write-Host "[$(Get-Date -Format HH:mm:ss)] started app (PID $($script:app.Id))" -ForegroundColor Green
}

$watcher = New-Object System.IO.FileSystemWatcher $repo
$watcher.IncludeSubdirectories = $true
$watcher.NotifyFilter = [System.IO.NotifyFilters]'LastWrite, FileName, DirectoryName, Size'
$watcher.EnableRaisingEvents = $true

$action = {
    $path = $Event.SourceEventArgs.FullPath
    if ($path -match '\.(cs|xaml|csproj|props|targets|resx)$' -and
        $path -notmatch '\\(bin|obj|\.git|\.vs|outputs|docs|node_modules)\\' -and
        $path -notmatch '\.Tests\\') {
        $s = $Event.MessageData
        $s.Last = Get-Date
        $s.Dirty = $true
        $s.Reason = $path
    }
}
$subs = @(
    Register-ObjectEvent $watcher Changed -Action $action -MessageData $state
    Register-ObjectEvent $watcher Created -Action $action -MessageData $state
    Register-ObjectEvent $watcher Deleted -Action $action -MessageData $state
    Register-ObjectEvent $watcher Renamed -Action $action -MessageData $state
)

try {
    Write-Host "Watching $repo  (settle ${SettleSeconds}s, Ctrl+C to stop)"
    Build-And-Start
    while ($true) {
        Start-Sleep -Milliseconds 500
        if ($state.Dirty -and ((Get-Date) - $state.Last).TotalSeconds -ge $SettleSeconds) {
            $changed = $state.Reason
            $state.Dirty = $false
            Write-Host "[$(Get-Date -Format HH:mm:ss)] change settled (last: $changed)"
            Stop-App
            Build-And-Start
        }
    }
}
finally {
    $subs | ForEach-Object { Unregister-Event -SubscriptionId $_.Id -ErrorAction SilentlyContinue }
    $watcher.Dispose()
    Stop-App
}
