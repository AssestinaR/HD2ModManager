param(
    [string]$LuaJit = "luajit.exe",
    [string]$Plaintext = "D:\SteamLibrary\steamapps\common\Helldivers 2\data\game\generated_customization_armor_sets",
    [string]$Samples = "$PSScriptRoot/../evidence/composition-samples.json"
)
$ErrorActionPreference = "Stop"
$document = Get-Content -Raw -LiteralPath $Samples | ConvertFrom-Json
$expected = foreach ($kit in $document.kits) {
    foreach ($piece in $kit.pieces) {
        $resources = @($piece.resources.PSObject.Properties |
            ForEach-Object { "$($_.Name)=$($_.Value)" } | Sort-Object -CaseSensitive)
        @($kit.itemId, $kit.archive, $piece.rawBodyType, $piece.slot,
            $piece.pieceType, $piece.weight, $piece.toneVariations,
            $piece.unit, ($resources -join ',')) -join "`t"
    }
}
$actual = @(& $LuaJit "$PSScriptRoot/Inspect-Composition.lua" "$PSScriptRoot/../src/composition_schema.lua" $Plaintext)
if ($LASTEXITCODE -ne 0) { throw "Lua decoder failed." }
$difference = Compare-Object @($expected | Sort-Object) @($actual | Sort-Object) -CaseSensitive
if ($difference) { $difference | Format-Table; throw "Authority composition mismatch." }
if ($actual.Count -ne 109) { throw "Unexpected total sample cardinality: $($actual.Count)" }
Write-Output "PASS: all 109 authority pieces match plaintext, including slots, body types and auxiliary resources."
