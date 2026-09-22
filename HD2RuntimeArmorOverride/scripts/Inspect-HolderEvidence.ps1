param([Parameter(Mandatory)][string]$Log)
$ErrorActionPreference = "Stop"
$rows = @(Get-Content -LiteralPath $Log | ForEach-Object { $_ | ConvertFrom-Json })
$graph = $rows | Where-Object record_type -eq composition_holder_graph |
    Select-Object -Last 1
if (-not $graph) { throw "Missing holder graph." }

function Read-U64([byte[]]$Bytes, [int]$Offset) {
    if ($Offset -lt 0 -or $Offset + 8 -gt $Bytes.Length) { return $null }
    return [BitConverter]::ToUInt64($Bytes, $Offset)
}

$index = @($graph.holders.index_base |
    Where-Object { $_.address -like "0x000002*" -and $_.context })
$blob = @($graph.holders.blob_base |
    Where-Object { $_.address -like "0x000002*" -and $_.context })
if (-not $index -or -not $blob) { throw "No high-address holder references." }

$pairs = foreach ($i in $index) {
    $ia = [Convert]::ToUInt64($i.address.Substring(2), 16)
    foreach ($b in $blob) {
        $ba = [Convert]::ToUInt64($b.address.Substring(2), 16)
        $delta = [Math]::Abs([int64]($ba - $ia))
        if ($delta -le 0x200) {
            $ib = [Convert]::FromHexString($i.context)
            $bb = [Convert]::FromHexString($b.context)
            $iq = @()
            for ($q = 0; $q -lt $ib.Length; $q += 8) {
                $value = Read-U64 $ib $q
                if ($null -ne $value -and $value -ne 0) {
                    $iq += [pscustomobject]@{
                        Offset=($q-64); Address=("0x{0:x16}" -f ($ia+$q-64))
                        Value=("0x{0:x16}" -f $value)
                    }
                }
            }
            $bq = @()
            for ($q = 0; $q -lt $bb.Length; $q += 8) {
                $value = Read-U64 $bb $q
                if ($null -ne $value -and $value -ne 0) {
                    $bq += [pscustomobject]@{
                        Offset=($q-64); Address=("0x{0:x16}" -f ($ba+$q-64))
                        Value=("0x{0:x16}" -f $value)
                    }
                }
            }
            [pscustomobject]@{
                IndexReference=$i.address
                BlobReference=$b.address
                ReferenceDelta=("0x{0:x}" -f $delta)
                Region=$i.region
                RegionType=$i.region_type
                Protection=$i.protection
                IndexContextQwords=$iq
                BlobContextQwords=$bq
                CountLike411=(@($iq | Where-Object Value -eq "0x000000000000019b").Count -gt 0)
                CandidateHolderOnly=$true
                OwnershipProven=$false
                ConsumerProven=$false
            }
        }
    }
}
$pairs | Sort-Object ReferenceDelta | ConvertTo-Json -Depth 8
