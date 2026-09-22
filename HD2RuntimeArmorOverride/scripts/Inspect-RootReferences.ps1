param([Parameter(Mandatory)][string]$Log)
$ErrorActionPreference = "Stop"
$rows = @(Get-Content -LiteralPath $Log | ForEach-Object { $_ | ConvertFrom-Json })
$record = $rows | Where-Object record_type -eq composition_root_backrefs |
    Select-Object -Last 1
if (-not $record) { throw "No completed root reference pass in this log." }
$roots = @{}
foreach ($chain in $rows | Where-Object record_type -eq composition_chain_candidate) {
    if ($chain.storage.wrapper_valid) {
        $key = [string]$chain.item
        if (-not $roots.ContainsKey($key)) { $roots[$key] = @() }
        $roots[$key] += [Convert]::ToUInt64($chain.root.Substring(2), 16)
    }
}
$result = foreach ($property in $record.references.PSObject.Properties) {
    if (-not $roots.ContainsKey($property.Name)) { throw "Unknown item reference." }
    foreach ($reference in $property.Value) {
        if (-not $reference.context) { continue }
        $data = [Convert]::FromHexString($reference.context)
        if ($data.Length -ne 96) { throw "Unexpected context length." }
        $value = [BitConverter]::ToUInt64($data, 32)
        $matches = $roots[$property.Name] -contains $value
        $itemOffsets = @(
            for ($i = 0; $i -le $data.Length - 4; $i += 4) {
                if ([BitConverter]::ToUInt32($data, $i) -eq [uint32]$property.Name) {
                    $i - 32
                }
            }
        )
        [pscustomobject]@{
            Item = $property.Name
            Address = $reference.address
            NumericAddress = [Convert]::ToUInt64($reference.address.Substring(2), 16)
            RootValue = "0x{0:x16}" -f $value
            ExactRootMatch = $matches
            ItemOffsets = $itemOffsets -join ","
            PreviousQword = "0x{0:x16}" -f [BitConverter]::ToUInt64($data, 24)
            NextQword = "0x{0:x16}" -f [BitConverter]::ToUInt64($data, 40)
            ConsumerProven = $false
        }
    }
}
$previous = $null
foreach ($row in $result | Sort-Object NumericAddress) {
    $gap = if ($null -eq $previous) { $null } else { $row.NumericAddress - $previous }
    $previous = $row.NumericAddress
    $row | Add-Member -NotePropertyName GapFromPreviousReference -NotePropertyValue $gap
    $row | Select-Object Item, Address, RootValue, ExactRootMatch, ItemOffsets,
        PreviousQword, NextQword, GapFromPreviousReference, ConsumerProven
}
