param(
    [string]$RegistryRoot = "$env:LOCALAPPDATA\CowboyBingus\Helldivers2\ArmorRegistry",
    [string]$OutputRoot = "$PSScriptRoot\..\evidence\refresh",
    [ValidateRange(10,40)][int]$CaptureSeconds = 10,
    [switch]$HandshakeOnly,
    [switch]$EntityPreflight,
    [switch]$SpatialPreflight,
    [switch]$SpatialMovement,
    [switch]$OwnerDataPreflight,
    [switch]$BindingPreflight,
    [switch]$NativeOwnerPreflight,
    [switch]$NativeOwnerTransitions,
    [switch]$CodeReferencePreflight,
    [switch]$RouteCodePreflight,
    [switch]$RouteIdentityPreflight,
    [switch]$ExperimentalPendingPulse,
    [switch]$PreflightOnly
)
$ErrorActionPreference = 'Stop'
$session = [Guid]::NewGuid().ToString('N')
$sequence = 0
$gamePid = 0
$gameStartTicks = 0L
$output = $null
$lease = $null
$handshake = $false
$notes = [Collections.Generic.List[object]]::new()
$utf8 = [Text.UTF8Encoding]::new($false)
$captureStartedUtc = [datetime]::UtcNow

function Save-Context([string]$Label, [bool]$Events = $false) {
    try {
        & "$PSScriptRoot\Save-ArmorRefreshContext.ps1" -OutputDirectory $output `
            -Label $Label -GameProcessId $gamePid -SinceUtc $captureStartedUtc `
            -RegistryRoot $RegistryRoot -CollectEvents:$Events
    } catch { Write-Warning "上下文保存失败（不影响探针停止）：$($_.Exception.Message)" }
}
function Save-PhaseMarker([string]$Phase, [string]$Event) {
    $row = [ordered]@{session=$session;pid=$gamePid;request=$sequence;phase=$Phase;
        event=$Event;utc=[datetime]::UtcNow.ToString('o');userActionConfirmed=$false}
    [IO.File]::AppendAllText((Join-Path $output 'phase-markers.jsonl'),
        ($row|ConvertTo-Json -Compress)+"`n",$utf8)
}

