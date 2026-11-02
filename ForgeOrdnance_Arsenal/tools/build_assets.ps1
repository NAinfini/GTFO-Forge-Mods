#Requires -Version 7
# Builds forge-ordnance-arsenal.bundle with ForgeOrdnance's asset pipeline (Mods/ForgeOrdnance/tools): the shell (M11),
# bomblet (M12), gas canister (M13), stasis emitter (M14), heavy weapon (M08, M09, M10) and turret look (M04 burst, M05 sniper, M06 shotgun) models, plus the strike effect
# textures of <Assets>/vfx (staged under textures/). Sources are only read; everything is written under -Build.
# Run from the repository root:
#   pwsh Mods/ForgeOrdnance_Arsenal/tools/build_assets.ps1 [-Install <BepInEx plugins folder of a test profile>]
param(
    [string]$Assets = 'Source/mods/ordnance-assets',
    [string]$Build = 'artifacts/work/ordnance-build-arsenal',
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\2019.4.21f1\Editor\Unity.exe',
    [string]$Blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe',
    [string]$Install = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../../Tools/safe-delete.ps1')
$pipeline = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../ForgeOrdnance/tools'))
$Assets = [IO.Path]::GetFullPath($Assets)
$Build = [IO.Path]::GetFullPath($Build)
$bundleName = 'forge-ordnance-arsenal.bundle'
$stage = Join-Path $Build 'stage'
$out = Join-Path $Build 'out'
$manifest = Join-Path $out 'forge-ordnance-arsenal.manifest.json'
New-Item -ItemType Directory -Force $out | Out-Null
# Invalidate the previous receipt before any step can fail. The previous bundle may remain for comparison, but cannot ship.
if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest }
# Prefab name -> model folder. Keep in step with ArsenalArt.cs.
$models = [ordered]@{ 'shell' = 'M11'; 'bomblet' = 'M12'; 'gas-canister' = 'M13'; 'stasis-emitter' = 'M14'; 'heavy-mg' = 'M08'; 'anti-materiel-rifle' = 'M09'; 'grenade-launcher' = 'M10'; 'turret-burst' = 'M04'; 'turret-sniper' = 'M05'; 'turret-shotgun' = 'M06' }
# Turret looks (ArsenalTurrets): base and yaw origins on the ground at the yaw axis, head origin on the pitch axis, muzzle at the barrel tip.
$turretParts = 'base,yaw,head,muzzle'
$inputs = @($Unity, $Blender, "$pipeline/export_blender.py", "$pipeline/OrdnanceBundleBuild.cs")
foreach ($m in $models.Values) { $inputs += "$Assets/models/$m/$m.blend" }
# Effect texture names. Keep in step with ArsenalArt.cs.
$effects = @('vfx-flare', 'vfx-spark', 'vfx-mist', 'vfx-fire', 'vfx-smoke', 'vfx-ring', 'vfx-scorch', 'vfx-streak', 'vfx-glow', 'vfx-blast', 'vfx-fireball', 'vfx-flash', 'vfx-flame')
foreach ($e in $effects) { $inputs += "$Assets/vfx/$e.png" }
foreach ($path in $inputs) { if (!(Test-Path -LiteralPath $path)) { throw "Missing required input: $path" } }

if (Test-Path -LiteralPath $stage) { Remove-SafeTree -Target $stage -Root $Build }
New-Item -ItemType Directory -Force (Join-Path $stage 'models'), (Join-Path $stage 'textures'), $out | Out-Null
foreach ($e in $effects) { Copy-Item -LiteralPath "$Assets/vfx/$e.png" -Destination (Join-Path $stage "textures/$e.png") }
foreach ($prefab in $models.Keys) {
    $m = $models[$prefab]
    $textureSize = if ($prefab -in @('shell', 'bomblet', 'gas-canister', 'stasis-emitter')) { 512 } else { 1024 }
    $require = if ($prefab -like 'turret-*') { @('--require', $turretParts) } else { @() }
    & $Blender --background --factory-startup "$Assets/models/$m/$m.blend" --python-exit-code 1 --python "$pipeline/export_blender.py" -- --prefab $prefab --out "$stage/models/$prefab" --max-texture $textureSize @require
    if ($LASTEXITCODE -ne 0) { throw "Export of $m ($prefab) failed." }
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
# The package keeps the verified bundle and its Unity receipt in its own Assets/Models, where package_release.py reads them (the build folder is disposable).
$kept = Join-Path $PSScriptRoot '../Assets/Models'
New-Item -ItemType Directory -Force $kept | Out-Null
foreach ($file in @($bundle, $manifest)) { Copy-Item -LiteralPath $file -Destination $kept -Force }

if ($Install) {
    New-Item -ItemType Directory -Force $Install | Out-Null
    Copy-Item -LiteralPath $bundle -Destination (Join-Path $Install $bundleName) -Force
    Write-Host "Installed the bundle into $Install."
}
