#Requires -Version 7
# Renders the lobby picker icon of every Arsenal call-in (tools/render_icon.py in Blender) into -Out (Assets/icons, the package's own
# icon source), one transparent 256 x 256 PNG per definition, named as the definition's "Icon" field. A file already there is kept (the
# owner's hand-made icons live there); only missing icons are rendered, and -Redo <file>[,<file>] or -Redo all re-renders on purpose. Supplies show the game's own pickups exactly as the definition delivers them (one picture per item, the Count of each line,
# read from the Item payload; tools/extract_pickups.py reads their meshes and textures from the game files), turrets their own look (M04 to M06), strikes their shell, canister or emitter, heavy weapons their model.
# Sources are only read. Run from the repository root:
#   pwsh Mods/ForgeOrdnance_Arsenal/tools/build_icons.ps1
param(
    [string]$Models = 'Source/mods/ordnance-assets/models',
    [string]$Pickups = 'Source/mods/pod-native/meshes',
    [string]$GameData = '<GTFO install>\GTFO_Data',
    [string]$Out = 'Mods/ForgeOrdnance_Arsenal/Assets/icons',
    [string[]]$Redo = @(),
    [string]$Blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
)
$ErrorActionPreference = 'Stop'
$Models = [IO.Path]::GetFullPath($Models)
$Pickups = [IO.Path]::GetFullPath($Pickups)
$Out = [IO.Path]::GetFullPath($Out)
$script = Join-Path $PSScriptRoot 'render_icon.py'
$definitions = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../ForgeOrdnance/arsenal.json') -Raw | ConvertFrom-Json

# Id -> model file (relative to -Models; '' for none), layout, glow colour.
$looks = @{
    'arsenal.supply.mixed'           = @('', 'supply', '')
    'arsenal.supply.ammo'            = @('', 'supply', '')
    'arsenal.supply.medical'         = @('', 'supply', '')
    'arsenal.supply.tool'            = @('', 'supply', '')
    'arsenal.supply.consumables'     = @('', 'supply', '')
    'arsenal.turret.watch'           = @('M04/M04.blend', 'single', '')
    'arsenal.turret.bulwark'         = @('M05/M05.blend', 'single', '')
    'arsenal.turret.scythe'          = @('M06/M06.blend', 'single', '')
    'arsenal.strike.precision'       = @('M11/M11.blend', 'single', '')
    'arsenal.strike.area'            = @('M11/M11.blend', 'scatter', '')
    'arsenal.strike.creeping'        = @('M11/M11.blend', 'row', '')
    'arsenal.strike.cluster'         = @('M12/M12.blend', 'line', '')
    'arsenal.strike.incendiary'      = @('M11/M11.blend', 'single', '')
    'arsenal.strike.gas'             = @('M13/M13.blend', 'single', '0.45,1,0.2')
    'arsenal.strike.stasis'          = @('M14/M14.blend', 'single', '0.3,0.85,1')
    'arsenal.strike.beam'            = @('', 'beam', '')
    'arsenal.strike.hunter'          = @('M11/M11.blend', 'single', '')
    'arsenal.weapon.machinegun'      = @('M08/M08.blend', 'single', '')
    'arsenal.weapon.antimateriel'    = @('M09/M09.blend', 'single', '')
    'arsenal.weapon.grenadelauncher' = @('M10/M10.blend', 'single', '')
}

