#Requires -Version 7
# Builds one model from a cut file the hinge add-on baked and renders the opening (and a frame-by-frame sheet), reporting every
# failed check instead of stopping (hinge_tool/build.ps1 bakes the owner's cut file and runs this):
#   pwsh Mods/ForgeOrdnance/tools/Frames/preview_cut.ps1 M15 -File build/M15/M15_baked.blend
# Reads <Cut>/<File>; writes the build to <Cut>/build/<model> and the pictures to <Cut>/preview.
param(
    [Parameter(Mandatory)][ValidateSet('M15', 'M16', 'M17', 'M18')][string]$Model,
    [string]$Cut = 'Source/mods/frame-cut',
    [Parameter(Mandatory)][string]$File,
    [string]$Blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
)
$ErrorActionPreference = 'Stop'
$tools = $PSScriptRoot
$Cut = [IO.Path]::GetFullPath($Cut)
$file = Join-Path $Cut $File
if (!(Test-Path -LiteralPath $file)) { throw "No cut file $file" }
$out = Join-Path $Cut "build/$Model"
$pictures = Join-Path $Cut 'preview'
New-Item -ItemType Directory -Force $out, $pictures | Out-Null
$log = Join-Path $out 'build.log'
& $Blender --background --factory-startup --disable-autoexec --python-exit-code 1 $file --python "$tools/build_frame.py" -- --model $Model --out $out --preview *> $log
if ($LASTEXITCODE -ne 0) {
    Get-Content -LiteralPath $log | Where-Object { $_ -match '^FRAME (STOP|PROBLEM)' } | ForEach-Object { $_ -replace '^FRAME (STOP|PROBLEM) ', '' } | Select-Object -Last 12
    throw "构建失败，详见 $log"
}
& $Blender --background --factory-startup --disable-autoexec --python-exit-code 1 (Join-Path $out "${Model}_rigged.blend") --python "$tools/preview_opening.py" -- (Join-Path $out "${Model}_rig.json") $pictures *> (Join-Path $out 'preview.log')
if ($LASTEXITCODE -ne 0) { throw "预览渲染失败，详见 $(Join-Path $out 'preview.log')" }
$entry = Get-Content -LiteralPath (Join-Path $out "${Model}_entry.json") -Raw | ConvertFrom-Json
$rig = Get-Content -LiteralPath (Join-Path $out "${Model}_rig.json") -Raw | ConvertFrom-Json
$plain = @{ pelvis = '骨盆'; spine = '腰'; chest = '胸'; head = '头盔' }
foreach ($s in @(@('L', '左'), @('R', '右'))) {
    foreach ($b in @(@('upperarm', '上臂'), @('forearm', '前臂'), @('hand', '手'), @('thigh', '大腿'), @('shin', '小腿'), @('foot', '脚'))) {
        $plain["$($b[0]).$($s[0])"] = "$($b[1])-$($s[1])（固定）"
    }
}
foreach ($b in @('pelvis', 'spine', 'chest')) { $plain[$b] += '（固定）' }
foreach ($p in $rig.plates) { $plain[$p.node] = $p.name }
function Plain([string]$name) { if ($plain.ContainsKey($name)) { $plain[$name] } else { $name } }
Write-Host "== $Model 检查结果"
Write-Host ("打开的部件：{0} 个" -f $entry.parts)
Write-Host ("动画：第 1 到 {0} 帧" -f $rig.opening.samples)
if ($entry.raysBlocked -eq 0) { Write-Host ("进出通道（只报数）：{0} 条射线全部畅通" -f $entry.rays) }
else {
    $by = ($entry.blockedBy.PSObject.Properties | ForEach-Object { "$(Plain $_.Name) $($_.Value) 条" }) -join '，'
    Write-Host ("进出通道（只报数）：{0} 条射线中 {1} 条被挡（{2:P0}；挡路的是：{3}）" -f $entry.rays, $entry.raysBlocked, ($entry.raysBlocked / $entry.rays), $by)
}
$gaps = @($entry.layerGaps.PSObject.Properties)
if ($gaps.Count) {
    Write-Host '内外层最小间隙（后开的件在打开途中离先开的件最近处，只报数）：'
    foreach ($g in $gaps) {
        $pair = ($g.Name -split ' \| ' | ForEach-Object { Plain $_ }) -join ' 对 '
        Write-Host ("  {0}：{1:N1} cm" -f $pair, ($g.Value * 100))
    }
}
$events = $rig.plates | Sort-Object { $_.events.unlock }
Write-Host '事件时间（秒；打开：解锁 / 开始转 / 到位；关闭：开始 / 扣紧）：'
foreach ($p in $events) {
    $e = $p.events
    Write-Host ("  {0}：{1:N2} / {2:N2} / {3:N2}；{4:N2} / {5:N2}" -f $p.name, $e.unlock, $e.turn, $e.open, $e.closeStart, $e.latch)
}
foreach ($kind in @(@('sweptIntoBody', '打开途中穿过身体'), @('sweptIntoParts', '打开途中部件互相穿过'))) {
    $props = @($entry.($kind[0]).PSObject.Properties)
    if ($props.Count) { Write-Host ("{0}：{1}" -f $kind[1], (($props | ForEach-Object { "$($_.Name -replace 'plate\.\w+\.\w\.\d+', { Plain $_.Value }) $($_.Value) 处" }) -join '，')) }
}
if ($entry.secondLayersLeft) { Write-Host ("双层残留：{0} 个固定面压在打开部件上下，例如 {1}" -f $entry.secondLayersLeft, (($entry.secondLayersAt | Select-Object -First 3 | ForEach-Object { '(' + ($_ -join ', ') + ')' }) -join ' ')) }
foreach ($h in $entry.hovering) { Write-Host ("悬空小件：({0}) 在「{1}」里，却只贴着「{2}」" -f ($h.at -join ', '), $h.in, ($h.sitsOn -join '、')) }
Write-Host "预览图：$pictures\${Model}_*.png（逐帧拼图：${Model}_frames.png）"