function Read-Status {
    $values = @{}
    $path = Join-Path $RegistryRoot 'refresh-probe.status'
    try {
        foreach ($line in [IO.File]::ReadAllLines($path)) {
            if ($line -match '^([^=]+)=(.*)$') { $values[$matches[1]] = $matches[2] }
        }
    } catch [IO.IOException] { return @{} }
    return $values
}
function Send-Request([string]$Command, [string]$Phase = '', [string]$Mode = 'metadata') {
    $script:sequence++
    $text = "schema=1`npid=$gamePid`nsession=$session`nrequest=$sequence`ncommand=$Command`n"
    if ($Phase) { $text += "phase=$Phase`nduration=$CaptureSeconds`nmode=$Mode`n" }
    if ($Mode -eq 'route_pulse') { $text += "confirm=one_pending_byte_once`n" }
    $path = Join-Path $RegistryRoot 'refresh-probe.request'
    $temp = "$path.$session.tmp"
    [IO.File]::WriteAllText($temp,$text,$utf8)
    try {
        if ([IO.File]::Exists($path)) {
            $old = "$path.$session.previous"
            [IO.File]::Replace($temp,$path,$old)
            [IO.File]::Delete($old)
        }
        else { [IO.File]::Move($temp,$path) }
    } finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
}
function Wait-State([string]$Expected, [int]$Timeout) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Timeout) {
        $sameProcess = $false
        $current = Get-Process -Id $gamePid -ErrorAction SilentlyContinue
        try {
            $sameProcess = $null -ne $current -and -not $current.HasExited -and
                $current.ProcessName -eq 'helldivers2' -and
                $current.StartTime.ToUniversalTime().Ticks -eq $gameStartTicks
        } catch { $sameProcess = $false }
        finally { if ($current) { $current.Dispose() } }
        if (-not $sameProcess) {
            throw '本次游戏进程已退出或身份改变，采样终止。崩溃可能留下残留进程；请在 Steam 停止并确认完全退出后重新启动，不要沿用旧 PID。'
        }
        $s = Read-Status
        if ($s.session -eq $session -and $s.request -eq "$sequence" -and $s.pid -eq "$gamePid") {
            if ($s.state -eq 'error') { throw 'Lua 探针报告错误。' }
            if ($s.state -eq $Expected) { return $s }
            if ($Expected -eq 'capturing' -and $s.state -eq 'complete') {
                # These automatic read-only modes can complete in <100ms.
                # No operator action must be synchronized to their start.
                if ($RouteIdentityPreflight -or $ExperimentalPendingPulse) { return $s }
                throw '操作提示前采样已结束；请重新运行向导。'
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "等待 Lua 状态 $Expected 超时。请确认游戏已进入舰船、部署了新版 Lua，且游戏未暂停 update。"
}
function Beep-Notice {
    try { [Console]::Beep(880,250) } catch { }
}
try {
    if (-not (Test-Path -LiteralPath $RegistryRoot)) { throw '没有找到运行时目录，请先部署并启动游戏。' }
    $lease = [IO.File]::Open((Join-Path $RegistryRoot 'refresh-guide.lock'),
        [IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $games = @(Get-Process -Name helldivers2 -ErrorAction SilentlyContinue)
    if ($games.Count -ne 1) { throw '请先从 Steam 启动游戏并进入舰船；只能有一个游戏进程。' }
    try {
        if ($games[0].HasExited) {
            throw '发现已退出但仍被枚举的游戏残留进程。请先在 Steam 停止游戏，确认完全退出后再启动。'
        }
        $gamePid = $games[0].Id
        $gameStartTicks = $games[0].StartTime.ToUniversalTime().Ticks
    } finally { foreach ($game in $games) { $game.Dispose() } }
    $output = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ((Get-Date -Format yyyyMMdd-HHmmss)+'-'+$session)
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Save-Context 'start'
    if ($ExperimentalPendingPulse) {
        Write-Host '实验性单次刷新：将写入一个待更新字节；不直接调用未知函数。当前必须零映射、单人舰船、关闭军械库。' -ForegroundColor Yellow
    } else { Write-Host '护甲换装采样向导（只读；不自动换装或刷新模型）' -ForegroundColor Cyan }
    if ($NativeOwnerPreflight -or $BindingPreflight -or $CodeReferencePreflight -or $RouteCodePreflight -or $RouteIdentityPreflight -or $ExperimentalPendingPulse -or ($NativeOwnerTransitions -and $PreflightOnly)) {
        Write-Host '本轮自动执行；无需切换护甲，不要更改映射或部署。'
    } elseif ($SpatialMovement) {
        Write-Host '本轮仅对照舰船内站立和移动；不要更改映射、部署或打开军械库。'
    } else {
        Write-Host '请选择两套容易区分的护甲 A、B。本轮不要更改映射或部署。'
    }
    if (-not ($NativeOwnerPreflight -or $BindingPreflight -or $CodeReferencePreflight -or $RouteCodePreflight -or $RouteIdentityPreflight -or $ExperimentalPendingPulse -or ($NativeOwnerTransitions -and $PreflightOnly))) {
        Write-Host '采样前按回车，3秒内切回游戏；听到提示音后执行操作。结束时会再响一次。'
    }
    Send-Request 'ping'
    $hello = Wait-State 'ready' 30
    $handshake = $true
    if ($hello.probe_version -ne '10') { throw '游戏内仍是旧版探针。请退出游戏、重新部署后再启动。' }
    if ($HandshakeOnly) {
        Write-Host 'Lua 握手通过。未开始采样。' -ForegroundColor Green
    } else {
        Write-Host '先执行只读实例访问预检；保持舰船场景，不操作军械库。' -ForegroundColor Cyan
        $modes = if ($EntityPreflight) { @('worlds','entities') }
            elseif ($ExperimentalPendingPulse) { @('worlds','route_identity','route_pulse') }
            elseif ($RouteIdentityPreflight) { @('worlds','route_identity') }
            elseif ($RouteCodePreflight) { @('worlds','route_code') }
            elseif ($CodeReferencePreflight) { @('worlds','code_refs') }
            elseif ($BindingPreflight) { @('worlds','bindings') }
            elseif ($NativeOwnerPreflight) { @('worlds','owner_native') }
            elseif ($NativeOwnerTransitions) { @('worlds','owner_trace') }
            elseif ($OwnerDataPreflight) { @('worlds','owner_data') }
            elseif ($SpatialPreflight -or $SpatialMovement) { @('worlds','spatial') }
            else { @('worlds','focus') }
        foreach ($mode in $modes) {
            Send-Request 'capture' "preflight_$mode" $mode
            $null=Wait-State 'capturing' 15
            $s=Wait-State 'complete' ($CaptureSeconds+15)
            $expectedLog="refresh-$session-$sequence.jsonl"
            if ($s.log -ne $expectedLog) { throw '预检日志文件名不匹配。' }
            Copy-Item -LiteralPath (Join-Path $RegistryRoot $expectedLog) -Destination $output
            Copy-Item -LiteralPath (Join-Path $RegistryRoot 'refresh-probe.status') -Destination (Join-Path $output "$mode.status") -Force
            Write-Host "预检 $mode 完成：World验证=$($s.worlds_validated)，Unit观察=$($s.native_units_seen)，错误=$($s.native_errors)"
            if ($s.native_errors -ne '0' -or $s.worlds_validated -ne 'true') {
                throw "预检未通过：$($s.native_last_error)。World列表=$($s.world_count)，已查询=$($s.world_queries)。不继续换装采样。"
            }
            if (($mode -eq 'focus' -or $mode -eq 'spatial') -and [int]$s.native_units_seen -le 0) {
                if ($mode -eq 'spatial' -and [int]$s.largest_world_units -lt 256) {
                    throw '尚未进入舰船场景（当前最大 World 不足 256 个 Unit）。请进入舰船后重新采样。'
                }
                throw '未取得 Unit 实例，不继续换装采样。请将结果目录发回分析。'
            }
            if ($mode -eq 'owner_trace') {
                $candidates = @(Get-Content -LiteralPath (Join-Path $output $expectedLog) |
                    ForEach-Object { $_ | ConvertFrom-Json } |
                    Where-Object { $_.kind -eq 'native_owner_trace' -and
                        $_.state -eq 'resolved_candidate' -and $_.association_state -eq 'complete' })
                if ($candidates.Count -lt 2) { throw '未获得可重复解析的 avatar 关联链，不继续换装。' }
            }
            if ($mode -eq 'code_refs' -or $mode -eq 'route_code' -or $mode -eq 'route_identity' -or $mode -eq 'route_pulse') {
                $end = @(Get-Content -LiteralPath (Join-Path $output $expectedLog) |
                    ForEach-Object { $_ | ConvertFrom-Json } |
                    Where-Object { $_.kind -eq 'code_survey_end' -and $_.complete -eq $true })
                if ($end.Count -ne 1 -or $s.truncated -ne 'false') { throw '代码引用调查未完整完成；保留结果，不做完整性推断。' }
                if ($ExperimentalPendingPulse -and $mode -eq 'route_identity') { continue }
                if ($mode -eq 'route_pulse') { Write-Host '单次待更新位实验结束；请分析结果，不能据此宣称安全。' }
                else { Write-Host '只读代码/身份调查完成；未调用候选函数。' }
                return
            }
        }
        if ($PreflightOnly -or $EntityPreflight -or $SpatialPreflight -or $OwnerDataPreflight -or $BindingPreflight -or $NativeOwnerPreflight) {
            if ($NativeOwnerTransitions) {
                Write-Host 'avatar 关联链重复读取预检通过；未执行换装，尚未证明本地玩家身份或刷新入口。'
                return
            }
            if ($NativeOwnerPreflight) {
                Write-Host '只读原生对象检查已结束；需检查候选日志，不能仅凭阶段完成断言解析成功。'
                return
            }
            if ($BindingPreflight) {
                Write-Host '绑定只读检查已结束；请检查日志中的 captured/unavailable 状态，尚未验证调用参数。'
                return
            }
            if ($OwnerDataPreflight) {
                Write-Host "角色候选=$($s.owner_candidates)，候选键查询=$($s.owner_queries)，存在=$($s.owner_present)。这不代表所属关系已验证。"
                if ([int]$s.owner_candidates -eq 0) { Write-Warning '未观察到角色候选，不能判断候选键是否存在。' }
                return
            }
            if ($EntityPreflight) {
                if ([int]$s.native_units_seen -gt 0) {
                    Write-Host "实体查询完成，取得存活 Unit 引用；尚未证明护甲所属关系。结果：$output" -ForegroundColor Green
                } else {
                    Write-Warning "实体查询完成，但没有取得存活 Unit 引用。这不是所属关系验证通过。结果：$output"
                }
            } else {
                Write-Host "World/Unit 访问预检通过；未执行换装。结果：$output" -ForegroundColor Green
            }
            return
        }
        if ($NativeOwnerTransitions) {
            $armorA = Read-Host '先穿上 A，打开军械库并保持；填写 A 名称'
            $armorB = Read-Host '填写待切换的 B 名称（本轮不要改变映射）'
            $phases = @(
                @('idle','保持军械库界面，不切换护甲。'),
                @('equip_b','在军械库中确认装备 B，然后保持不动。'),
                @('equip_a','切回并确认装备 A，然后保持不动。')
            )
        } elseif ($SpatialMovement) {
            $armorA = ''; $armorB = ''
            $phases = @(
                @('still','在舰船站立不动，不打开军械库。'),
                @('walk','在舰船中走几步并停下，不打开军械库。')
            )
        } else {
            $armorA = Read-Host '护甲 A 的名称（先穿上 A）'
            $armorB = Read-Host '护甲 B 的名称'
            $phases = @(
                @('idle','穿着 A，在舰船站立不动。'),
                @('open_armory','打开军械库，不切换护甲，保持界面。'),
                @('preview_b','仅预览 B，不确认装备；如果无法区分预览与装备，请保持不动并在备注中注明。'),
                @('equip_b','确认装备 B，然后保持不动。'),
                @('equip_a','切回并确认装备 A，然后保持不动。'),
                @('equip_b_repeat','再次切换并确认装备 B，然后保持不动。'),
                @('equip_a_repeat','再次切回并确认装备 A，然后保持不动。'),
                @('close_armory','关闭军械库，站立不动。')
            )
        }
        foreach ($phase in $phases) {
            Write-Host "`n下一阶段：$($phase[1])" -ForegroundColor Yellow
            $null = Read-Host '准备好后按回车；先不要执行动作'
            Send-Request 'capture' $phase[0] $(if ($NativeOwnerTransitions) { 'owner_trace' } elseif ($SpatialMovement) { 'spatial' } else { 'focus' })
            $null = Wait-State 'capturing' 15
            Save-PhaseMarker $phase[0] 'capture_acknowledged'
            Save-Context ("before_"+$phase[0])
            $ack = (Get-Date).ToUniversalTime().ToString('o')
            for ($i=3; $i -gt 0; $i--) { Write-Host "切回游戏：$i"; Start-Sleep -Seconds 1 }
            Beep-Notice
            $cue = (Get-Date).ToUniversalTime().ToString('o')
            Save-PhaseMarker $phase[0] 'action_cue'
            Write-Host "现在执行：$($phase[1])"
            $s = Wait-State 'complete' ($CaptureSeconds+15)
            Save-PhaseMarker $phase[0] 'capture_complete'
            Beep-Notice
            $expectedLog = "refresh-$session-$sequence.jsonl"
            if ($s.log -ne $expectedLog) { throw 'Lua 返回的日志文件名与本次请求不匹配。' }
            Copy-Item -LiteralPath (Join-Path $RegistryRoot $expectedLog) -Destination $output
            if ($s.native_errors -ne '0') { throw '实例读取失败，停止后续换装采样。结果已保存。' }
            Write-Host "阶段完成，快照数=$($s.samples)，截断=$($s.truncated)。请切回此窗口。"
            $note = Read-Host '备注（是否成功执行、是否误操作，可直接回车）'
            $notes.Add([pscustomobject]@{phase=$phase[0];request=$sequence;ackUtc=$ack;
                cueUtc=$cue;note=$note;log=$expectedLog;samples=$s.samples;truncated=$s.truncated})
            [IO.File]::WriteAllText((Join-Path $output 'session.json'),
                ([ordered]@{schema=1;session=$session;pid=$gamePid;armorA=$armorA;armorB=$armorB;
                    captureSeconds=$CaptureSeconds;phases=@($notes.ToArray())}|ConvertTo-Json -Depth 6),$utf8)
        }
        Write-Host "`n采样完成，提供此目录给我即可：`n$output" -ForegroundColor Green
    }
} finally {
    if ($handshake) {
        try { Send-Request 'stop'; $null=Wait-State 'stopped' 5 } catch { Write-Warning "未收到停止确认；当前采样将在限时后自动结束。$($_.Exception.Message)" }
    }
    if ($output) {
        Save-Context 'end' $true
        # Preserve partial captures if interrupted or the game exits.
        Get-ChildItem -LiteralPath $RegistryRoot -Filter "refresh-$session-*.jsonl" -File |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $output -Force -ErrorAction SilentlyContinue }
        foreach ($name in @('runtime.status','refresh-probe.status')) {
            $path=Join-Path $RegistryRoot $name
            if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $output -Force }
        }
        Write-Host "结果目录：$output"
    }
    if ($lease) { $lease.Dispose() }
}
