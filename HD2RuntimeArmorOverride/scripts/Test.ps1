param(
    [string]$LuaJit = "luajit.exe"
)

$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent $project

Push-Location $repo
try {
    & $LuaJit `
        "$project/tests/test_lua_surface_probe.lua" `
        "$project/src/lua_surface_probe.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $LuaJit `
        "$project/tests/test_re824_to_re2310_override.lua" `
        "$project/src/re824_to_re2310_override.lua"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
