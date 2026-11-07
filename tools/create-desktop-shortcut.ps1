# Dev helper: create a Desktop shortcut "HD2 Mod Manager (dev)" that builds the latest code and launches it,
# using the app icon. Shortcuts hold absolute paths, so this is generated per machine instead of committed.
# Usage: pwsh tools\create-desktop-shortcut.ps1 [-Configuration Debug|Release]
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$cmd  = Join-Path $repo 'tools\run-hd2.cmd'
$icon = Join-Path $repo 'HD2ModManager\Resources\helldivers2.ico'
$lnk  = Join-Path ([Environment]::GetFolderPath('Desktop')) 'HD2 Mod Manager (dev).lnk'

$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($lnk)
$sc.TargetPath = $cmd
$sc.Arguments = $Configuration
$sc.WorkingDirectory = $repo
$sc.IconLocation = "$icon,0"
$sc.WindowStyle = 1   # normal window so a build failure stays visible (the .cmd pauses on error)
$sc.Description = 'Build latest HD2ModManager and launch it'
$sc.Save()
Write-Host "Created $lnk"
