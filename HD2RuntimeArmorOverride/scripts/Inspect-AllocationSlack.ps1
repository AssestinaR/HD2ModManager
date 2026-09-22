param([Parameter(Mandatory)][string]$Log)
$ErrorActionPreference = "Stop"
$rows = @(Get-Content -LiteralPath $Log | ForEach-Object { $_ | ConvertFrom-Json })
$scan = $rows | Where-Object record_type -eq composition_scan | Select-Object -Last 1
$graph = $rows | Where-Object record_type -eq composition_holder_graph | Select-Object -Last 1
if (-not $scan -or -not $graph) { throw "Completed scan and holder graph are required." }
$chains = @($rows | Where-Object record_type -eq composition_chain_candidate |
    Where-Object { $_.storage.wrapper_valid -and $_.storage.packed_payload })
foreach ($target in $graph.target_regions.PSObject.Properties) {
    $region = $target.Value
    $base = [Convert]::ToUInt64($region.base.Substring(2),16)
    $end = [Convert]::ToUInt64($region.end_address.Substring(2),16)
    $inside = @($chains | Where-Object {
        $root = [Convert]::ToUInt64($_.root.Substring(2),16)
        $root -ge $base -and $root -lt $end
    })
    $maxEnd = $base
    foreach ($chain in $inside) {
        $root = [Convert]::ToUInt64($chain.root.Substring(2),16)
        $length = [uint64]$chain.storage.payload_bytes
        if ($root+$length -gt $maxEnd) { $maxEnd = $root+$length }
    }
    [pscustomobject]@{
        Target=$target.Name
        RegionBase=$region.base
        RegionEnd=$region.end_address
        RegionBytes=$region.size
        RegionType=$region.region_type
        Protection=$region.protection
        SampledKitPayloadEnd=("0x{0:x16}" -f $maxEnd)
        TailAfterSampledPayload=($end-$maxEnd)
        AllocationCapacityProven=$false
        SafeExpansionProven=$false
    }
}
