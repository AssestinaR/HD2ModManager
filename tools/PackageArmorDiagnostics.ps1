param()
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$build=Join-Path $repo 'HD2ModCLI\bin\Debug\net10.0'
# Package only already-built Debug CLI artifacts; never build outside bin/obj.
$package=Join-Path $build ('diagnostics-package-'+(Get-Date -Format yyyyMMdd-HHmmss)+'-'+[guid]::NewGuid().ToString('N').Substring(0,6))
$folder=Join-Path $package 'Armor-Diagnostics'
New-Item -ItemType Directory -Path $folder -ErrorAction Stop|Out-Null
foreach($required in @('hd2mod.exe','hd2mod.dll','hd2mod.deps.json','hd2mod.runtimeconfig.json')){
    if(-not(Test-Path -LiteralPath (Join-Path $build $required))){throw '请先完成 CLI Debug 构建。'}
}
Get-ChildItem -LiteralPath $build -File |Where-Object {
    $_.Extension -in @('.dll','.exe') -or $_.Name -in @('hd2mod.deps.json','hd2mod.runtimeconfig.json')
}|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $folder}
if(Test-Path -LiteralPath (Join-Path $build 'runtimes')){
    Copy-Item -LiteralPath (Join-Path $build 'runtimes') -Destination $folder -Recurse
}
$launcher=@'
@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist "%~dp0..\settings.json" (
  echo Place the whole Armor-Diagnostics folder inside your configured manager folder.
  pause
  exit /b 2
)
if exist "%~dp0..\bin\dotnet\dotnet.exe" (
  "%~dp0..\bin\dotnet\dotnet.exe" "%~dp0hd2mod.dll" diagnostics armor wizard --manager-root "%~dp0.."
) else (
  "%~dp0hd2mod.exe" diagnostics armor wizard --manager-root "%~dp0.."
)
echo.
pause
'@
[IO.File]::WriteAllText((Join-Path $folder 'Start.cmd'),$launcher.Replace("`n","`r`n"),[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $repo 'HD2ModCLI\ARMOR-DIAGNOSTICS.md') -Destination (Join-Path $folder 'README.md')
$zip=Join-Path $package 'Armor-Diagnostics-Debug.zip'
Compress-Archive -LiteralPath $folder -DestinationPath $zip
$hash=(Get-FileHash -LiteralPath $zip).Hash
Write-Host "诊断附件（不含第三方 Lua 注入器/模型/配置）：$zip"
Write-Host "SHA256: $hash"
