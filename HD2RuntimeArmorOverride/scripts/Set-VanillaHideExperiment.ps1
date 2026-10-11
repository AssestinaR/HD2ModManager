param(
    [Parameter(Mandatory)][ValidateSet('Deploy', 'Restore')][string]$Action,
    [string]$Experiment = (Join-Path $PSScriptRoot '..\evidence\vanilla-hide-ab-20260928')
)
$ErrorActionPreference = 'Stop'
$experiment = [IO.Path]::GetFullPath($Experiment)
$manifest = Get-Content -LiteralPath (Join-Path $experiment 'B-hide-manifest.json') -Raw | ConvertFrom-Json
$data = [IO.Path]::GetFullPath($manifest.gameData)
$patch = Join-Path $experiment 'patch'
$names = @($manifest.patchName, "$($manifest.patchName).gpu_resources")
$game = Get-Process helldivers2, HD2ModManager -ErrorAction SilentlyContinue
if ($game) { throw '请先完全退出游戏和管理器。' }
foreach ($deployed in $manifest.deployed) {
    $path = Join-Path $data $deployed.name
    if (-not (Test-Path -LiteralPath $path)) { throw "现有部署已变化：$path" }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $deployed.sha256) { throw "现有部署内容已变化：$path" }
}
if ($Action -eq 'Deploy') {
    foreach ($name in $names) {
        if (Test-Path -LiteralPath (Join-Path $data $name)) { throw "实验槽位已占用：$name" }
        if (-not (Test-Path -LiteralPath (Join-Path $patch $name))) { throw "实验输出缺失：$name" }
    }
    $created = @()
    try {
        foreach ($name in $names) {
            $destination = Join-Path $data $name
            New-Item -ItemType SymbolicLink -Path $destination -Target (Join-Path $patch $name) -ErrorAction Stop | Out-Null
            $created += $destination
        }
    } catch {
        foreach ($destination in $created) { Remove-Item -LiteralPath $destination -Force }
        throw
    }
    $record = [pscustomobject]@{
        utc = [DateTimeOffset]::UtcNow.ToString('o')
        action = 'Deploy'
        gameData = $data
        links = @($names | ForEach-Object {
            $name = $_
            [pscustomobject]@{
                name = $name
                target = (Join-Path $patch $name)
                sha256 = (Get-FileHash -LiteralPath (Join-Path $data $name) -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
    }
    $record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $experiment 'B-deployment.json') -Encoding UTF8
    Write-Host 'B 组已部署；其余 Patch 未修改。'
} else {
    foreach ($name in $names) {
        $destination = Join-Path $data $name
        $item = Get-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
        if (-not $item) { continue }
        if ($item.LinkType -ne 'SymbolicLink' -or
            [IO.Path]::GetFullPath($item.Target) -ne [IO.Path]::GetFullPath((Join-Path $patch $name))) {
            throw "实验槽位不是本工具创建的链接，未删除：$destination"
        }
        Remove-Item -LiteralPath $destination -Force
    }
    Write-Host 'B 组实验链接已撤销；原有 Patch 保持不变。'
}
