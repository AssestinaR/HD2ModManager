[CmdletBinding()]
param(
    [string] $OutputDirectory = "artifacts\publish-compat",
    [switch] $KeepExistingOutput
)

# Targeted compatibility release for older Windows 10 / older hardware.
# The normal release remains unchanged; this only passes CETCompat=false to
# the Manager and CLI publish invocations and labels the resulting ZIP files.
& (Join-Path $PSScriptRoot 'tools\PublishRelease.ps1') `
    -OutputDirectory $OutputDirectory `
    -CompatibilityMode `
    -KeepExistingOutput:$KeepExistingOutput
exit $LASTEXITCODE
