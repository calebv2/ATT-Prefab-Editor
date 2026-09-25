# Builds PrefabEditor.dll - the ATT Prefab Editor client mod (MelonLoader, C# 5/net35).
# Legacy Framework csc, /nostdlib against the game's own Managed folder.
#
# Requirements: your own installed copy of A Township Tale, patched with
# MelonLoader (the game's DLLs are never distributed with this kit), plus
# Windows .NET Framework 4.x. Point -GamePath (or ATT_GAME_DIR) at the game
# folder containing "A Township Tale_Data" and "MelonLoader".
#
# Most users never run this: the kit ships a prebuilt DLL in player-kit\dist.
# Pass -Deploy to also copy the fresh DLL into <game>\Mods.
param(
    [string]$GamePath = $(if ($env:ATT_GAME_DIR) { $env:ATT_GAME_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\A Township Tale' }),
    [switch]$Deploy
)
$ErrorActionPreference = 'Stop'

$mgd  = Join-Path $GamePath 'A Township Tale_Data\Managed'
$ml   = Join-Path $GamePath 'MelonLoader\net35'
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $here 'src'
$out  = Join-Path $here 'PrefabEditor.dll'
$dist = Join-Path (Split-Path -Parent $here) 'dist'
$imageIndex = Join-Path $dist 'PrefabEditorImages\index.json'

if (-not (Test-Path $csc)) { throw "csc not found ($csc). Install .NET Framework 4.x developer tools." }
if (-not (Test-Path $mgd)) { throw "Managed dir not found: $mgd - pass -GamePath <your game folder>" }
if (-not (Test-Path $ml))  { throw "MelonLoader\net35 not found under $GamePath - patch the game with MelonLoader first" }

$refs = @()
Get-ChildItem (Join-Path $mgd '*.dll') | ForEach-Object { $refs += "/r:$($_.FullName)" }
$refs += "/r:$(Join-Path $ml 'MelonLoader.dll')"

$files = @(Get-ChildItem $src -Filter '*.cs' | ForEach-Object { $_.FullName })

$cscArgs = @('/target:library','/nostdlib+','/noconfig','/unsafe-','/warn:1',
             "/out:$out") + $refs + $files

Write-Host "Compiling PrefabEditor.dll ($($refs.Count) refs, $($files.Count) sources)..."
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }

New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item $out (Join-Path $dist 'PrefabEditor.dll') -Force
Write-Host "OK -> $out"
Write-Host "Staged -> $(Join-Path $dist 'PrefabEditor.dll')"

# Keep the bundled preview images beside the compiled mod and report how many
# catalog entries have a staged image. The Workbench uses this same folder.
$imageCount = 0
$distImages = Join-Path $dist 'PrefabEditorImages'
if (Test-Path $imageIndex) {
    $imageData = Get-Content -Raw $imageIndex | ConvertFrom-Json
    foreach ($property in $imageData.items.PSObject.Properties) {
        if ($property.Value.file -and $property.Name -match '^\d+$' -and
            (Test-Path (Join-Path $distImages ($property.Name + '.png')))) { $imageCount++ }
    }
    Write-Host "Using $imageCount staged prefab images -> $distImages"
} else {
    Write-Warning "Prefab image index not found in $distImages; the mod will show previews only for staged hash-named PNG files."
}

if ($Deploy) {
    $mods = Join-Path $GamePath 'Mods'
    New-Item -ItemType Directory -Force $mods | Out-Null
    Copy-Item $out (Join-Path $mods 'PrefabEditor.dll') -Force
    Write-Host "Deployed -> $(Join-Path $mods 'PrefabEditor.dll')"
    $modsImages = Join-Path $mods 'PrefabEditorImages'
    New-Item -ItemType Directory -Force $modsImages | Out-Null
    if (Test-Path (Join-Path $dist 'PrefabEditorImages')) {
        Copy-Item (Join-Path $dist 'PrefabEditorImages\*.png') $modsImages -Force
        Write-Host "Deployed prefab images -> $modsImages"
    }
}
Get-Item $out | Select-Object Name, Length, LastWriteTime | Format-List
