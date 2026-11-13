#Requires -Version 7
# Builds forge-ordnance-arsenal.bundle with the carrier's asset pipeline (tools/Carrier): the heavy weapons (M08, M09, M10, M20), and from their masters under
# <Assets>/masters (tripo_masters.py) the munitions, the recoilless rifle, the turret looks and the devices, plus the strike effect
# textures of <Assets>/vfx (staged under textures/). Sources are only read; everything is written under -Build.
# Run from the repository root:
#   pwsh Mods/ForgeOrdnance/tools/Arsenal/build_assets.ps1 [-Install <BepInEx plugins folder of a test profile>]
param(
    [string]$Assets = 'Source/mods/ordnance-assets',
    [string]$Build = 'artifacts/work/ordnance-build-arsenal',
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\2019.4.21f1\Editor\Unity.exe',
    [string]$Blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe',
    [string]$Install = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../../../Tools/safe-delete.ps1')
$pipeline = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../Carrier'))
$Assets = [IO.Path]::GetFullPath($Assets)
$Build = [IO.Path]::GetFullPath($Build)
$bundleName = 'forge-ordnance-arsenal.bundle'
$stage = Join-Path $Build 'stage'
$out = Join-Path $Build 'out'
$manifest = Join-Path $out 'forge-ordnance-arsenal.manifest.json'
New-Item -ItemType Directory -Force $out | Out-Null
# Invalidate the previous receipt before any step can fail: package_release.py reads the copy kept in the package, so that one goes too.
# The previous bundle may remain for comparison, but without its receipt it cannot ship.
$kept = Join-Path $PSScriptRoot '../../Assets/Arsenal/Models'
foreach ($stale in @($manifest, (Join-Path $kept 'forge-ordnance-arsenal.manifest.json'))) {
    if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale }
}
# Prefab name -> model folder. Keep in step with ArsenalArt.cs.
$masters = 'shell', 'shell-precision', 'shell-hunter', 'shell-incendiary', 'bomblet', 'rocket', 'grenade-40', 'gas-canister', 'stasis-emitter', 'mine', 'glue-mine', 'recoilless', 'turret-burst', 'turret-sniper', 'turret-shotgun', 'dispenser', 'radar', 'floodlight'
$models = [ordered]@{ 'heavy-mg' = 'M08'; 'tracking-rifle' = 'M20'; 'anti-materiel-rifle' = 'M09'; 'grenade-launcher' = 'M10' }
$blends = @{}
foreach ($prefab in $masters) { $blends[$prefab] = "$Assets/masters/$prefab/$prefab.blend" }
foreach ($prefab in $models.Keys) { $blends[$prefab] = "$Assets/models/$($models[$prefab])/$($models[$prefab]).blend" }
# Turret looks (ArsenalTurrets): base and yaw origins on the ground at the yaw axis, head origin on the pitch axis, muzzle at the barrel tip.
$turretParts = 'base,yaw,head,muzzle'
$inputs = @($Unity, $Blender, "$pipeline/export_blender.py", "$pipeline/OrdnanceBundleBuild.cs")
foreach ($b in $blends.Values) { $inputs += $b }
# Effect texture names. Keep in step with ArsenalArt.cs.
$effects = @('vfx-flare', 'vfx-spark', 'vfx-smoke', 'vfx-ring', 'vfx-scorch', 'vfx-streak', 'vfx-glow', 'vfx-fireball')
foreach ($e in $effects) { $inputs += "$Assets/vfx/$e.png" }
foreach ($path in $inputs) { if (!(Test-Path -LiteralPath $path)) { throw "Missing required input: $path" } }

if (Test-Path -LiteralPath $stage) { Remove-SafeTree -Target $stage -Root $Build }
New-Item -ItemType Directory -Force (Join-Path $stage 'models'), (Join-Path $stage 'textures'), $out | Out-Null
foreach ($e in $effects) { Copy-Item -LiteralPath "$Assets/vfx/$e.png" -Destination (Join-Path $stage "textures/$e.png") }
foreach ($prefab in $blends.Keys) {
    $require = if ($prefab -like 'turret-*') { @('--require', $turretParts) } elseif ($prefab -eq 'dispenser') { @('--require', 'ring,column,plate1,plate6,mount1,mount36') } elseif ($prefab -eq 'radar') { @('--require', 'base,yaw,head,petal1') } elseif ($prefab -eq 'floodlight') { @('--require', 'base,yaw,head,fan') } else { @() }
    & $Blender --background --factory-startup $blends[$prefab] --python-exit-code 1 --python "$pipeline/export_blender.py" -- --prefab $prefab --out "$stage/models/$prefab" --max-texture 2048 @require
    if ($LASTEXITCODE -ne 0) { throw "Export of $prefab failed." }
}
$project = Join-Path $Build 'unity'
if (!(Test-Path -LiteralPath (Join-Path $project 'ProjectSettings'))) {
    $log = Join-Path $Build 'unity-create.log'
    $proc = Start-Process -FilePath $Unity -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode', '-nographics', '-quit', '-createProject', "`"$project`"", '-logFile', "`"$log`"")
    $proc.WaitForExit()
    if ($proc.ExitCode -ne 0) { throw "Unity could not create the project; see $log" }
}
New-Item -ItemType Directory -Force (Join-Path $project 'Assets/Editor') | Out-Null
Copy-Item -LiteralPath "$pipeline/OrdnanceBundleBuild.cs" -Destination (Join-Path $project 'Assets/Editor/OrdnanceBundleBuild.cs') -Force

$env:FORGE_ORDNANCE_BUNDLE = $bundleName
$env:FORGE_ORDNANCE_STAGE = $stage
$env:FORGE_ORDNANCE_OUT = $out
$log = Join-Path $Build 'unity-build.log'
if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log }
$proc = Start-Process -FilePath $Unity -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode', '-nographics', '-quit', '-projectPath', "`"$project`"", '-executeMethod', 'OrdnanceBundleBuild.Build', '-logFile', "`"$log`"")
$proc.WaitForExit()
if ($proc.ExitCode -ne 0) { throw "Unity build failed; see $log" }
$bundle = Join-Path $out $bundleName
$length = (Get-Item -LiteralPath $bundle).Length
$hash = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash.ToLowerInvariant()
if (!(Select-String -LiteralPath $log -Pattern "ORDNANCE_BUNDLE_VERIFIED $bundleName $length bytes $hash" -SimpleMatch -Quiet)) { throw "This run did not verify the bundle; see $log" }
$receipt = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
if ($receipt.bundle -ne $bundleName -or $receipt.bytes -ne $length -or $receipt.sha256 -ne $hash) { throw "The Unity build receipt does not match $bundleName." }
Write-Host "Built and verified $bundle ($length bytes, $hash)."
# The package keeps the verified bundle and its Unity receipt in its own Assets/Arsenal/Models, where package_release.py reads them (the build folder is disposable).
New-Item -ItemType Directory -Force $kept | Out-Null
foreach ($file in @($bundle, $manifest)) { Copy-Item -LiteralPath $file -Destination $kept -Force }

if ($Install) {
    New-Item -ItemType Directory -Force $Install | Out-Null
    Copy-Item -LiteralPath $bundle -Destination (Join-Path $Install $bundleName) -Force
    Write-Host "Installed the bundle into $Install."
}
