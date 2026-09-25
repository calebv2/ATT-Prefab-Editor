param(
    [string]$GamePath
)
$ErrorActionPreference = 'Stop'
$KitRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$Dll = Join-Path $KitRoot 'player-kit\dist\PrefabEditor.dll'
$Images = Join-Path $KitRoot 'player-kit\dist\PrefabEditorImages'

if (-not $GamePath) {
    $GamePath = Read-Host 'Paste your A Township Tale game folder (the folder with A Township Tale.exe)'
}
$GamePath = (Resolve-Path -LiteralPath $GamePath).Path
if (-not (Test-Path -LiteralPath (Join-Path $GamePath 'A Township Tale.exe'))) {
    throw "A Township Tale.exe was not found in $GamePath. Pass the game's install folder."
}
if (-not (Test-Path -LiteralPath $Dll)) { throw "PrefabEditor.dll is missing from $Dll" }
if (-not (Test-Path -LiteralPath $Images)) { throw "PrefabEditorImages is missing from $Images" }

$Mods = Join-Path $GamePath 'Mods'
New-Item -ItemType Directory -Path $Mods -Force | Out-Null
$Target = Join-Path $Mods 'PrefabEditor.dll'
if (Test-Path -LiteralPath $Target) {
    $backup = "$Target.before-v2-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item -LiteralPath $Target -Destination $backup
    Write-Host "Backed up the old mod to $backup"
}
Copy-Item -LiteralPath $Dll -Destination $Target -Force
Copy-Item -LiteralPath $Images -Destination (Join-Path $Mods 'PrefabEditorImages') -Recurse -Force
Write-Host "Prefab Editor installed to $Mods"
Write-Host 'Launch the game and press F1. Set PanelUrl in UserData\MelonPreferences.cfg if your panel is remote.'
