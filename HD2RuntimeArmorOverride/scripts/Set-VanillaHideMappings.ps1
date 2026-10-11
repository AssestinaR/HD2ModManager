param(
    [Parameter(Mandatory)][ValidateSet('Disable', 'Restore')][string]$Action,
    [string]$Experiment = (Join-Path $PSScriptRoot '..\evidence\vanilla-hide-ab-20260928')
)
$ErrorActionPreference = 'Stop'
if (Get-Process helldivers2, HD2ModManager -ErrorAction SilentlyContinue) {
    throw '请先完全退出游戏和管理器。'
}
$experiment = [IO.Path]::GetFullPath($Experiment)
$protocol = Join-Path $env:LOCALAPPDATA 'CowboyBingus\Helldivers2\ArmorRegistry\armor-registry.protocol'
$backup = Join-Path $experiment 'original-armor-registry.protocol'
$disabledHashPath = Join-Path $experiment 'disabled-protocol.sha256'
if ($Action -eq 'Disable') {
    if (Test-Path -LiteralPath $backup) { throw '协议备份已存在，拒绝覆盖。' }
    $original = [IO.File]::ReadAllText($protocol)
    if ($original -notmatch '(?m)^enabled=true$' -or
        $original -notmatch '(?m)^revision=(\d+)$') {
        throw '运行时协议缺少预期的启用状态或修订号。'
    }
    $revision = [int]$Matches[1]
    [IO.File]::Copy($protocol, $backup)
    $disabled = [regex]::Replace($original, '(?m)^revision=\d+$', "revision=$($revision + 1)")
    $disabled = [regex]::Replace($disabled, '(?m)^enabled=true$', 'enabled=false')
    $temporary = "$protocol.hide-ab-tmp"
    try {
        [IO.File]::WriteAllText($temporary, $disabled, [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $protocol, $true)
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
    (Get-FileHash -LiteralPath $protocol -Algorithm SHA256).Hash.ToLowerInvariant() |
        Set-Content -LiteralPath $disabledHashPath -NoNewline
    Write-Host "已临时关闭映射；原协议保存在 $backup"
} else {
    if (-not (Test-Path -LiteralPath $backup) -or
        -not (Test-Path -LiteralPath $disabledHashPath)) {
        throw '缺少本实验的协议备份或禁用状态指纹。'
    }
    $expected = (Get-Content -LiteralPath $disabledHashPath -Raw).Trim()
    $actual = (Get-FileHash -LiteralPath $protocol -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw '协议在实验期间被修改，拒绝覆盖；请人工合并备份。' }
    [IO.File]::Copy($backup, $protocol, $true)
    Write-Host '已恢复实验前的完整运行时协议。'
}
