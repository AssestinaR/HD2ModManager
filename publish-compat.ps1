[CmdletBinding()]
param(
    [string] $OutputDirectory = "artifacts\publish-compat",
    [switch] $KeepExistingOutput
)

# Keep native dotnet output readable in both Windows PowerShell 5.1 and
# PowerShell 7. The entry script is intentionally ASCII-safe; the same setup
# is also applied by tools\PublishRelease.ps1 for direct invocation.
$publishUtf8 = New-Object System.Text.UTF8Encoding($false)
try { & chcp.com 65001 | Out-Null } catch { }
[Console]::InputEncoding = $publishUtf8
[Console]::OutputEncoding = $publishUtf8
$OutputEncoding = $publishUtf8

# Targeted compatibility release for older Windows 10 / older hardware.
# The normal release remains unchanged; this only passes CETCompat=false to
# the Manager and CLI publish invocations and labels the resulting ZIP files.
& (Join-Path $PSScriptRoot 'tools\PublishRelease.ps1') `
    -OutputDirectory $OutputDirectory `
    -CompatibilityMode `
    -KeepExistingOutput:$KeepExistingOutput
exit $LASTEXITCODE
