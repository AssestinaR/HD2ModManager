[CmdletBinding()]
param(
    [string] $OutputDirectory = "artifacts\publish",
    [switch] $KeepExistingOutput
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$managerProject = Join-Path $repoRoot 'HD2ModManager\HD2ModManager.csproj'
$cliProject = Join-Path $repoRoot 'HD2ModCli\HD2ModCli.csproj'
$cliBuildOutput = Join-Path $repoRoot 'HD2ModCli\bin\Release\net10.0\win-x64'
$cliDocumentationSource = Join-Path $repoRoot 'HD2ModCli'
$releaseReadmeSource = Join-Path $repoRoot 'tools\ReleasePackageReadme.md'

if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $archiveDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    $archiveDirectory = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}

$repoRootUri = New-Object -TypeName System.Uri -ArgumentList ([System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\')
$archiveUri = New-Object -TypeName System.Uri -ArgumentList ($archiveDirectory.TrimEnd('\') + '\')
$relativeArchiveDirectory = [System.Uri]::UnescapeDataString($repoRootUri.MakeRelativeUri($archiveUri).ToString()).Replace('/', '\').TrimEnd('\')
if ([System.IO.Path]::IsPathRooted($relativeArchiveDirectory) -or
    $relativeArchiveDirectory -eq '..' -or
    $relativeArchiveDirectory.StartsWith("..$([System.IO.Path]::DirectorySeparatorChar)")) {
    throw "OutputDirectory must be inside the repository: $repoRoot"
}

function Invoke-DotNet {
    param([string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Remove-PublishOwnedPath {
    param([string] $Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Get-TargetRuntimeMajorMinor {
    param([string] $ProjectPath)

    [xml] $project = Get-Content -LiteralPath $ProjectPath -Raw
    $targetFramework = @($project.Project.PropertyGroup |
        ForEach-Object { $_.TargetFramework } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1)[0]
    if ($targetFramework -notmatch '^net(?<version>\d+\.\d+)') {
        throw "Unable to determine the .NET runtime family from $ProjectPath."
    }
    return $Matches['version']
}

function Get-PrivateRuntimeLayout {
    param([string] $TargetMajorMinor)

    $dotnetExecutable = (Get-Command dotnet -ErrorAction Stop).Source
    $dotnetRoot = Split-Path -Parent $dotnetExecutable
    $runtimePattern = "$TargetMajorMinor.*"
    $coreRoot = Join-Path $dotnetRoot 'shared\Microsoft.NETCore.App'
    $desktopRoot = Join-Path $dotnetRoot 'shared\Microsoft.WindowsDesktop.App'
    $hostFxrRoot = Join-Path $dotnetRoot 'host\fxr'

    $coreVersions = @(Get-ChildItem -LiteralPath $coreRoot -Directory |
        Where-Object { $_.Name -like $runtimePattern } |
        ForEach-Object { $_.Name })
    $desktopVersions = @(Get-ChildItem -LiteralPath $desktopRoot -Directory |
        Where-Object { $_.Name -like $runtimePattern } |
        ForEach-Object { $_.Name })
    $runtimeVersion = @($coreVersions |
        Where-Object { $desktopVersions -contains $_ } |
        Sort-Object { [version] $_ } |
        Select-Object -Last 1)[0]
    if ([string]::IsNullOrWhiteSpace($runtimeVersion)) {
        throw "No matching Microsoft.NETCore.App and Microsoft.WindowsDesktop.App $runtimePattern runtime is installed under $dotnetRoot."
    }

    $hostFxrVersion = @(Get-ChildItem -LiteralPath $hostFxrRoot -Directory |
        Where-Object { $_.Name -like $runtimePattern } |
        Sort-Object { [version] $_.Name } |
        Select-Object -Last 1 |
        ForEach-Object { $_.Name })[0]
    if ([string]::IsNullOrWhiteSpace($hostFxrVersion)) {
        throw "No hostfxr $runtimePattern runtime is installed under $dotnetRoot."
    }

    return [pscustomobject]@{
        DotNetRoot = $dotnetRoot
        RuntimeVersion = $runtimeVersion
        HostFxrVersion = $hostFxrVersion
        HostFxrDirectory = Join-Path $hostFxrRoot $hostFxrVersion
        CoreDirectory = Join-Path $coreRoot $runtimeVersion
        WindowsDesktopDirectory = Join-Path $desktopRoot $runtimeVersion
    }
}

function Copy-PrivateRuntime {
    param(
        [pscustomobject] $Layout,
        [string] $Destination
    )

    Remove-PublishOwnedPath $Destination
    New-Item -ItemType Directory -Path (Join-Path $Destination 'host\fxr') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $Destination 'shared\Microsoft.NETCore.App') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $Destination 'shared\Microsoft.WindowsDesktop.App') -Force | Out-Null

    Copy-Item -LiteralPath $Layout.HostFxrDirectory -Destination (Join-Path $Destination 'host\fxr') -Recurse -Force
    Copy-Item -LiteralPath $Layout.CoreDirectory -Destination (Join-Path $Destination 'shared\Microsoft.NETCore.App') -Recurse -Force
    Copy-Item -LiteralPath $Layout.WindowsDesktopDirectory -Destination (Join-Path $Destination 'shared\Microsoft.WindowsDesktop.App') -Recurse -Force
}

function Remove-LegacyCliRootArtifacts {
    param([string] $PackageDirectory)

    # HD2ModManager references the CLI only as a build input. During publish
    # that target can leave its normal-build entry artifacts at the package
    # root, where they do not belong in either release layout.
    if (Test-Path -LiteralPath $cliBuildOutput) {
        Get-ChildItem -LiteralPath $cliBuildOutput -File | ForEach-Object {
            $candidate = Join-Path $PackageDirectory $_.Name
            if (Test-Path -LiteralPath $candidate) {
                Remove-PublishOwnedPath $candidate
            }
        }
    }

    foreach ($name in @('hd2mod.exe', 'hd2mod.dll', 'hd2mod.deps.json', 'hd2mod.runtimeconfig.json', 'hd2mod.pdb')) {
        Remove-PublishOwnedPath (Join-Path $PackageDirectory $name)
    }
}

function Write-RootReadme {
    param(
        [string] $PackageDirectory,
        [bool] $IncludesPrivateRuntime,
        [pscustomobject] $RuntimeLayout
    )

    if (-not (Test-Path -LiteralPath $releaseReadmeSource)) {
        throw "Release README source is missing: $releaseReadmeSource"
    }

    $runtimeInstructions = if ($IncludesPrivateRuntime) {
        "This package includes the shared .NET $($RuntimeLayout.RuntimeVersion) Windows Desktop Runtime. bin\dotnet is the private runtime shared by Manager and CLI."
    }
    else {
        "This package does not include .NET Runtime. Install .NET $($RuntimeLayout.RuntimeVersion.Split('.')[0]) Windows Desktop Runtime (x64) first. If it is missing, the .NET host shows an install prompt when HD2ModManager.exe starts."
    }

    $content = (Get-Content -LiteralPath $releaseReadmeSource -Raw -Encoding UTF8).Replace('{{RUNTIME_INSTRUCTIONS}}', $runtimeInstructions)
    $encoding = New-Object -TypeName System.Text.UTF8Encoding -ArgumentList $false
    [System.IO.File]::WriteAllText(
        (Join-Path $PackageDirectory 'README.md'),
        ($content.TrimEnd() + [Environment]::NewLine),
        $encoding)
}

function Write-CliLauncher {
    param([string] $PackageDirectory)

    $content = @'
@echo off
"%~dp0bin\hd2mod.exe" %*
exit /b %ERRORLEVEL%
'@
    $encoding = New-Object -TypeName System.Text.UTF8Encoding -ArgumentList $false
    [System.IO.File]::WriteAllText((Join-Path $PackageDirectory 'hd2mod.cmd'), $content.TrimEnd() + [Environment]::NewLine, $encoding)
}

function Assert-ReleaseLayout {
    param(
        [string] $PackageDirectory,
        [bool] $IncludesPrivateRuntime,
        [pscustomobject] $RuntimeLayout
    )

    $requiredFiles = @(
        'HD2ModManager.exe',
        'README.md',
        'hd2mod.cmd',
        'data\resources\bonehash.txt',
        'third_party\7zip\7z.exe',
        'third_party\7zip\7z.dll',
        'bin\hd2mod.exe',
        'bin\third_party\7zip\7z.exe',
        'bin\third_party\7zip\7z.dll',
        'hd2mod-docs\README.md',
        'hd2mod-docs\SKILL.md',
        'hd2mod-docs\skills\hd2mod-agent-skills.manifest.json',
        'hd2mod-docs\skills\hd2mod-safe-import\SKILL.md',
        'hd2mod-docs\skills\hd2mod-asset-metadata-management\SKILL.md'
    )

    if ($IncludesPrivateRuntime) {
        $requiredFiles += @(
            "bin\dotnet\host\fxr\$($RuntimeLayout.HostFxrVersion)\hostfxr.dll",
            "bin\dotnet\shared\Microsoft.NETCore.App\$($RuntimeLayout.RuntimeVersion)\System.Private.CoreLib.dll",
            "bin\dotnet\shared\Microsoft.WindowsDesktop.App\$($RuntimeLayout.RuntimeVersion)\PresentationFramework.dll"
        )
    }
    elseif (Test-Path -LiteralPath (Join-Path $PackageDirectory 'bin\dotnet')) {
        throw "The framework-dependent package unexpectedly contains bin\dotnet."
    }

    foreach ($relativePath in $requiredFiles) {
        $path = Join-Path $PackageDirectory $relativePath
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Publish output is incomplete; missing: $path"
        }
    }

    $unexpectedRootBinaries = @(Get-ChildItem -LiteralPath $PackageDirectory -File |
        Where-Object { $_.Extension -in @('.dll', '.pdb') })
    if ($unexpectedRootBinaries.Count -gt 0) {
        throw "The release root must remain user-facing; unexpected binaries: $($unexpectedRootBinaries.Name -join ', ')"
    }

    $legacyRootArtifacts = @('hd2mod.exe', 'hd2mod.dll', 'hd2mod.deps.json', 'hd2mod.runtimeconfig.json', 'hd2mod.pdb') |
        Where-Object { Test-Path -LiteralPath (Join-Path $PackageDirectory $_) }
    if ($legacyRootArtifacts.Count -gt 0) {
        throw "Legacy CLI artifacts leaked into the release root: $($legacyRootArtifacts -join ', ')"
    }
}

function Compress-ReleaseStage {
    param(
        [string] $StageDirectory,
        [string] $ArchivePath
    )

    if (Test-Path -LiteralPath $ArchivePath) {
        throw "Timestamp collision: archive already exists: $ArchivePath"
    }

    Compress-Archive -Path (Join-Path $StageDirectory '*') -DestinationPath $ArchivePath -CompressionLevel Optimal
    if (-not (Test-Path -LiteralPath $ArchivePath)) {
        throw "Archive creation failed: $ArchivePath"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName })
        foreach ($requiredEntry in @('HD2ModManager.exe', 'README.md', 'hd2mod.cmd')) {
            if ($entries -notcontains $requiredEntry) {
                throw "Archive is missing required root entry: $requiredEntry"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Publish-ReleaseVariant {
    param(
        [string] $VariantName,
        [bool] $IncludesPrivateRuntime,
        [string] $Timestamp,
        [pscustomobject] $RuntimeLayout,
        [string] $StagingDirectory
    )

    $stageDirectory = Join-Path $StagingDirectory $VariantName
    Remove-PublishOwnedPath $stageDirectory
    New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null

    # AppHostRelativeDotNet is embedded into the native app host. Clean each
    # project first so the complete and requires-dotnet variants never reuse
    # an AppHost generated for the other layout.
    Invoke-DotNet @('clean', $managerProject, '--configuration', 'Release', '--runtime', 'win-x64', '--nologo')

    $managerArguments = @(
        'publish', $managerProject,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'false',
        '--output', $stageDirectory,
        '--nologo',
        '-p:PublishSingleFile=true',
        '-p:PublishReadyToRun=false',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        # The CLI ProjectReference is build-only (ReferenceOutputAssembly=false)
        # and is published independently below, so SDK executable-reference
        # self-contained validation is not relevant for this layout.
        '-p:ValidateExecutableReferencesMatchSelfContained=false'
    )
    if ($IncludesPrivateRuntime) {
        $managerArguments += @(
            '-p:AppHostDotNetSearch=AppRelative',
            '-p:AppHostRelativeDotNet=bin\dotnet'
        )
    }
    Invoke-DotNet $managerArguments

    Remove-LegacyCliRootArtifacts $stageDirectory

    $cliDirectory = Join-Path $stageDirectory 'bin'
    $privateRuntimeDirectory = Join-Path $stageDirectory 'bin\dotnet'
    $documentationDirectory = Join-Path $stageDirectory 'hd2mod-docs'
    Remove-PublishOwnedPath $cliDirectory
    Remove-PublishOwnedPath $privateRuntimeDirectory
    Remove-PublishOwnedPath $documentationDirectory

    Invoke-DotNet @('clean', $cliProject, '--configuration', 'Release', '--runtime', 'win-x64', '--nologo')

    $cliArguments = @(
        'publish', $cliProject,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'false',
        '--output', $cliDirectory,
        '--nologo',
        '-p:PublishSingleFile=true',
        '-p:PublishReadyToRun=false',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )
    if ($IncludesPrivateRuntime) {
        $cliArguments += @(
            '-p:AppHostDotNetSearch=AppRelative',
            '-p:AppHostRelativeDotNet=dotnet'
        )
    }
    Invoke-DotNet $cliArguments

    if ($IncludesPrivateRuntime) {
        Copy-PrivateRuntime -Layout $RuntimeLayout -Destination $privateRuntimeDirectory
    }

    New-Item -ItemType Directory -Path $documentationDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $cliDocumentationSource 'README.md') -Destination (Join-Path $documentationDirectory 'README.md') -Force
    Copy-Item -LiteralPath (Join-Path $cliDocumentationSource 'SKILL.md') -Destination (Join-Path $documentationDirectory 'SKILL.md') -Force

    $publishedCli = Join-Path $cliDirectory 'hd2mod.exe'
    $documentationSkillsDirectory = Join-Path $documentationDirectory 'skills'
    & $publishedCli 'agent' 'skills' 'export' '--output' $documentationSkillsDirectory '--overwrite'
    if ($LASTEXITCODE -ne 0) {
        throw "The published CLI could not export its Agent Skills (exit code $LASTEXITCODE)."
    }

    Write-RootReadme -PackageDirectory $stageDirectory -IncludesPrivateRuntime $IncludesPrivateRuntime -RuntimeLayout $RuntimeLayout
    Write-CliLauncher -PackageDirectory $stageDirectory
    Assert-ReleaseLayout -PackageDirectory $stageDirectory -IncludesPrivateRuntime $IncludesPrivateRuntime -RuntimeLayout $RuntimeLayout

    $archivePath = Join-Path $archiveDirectory "HD2ModManager-win-x64-$Timestamp-$VariantName.zip"
    Compress-ReleaseStage -StageDirectory $stageDirectory -ArchivePath $archivePath
    Remove-PublishOwnedPath $stageDirectory
    return $archivePath
}

if (-not (Test-Path -LiteralPath $releaseReadmeSource)) {
    throw "Release README source is missing: $releaseReadmeSource"
}

New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null
if ($KeepExistingOutput) {
    Write-Verbose 'Timestamped archives are always preserved; -KeepExistingOutput is retained only for backwards-compatible invocation.'
}

$targetRuntimeMajorMinor = Get-TargetRuntimeMajorMinor $managerProject
$privateRuntimeLayout = Get-PrivateRuntimeLayout $targetRuntimeMajorMinor
$timestamp = (Get-Date).ToString('yyyyMMdd-HHmmssfff')
$stagingDirectory = Join-Path $archiveDirectory '.staging'
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

$completeArchive = Publish-ReleaseVariant -VariantName 'complete' -IncludesPrivateRuntime $true -Timestamp $timestamp -RuntimeLayout $privateRuntimeLayout -StagingDirectory $stagingDirectory
$requiresDotNetArchive = Publish-ReleaseVariant -VariantName 'requires-dotnet' -IncludesPrivateRuntime $false -Timestamp $timestamp -RuntimeLayout $privateRuntimeLayout -StagingDirectory $stagingDirectory

if ((Get-ChildItem -LiteralPath $stagingDirectory -Force | Measure-Object).Count -eq 0) {
    Remove-PublishOwnedPath $stagingDirectory
}

Write-Host "Complete package: $completeArchive"
Write-Host "Requires .NET package: $requiresDotNetArchive"
Write-Host "Shared private runtime: .NET $($privateRuntimeLayout.RuntimeVersion)"
