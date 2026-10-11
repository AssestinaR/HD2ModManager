param(
    [Parameter(Mandatory)][ValidateSet('Deploy', 'Restore')][string]$Action,
    [uint32]$TargetArmorItemId = 4259903063,
    [uint32]$TargetHelmetItemId = 513033465,
    [string]$Library = 'E:\mod'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$evidence = Join-Path $root 'evidence\custom-model-split-smoke-20260928'
$registry = Join-Path $env:LOCALAPPDATA 'CowboyBingus\Helldivers2\ArmorRegistry'
$protocol = Join-Path $registry 'armor-registry.protocol'
$backup = Join-Path $evidence 'original-armor-registry.protocol'
$recordPath = Join-Path $evidence 'deployment.json'
$workspace = Join-Path $Library '.hd2modmanager\armor-registry.workspace.json'
$cli = [IO.Path]::GetFullPath((Join-Path $root '..\HD2ModCli\bin\Debug\net10.0\hd2mod.exe'))
$spec = Join-Path $evidence 'split.spec.json'
$plan = Join-Path $evidence 'split.plan.json'
if (Get-Process helldivers2, HD2ModManager -ErrorAction SilentlyContinue) {
    throw '请先完全退出游戏和管理器。'
}
if ($Action -eq 'Deploy') {
    if ((Test-Path -LiteralPath $recordPath) -or (Test-Path -LiteralPath $backup)) {
        throw '测试备份已存在；请先恢复或核对上一次测试。'
    }
    if (-not (Test-Path -LiteralPath $workspace) -or -not (Test-Path -LiteralPath $protocol)) {
        throw '找不到当前护甲注册源文件或协议。'
    }
    $sourceDocument = Get-Content -LiteralPath $workspace -Raw | ConvertFrom-Json
    $sourceModel = @($sourceDocument.models | Where-Object { $_.pieces.Count -gt 0 } | Select-Object -First 1)
    if ($sourceModel.Count -ne 1) { throw '当前库没有唯一的可测试自定义素体。' }
    $bodyPieces = @($sourceModel.pieces | Where-Object {
        $_.enabled -ne $false -and -not ($_.slot -eq 0 -or ($_.body -eq 3 -and $_.slot -eq 1))
    })
    $helmetPieces = @($sourceModel.pieces | Where-Object {
        $_.enabled -ne $false -and ($_.slot -eq 0 -or ($_.body -eq 3 -and $_.slot -eq 1))
    })
    if ($bodyPieces.Count -eq 0 -or $helmetPieces.Count -eq 0) {
        throw '自定义素体必须同时拥有身体 Unit 和头盔 Unit。'
    }
    New-Item -ItemType Directory -Path $evidence -Force | Out-Null
    $bodyKey = 'smoke/body'
    $helmetKey = 'smoke/helmet'
    $models = @(
        [ordered]@{ key = $bodyKey; pieces = @($bodyPieces | ForEach-Object {
            [ordered]@{ body=[uint32]$_.body; unit=$_.unit; slot=[uint32]$_.slot; pieceType=[uint32]$_.pieceType; tone=[byte]$_.tone }
        }) },
        [ordered]@{ key = $helmetKey; pieces = @($helmetPieces | ForEach-Object {
            [ordered]@{ body=3; unit=$_.unit; slot=0; pieceType=[uint32]$_.pieceType; tone=[byte]$_.tone }
        }) }
    )
    $specDocument = [ordered]@{
        schema = 'hd2mod.armor-registry-spec'
        enabled = $true
        models = $models
        mappings = @(
            [ordered]@{ targetItemId=$TargetArmorItemId; modelKey=$bodyKey }
            [ordered]@{ targetItemId=$TargetHelmetItemId; modelKey=$helmetKey }
        )
    }
    $specDocument | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $spec -Encoding UTF8
    [IO.File]::Copy($protocol, $backup)
    try {
        & $cli armor-registry plan $spec --output $plan --json | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'CLI 生成自定义素体分体计划失败。' }
        & $cli armor-registry validate --plan $plan --json | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'CLI 自定义素体分体计划校验失败。' }
        & $cli armor-registry apply --plan $plan --confirm --json | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'CLI 应用自定义素体分体协议失败。' }
        [pscustomobject]@{
            sourceModelKey=$sourceModel.key
            bodyPieceCount=$bodyPieces.Count
            helmetPieceCount=$helmetPieces.Count
            targetArmorItemId=$TargetArmorItemId
            targetHelmetItemId=$TargetHelmetItemId
            originalProtocolHash=(Get-FileHash $backup -Algorithm SHA256).Hash
            testProtocolHash=(Get-FileHash $protocol -Algorithm SHA256).Hash
            utc=[DateTimeOffset]::UtcNow.ToString('o')
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $recordPath -Encoding UTF8
        Write-Host "已部署自定义素体分体测试：身体 -> $TargetArmorItemId；头盔 -> $TargetHelmetItemId。"
    } catch {
        [IO.File]::Copy($backup, $protocol, $true)
        throw
    }
} else {
    if (-not (Test-Path -LiteralPath $recordPath) -or -not (Test-Path -LiteralPath $backup)) {
        throw '找不到完整测试备份，未修改任何文件。'
    }
    $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
    if ((Get-FileHash $protocol -Algorithm SHA256).Hash -ne $record.testProtocolHash) {
        throw '协议已被其他操作修改，拒绝覆盖；需人工合并备份。'
    }
    [IO.File]::Copy($backup, $protocol, $true)
    if ((Get-FileHash $protocol -Algorithm SHA256).Hash -ne $record.originalProtocolHash) {
        throw '恢复后协议指纹不一致。'
    }
    Write-Host '已恢复原协议；管理器源 JSON 未修改。'
}
