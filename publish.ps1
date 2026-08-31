[CmdletBinding()]
param(
    [string] $OutputDirectory = "artifacts\\publish\\HD2ModManager-win-x64",
    [switch] $KeepExistingOutput
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$managerProject = Join-Path $repoRoot 'HD2ModManager\\HD2ModManager.csproj'
$cliProject = Join-Path $repoRoot 'HD2ModCli\\HD2ModCli.csproj'
$cliOutput = Join-Path $repoRoot 'HD2ModCli\\bin\\Release\\net10.0\\win-x64'

if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $publishDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    $publishDirectory = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}

$relativePublishDirectory = [System.IO.Path]::GetRelativePath($repoRoot, $publishDirectory)
if ([System.IO.Path]::IsPathRooted($relativePublishDirectory) -or
    $relativePublishDirectory -eq '..' -or
    $relativePublishDirectory.StartsWith("..$([System.IO.Path]::DirectorySeparatorChar)")) {
    throw "OutputDirectory must be inside the repository: $repoRoot"
}

function Invoke-DotNet {
    param([string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

if (-not $KeepExistingOutput -and (Test-Path -LiteralPath $publishDirectory)) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

# The Manager's build target copies CLI artifacts from this RID-specific folder.
Invoke-DotNet @('build', $cliProject, '--configuration', 'Release', '--runtime', 'win-x64', '--nologo')

$requiredCliFiles = @('hd2mod.exe', 'hd2mod.deps.json', 'hd2mod.runtimeconfig.json')
foreach ($file in $requiredCliFiles) {
    $path = Join-Path $cliOutput $file
    if (-not (Test-Path -LiteralPath $path)) {
        throw "CLI build did not produce required file: $path"
    }
}

Invoke-DotNet @(
    'publish', $managerProject,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--output', $publishDirectory,
    '--nologo',
    '-p:PublishSingleFile=true',
    '-p:PublishReadyToRun=false'
)

# PublishSingleFile packages the Manager only. Keep the companion CLI and all
# of its runtime dependencies beside it so hd2mod.exe remains directly usable.
Get-ChildItem -LiteralPath $cliOutput -File -Recurse | ForEach-Object {
    $relativePath = [System.IO.Path]::GetRelativePath($cliOutput, $_.FullName)
    $destination = Join-Path $publishDirectory $relativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
}

$requiredPublishFiles = @('HD2ModManager.exe', 'hd2mod.exe', 'hd2mod.deps.json', 'hd2mod.runtimeconfig.json')
foreach ($file in $requiredPublishFiles) {
    $path = Join-Path $publishDirectory $file
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Publish output is incomplete; missing: $path"
    }
}

Write-Host "Publish completed: $publishDirectory"