# Item data block id -> the pickup kind tools/extract_pickups.py extracts. A supply picture is built from its payload: one pickup per
# Count of every Items line, so a changed delivery re-renders into a changed picture.
$pickupKinds = @{ 101 = 'ammo'; 102 = 'medi'; 127 = 'tool'; 132 = 'disinfect'; 115 = 'cfoam' }
function Get-SupplyKinds($definition) {
    $kinds = foreach ($line in $definition.Payload.Items) {
        $id = [int]$line.Item
        if (!$pickupKinds.ContainsKey($id)) { throw "$($definition.Id) delivers item $id, which has no pickup kind; add it to `$pickupKinds and tools/extract_pickups.py." }
        $count = if ($line.Count) { [int]$line.Count } else { 1 }
        1..$count | ForEach-Object { $pickupKinds[$id] }
    }
    return ($kinds -join ',')
}
# These shells and the beam differ in the game only by colour, so their pictures are one colour too: the definition's BeamColour.
$tinted = @('arsenal.strike.precision', 'arsenal.strike.incendiary', 'arsenal.strike.hunter', 'arsenal.strike.beam')
# The three red shells look alike, so each carries a sign drawn into its picture (render_icon.py --mark).
# Gunmetal models render near black; they get more light in their own colours (render_icon.py --exposure), each as much as its own
# textures take: the M82's pale-blue finish washes out to flat grey above about 1.2.
$exposure = @{ 'arsenal.weapon.machinegun' = '2.0'; 'arsenal.weapon.antimateriel' = '1.2'; 'arsenal.weapon.grenadelauncher' = '2.0' }
$marks = @{ 'arsenal.strike.precision' = 'crosshair'; 'arsenal.strike.incendiary' = 'flame'; 'arsenal.strike.hunter' = 'lock' }

New-Item -ItemType Directory -Force $Out | Out-Null
$redoAll = $Redo -contains 'all'
foreach ($d in $definitions) {
    if (!$looks.ContainsKey($d.Id)) { throw "No icon look for $($d.Id); add it to this script." }
    if (!$d.Icon) { throw "$($d.Id) has no Icon field." }
    $model, $layout, $glow = $looks[$d.Id]
    $png = Join-Path $Out $d.Icon
    if ((Test-Path -LiteralPath $png) -and !$redoAll -and ($Redo -notcontains $d.Icon)) { Write-Host "Kept $($d.Icon) (already present; -Redo $($d.Icon) or -Redo all re-renders it)."; continue }
    $arguments = @('-b')
    if ($model) {
        $blend = Join-Path $Models $model
        if (!(Test-Path -LiteralPath $blend)) { throw "Missing model: $blend" }
        $arguments += $blend
    }
    $arguments += @('--factory-startup', '--python-exit-code', '1', '--python', $script, '--', '--out', $png, '--layout', $layout)
    if ($glow) { $arguments += @('--glow', $glow) }
    if ($layout -eq 'supply') {
        if (!(Test-Path -LiteralPath (Join-Path $Pickups 'medi.obj'))) {
            if (!$env:PYTHONPATH) { throw 'Set PYTHONPATH to a folder holding UnityPy so the game pickups can be extracted.' }
            python (Join-Path $PSScriptRoot 'extract_pickups.py') --game $GameData --out $Pickups
            if ($LASTEXITCODE -ne 0) { throw 'Extracting the game pickups failed.' }
        }
        $arguments += @('--supply', (Get-SupplyKinds $d), '--pickups', $Pickups, '--view', '0.7,-1,1.2')
    }
    if ($exposure.ContainsKey($d.Id)) { $arguments += @('--exposure', $exposure[$d.Id]) }
    if ($tinted -contains $d.Id) {
        if ($d.BeamColour -notmatch '^#[0-9A-Fa-f]{6}$') { throw "$($d.Id) needs a BeamColour to tint its icon." }
        $rgb = @(1, 3, 5) | ForEach-Object { [Convert]::ToInt32($d.BeamColour.Substring($_, 2), 16) / 255 }
        $arguments += @('--tint', ($rgb -join ','))
        if ($marks.ContainsKey($d.Id)) { $arguments += @('--mark', $marks[$d.Id]) }
    }
    $log = & $Blender @arguments 2>&1
    if ($LASTEXITCODE -ne 0 -or !($log -match 'ICON_RENDERED')) { $log | Select-Object -Last 20 | Write-Host; throw "Icon of $($d.Id) failed." }
    Write-Host "Rendered $($d.Icon) for $($d.Id)."
}
