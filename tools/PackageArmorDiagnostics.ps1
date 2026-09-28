param([switch]$Composition,[switch]$Memory)
$ErrorActionPreference='Stop'
if($Composition -and $Memory){throw '只能选择一种诊断包。'}
$repo=Split-Path -Parent $PSScriptRoot
$project=Join-Path $repo 'HD2ModCli\HD2ModCli.csproj'
$build=Join-Path $repo 'HD2ModCli\bin\Debug\net10.0\win-x64\diagnostics-publish'
# Keep all generated output in the project's ignored bin directory.
dotnet publish $project -c Debug -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=false `
    -p:IncludeNativeLibrariesForSelfExtract=true -o $build -v quiet
if($LASTEXITCODE -ne 0){throw '自包含诊断程序构建失败。'}
$prefix=if($Memory){'memory-package-'}elseif($Composition){'composition-package-'}else{'diagnostics-package-'}
$name=if($Memory){'Armor-Memory-Check'}elseif($Composition){'Armor-Composition-Check'}else{'Armor-Diagnostics'}
$package=Join-Path $repo 'HD2ModCli\bin\Debug' `
    ($prefix+(Get-Date -Format yyyyMMdd-HHmmss)+'-'+[guid]::NewGuid().ToString('N').Substring(0,6))
$folder=Join-Path $package $name
New-Item -ItemType Directory -Path $folder -ErrorAction Stop|Out-Null
$exe=Join-Path $build 'hd2mod.exe'
if(-not(Test-Path -LiteralPath $exe)){throw '未找到自包含可执行文件。'}
$previousRoot=$env:DOTNET_ROOT
try {
    $env:DOTNET_ROOT='Z:\missing-runtime'
    $smoke=& $exe --help
    if($LASTEXITCODE -ne 0 -or -not($smoke -match 'diagnostics armor matrix') -or -not($smoke -match 'diagnostics armor memory')){
        throw '自包含可执行文件启动校验失败。'
    }
} finally { $env:DOTNET_ROOT=$previousRoot }
Copy-Item -LiteralPath $exe -Destination $folder
$commandName=if($Memory){'memory'}elseif($Composition){'composition'}else{'matrix'}
$launcher=@'
@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist "%~dp0..\settings.json" (
  echo 请将整个诊断文件夹放进管理器目录，然后双击文件夹里的 Start.cmd。
  pause
  exit /b 2
)
"%~dp0hd2mod.exe" diagnostics armor __MODE__ --manager-root "%~dp0.."
echo.
pause
'@
[IO.File]::WriteAllText((Join-Path $folder 'Start.cmd'),$launcher.Replace('__MODE__',$commandName).Replace("`n","`r`n"),[Text.UTF8Encoding]::new($false))
$readme=if($Memory){'ARMOR-MEMORY-CHECK.md'}elseif($Composition){'ARMOR-COMPOSITION-CHECK.md'}else{'ARMOR-DIAGNOSTICS.md'}
Copy-Item -LiteralPath (Join-Path $repo 'HD2ModCli' $readme) -Destination (Join-Path $folder 'README.md')
$zip=Join-Path $package $(if($Memory){'Armor-Memory-Check-win-x64.zip'}elseif($Composition){'Armor-Composition-Check-win-x64.zip'}else{'Armor-Diagnostics-SelfContained-win-x64.zip'})
Compress-Archive -LiteralPath $folder -DestinationPath $zip
$hash=(Get-FileHash -LiteralPath $zip).Hash
Write-Host "自包含诊断附件（不含第三方 Lua 注入器/模型/配置）：$zip"
Write-Host "SHA256: $hash"
