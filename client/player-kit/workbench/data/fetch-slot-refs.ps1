# Pulls Ethyn's Visual Slot Reference (community Google Sheet) into
# data/slot-refs/: one annotated reference image per prefab + index.json with
# the color legend (color -> slot name -> slot hash, att-string-transcoder
# naming). The workbench shows these behind the "slots" button.
#
#   powershell -ExecutionPolicy Bypass -File data\fetch-slot-refs.ps1
#
# The sheet is link-shared (not published), so images ride the xlsx export:
# over-grid drawings anchored per row; the CSV export carries the text table.
# Both are fetched fresh unless data\sheets-raw\ethyn.xlsx already exists
# (delete it to force a redownload). Credit: Ethyn's Visual Slot Reference.

$ErrorActionPreference = 'Stop'
$doc = '1piXLvavmMETWma-mQxY9w7kYxvSG3jzIHyfFvdQnUxQ'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$rawDir = Join-Path $root 'sheets-raw'
$outDir = Join-Path $root 'slot-refs'
$xlsx = Join-Path $rawDir 'ethyn.xlsx'
$csv = Join-Path $rawDir 'ethyn.csv'
New-Item -ItemType Directory -Force $rawDir | Out-Null
New-Item -ItemType Directory -Force $outDir | Out-Null

$h = @{ 'User-Agent' = 'Mozilla/5.0' }
if (-not (Test-Path $xlsx)) {
    Write-Host "[slot-refs] downloading xlsx export (~52 MB)..."
    Invoke-WebRequest -UseBasicParsing -Headers $h -TimeoutSec 600 `
        -Uri "https://docs.google.com/spreadsheets/d/$doc/export?format=xlsx" -OutFile $xlsx
}
Write-Host "[slot-refs] downloading csv (text table)..."
Invoke-WebRequest -UseBasicParsing -Headers $h -TimeoutSec 120 `
    -Uri "https://docs.google.com/spreadsheets/d/$doc/export?format=csv&gid=0" -OutFile $csv

# ---- parse the CSV: prefab blocks + slot legend ------------------------------
# Row layout (sheet row = csv line index, header = row 0):
#   PrefabName,Reference,Legend,SlotName,SlotHash,Comments
# A block starts where col A is non-empty; legend rows follow inside the block.
$rows = @(Import-Csv $csv)
$blocks = @()   # ordered: @{ row; name; slots }
for ($i = 0; $i -lt $rows.Count; $i++) {
    $r = $rows[$i]
    $name = ('' + $r.'Prefab Name').Trim()
    if ($name -ne '') {
        $blocks += @{ row = $i + 1; name = $name; slots = @() }   # +1: header is sheet row 0
    }
    $legend = ('' + $r.Legend).Trim()
    if ($legend -ne '' -and $blocks.Count -gt 0) {
        $blocks[-1].slots += @(@{
            color = $legend.ToLower()
            slot = ('' + $r.'Slot Name (att-string-transcoder)').Trim()
            hash = [int]('0' + (('' + $r.'Slot Hash').Trim() -replace '[^\d]', ''))
            comment = ('' + $r.Comments).Trim()
        })
    }
}
Write-Host "[slot-refs] csv: $($rows.Count) rows, $($blocks.Count) prefab blocks."

# ---- unzip + map drawing anchors to blocks -----------------------------------
Add-Type -AssemblyName System.IO.Compression.FileSystem
$tmp = Join-Path $env:TEMP ("ethyn-x-" + [IO.Path]::GetRandomFileName())
[IO.Compression.ZipFile]::ExtractToDirectory($xlsx, $tmp)
try {
    [xml]$dx = Get-Content (Join-Path $tmp 'xl\drawings\drawing1.xml') -Raw
    [xml]$rels = Get-Content (Join-Path $tmp 'xl\drawings\_rels\drawing1.xml.rels') -Raw
    $ridMap = @{}
    foreach ($rel in $rels.Relationships.Relationship) { $ridMap[$rel.Id] = $rel.Target -replace '\.\./', 'xl/' }

    $index = [ordered]@{}
    foreach ($b in $blocks) { $index[$b.name] = [ordered]@{ file = $null; slots = $b.slots } }

    $mapped = 0; $skipped = 0
    foreach ($a in $dx.wsDr.oneCellAnchor) {
        $col = [int]$a.from.col
        $row = [int]$a.from.row
        if ($col -ne 1) { $skipped++; continue }   # col B = the Reference column
        $rid = $a.pic.blipFill.blip.embed
        if (-not $rid -or -not $ridMap[$rid]) { $skipped++; continue }
        # the block this image belongs to: last block starting at or before this row
        $owner = $null
        foreach ($b in $blocks) { if ($b.row -le $row) { $owner = $b } else { break } }
        if (-not $owner) { $skipped++; continue }
        if ($index[$owner.name].file) { continue }   # first image per block wins
        $src = Join-Path $tmp ($ridMap[$rid] -replace '/', '\')
        $ext = [IO.Path]::GetExtension($src).TrimStart('.')
        $safe = ($owner.name -replace '[^A-Za-z0-9_()-]', '_')
        $dest = "$safe.$ext"
        Copy-Item $src (Join-Path $outDir $dest) -Force
        $index[$owner.name].file = $dest
        $mapped++
    }

    $withImg = @($index.Values | Where-Object { $_.file }).Count
    $indexOut = [ordered]@{
        generated = (Get-Date).ToUniversalTime().ToString('o')
        source = "Ethyn's Visual Slot Reference (community sheet)"
        prefabs = $index
    }
    # BOM-less UTF-8 (PS 5.1's -Encoding utf8 writes a BOM, which breaks JSON.parse)
    [IO.File]::WriteAllText((Join-Path $outDir 'index.json'),
        ($indexOut | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
    Write-Host "[slot-refs] done: $withImg/$($blocks.Count) prefabs got a reference image ($mapped anchors mapped, $skipped non-reference anchors skipped) -> $outDir"
} finally {
    Remove-Item $tmp -Recurse -Force
}
