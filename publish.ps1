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

$cliDocumentationSource = Join-Path $repoRoot 'HD2ModCli'

if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $publishDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    $publishDirectory = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}

$repoRootUri = New-Object -TypeName System.Uri -ArgumentList ([System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\')
$publishUri = New-Object -TypeName System.Uri -ArgumentList ($publishDirectory.TrimEnd('\') + '\')
$relativePublishDirectory = [System.Uri]::UnescapeDataString($repoRootUri.MakeRelativeUri($publishUri).ToString()).Replace('/', '\').TrimEnd('\')
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

$cliOutputUri = New-Object -TypeName System.Uri -ArgumentList ([System.IO.Path]::GetFullPath($cliOutput).TrimEnd('\') + '\')

# PublishSingleFile packages the Manager only. Keep the companion CLI and all
# of its runtime dependencies beside it so hd2mod.exe remains directly usable.
Get-ChildItem -LiteralPath $cliOutput -File -Recurse | ForEach-Object {
    $fileUri = New-Object -TypeName System.Uri -ArgumentList $_.FullName
    $relativePath = [System.Uri]::UnescapeDataString($cliOutputUri.MakeRelativeUri($fileUri).ToString()).Replace('/', '\')
    $destination = Join-Path $publishDirectory $relativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
}

# The executable embeds every Skill for `agent skills export`, while these
# files are the directly discoverable documentation shipped beside the GUI.
$documentationOutput = Join-Path $publishDirectory 'hd2mod-docs'
New-Item -ItemType Directory -Path $documentationOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $cliDocumentationSource 'README.md') -Destination (Join-Path $documentationOutput 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $cliDocumentationSource 'SKILL.md') -Destination (Join-Path $documentationOutput 'SKILL.md') -Force
Copy-Item -LiteralPath (Join-Path $cliDocumentationSource 'skills') -Destination $documentationOutput -Recurse -Force

$requiredPublishFiles = @(
    'HD2ModManager.exe',
    'hd2mod.exe',
    'hd2mod.deps.json',
    'hd2mod.runtimeconfig.json',
    'hd2mod-docs\README.md',
    'hd2mod-docs\SKILL.md',
    'hd2mod-docs\skills\safe-import\SKILL.md',
    'hd2mod-docs\skills\asset-metadata-management\SKILL.md'
)
foreach ($file in $requiredPublishFiles) {
    $path = Join-Path $publishDirectory $file
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Publish output is incomplete; missing: $path"
    }
}

Write-Host "Publish completed: $publishDirectory"
