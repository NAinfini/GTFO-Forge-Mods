#Requires -Version 7
# Renders the effect previews (PNG key frames) with Unity 2019.4.21f1 using a graphics device; make_previews.py then builds the
# GIFs and index.html. Run build_fx.ps1 first (it saves the prefab in the project); take artifacts/work/unity-lock.txt.
#   pwsh Mods/ForgeOrdnance_Frames/tools/fx/preview_fx.ps1 [-Work artifacts/work/vfx-frames/unity] [-Out artifacts/work/vfx-frames]
param(
    [string]$Work = 'artifacts/work/vfx-frames/unity',
    [string]$Out = 'artifacts/work/vfx-frames',
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\2019.4.21f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$tools = $PSScriptRoot
$Work = [IO.Path]::GetFullPath($Work)
$Out = [IO.Path]::GetFullPath($Out)
Copy-Item -LiteralPath "$tools/FrameFxPreview.cs" -Destination (Join-Path $Work 'Assets/Editor/FrameFxPreview.cs') -Force
Copy-Item -LiteralPath "$tools/PreviewUnlit.shader" -Destination (Join-Path $Work 'Assets/Editor/PreviewUnlit.shader') -Force
$env:FORGE_FX_PREVIEW =Join-Path $Out 'frames'
$log = "$Work.preview.log"
$proc = Start-Process -FilePath $Unity -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode', '-quit', '-projectPath', "`"$Work`"", '-executeMethod', 'FrameFxPreview.Render', '-logFile', "`"$log`"")
$proc.WaitForExit()
if ($proc.ExitCode -ne 0) {
    Select-String -LiteralPath $log -Pattern 'error|Exception' | Select-Object -First 15 | ForEach-Object { Write-Host $_.Line }
    throw "Unity preview failed; see $log"
}
if (!(Select-String -LiteralPath $log -Pattern 'FRAMES_FX_PREVIEWED' -SimpleMatch -Quiet)) { throw "Unity did not render the previews; see $log" }
Write-Host "Frames in $($env:FORGE_FX_PREVIEW)"
