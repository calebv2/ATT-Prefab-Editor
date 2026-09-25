param(
    [string]$ServerRoot,
    [string]$GamePath,
    [string]$PanelPath
)
$ErrorActionPreference = 'Stop'
$KitRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$ServerKit = Join-Path $KitRoot 'server-kit'
$Dll = Join-Path $ServerKit 'dist\PrefabEditorCore.dll'

if (-not $GamePath) {
    if (-not $ServerRoot) {
        $ServerRoot = Read-Host 'Paste the A Township Tale server folder (or its parent folder)'
    }
    $ServerRoot = (Resolve-Path -LiteralPath $ServerRoot).Path
    if ((Test-Path -LiteralPath (Join-Path $ServerRoot 'MelonLoader')) -and
        (Test-Path -LiteralPath (Join-Path $ServerRoot 'version.dll'))) {
        $GamePath = $ServerRoot
    } else {
        $candidates = @('game-source','game-server','game') |
            ForEach-Object { Join-Path $ServerRoot $_ } |
            Where-Object { (Test-Path -LiteralPath (Join-Path $_ 'MelonLoader')) -and
                           (Test-Path -LiteralPath (Join-Path $_ 'version.dll')) }
        if ($candidates.Count -eq 1) {
            $GamePath = $candidates[0]
        } elseif ($candidates.Count -gt 1) {
            throw "Several game folders were found. Rerun with -GamePath pointing to the one your server starts."
        } else {
            throw "No MelonLoader game folder found under $ServerRoot. Pass -GamePath to the folder containing MelonLoader and version.dll."
        }
    }
}
$GamePath = (Resolve-Path -LiteralPath $GamePath).Path
if (-not (Test-Path -LiteralPath (Join-Path $GamePath 'MelonLoader'))) {
    throw "MelonLoader was not found in $GamePath. Install it in the server's game folder first."
}
if (-not (Test-Path -LiteralPath (Join-Path $GamePath 'version.dll'))) {
    throw "version.dll was not found in $GamePath. Confirm this is the server game folder."
}
if (-not (Test-Path -LiteralPath $Dll)) { throw "PrefabEditorCore.dll is missing from $Dll" }
foreach ($clash in @('TavernCore.dll','TavernConsole.dll')) {
    if (Test-Path -LiteralPath (Join-Path $GamePath "Mods\$clash")) {
        throw "$clash provides overlapping console modules. Remove it or skip this kit."
    }
}

$Mods = Join-Path $GamePath 'Mods'
New-Item -ItemType Directory -Path $Mods -Force | Out-Null
$Target = Join-Path $Mods 'PrefabEditorCore.dll'
if (Test-Path -LiteralPath $Target) {
    $backup = "$Target.before-v2-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item -LiteralPath $Target -Destination $backup
    Write-Host "Backed up the old core to $backup"
}
Copy-Item -LiteralPath $Dll -Destination $Target -Force

if (-not $PanelPath) { $PanelPath = Join-Path $env:USERPROFILE 'att-prefab-panel' }
$PanelPath = [System.IO.Path]::GetFullPath($PanelPath)
$SourcePanel = Join-Path $ServerKit 'panel'
$SourceData = Join-Path $ServerKit 'data\prefabs.json'
if (Test-Path -LiteralPath (Join-Path $PanelPath 'panel\server.js')) {
    $backup = Join-Path $PanelPath ("panel\server.js.before-v2-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    Copy-Item -LiteralPath (Join-Path $PanelPath 'panel\server.js') -Destination $backup
}
New-Item -ItemType Directory -Path (Join-Path $PanelPath 'panel') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $PanelPath 'data') -Force | Out-Null
foreach ($file in @('server.js')) {
    Copy-Item -LiteralPath (Join-Path $SourcePanel $file) -Destination (Join-Path $PanelPath 'panel') -Force
}
foreach ($folder in @('lib','public')) {
    Copy-Item -LiteralPath (Join-Path $SourcePanel $folder) -Destination (Join-Path $PanelPath 'panel') -Recurse -Force
}
Copy-Item -LiteralPath $SourceData -Destination (Join-Path $PanelPath 'data\prefabs.json') -Force

$consoleDir = Join-Path $GamePath 'UserData\att_console'
$launcher = @(
    '@echo off',
    'set "ATT_CONSOLE_DIR=' + $consoleDir + '"',
    'set "ATT_CONSOLE_DOCKER_CONTAINER="',
    'cd /d "%~dp0"',
    'node panel\server.js',
    'pause'
)
[System.IO.File]::WriteAllLines((Join-Path $PanelPath 'start-panel.bat'), $launcher)

Write-Host "Installed server core to $Target"
Write-Host "Installed panel to $PanelPath"
Write-Host 'Restart the A Township Tale server to load the core mod.'
if (Get-Command node -ErrorAction SilentlyContinue) {
    Write-Host 'After the server starts and provisions its console keys, double-click start-panel.bat to start the private panel.'
} else {
    Write-Host 'Install Node.js 20 or later, then double-click start-panel.bat to start the private panel.'
}
Write-Host 'Panel address: http://127.0.0.1:1766. Do not expose port 1766 publicly.'
