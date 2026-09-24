param(
    [string]$ManagerRoot = "$PSScriptRoot/../../HD2ModManager/bin/Debug/net10.0-windows",
    [string]$GameData = "D:\SteamLibrary\steamapps\common\Helldivers 2\data",
    [string]$Output = "$PSScriptRoot/../evidence/composition-samples.json"
)
$ErrorActionPreference = "Stop"
$ManagerRoot = (Resolve-Path -LiteralPath $ManagerRoot).Path
$status = & (Join-Path $ManagerRoot "hd2mod.exe") gamedata status --manager-root $ManagerRoot --game-data $GameData --json
if ($LASTEXITCODE -ne 0) { throw "CLI index validation failed: $status" }
$index = ($status | ConvertFrom-Json).inspection.index
if (-not $index.mappingTrusted) { throw "Refusing to export stale index facts." }
foreach ($assembly in @("SQLitePCLRaw.core.dll", "SQLitePCLRaw.provider.e_sqlite3.dll", "Microsoft.Data.Sqlite.dll")) {
    [System.Reflection.Assembly]::LoadFrom((Join-Path $ManagerRoot $assembly)) | Out-Null
}
$builder = [Microsoft.Data.Sqlite.SqliteConnectionStringBuilder]::new()
$builder.DataSource = Join-Path $ManagerRoot "data/indexes/asset-index.sqlite"
$builder.Mode = [Microsoft.Data.Sqlite.SqliteOpenMode]::ReadOnly
$connection = [Microsoft.Data.Sqlite.SqliteConnection]::new($builder.ConnectionString)
try {
    $connection.Open()
    $transaction = $connection.BeginTransaction()
    $command = $connection.CreateCommand()
    $command.Transaction = $transaction
    # Authority pieces, not visible meshes: slot 1 and non-rendering records matter.
    $command.CommandText = @"
SELECT k.item_id,k.archive_id,k.display_name,k.dlc_id,k.set_id,
       p.piece_id,p.raw_body_type,p.slot,p.piece_type,p.weight,
       p.tone_variations,p.unit_file_id,r.role,r.file_id
FROM game_data_authority_kits k
JOIN game_data_authority_pieces p ON p.item_id=k.item_id
LEFT JOIN game_data_authority_resources r ON r.piece_id=p.piece_id
WHERE k.kit_kind=0 AND k.archive_id IN
('dfa7c28c3b490a8c','39fe1f0308f4514a','d923906f8b815987','f50c7fd427839c47','88ccc33c4cca4d20')
ORDER BY k.item_id,p.piece_id,r.role,r.file_id
"@
    $reader = $command.ExecuteReader()
    $kits = [ordered]@{}
    $pieces = @{}
    while ($reader.Read()) {
        $key = $reader.GetInt64(0).ToString()
        if (-not $kits.Contains($key)) {
            $kits[$key] = [ordered]@{
                itemId = $reader.GetInt64(0); archive = $reader.GetString(1)
                name = $reader.GetString(2); dlcId = $reader.GetInt64(3)
                setId = $reader.GetInt64(4); pieces = [System.Collections.Generic.List[object]]::new()
            }
        }
        $pieceId = $reader.GetInt64(5)
        if (-not $pieces.ContainsKey($pieceId)) {
            $piece = [ordered]@{
                rawBodyType = $reader.GetInt32(6); slot = $reader.GetInt32(7)
                pieceType = $reader.GetInt32(8); weight = $reader.GetInt32(9)
                toneVariations = $reader.GetInt32(10)
                unit = $reader.GetInt64(11).ToString("x16")
                resources = [ordered]@{}
            }
            $pieces[$pieceId] = $piece
            $kits[$key].pieces.Add($piece)
        }
        if (-not $reader.IsDBNull(12)) {
            $pieces[$pieceId].resources[$reader.GetString(12)] = $reader.GetInt64(13).ToString("x16")
        }
    }
    $reader.Dispose()
    $transaction.Commit()
    if ($kits.Count -ne 5) { throw "Expected five complete armor samples." }
    $document = [ordered]@{
        schema = 1; sourceFingerprint = $index.observedSourceFingerprint
        scope = "Indexed authority records; not runtime ownership or capacity evidence"
        kits = @($kits.Values)
    }
    $document | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Output -Encoding utf8
    foreach ($kit in $kits.Values) {
        Write-Output "$($kit.name): $($kit.pieces.Count) authority pieces"
    }
}
finally {
    $connection.Dispose()
}
