# Build the plugin (Debug) and start Rhino 9 with it loaded and the RhinoCode script server running.
# Waits until `rhinocode list` shows the instance. Usage (from the repo root):
#   powershell -ExecutionPolicy Bypass -File .claude/skills/sizzle-capture/scripts/launch-rhino.ps1
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..\..\..\..").Path
$proj = Join-Path $repo 'src\Freppuccino'
$bin = Join-Path $proj 'bin\Debug\net10.0\'
$rhino = 'C:\Program Files\Rhino 9\System\Rhino.exe'
$rhinocode = 'C:\Program Files\Rhino 9\System\RhinoCode.exe'

if (-not $NoBuild -and (Get-Process Rhino -ErrorAction SilentlyContinue)) {
    # A running Rhino holds Freppuccino.rhp locked: the build compiles but can't copy (MSB3027), so Rhino would keep the old plugin.
    throw 'Rhino is running: close it first to rebuild the plugin, or pass -NoBuild to keep the loaded build.'
}
if (-not $NoBuild) {
    Push-Location $proj
    try { dotnet build -c Debug | Select-Object -Last 3; if ($LASTEXITCODE -ne 0) { throw 'plugin build failed' } } finally { Pop-Location }
}
if (-not (Test-Path (Join-Path $bin 'Freppuccino.rhp'))) { throw "Plugin not built: $bin" }

$existing = & $rhinocode list | Select-String 'rhinocode_remotepipe'
if ($existing) { "Rhino already running with the script server: $existing"; exit 0 }

$env:FREPPUCCINO_AUTOSTART = '1'          # switch views to the Freppuccino display mode
$env:RHINO_PACKAGE_DIRS = $bin            # auto-load the freshly built .rhp
# Opening the Script Editor at startup is what starts the RhinoCode server; without it `rhinocode list` stays empty.
Start-Process -FilePath $rhino -ArgumentList '/nosplash', '/runscript="_ScriptEditor"'

$deadline = (Get-Date).AddSeconds(150)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    $line = & $rhinocode list | Select-String 'rhinocode_remotepipe'
    if ($line) { "Rhino ready: $line"; exit 0 }
}
throw 'Rhino started but the RhinoCode server never appeared (is a license or update dialog open?).'
