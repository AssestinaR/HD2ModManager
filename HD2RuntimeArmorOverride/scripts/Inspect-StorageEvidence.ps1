param([Parameter(Mandatory)][string]$Log)
$ErrorActionPreference = "Stop"
$rows = @(Get-Content -LiteralPath $Log | ForEach-Object { $_ | ConvertFrom-Json })
$scan = $rows | Where-Object record_type -eq composition_scan | Select-Object -Last 1
if (-not $scan) { throw "A completed scan is required." }
foreach ($chain in $rows | Where-Object record_type -eq composition_chain_candidate) {
    $hit = @($scan.hits | Where-Object address -eq $chain.root)
    if ($hit.Count -ne 1) { throw "Expected exactly one context for $($chain.root)." }
    $window = [Convert]::FromHexString($hit[0].window)
    $wrapper = 0x100 - 24
    if ([Text.Encoding]::ASCII.GetString($window, $wrapper, 4) -ne "LDLD") {
        Write-Warning "Excluded unwrapped candidate $($chain.root); ownership unknown."
        continue
    }
    $length = [BitConverter]::ToUInt32($window, $wrapper + 12)
    $root = [Convert]::ToUInt64($chain.root.Substring(2), 16)
    $bodies = [Convert]::ToUInt64($chain.bodies.Substring(2), 16)
    $spans = @(
        [pscustomobject]@{ Start=$root; End=$root+64 }
        [pscustomobject]@{ Start=$bodies; End=$bodies+24*$chain.arrays.Count }
        foreach ($array in $chain.arrays) {
            $start = [Convert]::ToUInt64($array.address.Substring(2), 16)
            [pscustomobject]@{ Start=$start; End=$start+96*$array.count }
        }
    ) | Sort-Object Start
    $end = $root
    $packed = $true
    foreach ($span in $spans) {
        if ($span.Start -ne $end) { $packed = $false }
        $end = $span.End
    }
    if ($end -ne $root+$length) { $packed = $false }
    [pscustomobject]@{
        Item=$chain.item
        Root=$chain.root
        PayloadBytes=$length
        PieceCount=$chain.matched_pieces
        PackedWithoutGaps=$packed
        InPlaceGrowthProven=$false
        ConsumerProven=$false
    }
}
