# Builds PrefabEditorCore.dll - the server half of ATT Prefab Editor.
# Legacy Framework csc (C# 5), /nostdlib against the game's own Managed folder so
# the compile-time surface exactly matches the server's runtime.
#
# Requirements: a MelonLoader-patched copy of A Township Tale (you must own the
# game - its DLLs are never distributed with this kit) and Windows .NET
# Framework 4.x. Point -GamePath (or the ATT_GAME_DIR env var) at the game
# folder that contains "A Township Tale_Data" and "MelonLoader".
#
# Most users never run this: the kit ships a prebuilt DLL in server-kit\dist.
param(
    [string]$GamePath = $(if ($env:ATT_GAME_DIR) { $env:ATT_GAME_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\A Township Tale' })
)
$ErrorActionPreference = 'Stop'

$mgd  = Join-Path $GamePath 'A Township Tale_Data\Managed'
$ml   = Join-Path $GamePath 'MelonLoader\net35'
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out  = Join-Path $here 'PrefabEditorCore.dll'
$dist = Join-Path (Split-Path -Parent $here) 'dist'

if (-not (Test-Path $csc)) { throw "csc not found ($csc). Install .NET Framework 4.x developer tools." }
if (-not (Test-Path $mgd)) { throw "Managed dir not found: $mgd - pass -GamePath <your game folder> (must be MelonLoader-patched)" }
if (-not (Test-Path $ml))  { throw "MelonLoader\net35 not found under $GamePath - patch the game with MelonLoader first" }

$refs = @()
Get-ChildItem (Join-Path $mgd '*.dll') | ForEach-Object { $refs += "/r:$($_.FullName)" }
$refs += "/r:$(Join-Path $ml 'MelonLoader.dll')"
$refs += "/r:$(Join-Path $ml '0Harmony.dll')"

$src = @(Get-ChildItem $here -Recurse -Filter '*.cs' | ForEach-Object { $_.FullName })

$cscArgs = @('/target:library','/nostdlib+','/noconfig','/unsafe-','/warn:1',
             "/out:$out") + $refs + $src

Write-Host "Compiling PrefabEditorCore.dll ($($refs.Count) refs, $($src.Count) sources)..."
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }

New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item $out (Join-Path $dist 'PrefabEditorCore.dll') -Force
Write-Host "OK -> $out"
Write-Host "Staged -> $(Join-Path $dist 'PrefabEditorCore.dll')"
Get-Item $out | Select-Object Name, Length, LastWriteTime | Format-List
