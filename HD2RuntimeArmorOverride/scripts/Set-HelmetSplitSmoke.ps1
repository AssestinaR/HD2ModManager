param(
    [Parameter(Mandatory)][ValidateSet('Deploy', 'Restore')][string]$Action,
    [string]$Library = 'E:\mod',
    [string]$GameData = 'D:\SteamLibrary\steamapps\common\Helldivers 2\data',
    [string]$SourceMod = '【4.28】Chiffon-LUNALICE替换RE-2310'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$evidence = Join-Path $root 'evidence\helmet-split-smoke-20260928'
$registry = Join-Path $env:LOCALAPPDATA 'CowboyBingus\Helldivers2\ArmorRegistry'
$protocol = Join-Path $registry 'armor-registry.protocol'
$backup = Join-Path $evidence 'original-armor-registry.protocol'
$recordPath = Join-Path $evidence 'deployment.json'
$cli = [IO.Path]::GetFullPath((Join-Path $root '..\HD2ModCli\bin\Debug\net10.0\hd2mod.exe'))
$scannerDll = [IO.Path]::GetFullPath((Join-Path $root '..\HD2ModCli\bin\Debug\net10.0\HD2ModAdaptation.dll'))
$spec = Join-Path $root 'protocol\helmet-split-smoke.spec.json'
$carrier = '9ba626afa44a3aa3'
$suffixes = @('', '.gpu_resources', '.stream')
$source = Join-Path (Join-Path $Library $SourceMod) "$carrier.patch_0"
$destination = Join-Path $GameData "$carrier.patch_5"
if (Get-Process helldivers2, HD2ModManager -ErrorAction SilentlyContinue) {
    throw '请先完全退出游戏和管理器。'
}
if ($Action -eq 'Deploy') {
    if ((Test-Path -LiteralPath $recordPath) -or (Test-Path -LiteralPath $backup)) {
        throw '测试备份已存在；请先恢复或核对上一次测试。'
    }
    if (-not (Test-Path -LiteralPath $protocol) -or -not (Test-Path -LiteralPath $cli)) {
        throw '运行时协议或 Debug CLI 不存在。'
    }
    foreach ($suffix in $suffixes) {
        if (-not (Test-Path -LiteralPath "$source$suffix")) { throw "来源缺少 $suffix 文件。" }
        if (Test-Path -LiteralPath "$destination$suffix") { throw "实验槽位已被占用：$destination$suffix" }
    }
    Add-Type -Path $scannerDll
    $scanner = [HD2ModAdaptation.PatchReconstruction.PatchTocScanner]::new()
    $sourceEntries = $scanner.ScanEntriesAsync($source).AsTask().GetAwaiter().GetResult()
    $occupied = [Collections.Generic.HashSet[HD2ModAdaptation.PatchReconstruction.AssetKey]]::new()
    $existing = @(Get-ChildItem -LiteralPath $GameData -Filter "$carrier.patch_*" |
        Where-Object { $_.Name -notmatch '\.(stream|gpu_resources)$' })
    foreach ($patch in $existing) {
        foreach ($entry in $scanner.ScanEntriesAsync($patch.FullName).AsTask().GetAwaiter().GetResult()) {
            [void]$occupied.Add($entry.AssetKey)
        }
    }
    $collisions = @($sourceEntries | Where-Object { $occupied.Contains($_.AssetKey) })
    if ($collisions.Count) { throw "$($collisions.Count) 个资产与当前部署重叠，拒绝测试。" }
    New-Item -ItemType Directory -Path $evidence -Force | Out-Null
    [IO.File]::Copy($protocol, $backup)
    $created = @()
    try {
        foreach ($suffix in $suffixes) {
            $path = "$destination$suffix"
            New-Item -ItemType SymbolicLink -Path $path -Target "$source$suffix" -ErrorAction Stop | Out-Null
            $created += $path
        }
        $plan = Join-Path $evidence 'plan.json'
        & $cli armor-registry plan $spec --output $plan --json | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'CLI 生成测试协议计划失败。' }
        & $cli armor-registry validate --plan $plan --json | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'CLI 协议计划校验失败。' }
        & $cli armor-registry apply --plan $plan --confirm --json | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'CLI 应用测试协议失败。' }
        $record = [pscustomobject]@{
            source = $source
            sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
            originalProtocolHash = (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash
            testProtocolHash = (Get-FileHash -LiteralPath $protocol -Algorithm SHA256).Hash
            links = $created
            utc = [DateTimeOffset]::UtcNow.ToString('o')
        }
        $record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $recordPath -Encoding UTF8
        Write-Host '测试包已部署：RE-2310 身体到 RE-824，RE-2310 头盔到 FS-23。'
    } catch {
        [IO.File]::Copy($backup, $protocol, $true)
        foreach ($path in $created) { Remove-Item -LiteralPath $path -Force }
        throw
    }
} else {
    if (-not (Test-Path -LiteralPath $recordPath) -or -not (Test-Path -LiteralPath $backup)) {
        throw '找不到完整测试备份，未修改任何文件。'
    }
    $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
    if ((Get-FileHash -LiteralPath $protocol -Algorithm SHA256).Hash -ne $record.testProtocolHash) {
        throw '协议已被其他操作修改，拒绝覆盖；需人工合并备份。'
    }
    foreach ($suffix in $suffixes) {
        $path = "$destination$suffix"
        $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if ($item.LinkType -ne 'SymbolicLink' -or
            [IO.Path]::GetFullPath($item.Target) -ne [IO.Path]::GetFullPath("$source$suffix")) {
            throw "实验槽位内容已变化，拒绝删除：$path"
        }
    }
    [IO.File]::Copy($backup, $protocol, $true)
    foreach ($suffix in $suffixes) { Remove-Item -LiteralPath "$destination$suffix" -Force }
    if ((Get-FileHash -LiteralPath $protocol -Algorithm SHA256).Hash -ne $record.originalProtocolHash) {
        throw '恢复后协议指纹不一致。'
    }
    Write-Host '已恢复原协议并撤下实验 Patch；管理器源 JSON 未修改。'
}
