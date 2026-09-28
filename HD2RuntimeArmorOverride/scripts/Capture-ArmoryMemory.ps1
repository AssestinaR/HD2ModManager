param(
    [Parameter(Mandatory)][ValidateSet('before','after')][string]$Phase,
    [Parameter(Mandatory)][int]$GamePid,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
$game = Get-Process -Id $GamePid -ErrorAction Stop
$started = $game.StartTime.ToUniversalTime().ToString('o')
$registry = Join-Path $env:LOCALAPPDATA 'CowboyBingus\Helldivers2\ArmorRegistry'
$status = Join-Path $registry 'runtime.status'
$statusLines = @(Get-Content -LiteralPath $status -ErrorAction Stop)
$identity = @{}
foreach ($line in $statusLines) {
    $pair = $line -split '=', 2
    if ($pair.Count -eq 2 -and $pair[0] -in @('pid','revision','state','active_mappings','retained_clone_count')) {
        $identity[$pair[0]] = $pair[1]
    }
}
if ($identity.pid -ne "$GamePid" -or $identity.state -ne 'ready') {
    throw '当前游戏 PID 与已就绪的 Lua 状态不一致。'
}
$gpuPaths = @(Get-Counter -ListSet 'GPU Process Memory' -ErrorAction SilentlyContinue |
    ForEach-Object PathsWithInstances | Where-Object {
        $_ -match "\\GPU Process Memory\(pid_${GamePid}_.*\)\\(Dedicated|Shared) Usage$"
    })
$counters = @(
    '\Memory\Available MBytes',
    '\Memory\Committed Bytes',
    '\Memory\Commit Limit',
    '\Memory\Standby Cache Normal Priority Bytes',
    '\Memory\Modified Page List Bytes'
) + $gpuPaths
$rows = @()
for ($i = 0; $i -lt 8; $i++) {
    $game.Refresh()
    if ($game.HasExited -or $game.StartTime.ToUniversalTime().ToString('o') -ne $started) {
        throw '游戏已退出或 PID 被复用，对照无效。'
    }
    $counterValues = @{}
    try {
        $counter = Get-Counter -Counter $counters -ErrorAction Stop
        foreach ($sample in $counter.CounterSamples) {
            $counterValues[$sample.Path.ToLowerInvariant()] = [double]$sample.CookedValue
        }
    } catch {
        $counterValues['collection_error'] = $_.Exception.GetType().Name
    }
    $io = Get-CimInstance Win32_PerfRawData_PerfProc_Process -Filter "IDProcess=$GamePid" -ErrorAction SilentlyContinue
    $gpuDedicated = 0.0
    $gpuShared = 0.0
    foreach ($entry in $counterValues.GetEnumerator()) {
        if ($entry.Key -match "\\gpu process memory\(pid_${GamePid}_.*\)\\dedicated usage$") {
            $gpuDedicated += $entry.Value
        }
        if ($entry.Key -match "\\gpu process memory\(pid_${GamePid}_.*\)\\shared usage$") {
            $gpuShared += $entry.Value
        }
    }
    $rows += [pscustomobject]@{
        utc = [DateTimeOffset]::UtcNow.ToString('o')
        workingSetBytes = $game.WorkingSet64
        privateBytes = $game.PrivateMemorySize64
        handleCount = $game.HandleCount
        ioReadBytesTotal = if ($io) { [long]$io.IOReadBytesPersec } else { $null }
        ioReadOperationsTotal = if ($io) { [long]$io.IOReadOperationsPersec } else { $null }
        gpuDedicatedBytes = if ($gpuPaths.Count) { $gpuDedicated } else { $null }
        gpuSharedBytes = if ($gpuPaths.Count) { $gpuShared } else { $null }
        memoryCounters = $counterValues
    }
    Start-Sleep -Milliseconds 750
}
$game.Dispose()
$report = [pscustomobject]@{
    schema = 1
    phase = $Phase
    pid = $GamePid
    processStartUtc = $started
    runtime = $identity
    armoryState = 'operator_reported'
    counterPaths = $counters
    samples = $rows
}
$parent = Split-Path -Parent $Output
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output -Encoding UTF8
$report | Select-Object phase,pid,processStartUtc,runtime
