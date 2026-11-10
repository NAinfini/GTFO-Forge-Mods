#Requires -Version 7
# Renders the lobby picker icon of every exosuit definition (tools/render_icon.py in Blender) into -Out (Assets/icons, the
# package's own icon source), one transparent 256 x 256 PNG per definition, named as its "Icon" field, only when that file is
# missing (-Redo <file>[,<file>] or -Redo all re-renders on purpose), from that definition's rigged model and surface. Sources are only
# read. Run from the repository root after build_assets.ps1:
#   pwsh Mods/ForgeOrdnance_Frames/tools/build_icons.ps1
param(
    [string]$Models = 'Source/mods/ordnance-assets/models',
    [string]$Out = 'Mods/ForgeOrdnance_Frames/Assets/icons',
    [string[]]$Redo = @(),
    [string]$Blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
)
$ErrorActionPreference = 'Stop'
$Models = [IO.Path]::GetFullPath($Models)
$Out = [IO.Path]::GetFullPath($Out)
$script = Join-Path $PSScriptRoot 'render_icon.py'
$definitions = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Definitions/frames.forge') -Raw | ConvertFrom-Json
# Prefab name the definitions use ("Model" in the payload) -> model folder.
$folders = @{ 'frame-m15' = 'M15'; 'frame-m16' = 'M16'; 'frame-m17' = 'M17'; 'frame-m18' = 'M18' }

New-Item -ItemType Directory -Force $Out | Out-Null
$redoAll = $Redo -contains 'all'
foreach ($d in $definitions) {
    if (!$d.Icon) { throw "$($d.Id) has no Icon field." }
    $m = $folders[$d.Payload.Model -replace '^Frames:','']
    if (!$m) { throw "No model folder for $($d.Payload.Model); add it to this script." }
    $dir = Join-Path $Models $m
    $blend = Join-Path $dir "${m}_rigged.blend"
    if (!(Test-Path -LiteralPath $blend)) { throw "Missing model: $blend (run build_assets.ps1)" }
    $png = Join-Path $Out $d.Icon
    if ((Test-Path -LiteralPath $png) -and !$redoAll -and ($Redo -notcontains $d.Icon)) { Write-Host "Kept $($d.Icon) (already present; -Redo $($d.Icon) or -Redo all re-renders it)."; continue }
    $log = & $Blender -b $blend --factory-startup --python-exit-code 1 --python $script -- --textures $dir --model $m --out $png 2>&1
    if ($LASTEXITCODE -ne 0 -or !($log -match 'ICON_RENDERED')) { $log | Select-Object -Last 20 | Write-Host; throw "Icon of $($d.Id) failed." }
    Write-Host "Rendered $($d.Icon) for $($d.Id)."
}
