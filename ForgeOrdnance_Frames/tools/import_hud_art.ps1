#Requires -Version 7
# Copies the finished cockpit art (white shapes on transparency, 1x and @2x) from the owner's delivery folder into the package's local
# media folder Assets/hud/ under the names the plugin loads (Native/FrameHudArt.cs), so packaging ships them in hud/. Sources are only
# read; Assets/hud/*.png stays out of Git. A missing file is listed; packaging refuses to build without it. Run from the repository root:
#   pwsh Mods/ForgeOrdnance_Frames/tools/import_hud_art.ps1 [-From <folder>]
param(
    [string]$From = (Join-Path (git rev-parse --path-format=absolute --git-common-dir | Split-Path) 'Source/mods/frames-ui-review/assets/final')
)
$ErrorActionPreference = 'Stop'
$names = [ordered]@{
    'A2-冲撞' = 'skill-charge'; 'A2-架设' = 'skill-siege'; 'A2-扫描' = 'skill-scan'; 'A2-火箭巢' = 'skill-rockets'; 'A2-头灯' = 'skill-lamp'; 'A2-下机' = 'skill-exit'
    'A5-护盾' = 'shield'; 'A5-热量' = 'heat'; 'A5-目标' = 'objective'; 'A5-队友' = 'teammate'; 'A7-绕背箭头' = 'arrow-around'
    'A6-突击' = 'mark-frame-m15'; 'A6-坦克' = 'mark-frame-m16'; 'A6-火力' = 'mark-frame-m17'; 'A6-侦察' = 'mark-frame-m18'
    'A1-前' = 'damage-front'; 'A1-后' = 'damage-back'; 'A1-左' = 'damage-left'; 'A1-右' = 'damage-right'
}
$out = Join-Path $PSScriptRoot '../Assets/hud'
New-Item -ItemType Directory -Force $out | Out-Null
$missing = @()
foreach ($source in $names.Keys) {
    foreach ($scale in '', '@2x') {
        $file = Join-Path $From "$source$scale.png"
        if (!(Test-Path -LiteralPath $file)) { $missing += "$source$scale.png"; continue }
        $head = [IO.File]::ReadAllBytes($file)[0..7]
        if (($head -join ',') -ne '137,80,78,71,13,10,26,10') { throw "$file is not a PNG." }
        Copy-Item -LiteralPath $file (Join-Path $out "$($names[$source])$scale.png") -Force
    }
}
Write-Host "Imported $(2 * $names.Count - $missing.Count) of $(2 * $names.Count) files into $([IO.Path]::GetFullPath($out))."
if ($missing) { Write-Host "Missing (packaging refuses to build until delivered): $($missing -join ', ')" }
