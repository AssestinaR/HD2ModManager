# Dev helper: keep HD2ModManager running and auto-rebuild + restart it when source code changes.
#
#   tools\watch-hd2.cmd [Debug|Release] [-SettleSeconds 4]
#
# How it works
#   * A FileSystemWatcher marks the tree dirty on any change to .cs/.xaml/.csproj/.props/.targets/.resx
#     (bin/obj/.git/.vs/outputs/docs and *.Tests projects are ignored).
#   * Before restarting, the watched files are content-hashed and compared with the snapshot of the last
#     build; a save that did not change content (editor touch, git checkout/stash, formatter no-op, a build
#     step rewriting a file) is ignored, so the app is NOT restarted spuriously.
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

$excludeDir = '(?i)\\(bin|obj|\.[^\\]+|outputs|docs|artifacts|node_modules|publish)(\\|$)'
$watchFile  = '(?i)\.(cs|xaml|csproj|props|targets|resx)$'

# Hash every watched source file (pruning excluded directories) -> 'path' = 'sha1'.
function Get-Snapshot {
    $snap = @{}
    $stack = [System.Collections.Generic.Stack[string]]::new()
    $stack.Push($repo)
    while ($stack.Count -gt 0) {
        $dir = $stack.Pop()
        foreach ($d in [System.IO.Directory]::EnumerateDirectories($dir)) {
            $rel = $d.Substring($repo.Length)
            if ($rel -notmatch $excludeDir -and $d -notmatch '\.Tests$') { $stack.Push($d) }
        }
        foreach ($f in [System.IO.Directory]::EnumerateFiles($dir)) {
            if ($f -notmatch $watchFile) { continue }
            try {
                $snap[$f] = (Get-FileHash -LiteralPath $f -Algorithm SHA1 -ErrorAction Stop).Hash
            } catch { $snap[$f] = 'unreadable' }   # mid-write / locked: treated as a change, re-checked next settle
        }
    }
    return $snap
}

function Get-SnapshotDiff($old, $new) {
    if ($null -eq $old) { return @('(initial)') }
    $diff = foreach ($k in $new.Keys) { if ($old[$k] -ne $new[$k]) { $k } }
    $diff += foreach ($k in $old.Keys) { if (-not $new.ContainsKey($k)) { $k } }
    return @($diff)
}

$script:built = $null
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
        $path -notmatch '(?i)\\(bin|obj|\.[^\\]+|outputs|docs|artifacts|node_modules|publish)\\' -and
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
    $script:built = Get-Snapshot
    Build-And-Start
    while ($true) {
        Start-Sleep -Milliseconds 500
        if ($state.Dirty -and ((Get-Date) - $state.Last).TotalSeconds -ge $SettleSeconds) {
            $state.Dirty = $false
            $snap = Get-Snapshot
            $diff = Get-SnapshotDiff $script:built $snap
            if ($diff.Count -eq 0) {
                Write-Host "[$(Get-Date -Format HH:mm:ss)] file events but no content change (last: $($state.Reason)) - ignored"
                continue
            }
            $script:built = $snap   # taken BEFORE the build, so edits made during the build still trigger the next one
            Write-Host "[$(Get-Date -Format HH:mm:ss)] change settled ($($diff.Count) file(s), e.g. $($diff[0]))"
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
