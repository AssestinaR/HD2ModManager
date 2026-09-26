param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[a-z_]+$')][string]$Label,
    [Parameter(Mandatory)][int]$GameProcessId,
    [Parameter(Mandatory)][datetime]$SinceUtc,
    [string]$RegistryRoot = "$env:LOCALAPPDATA\CowboyBingus\Helldivers2\ArmorRegistry",
    [switch]$CollectEvents
)
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
$destination = Join-Path $OutputDirectory "context-$Label"
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$errors = [Collections.Generic.List[string]]::new()
$record = [ordered]@{
    schema = 1
    capturedUtc = [datetime]::UtcNow.ToString('o')
    requestedPid = $GameProcessId
    sinceUtc = $SinceUtc.ToUniversalTime().ToString('o')
    processPresent = $false
    protocolSha256 = $null
    eventsRequested = [bool]$CollectEvents
    eventReadState = 'not_requested'
}
try {
    $game = Get-Process -Id $GameProcessId -ErrorAction Stop
    if ($game.ProcessName -eq 'helldivers2') {
        $record.processPresent = $true
        $record.processStartUtc = $game.StartTime.ToUniversalTime().ToString('o')
        $record.workingSetBytes = $game.WorkingSet64
        $record.privateMemoryBytes = $game.PrivateMemorySize64
    }
} catch { $errors.Add('process_not_available') }
foreach ($name in @('runtime.status','refresh-probe.status')) {
    try {
        Copy-Item -LiteralPath (Join-Path $RegistryRoot $name) -Destination $destination -Force
    } catch { $errors.Add("status_copy_failed:$name") }
}
try {
    $record.protocolSha256 = (Get-FileHash -LiteralPath (Join-Path $RegistryRoot 'armor-registry.protocol') -Algorithm SHA256).Hash
} catch { $errors.Add('protocol_hash_failed') }
# Bound collection; do not copy unrelated application events, full dumps,
# account details, manager settings, or the user's complete protocol.
if ($CollectEvents) {
    try {
        $eventErrors = @()
        $events = @(Get-WinEvent -FilterHashtable @{
            LogName='Application'; Id=@(1000,1001,1002)
            StartTime=$SinceUtc.ToLocalTime(); EndTime=(Get-Date)
        } -MaxEvents 256 -ErrorAction SilentlyContinue -ErrorVariable eventErrors)
        $unexpectedErrors = @($eventErrors | Where-Object { $_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*' })
        if ($unexpectedErrors.Count) { throw 'Event log read failed' }
        $selected = @($events | Where-Object { $_.ToXml() -match '(?i)helldivers2\.exe' } | ForEach-Object {
            [ordered]@{
                timeUtc=$_.TimeCreated.ToUniversalTime().ToString('o')
                eventId=$_.Id; provider=$_.ProviderName; recordId=$_.RecordId
                xml=$_.ToXml()
                # Reports may arrive late or omit PID. Correlation is reviewed
                # offline, not asserted from an application-name match.
                pidCorrelation='unverified'
            }
        })
        $record.eventReadState = 'complete'
        $record.eventQueryCapped = $events.Count -eq 256
        $record.matchingEventCount = $selected.Count
        [IO.File]::WriteAllText((Join-Path $destination 'game-events.json'),
            (ConvertTo-Json -InputObject $selected -Depth 6),$utf8)
    } catch {
        $record.eventReadState='failed'
        $errors.Add('event_query_failed')
    }
}
$record.errors = @($errors.ToArray())
$record.snapshotAtomic = $false
$record.absentEventDoesNotExcludeCrash = $true
[IO.File]::WriteAllText((Join-Path $destination 'context.json'),
    ($record | ConvertTo-Json -Depth 6),$utf8)
