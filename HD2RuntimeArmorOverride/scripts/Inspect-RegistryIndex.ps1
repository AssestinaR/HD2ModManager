param(
    [Parameter(Mandatory)][string]$Log,
    [string]$Plaintext = "D:\SteamLibrary\steamapps\common\Helldivers 2\data\game\generated_customization_armor_sets"
)
$ErrorActionPreference = "Stop"
$data = [IO.File]::ReadAllBytes($Plaintext)
$count = [BitConverter]::ToUInt32($data, 0)
$offset = 4
$items = @{}
$payloadOffsets = @()
for ($i = 0; $i -lt $count; $i++) {
    if ($offset + 24 -gt $data.Length -or
        [Text.Encoding]::ASCII.GetString($data, $offset, 4) -ne "LDLD") {
        throw "Invalid LDLD framing."
    }
    $length = [BitConverter]::ToUInt32($data, $offset + 12)
    if ($length -lt 64 -or $offset + 24 + $length -gt $data.Length) {
        throw "Invalid payload length."
    }
    $item = [string][BitConverter]::ToUInt32($data, $offset + 24)
    if ($items.ContainsKey($item)) { throw "Ambiguous item ID." }
    $items[$item] = [pscustomobject]@{ Index=$i; Offset=$offset+24 }
    $payloadOffsets += $offset + 24
    $offset += 24 + $length
}
if ($offset -ne $data.Length) { throw "Unparsed bytes in plaintext." }
$rows = @(Get-Content -LiteralPath $Log | ForEach-Object { $_ | ConvertFrom-Json })
$backrefs = $rows | Where-Object record_type -eq composition_root_backrefs |
    Select-Object -Last 1
if (-not $backrefs) { throw "Missing back-reference pass." }
$candidates = @{}
foreach ($chain in $rows | Where-Object record_type -eq composition_chain_candidate) {
    if (-not $chain.storage.packed_payload) { continue }
    $item = [string]$chain.item
    $source = $items[$item]
    if (-not $source) { throw "Item missing from plaintext." }
    $root = [Convert]::ToUInt64($chain.root.Substring(2),16)
    $blobBase = $root - $source.Offset
    foreach ($reference in $backrefs.references.$item) {
        if (-not $reference.context) { continue }
        $window = [Convert]::FromHexString($reference.context)
        if ($window.Length -ne 96 -or [BitConverter]::ToUInt64($window,32) -ne $root) {
            continue
        }
        $address = [Convert]::ToUInt64($reference.address.Substring(2),16)
        if ($address -lt 8*$source.Index) { continue }
        $indexBase = $address - 8*$source.Index
        $key = "$blobBase/$indexBase"
        if (-not $candidates.ContainsKey($key)) {
            $candidates[$key] = @{
                BlobBase=$blobBase; IndexBase=$indexBase; Items=@{}
                Verified=@{}; Contradictions=@{}
            }
        }
        $candidate = $candidates[$key]
        $candidate.Items[$item] = $true
        for ($q = 0; $q -lt 12; $q++) {
            $index = $source.Index + $q - 4
            if ($index -lt 0 -or $index -ge $count) { continue }
            $actual = [BitConverter]::ToUInt64($window, $q*8)
            if ($actual -eq $blobBase + $payloadOffsets[$index]) {
                $candidate.Verified[$index] = $true
            } else {
                $candidate.Contradictions[$index] = $true
            }
        }
    }
}
foreach ($candidate in $candidates.Values | Sort-Object IndexBase) {
    [pscustomobject]@{
        InferredBlobBase = "0x{0:x16}" -f $candidate.BlobBase
        InferredIndexBase = "0x{0:x16}" -f $candidate.IndexBase
        SampleItems = @($candidate.Items.Keys | Sort-Object)
        MatchedNeighborEntries = $candidate.Verified.Count
        ContradictingEntries = $candidate.Contradictions.Count
        SourceRecordCount = $count
        FullIndexRead = $false
        ConsumerProven = $false
        SelfCopyExcluded = $false
    }
}
