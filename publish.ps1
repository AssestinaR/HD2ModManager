[CmdletBinding()]
param(
    [string] $OutputDirectory = "artifacts\publish",
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

# Public Windows PowerShell entry point. The implementation is kept in tools
# so the two release layouts can be tested independently.
& (Join-Path $PSScriptRoot 'tools\PublishRelease.ps1') @PSBoundParameters
exit $LASTEXITCODE
