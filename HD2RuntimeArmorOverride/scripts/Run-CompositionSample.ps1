param(
    [int]$StartupTimeoutSeconds = 90,
    [int]$SamplingTimeoutSeconds = 780
)

$ErrorActionPreference = "Stop"
$log = Join-Path $env:LOCALAPPDATA `
    "CowboyBingus\Helldivers2\Logs\ArmorCompositionProbe_runtime.log"
$existingIds = @(
    Get-Process helldivers2 -ErrorAction SilentlyContinue |
        ForEach-Object Id
)
if ($existingIds.Count -gt 0) {
    throw "A game process is already running; no sampling process was launched."
}
if (Test-Path -LiteralPath $log) {
    $evidence = Join-Path $PSScriptRoot "../evidence"
    New-Item -ItemType Directory -Path $evidence -Force | Out-Null
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss-ffff"
    Move-Item -LiteralPath $log -Destination (Join-Path $evidence "pre-run-$stamp.jsonl")
}
Start-Process "steam://rungameid/553850" -WindowStyle Hidden

$game = $null
for ($second = 0; $second -lt $StartupTimeoutSeconds -and -not $game; $second++) {
    Start-Sleep -Seconds 1
    $game = Get-Process helldivers2 -ErrorAction SilentlyContinue |
        Where-Object { $existingIds -notcontains $_.Id } |
        Select-Object -First 1
}
if (-not $game) {
    throw "helldivers2 did not start within $StartupTimeoutSeconds seconds."
}

Write-Output "Started helldivers2 PID $($game.Id)"
try {
    for ($second = 1; $second -le $SamplingTimeoutSeconds; $second++) {
        Start-Sleep -Seconds 1
        if ($game.HasExited) {
            Write-Output "Game exited before the sampling timeout."
            break
        }
        if (Test-Path -LiteralPath $log) {
            $tail = Get-Content -LiteralPath $log -Tail 1 -ErrorAction SilentlyContinue
            if ($tail -match '"record_type":"(composition_scan|probe_error|probe_timeout)"') {
                Write-Output "Probe terminal record observed after $second seconds."
                break
            }
        }
    }
}
finally {
    if (-not $game.HasExited) {
        Stop-Process -Id $game.Id -Force -ErrorAction SilentlyContinue
        Write-Output "Stopped helldivers2 PID $($game.Id)"
    }
}
