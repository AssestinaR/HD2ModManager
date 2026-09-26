param(
    [string]$LuaJit = "luajit.exe"
)

$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent $project

Push-Location $repo
try {
    # Legacy probe tests emit fixtures here; the directory is git-ignored.
    New-Item -ItemType Directory -Path "$project/evidence" -Force | Out-Null
    & $LuaJit "$project/tests/test_armor_refresh_hotkey.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $LuaJit "$project/tests/test_armor_refresh_resources.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $LuaJit "$project/tests/test_armor_refresh_component.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $LuaJit "$project/tests/test_armor_refresh_multisite.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $LuaJit "$project/tests/test_armor_refresh_world_probe.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $LuaJit "$project/tests/test_armor_refresh_code_probe.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $LuaJit "$project/tests/test_armor_refresh_route_probe.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $LuaJit "$project/tests/test_refresh_binding_inspector.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $LuaJit "$project/tests/test_armor_refresh_probe.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $LuaJit "$project/tests/test_armor_registry_incremental.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $LuaJit "$project/tests/test_armor_registry_runtime.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $LuaJit "$project/tests/test_armor_metadata_probe_core.lua" `
        "$project/src/armor_metadata_probe_core.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $LuaJit `
        "$project/tests/test_lua_surface_probe.lua" `
        "$project/src/lua_surface_probe.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $LuaJit `
        "$project/tests/test_re824_to_re2310_override.lua" `
        "$project/src/re824_to_re2310_override.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $LuaJit `
        "$project/tests/test_composition_schema.lua" `
        "$project/src/composition_schema.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $LuaJit "$project/tests/test_composition_runtime_probe.lua" `
        "$project/src/composition_runtime_probe.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $LuaJit "$project/tests/test_composition_chain.lua" `
        "$project/src/composition_schema.lua" "$project/src/composition_chain.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $LuaJit "$project/tests/test_composition_index.lua" `
        "$project/src/composition_index.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $LuaJit "$project/tests/test_composition_clone_plan.lua" `
        "$project/src/composition_schema.lua" "$project/src/composition_clone_plan.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $LuaJit "$project/tests/test_composition_index_swap_test.lua" `
        "$project/src/composition_index_swap_test.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $LuaJit "$project/tests/test_composition_clone_publish_test.lua" `
        "$project/src/composition_clone_publish_test.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $LuaJit "$project/tests/test_armor_registry_protocol.lua" `
        "$project/src/armor_registry_protocol.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
