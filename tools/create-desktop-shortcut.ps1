# Dev helper: create a Desktop shortcut "HD2 Mod Manager (dev)" that builds the latest code, launches it and then keeps watching
# the source: it rebuilds and restarts the app on changes (tools\watch-hd2.cmd). Uses the app icon. Shortcuts hold absolute paths, so this is generated per machine instead of committed.
# Usage: pwsh tools\create-desktop-shortcut.ps1 [-Configuration Debug|Release]
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$cmd  = Join-Path $repo 'tools\watch-hd2.cmd'
$icon = Join-Path $repo 'HD2ModManager\Resources\helldivers2.ico'
$lnk  = Join-Path ([Environment]::GetFolderPath('Desktop')) 'HD2 Mod Manager (dev).lnk'

$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($lnk)
$sc.TargetPath = $cmd
$sc.Arguments = $Configuration
$sc.WorkingDirectory = $repo
$sc.IconLocation = "$icon,0"
$sc.WindowStyle = 1   # normal window: it is the watcher's console, close it to stop watching
$sc.Description = 'Build latest HD2ModManager, launch it, and rebuild + restart when source changes'
$sc.Save()
Write-Host "Created $lnk"
