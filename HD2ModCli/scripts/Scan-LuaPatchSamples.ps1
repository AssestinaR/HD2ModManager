[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Root,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-U32([byte[]]$Bytes, [int]$Offset) {
    [BitConverter]::ToUInt32($Bytes, $Offset)
}

function Read-U64([byte[]]$Bytes, [int]$Offset) {
    [BitConverter]::ToUInt64($Bytes, $Offset)
}

function Get-EntryKind([UInt64]$TypeId, [UInt64]$FileId) {
    $luaType = [Convert]::ToUInt64('A14E8DFA2CD117E2', 16)
    $bootFile = [Convert]::ToUInt64('F476DF93691895FA', 16)
    if ($TypeId -eq $luaType) {
        if ($FileId -eq $bootFile) { return 'lua-boot' }
        return 'lua-module'
    }
    return 'other'
}

function Inspect-Patch([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $magic = [Convert]::ToUInt32('F0000011', 16)
    if ($bytes.Length -lt 72 -or (Read-U32 $bytes 0) -ne $magic) {
        return [pscustomobject]@{
            Path = $Path; FileName = [IO.Path]::GetFileName($Path); IsPatch = $false
            EntryCount = 0; LuaEntries = 0; LuaBootEntries = 0; LuaModuleEntries = 0
            LuaNames = ''; OtherTypeCount = 0; Error = 'invalid patch header'
        }
    }
    $typeCount = [int](Read-U32 $bytes 4)
    $entryCount = [int](Read-U32 $bytes 8)
    $tableStart = 72 + (32 * $typeCount)
    $tableEnd = $tableStart + (80 * $entryCount)
    if ($typeCount -le 0 -or $entryCount -lt 0 -or $tableEnd -gt $bytes.Length) {
        return [pscustomobject]@{
            Path = $Path; FileName = [IO.Path]::GetFileName($Path); IsPatch = $true
            EntryCount = $entryCount; LuaEntries = 0; LuaBootEntries = 0; LuaModuleEntries = 0
            LuaNames = ''; OtherTypeCount = 0; Error = 'truncated TOC'
        }
    }
    $lua = [Collections.Generic.List[string]]::new()
    $otherTypes = [Collections.Generic.HashSet[UInt64]]::new()
    $boot = 0
    $module = 0
    for ($i = 0; $i -lt $entryCount; $i++) {
        $offset = $tableStart + (80 * $i)
        $fileId = Read-U64 $bytes $offset
        $typeId = Read-U64 $bytes ($offset + 8)
        $kind = Get-EntryKind $typeId $fileId
        if ($kind -eq 'lua-boot' -or $kind -eq 'lua-module') {
            $lua.Add(('{0}:0x{1:x16}' -f $kind, $fileId))
            if ($kind -eq 'lua-boot') { $boot++ } else { $module++ }
        } else {
            $null = $otherTypes.Add($typeId)
        }
    }
    [pscustomobject]@{
        Path = $Path
        FileName = [IO.Path]::GetFileName($Path)
        IsPatch = $true
        EntryCount = $entryCount
        LuaEntries = $lua.Count
        LuaBootEntries = $boot
        LuaModuleEntries = $module
        LuaNames = ($lua -join ';')
        OtherTypeCount = $otherTypes.Count
        Error = ''
    }
}

$out = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $out, (Join-Path $out 'lua-patches'), (Join-Path $out 'manifests') | Out-Null
$files = foreach ($rootPath in $Root) {
    if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) { continue }
    Get-ChildItem -LiteralPath $rootPath -Recurse -File |
        Where-Object { $_.Name -match '^[0-9a-fA-F]{16}\.patch_\d+$' }
}
$records = foreach ($file in $files) {
    $record = Inspect-Patch $file.FullName
    $sidecars = @(
        "$($file.FullName).stream",
        "$($file.FullName).gpu_resources"
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
    $record | Add-Member NoteProperty HasStream ($sidecars -contains "$($file.FullName).stream") -PassThru |
        Add-Member NoteProperty HasGpuResources ($sidecars -contains "$($file.FullName).gpu_resources") -PassThru
}

$records = @($records)
$records | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'scan.json') -Encoding UTF8
$records | Export-Csv -LiteralPath (Join-Path $out 'scan.csv') -NoTypeInformation -Encoding UTF8
$luaRecords = @($records | Where-Object { $_.LuaEntries -gt 0 })
$bootMeasure = $luaRecords | ForEach-Object { [int]$_.LuaBootEntries } | Measure-Object -Sum
$moduleMeasure = $luaRecords | ForEach-Object { [int]$_.LuaModuleEntries } | Measure-Object -Sum
$bootCount = if ($null -eq $bootMeasure.Sum) { 0 } else { [int]$bootMeasure.Sum }
$moduleCount = if ($null -eq $moduleMeasure.Sum) { 0 } else { [int]$moduleMeasure.Sum }
$luaRecords | ForEach-Object {
    $safe = ($_.Path -replace '[:\\\/ ]', '_')
    Copy-Item -LiteralPath $_.Path -Destination (Join-Path $out 'lua-patches' $safe) -Force
}
@(
    '# Lua Patch Sample Scan'
    ''
    "- Generated: $(Get-Date -Format o)"
    "- Roots: $($Root -join ', ')"
    "- Patch files: $($records.Count)"
    "- Lua patches: $($luaRecords.Count)"
    "- Lua boot entries: $bootCount"
    "- Lua module entries: $moduleCount"
    ''
    '## Lua patches'
    ($luaRecords | Sort-Object Path | ForEach-Object {
        "- $($_.Path) | entries=$($_.EntryCount) | lua=$($_.LuaEntries) | $($_.LuaNames) | sidecars: stream=$($_.HasStream), gpu=$($_.HasGpuResources)"
    })
    ''
    '## Interpretation'
    '- `lua-boot` is the original single-entry Lua resource and is the main candidate for Asset ID rewriting.'
    '- `lua-module` is already a separate resource and is generally safer to load through a manager loader.'
    '- Files with zero Lua entries are ordinary Patch archives and are not candidates for Lua module conversion.'
) | Set-Content -LiteralPath (Join-Path $out 'REPORT.md') -Encoding UTF8

Write-Output "Scanned $($records.Count) Patch files; found $($luaRecords.Count) Lua Patch files."
Write-Output "Report: $(Join-Path $out 'REPORT.md')"
