# Dev helper: build the latest HD2ModManager (incremental, near-instant when nothing changed) and launch it.
# Usage: tools\run-hd2.cmd [Debug|Release]   (default: Release)
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'HD2ModManager\HD2ModManager.csproj'
$exe  = Join-Path $repo "HD2ModManager\bin\$Configuration\net10.0-windows\HD2ModManager.exe"

$running = Get-Process -Name 'HD2ModManager' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "HD2ModManager is still running (PID $($running.Id -join ',')). Close it first, then run this again." -ForegroundColor Yellow
    exit 2
}

Write-Host "Building ($Configuration) ..."
& dotnet build $proj -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build FAILED - not launching." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "Launching $exe"
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
