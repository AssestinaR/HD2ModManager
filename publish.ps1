[CmdletBinding()]
param(
    [string] $OutputDirectory = "artifacts\publish",
    [switch] $KeepExistingOutput
)

# Public Windows PowerShell entry point. The implementation is kept in tools
# so the two release layouts can be tested independently.
& (Join-Path $PSScriptRoot 'tools\PublishRelease.ps1') @PSBoundParameters
exit $LASTEXITCODE
