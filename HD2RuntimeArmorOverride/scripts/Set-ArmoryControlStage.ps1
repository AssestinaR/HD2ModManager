param(
    [Parameter(Mandatory)][string]$ExperimentRoot,
    [Parameter(Mandatory)]
    [ValidateSet('baseline','low_present','expanded_present','low_absent','expanded_absent','restore')]
    [string]$Stage
)
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw '此工具需要 PowerShell 7。' }
if (Get-Process -Name helldivers2,HD2ModManager -ErrorAction SilentlyContinue) {
    throw '切换对照配置前必须完全退出游戏及管理器。'
}
$root=[IO.Path]::GetFullPath($ExperimentRoot)
$manifest=Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$cli=Join-Path $repo 'HD2ModCLI/bin/Debug/net10.0/hd2mod.dll'
$protocol=Join-Path $manifest.registryRoot 'armor-registry.protocol'
$hash=(Get-FileHash -LiteralPath $protocol).Hash.ToLowerInvariant()
$allowed=@($manifest.originalProtocolSha256)+@($manifest.stages.PSObject.Properties.Value.sha256)
if ($hash -notin $allowed) { throw '运行时协议已被其他操作修改，停止，避免覆盖你的新设置。' }
$stageInfo=$manifest.stages.$Stage
$file=if($Stage -eq 'restore'){'original.protocol'}else{$stageInfo.protocolFile}
$expectedHash=if($Stage -eq 'restore'){$manifest.originalProtocolSha256}else{$stageInfo.sha256}
$candidate=Join-Path $root $file
if ((Get-FileHash -LiteralPath $candidate).Hash.ToLowerInvariant() -ne $expectedHash) {
    throw '测试协议或恢复备份校验失败。'
}
$profile=if($Stage -eq 'restore'){$manifest.originalProfileId}else{$stageInfo.profileId}
$common=@('--manager-root',$manifest.managerRoot,'--library',$manifest.libraryRoot,
    '--game-data',$manifest.gameData,'--json')
function Invoke-Cli([string[]]$Arguments) {
    $text=& dotnet $cli @Arguments @common
    if($LASTEXITCODE -ne 0){throw ($text -join "`n")}
    return ($text -join "`n"|ConvertFrom-Json)
}
$planPath=Join-Path $root ("deploy-"+$Stage+"-"+[guid]::NewGuid().ToString('N')+'.json')
$plan=Invoke-Cli @('deploy','plan',$profile,'--output',$planPath)
if(-not $plan.ok -or -not $plan.deployment.ready -or @($plan.deployment.diagnostics).Count){
    throw '部署计划未通过检查。'
}
# Refuse profile drift; original profiles are never edited by this experiment.
$expectedEntries=if($profile -eq $manifest.originalProfileId){
    @('02585a86-88ba-47e3-992f-4a05b4555458','4a6d8ea4-3342-45f8-9977-8371e7f69703','612eaf70-d682-43c7-9efd-16dcc695f977')
}else{@('612eaf70-d682-43c7-9efd-16dcc695f977')}
if(($plan.deployment.profile.logicalEntryIds -join ',') -ne ($expectedEntries -join ',')){
    throw '对照使用的配置条目已变更，停止部署。'
}
$validation=Invoke-Cli @('deploy','validate','--plan',$planPath)
if(-not $validation.ok -or -not $validation.validation.isValid){throw '部署验证失败。'}
$applied=Invoke-Cli @('deploy','apply','--plan',$planPath,'--confirm')
if(-not $applied.ok){throw '部署失败，未更换测试协议；请检查部署计划。'}

# Reuse the manager's real deployed-Unit validator, including sidecar bounds
# and Windows symbolic-link handling. No independent guess at TOC layout.
[Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $cli) 'HD2ModAdaptation.dll'))|Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $cli) 'HD2ModCore.dll'))|Out-Null
$units=[HD2ModCore.Infrastructure.RuntimeArmorResourceFilter]::ReadDeployedUnitsAsync(
    $manifest.gameData,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
$presence=@()
if($Stage -ne 'restore'){
    # Even baseline checks all custom Units used by the expanded variants.
    foreach($id in $manifest.stages.expanded_present.customUnits){
        $exists=$units.Contains([Convert]::ToUInt64($id,16))
        $presence+= [ordered]@{unit=$id;present=$exists}
        if($exists -ne [bool]$stageInfo.patchPresent){throw "Unit 实際部署不符合测试条件：$id"}
    }
}
if(Get-Process -Name helldivers2,HD2ModManager -ErrorAction SilentlyContinue){
    throw '部署期间游戏或管理器启动，未切换测试协议。'
}
if((Get-FileHash -LiteralPath $protocol).Hash.ToLowerInvariant() -ne $hash){
    throw '部署期间协议改变，拒绝覆盖。'
}
$temp=$protocol+'.control-'+[guid]::NewGuid().ToString('N')+'.tmp'
[IO.File]::WriteAllBytes($temp,[IO.File]::ReadAllBytes($candidate))
[IO.File]::Move($temp,$protocol,$true)
if((Get-FileHash -LiteralPath $protocol).Hash.ToLowerInvariant() -ne $expectedHash){
    throw '测试协议写后校验失败。'
}
$state=[ordered]@{schema=1;stage=$Stage;preparedUtc=[datetime]::UtcNow.ToString('o');
    profileId=$profile;protocolSha256=$expectedHash;deploymentPlan=$planPath;
    deployedUnits=$presence;revision=if($Stage -eq 'restore'){45}else{$stageInfo.revision}}
$state|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $root 'current-stage.json') -Encoding utf8
Write-Host "已准备：$Stage；原始配置和源 JSON 未修改。" -ForegroundColor Green
