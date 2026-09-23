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

$compositionEntry = Join-Path $OutputDirectory "composition_probe.generated.lua"
& $Python (Join-Path $PSScriptRoot "build_composition_probe.py") --output $compositionEntry
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$registryEntry = Join-Path $OutputDirectory "armor_registry.generated.lua"
& $Python (Join-Path $PSScriptRoot "build_armor_registry.py") --output $registryEntry
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $Python $builder `
    --name mods/codex/armor_re824_to_re2310_override `
    --entry (Join-Path $project "src/re824_to_re2310_override.lua") `
    --guid e5fa6c24-346b-4fa7-9358-f8d1d81afe98 `
    --display-name "RE-824 to RE-2310 Runtime Override Test" `
    --output (Join-Path $OutputDirectory "RE-824-to-RE-2310-Runtime-Override-Test.zip")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $Python $builder `
    --name mods/codex/armor_composition_probe `
    --entry $compositionEntry `
    --guid 7f67b8b8-c8ee-4c1c-8da7-2b985d5094a4 `
    --display-name "HD2 Armor Composition Read-Only Probe" `
    --output (Join-Path $OutputDirectory "HD2-Armor-Composition-ReadOnly-Probe.zip")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $Python $builder `
    --name mods/codex/armor_composition_index_swap_test `
    --entry (Join-Path $project "src/composition_index_swap_test.lua") `
    --guid 48ff25cf-dd19-4f69-8c9a-6633dd349b1c `
    --display-name "RE-1861 to RE-2310 Composition Index Swap Test" `
    --output (Join-Path $OutputDirectory "RE-1861-to-RE-2310-Composition-Index-Swap-Test.zip")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $Python $builder `
    --name mods/codex/armor_composition_clone_publish_test `
    --entry (Join-Path $project "src/composition_clone_publish_test.lua") `
    --guid 554327d3-b267-4860-b7da-ec8329e70ec2 `
    --display-name "B-08 Identity-Preserving Composition Expansion Test" `
    --output (Join-Path $OutputDirectory "B-08-Identity-Preserving-Composition-Expansion-Test.zip")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $Python $builder `
    --name mods/codex/armor_lua_surface_probe `
    --entry (Join-Path $project "src/lua_surface_probe.lua") `
    --guid 63da255a-cba8-432b-b9e3-80eb30daf895 `
    --display-name "HD2 Armor Lua Surface Probe" `
    --output (Join-Path $OutputDirectory "HD2-Armor-Lua-Surface-Probe.zip")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $Python $builder `
    --name mods/codex/runtime_armor_registry `
    --entry $registryEntry `
    --guid 6dd5cb37-6b45-4f47-a57c-65e3f2f9e0ca `
    --display-name "HD2 Runtime Armor Registry" `
    --output (Join-Path $OutputDirectory "HD2-Runtime-Armor-Registry.zip")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
