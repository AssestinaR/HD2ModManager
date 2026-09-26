param(
    [string]$RegistryRoot="$env:LOCALAPPDATA\CowboyBingus\Helldivers2\ArmorRegistry",
    [string]$OutputRoot="$PSScriptRoot\..\evidence\refresh-hotkey",
    [ValidateSet('F6','F7','F8','F9','F10')][string]$Key='F8',
    [ValidateRange(1,30)][int]$Samples=8,
    [switch]$SelfTest,
    [switch]$SmokeTest
)
$ErrorActionPreference='Stop'
# RegisterHotKey/WM_HOTKEY only: no keyboard hook, injection or game input.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ArmorCaptureKeys {
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x,y; }
    [StructLayout(LayoutKind.Sequential)] struct MSG {
        public IntPtr hwnd; public uint message; public UIntPtr wParam;
        public IntPtr lParam; public uint time; public POINT pt; public uint lPrivate;
    }
    [DllImport("user32.dll",SetLastError=true)] static extern bool RegisterHotKey(IntPtr h,int id,uint mods,uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h,int id);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg,IntPtr h,uint min,uint max,uint remove);
    [DllImport("kernel32.dll")] static extern uint GetTickCount();
    [DllImport("kernel32.dll")] static extern bool QueryPerformanceCounter(out long value);
    [DllImport("kernel32.dll")] static extern bool QueryPerformanceFrequency(out long value);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    public static long Frequency() { long n; if(!QueryPerformanceFrequency(out n))throw new Exception("QPC unavailable");return n; }
    public static double Seconds() { long n; QueryPerformanceCounter(out n);return (double)n/Frequency(); }
    public static uint ForegroundPid() { uint p; GetWindowThreadProcessId(GetForegroundWindow(),out p);return p; }
    public static void Register(int id,uint key) {
        if(!RegisterHotKey(IntPtr.Zero,id,0x4000,key))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Shortcut unavailable; choose another key.");
    }
    public static void Remove(int id) { UnregisterHotKey(IntPtr.Zero,id); }
    public static int Poll(out uint delayMs) {
        MSG m; delayMs=0;
        if(!PeekMessage(out m,IntPtr.Zero,0x312,0x312,1))return 0;
        delayMs=unchecked(GetTickCount()-m.time);
        return (int)m.wParam.ToUInt32();
    }
}
'@
function Get-MarkerDecision([bool]$Ready,[int]$Pending) {
    if(-not $Ready){return 'not_ready'}
    if($Pending -gt 0){return 'busy'}
    return 'accept'
}
if($SelfTest) {
    $id=0x4921
    try {
        [ArmorCaptureKeys]::Register($id,[uint32](0x70+[int]$Key.Substring(1)-1))
        [ArmorCaptureKeys]::Register(0x4922,0x7A)
        $a=[ArmorCaptureKeys]::Seconds(); Start-Sleep -Milliseconds 20; $b=[ArmorCaptureKeys]::Seconds()
        if($b -le $a){throw 'QPC did not advance'}
        if((Get-MarkerDecision $true 0) -ne 'accept' -or
           (Get-MarkerDecision $false 0) -ne 'not_ready' -or
           (Get-MarkerDecision $true 2) -ne 'busy'){throw 'Marker decision test failed'}
        $foreground=[ArmorCaptureKeys]::ForegroundPid()
        Write-Host "PASS: hotkey register/unregister, marker gating, foreground diagnostic=$foreground, QPC frequency=$([ArmorCaptureKeys]::Frequency())"
    } finally {[ArmorCaptureKeys]::Remove($id);[ArmorCaptureKeys]::Remove(0x4922)}
    return
}
$lease=$null;$registered=$false;$armed=$false;$output=$null;$game=$null
$session=[guid]::NewGuid().ToString('N');$seq=0;$count=0;$pending=0;$copied=@{}
if($SmokeTest){$Samples=1}
$utf8=[Text.UTF8Encoding]::new($false);$invariant=[Globalization.CultureInfo]::InvariantCulture
function Read-Status {
    $r=@{}
    try {foreach($line in [IO.File]::ReadAllLines((Join-Path $RegistryRoot 'refresh-hotkey.status'))){
        if($line -match '^([^=]+)=(.*)$'){$r[$matches[1]]=$matches[2]}
    }} catch [IO.IOException] {}
    return $r
}
function Send-Request([string]$Command,[string]$Extra='') {
    $script:seq++
    $text="schema=1`npid=$gamePid`nsession=$session`nrequest=$seq`ncommand=$Command`n$Extra"
    $path=Join-Path $RegistryRoot 'refresh-hotkey.request';$temp="$path.$session.tmp"
    [IO.File]::WriteAllText($temp,$text,$utf8)
    try {
        if([IO.File]::Exists($path)){
            $backup="$path.$session.previous"
            [IO.File]::Replace($temp,$path,$backup)
            [IO.File]::Delete($backup)
        }
        else{[IO.File]::Move($temp,$path)}
    } finally {if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}}
}
function Tone([int]$Frequency) {try{[Console]::Beep($Frequency,65)}catch{}}
function Marker($row) {
    [IO.File]::AppendAllText((Join-Path $output 'markers.jsonl'),
        (($row|ConvertTo-Json -Compress)+"`n"),$utf8)
}
try {
    if(-not(Test-Path -LiteralPath $RegistryRoot)){throw '未找到运行时目录。'}
    $lease=[IO.File]::Open((Join-Path $RegistryRoot 'refresh-guide.lock'),'OpenOrCreate','ReadWrite','None')
    $games=@(Get-Process -Name helldivers2 -ErrorAction SilentlyContinue)
    if($games.Count -ne 1){throw '请先启动游戏并进入舰船；应只有一个游戏进程。'}
    $game=$games[0];if($game.HasExited){throw '游戏存在退出残留，请通过 Steam 结束后再启动。'}
    $gamePid=$game.Id;$startTicks=$game.StartTime.ToUniversalTime().Ticks
    $protocolHash=(Get-FileHash -LiteralPath (Join-Path $RegistryRoot 'armor-registry.protocol')).Hash
    $output=Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ((Get-Date -Format yyyyMMdd-HHmmss)+'-'+$session)
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    [ArmorCaptureKeys]::Register(0x4921,[uint32](0x70+[int]$Key.Substring(1)-1))
    try {[ArmorCaptureKeys]::Register(0x4922,0x7A)} catch {[ArmorCaptureKeys]::Remove(0x4921);throw}
    $registered=$true
    Write-Host "护甲瞬时采样：$Key 全局标记，F11 结束。前台窗口仅记录，不阻止采样。" -ForegroundColor Cyan
    Write-Host '工具运行时请勿在其他程序中使用该快捷键，避免产生与换装无关的标记。'
    Write-Host '保持舰船场景，等待“就绪”高音后再换装；不要应用映射、部署或切换场景。'
    Write-Host '建议：前2次只按键不换装；再做4次 A/B切换；最后2次切到不同结构的C。'
    Write-Host '同步按标记键和确认装备；完成提示后即可继续。无需输入备注。'
    Send-Request 'arm';$armed=$true
    $deadline=[datetime]::UtcNow.AddSeconds(35);$sessionDeadline=[datetime]::UtcNow.AddSeconds(295)
    $readyAnnounced=$false;$lastHealth=[datetime]::MinValue;$quit=$false
    while(-not $quit -and $count -lt $Samples) {
        if([datetime]::UtcNow -gt $sessionDeadline){throw '会话达到时间上限。'}
        if(([datetime]::UtcNow-$lastHealth).TotalSeconds -ge 1) {
            $game.Refresh()
            if($game.HasExited -or $game.StartTime.ToUniversalTime().Ticks -ne $startTicks){throw '游戏已退出；采样停止。'}
            $lastHealth=[datetime]::UtcNow
        }
        $s=Read-Status
        if($s.session -eq $session -and $s.pid -eq "$gamePid") {
            if($s.state -eq 'error'){throw "Lua采样错误：$($s.reason)"}
            if($s.state -eq 'stopped'){break}
            if($s.state -eq 'ready' -and -not $readyAnnounced) {
                if($s.hotkey_version -ne '4'){throw '请退出游戏并部署新版深度探针（hotkey_version=4）。'}
                if($s.frequency -ne "$([ArmorCaptureKeys]::Frequency())"){throw '两端QPC频率不一致。'}
                $readyAnnounced=$true;Tone 1200;Write-Host '已就绪：现在可同步按快捷键并确认装备。' -ForegroundColor Green
            }
            if($pending -gt 0 -and $s.state -eq 'ready' -and $s.request -eq "$pending") {
                $name="hotkey-$session-$pending.jsonl"
                if($s.log -ne $name){throw '结果文件身份不匹配。'}
                Copy-Item -LiteralPath (Join-Path $RegistryRoot $name) -Destination $output
                $copied[$name]=$true;$count++;$pending=0
                Marker @{event='completed';request=$s.request;utc=[datetime]::UtcNow.ToString('o');valid=$s.window_valid;reason=$s.reason}
                if($s.window_valid -eq 'true'){Tone 1200}else{Tone 350}
                Write-Host "[$count/$Samples] 完成，有效=$($s.window_valid)，原因=$($s.reason)；可以下一次。"
            }
        }
        if(($pending -gt 0 -or -not $readyAnnounced) -and [datetime]::UtcNow -gt $deadline){
            throw '等待Lua超时；请确认部署新版插件，且游戏未暂停update。'
        }
        [uint32]$delay=0
        $id=[ArmorCaptureKeys]::Poll([ref]$delay)
        if($SmokeTest -and $readyAnnounced -and $pending -eq 0 -and $count -eq 0){$id=0x4921;$delay=0}
        if($id -eq 0x4922){$quit=$true;continue}
        if($id -eq 0x4921) {
            $qpc=[ArmorCaptureKeys]::Seconds()
            $foregroundPid=[ArmorCaptureKeys]::ForegroundPid()
            $decision=Get-MarkerDecision $readyAnnounced $pending
            if($decision -ne 'accept') {
                Marker @{event='ignored';reason=$decision;qpc=$qpc;busy=($pending -gt 0);
                    ready=$readyAnnounced;foregroundPid=$foregroundPid;gamePid=$gamePid}
                Write-Host "标记未接受：$decision（尚未就绪或上一窗口未完成）。" -ForegroundColor Yellow
                Tone 350
            } else {
                # QPC at dequeue, OS queue age recorded separately, not an equip event.
                $extra="qpc_seconds=$($qpc.ToString('F9',$invariant))`nfrequency=$([ArmorCaptureKeys]::Frequency())`nmessage_delay_ms=$delay`n"
                Send-Request 'mark' $extra;$pending=$seq;$deadline=[datetime]::UtcNow.AddSeconds(5)
                Marker @{event=$(if($SmokeTest){'synthetic_control'}else{'hotkey'});request=$seq;qpc=$qpc;
                    messageDelayMs=$delay;utc=[datetime]::UtcNow.ToString('o');
                    foregroundPid=$foregroundPid;gamePid=$gamePid;gameForeground=($foregroundPid -eq $gamePid)}
                Write-Host "已接受标记 #$seq：主窗口前后各0.5秒，另补采0.75秒资源身份。"
                # No synchronous beep here: do not delay sending/handling the marker.
            }
        }
        Start-Sleep -Milliseconds 5
    }
} finally {
    if($registered){[ArmorCaptureKeys]::Remove(0x4921);[ArmorCaptureKeys]::Remove(0x4922)}
    if($armed){
        try {
            Send-Request 'stop'
            $end=[datetime]::UtcNow.AddSeconds(3);$confirmed=$false
            do {
                Start-Sleep -Milliseconds 50;$s=Read-Status
                $confirmed=$s.session -eq $session -and $s.request -eq "$seq" -and $s.state -eq 'stopped'
            }while(-not $confirmed -and [datetime]::UtcNow -lt $end)
            if(-not $confirmed){Write-Warning '未确认停止；探针有5分钟会话上限。'}
        } catch {Write-Warning $_}
    }
    if($output){
        Get-ChildItem -LiteralPath $RegistryRoot -Filter "hotkey-$session-*.jsonl" -File |
            ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $output -Force}
        if(Test-Path (Join-Path $RegistryRoot 'refresh-hotkey.status')){
            Copy-Item -LiteralPath (Join-Path $RegistryRoot 'refresh-hotkey.status') -Destination $output -Force
        }
        [IO.File]::WriteAllText((Join-Path $output 'session.json'),
            (@{schema=1;toolVersion=2;foregroundPolicy='diagnostic_only';session=$session;pid=$gamePid;startTicks=$startTicks;key=$Key;
                completed=$count;protocolHash=$protocolHash;windowSeconds=1;markerIsEquipEvent=$false;
                resourcesResolved=$false;syntheticControl=[bool]$SmokeTest}|ConvertTo-Json),$utf8)
        Write-Host "结果目录：$output" -ForegroundColor Cyan
    }
    if($lease){$lease.Dispose()};if($game){$game.Dispose()}
}
