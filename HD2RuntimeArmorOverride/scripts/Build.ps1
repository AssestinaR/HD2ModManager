param(
    [string]$Python = "python",
    [string]$LoaderSource,
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent $project
if (-not $LoaderSource) {
    $LoaderSource = Join-Path $repo "docs/BingusSharedLoader-main"
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $project "dist"
}
$builder = Join-Path $LoaderSource "scripts/build_addon.py"
if (-not (Test-Path -LiteralPath $builder)) {
    throw "Bingus addon builder not found: $builder"
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

& $Python $builder `
    --name mods/codex/armor_re824_to_re2310_override `
    --entry (Join-Path $project "src/re824_to_re2310_override.lua") `
    --guid e5fa6c24-346b-4fa7-9358-f8d1d81afe98 `
    --display-name "RE-824 to RE-2310 Runtime Override Test" `
    --output (Join-Path $OutputDirectory "RE-824-to-RE-2310-Runtime-Override-Test.zip")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $Python $builder `
    --name mods/codex/armor_lua_surface_probe `
    --entry (Join-Path $project "src/lua_surface_probe.lua") `
    --guid 63da255a-cba8-432b-b9e3-80eb30daf895 `
    --display-name "HD2 Armor Lua Surface Probe" `
    --output (Join-Path $OutputDirectory "HD2-Armor-Lua-Surface-Probe.zip")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
