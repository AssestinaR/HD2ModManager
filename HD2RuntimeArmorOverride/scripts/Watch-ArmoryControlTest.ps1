param(
    [Parameter(Mandatory)][string]$ExperimentRoot,
    [ValidateRange(1,5)][int]$Cycles=3
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($ExperimentRoot)
$manifest=Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw|ConvertFrom-Json
$utf8=[Text.UTF8Encoding]::new($false)
$run=Join-Path $root ('run-'+(Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory -Path $run -ErrorAction Stop|Out-Null
$since=[datetime]::UtcNow
$gamePid=0
$stage='not_started'
$lease=$null
function Read-Status {
    $s=@{}
    try{
        foreach($line in [IO.File]::ReadAllLines((Join-Path $manifest.registryRoot 'runtime.status'))){
            if($line -match '^([^=]+)=(.*)$'){$s[$matches[1]]=$matches[2]}
        }
    }catch [IO.IOException]{}
    return $s
}
function Mark([string]$Event,[int]$Cycle=0,[string]$Note=''){
    $r=[ordered]@{utc=[datetime]::UtcNow.ToString('o');stage=$stage;pid=$gamePid;
        event=$Event;cycle=$Cycle;note=$Note}
    [IO.File]::AppendAllText((Join-Path $run 'actions.jsonl'),($r|ConvertTo-Json -Compress)+"`n",$utf8)
}
function Context([string]$Label,[bool]$Events=$false){
    & "$PSScriptRoot/Save-ArmorRefreshContext.ps1" -OutputDirectory $run -Label $Label `
        -GameProcessId $gamePid -SinceUtc $since -RegistryRoot $manifest.registryRoot -CollectEvents:$Events
}
function Verify {
    $spec=$manifest.stages.$stage
    $s=Read-Status
    if($s.pid -ne "$gamePid"){
        throw "游戏会话已更换（原 PID=$gamePid，状态 PID=$($s.pid)），本轮停止；重启后的结果不能合并到原会话。"
    }
    if($s.state -ne 'ready' -or $s.revision -ne "$($spec.revision)" `
        -or $s.active_mappings -ne "$($spec.mappingCount)" `
        -or $s.retained_clone_count -ne "$($spec.mappingCount)" `
        -or $s.retained_clone_bytes -ne "$($spec.expectedCloneBytes)"){
        throw '本次游戏的运行时状态、映射数或克隆大小不符合测试条件。'
    }
    $h=(Get-FileHash -LiteralPath (Join-Path $manifest.registryRoot 'armor-registry.protocol')).Hash
    if($h.ToLowerInvariant() -ne $spec.sha256){throw '测试期间协议发生改变。'}
    if(-not(Get-Process -Id $gamePid -ErrorAction SilentlyContinue)){throw '游戏已退出。'}
}
try{
    $lease=[IO.File]::Open((Join-Path $root 'experiment.lock'),[IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    Write-Host '军械库对照：5组，每组重新启动。不要打开管理器、应用映射或改变身形/头盔。' -ForegroundColor Cyan
    Write-Host '目标 RE-1861。缺资源组身体不显示、或少部件组衣服缺失是预期现象；重点只观察关闭军械库。'
    Write-Host '任一组崩溃就停止，不必继续凑次数。程序不会附加调试器、强制刷新或调用未知函数。'
    foreach($stageName in @('baseline','low_present','expanded_present','low_absent','expanded_absent')){
        $stage=$stageName
        $null=Read-Host "准备测试 $stage；完全退出上一轮游戏后按回车"
        & "$PSScriptRoot/Set-ArmoryControlStage.ps1" -ExperimentRoot $root -Stage $stage
        Mark 'prepared'
        Copy-Item -LiteralPath (Join-Path $root 'current-stage.json') -Destination (Join-Path $run "$stage.prepared.json")
        Start-Process 'steam://rungameid/553850'
        $deadline=[datetime]::UtcNow.AddMinutes(3)
        $gamePid=0
        do{
            Start-Sleep -Seconds 1
            $games=@(Get-Process -Name helldivers2 -ErrorAction SilentlyContinue)
            if($games.Count -eq 1){$gamePid=$games[0].Id}
            if($games.Count -gt 1){throw '发现多个游戏进程。'}
            $s=Read-Status
            if($gamePid -and $s.pid -eq "$gamePid" -and $s.state -eq 'ready' `
                -and $s.revision -eq "$($manifest.stages.$stage.revision)"){break}
        }while([datetime]::UtcNow -lt $deadline)
        if(-not $gamePid){throw '游戏未启动。'}
        Verify
        Context ($stage+'_ready')
        Mark 'runtime_verified'
        if($stage -eq 'baseline'){
            Mark 'target_setup_prompt'
            Write-Host '进入舰船后，先装备 RE-1861，退出军械库。后续所有组保持穿着它。' -ForegroundColor Yellow
            $answer=Read-Host '准备成功直接回车；若设置过程中崩溃请输入 c'
            if($answer -eq 'c'){Mark 'reported_crash_setup';throw '基线设置阶段崩溃。'}
        }
        Mark 'ship_ready_prompt'
        $answer=Read-Host '进入舰船且仍穿 RE-1861 后回车；此前已崩溃输入 c，卡死输入 h；不要自行重启后接着本轮'
        if($answer -ne ''){
            Mark 'reported_before_armory' 0 $answer
            throw '用户报告军械库操作前异常或备注，已保存原文；停止本会话，等待分析。'
        }
        Mark 'ship_ready_confirmed'
        for($i=1;$i -le $Cycles;$i++){
            Verify
            Context ($stage+'_before_'+@('zero','one','two','three','four','five')[$i])
            Mark 'action_prompt' $i
            Write-Host "第 $i/$Cycles 次：打开军械库，不选其他护甲、不预览、不确认换装，直接退出。等约5秒，再切回此窗口。" -ForegroundColor Yellow
            $answer=Read-Host '成功回车；崩溃/窗口卡死请输入 c；误操作请输入 x'
            Mark 'user_result' $i $(if($answer -eq ''){'success'}else{$answer})
            Context ($stage+'_after_'+@('zero','one','two','three','four','five')[$i]) $true
            if($answer -eq 'c'){throw '用户报告崩溃，停止剩余测试。请用 Steam 彻底结束残留进程。'}
            if($answer -ne ''){throw '本轮存在误操作，停止以免混用结果。'}
            Verify
        }
        Mark 'stage_completed'
        $null=Read-Host '本组结束，请从游戏正常退出；确认 Steam 不再显示运行中，然后按回车'
        if(Get-Process -Name helldivers2 -ErrorAction SilentlyContinue){throw '游戏仍有残留进程，停止切换配置。'}
        Context ($stage+'_exited') $true
    }
}catch{
    Mark 'stopped_with_error' 0 $_.Exception.Message
    Write-Warning $_.Exception.Message
}finally{
    if($gamePid){try{Context 'final' $true}catch{Write-Warning $_.Exception.Message}}
    if(-not(Get-Process -Name helldivers2,HD2ModManager -ErrorAction SilentlyContinue)){
        try{& "$PSScriptRoot/Set-ArmoryControlStage.ps1" -ExperimentRoot $root -Stage restore}
        catch{Write-Warning ("恢复未完成："+$_.Exception.Message)}
    }else{
        Write-Warning '游戏/管理器仍运行，未恢复。完全结束后运行实验目录中的“恢复原配置.cmd”。'
    }
    if($lease){$lease.Dispose()}
    Write-Host "结果目录：$run" -ForegroundColor Cyan
}
