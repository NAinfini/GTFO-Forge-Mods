#Requires -Version 7
# Bakes the hinges set in a cut file and builds the suit from it, reporting every failed check:
#   pwsh Mods/ForgeOrdnance_Frames/tools/hinge_tool/build.ps1 M16 [-File M16_cut.draft.blend]
# Reads <Cut>/<File> (default <model>_cut.draft.blend; never written), writes the baked file, the checks and the build into
# <Cut>/build/<model> (preview_cut.ps1) with its pictures in <Cut>/preview.
param(
    [Parameter(Mandatory)][ValidateSet('M15', 'M16', 'M17', 'M18')][string]$Model,
    [string]$Cut = 'Source/mods/frame-cut',
    [string]$File = '',
    [string]$Blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
)
$ErrorActionPreference = 'Stop'
$Cut = [IO.Path]::GetFullPath($Cut)
$source = Join-Path $Cut ($(if ($File) { $File } else { "${Model}_cut.draft.blend" }))
if (!(Test-Path -LiteralPath $source)) { throw "No cut file $source" }
$dir = Join-Path $Cut "build/$Model"
New-Item -ItemType Directory -Force $dir | Out-Null
$baked = Join-Path $dir "${Model}_baked.blend"
$checks = Join-Path $dir "${Model}_checks.json"
$log = Join-Path $dir "${Model}_bake.log"
& $Blender --background --factory-startup --disable-autoexec --python-exit-code 1 $source --python (Join-Path $PSScriptRoot 'bake.py') -- $baked $checks *> $log
if ($LASTEXITCODE -ne 0) { throw "烘焙失败，详见 $log" }
Get-Content -LiteralPath $log | Where-Object { $_ -match '^BAKE' }
& (Join-Path (Split-Path $PSScriptRoot) 'preview_cut.ps1') $Model -Cut $Cut -File ([IO.Path]::GetRelativePath($Cut, $baked)) -Blender $Blender
