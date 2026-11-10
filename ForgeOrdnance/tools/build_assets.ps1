#Requires -Version 7
# Builds forge-ordnance.bundle: the summoning ball, the one drop pod (supplies and heavy weapons) and the turret base, from their masters under <Assets>/masters (made by tripo_masters.py). Sources are only read; everything is written under -Build.
# Run from the repository root:
#   pwsh Mods/ForgeOrdnance/tools/build_assets.ps1 [-Install <BepInEx plugins folder of a test profile>]
param(
    [string]$Assets = 'Source/mods/ordnance-assets',
    [string]$Build = 'artifacts/work/ordnance-build',
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\2019.4.21f1\Editor\Unity.exe',
    [string]$Blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe',
    [string]$UnityLabs = 'F:\Unity\特效\unity-labs-vfx-cc0',
    [string]$AssetStore = 'F:\Unity\特效\asset-store',
    [string]$Install = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../../Tools/safe-delete.ps1')
$tools = $PSScriptRoot
$Assets = [IO.Path]::GetFullPath($Assets)
$Build = [IO.Path]::GetFullPath($Build)
$log = Join-Path $Build 'unity-build.log'
# Any failed build attempt must invalidate the previous receipt so package_release.py cannot reuse an old bundle: it reads the copies kept in the package, so those go too (the bundle file stays; without its receipt and log it is refused).
$kept = Join-Path $PSScriptRoot '../Assets/Models'
foreach ($stale in @($log, (Join-Path $kept 'forge-ordnance.manifest.json'), (Join-Path $kept 'unity-build.log'))) {
    if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale }
}
$models = @('beacon', 'drop-pod', 'turret-shell') | ForEach-Object { "$Assets/masters/$_/$_.blend" }
$effects = @('vfx-mist', 'vfx-spark', 'vfx-flare')
$textures = $effects | ForEach-Object { "$Assets/vfx/$_.png" }
foreach ($path in @($Unity, $Blender) + $models + $textures) {
    if (!(Test-Path -LiteralPath $path)) { throw "Missing required input: $path" }
}
$stage = Join-Path $Build 'stage'
$out = Join-Path $Build 'out'
if (Test-Path -LiteralPath $stage) { Remove-SafeTree -Target $stage -Root $Build }
New-Item -ItemType Directory -Force (Join-Path $stage 'models'), (Join-Path $stage 'textures'), $out | Out-Null
foreach ($e in $effects) { Copy-Item -LiteralPath "$Assets/vfx/$e.png" -Destination (Join-Path $stage "textures/$e.png") }
# Flame: the Unity Labs free VFX flipbook (CC0, -UnityLabs). Smoke, explosion and shock ring: Unity Asset Store packs unpacked under -AssetStore.
& python "$tools/prepare_vfx.py" ([IO.Path]::GetFullPath($UnityLabs)) ([IO.Path]::GetFullPath($AssetStore)) (Join-Path $stage 'textures')
if ($LASTEXITCODE -ne 0) { throw 'Staging the effect flipbooks failed.' }
# The landing chunks: a procedurally drawn 2 x 2 atlas of faceted concrete and earth chunks.
& python "$tools/make_rocks.py" (Join-Path $stage 'textures/vfx-rock.png')
if ($LASTEXITCODE -ne 0) { throw 'Drawing the vfx-rock atlas failed.' }
# The flare is grey on alpha: whiten it so the tint at runtime is not darkened.
& python "$tools/whiten_vfx.py" "$Assets/vfx/vfx-flare.png" (Join-Path $stage 'textures/vfx-flare.png')
if ($LASTEXITCODE -ne 0) { throw 'Whitening vfx-flare failed.' }

function Export([string]$prefab, [string]$require, [string[]]$extra = @()) {
    & $Blender --background --factory-startup "$Assets/masters/$prefab/$prefab.blend" --python-exit-code 1 --python "$tools/export_blender.py" -- --prefab $prefab --out "$stage/models/$prefab" --require $require @extra
    if ($LASTEXITCODE -ne 0) { throw "Export of $prefab failed." }
}
# The ball is a first person held item seen up close; its amber band glows (OrdnanceAssets: beacon).
Export 'beacon' '' @('--glow', 'beacon=1.6,0.9,0.18')
Export 'drop-pod' 'body,cradle,door_1,door_2,door_3,door_4,interior,shelf_1,shelf_2,weapon_anchor_1,weapon_anchor_2,pickup_anchor_1,pickup_anchor_2,pickup_anchor_3,pickup_anchor_4'
Export 'turret-shell' 'body,well,lid_a,lid_b'
$project = Join-Path $Build 'unity'
if (!(Test-Path -LiteralPath (Join-Path $project 'ProjectSettings'))) {
    $createLog = Join-Path $Build 'unity-create.log'
    $proc = Start-Process -FilePath $Unity -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode', '-nographics', '-quit', '-createProject', "`"$project`"", '-logFile', "`"$createLog`"")
    $proc.WaitForExit()
    if ($proc.ExitCode -ne 0) { throw "Unity could not create the project; see $createLog" }
}
New-Item -ItemType Directory -Force (Join-Path $project 'Assets/Editor') | Out-Null
Copy-Item -LiteralPath "$tools/OrdnanceBundleBuild.cs" -Destination (Join-Path $project 'Assets/Editor/OrdnanceBundleBuild.cs') -Force

$env:FORGE_ORDNANCE_STAGE = $stage
$env:FORGE_ORDNANCE_OUT = $out
$env:FORGE_ORDNANCE_BUNDLE = 'forge-ordnance.bundle'
$proc = Start-Process -FilePath $Unity -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode', '-nographics', '-quit', '-projectPath', "`"$project`"", '-executeMethod', 'OrdnanceBundleBuild.Build', '-logFile', "`"$log`"")
$proc.WaitForExit()
if ($proc.ExitCode -ne 0) { throw "Unity build failed; see $log" }
# Unity can exit 0 without running the method (licence, compile errors): require this run's receipt.
$bundle = Join-Path $out 'forge-ordnance.bundle'
$length = (Get-Item -LiteralPath $bundle).Length
$hash = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash.ToLowerInvariant()
if (!(Select-String -LiteralPath $log -Pattern "ORDNANCE_BUNDLE_VERIFIED forge-ordnance.bundle $length bytes $hash" -SimpleMatch -Quiet)) { throw "This run did not verify the bundle; see $log" }
Write-Host "Built and verified $bundle ($length bytes, $hash)."
# The package keeps the verified bundle, its Unity receipt and build log in its own Assets/Models, where package_release.py reads them (the build folder is disposable).
New-Item -ItemType Directory -Force $kept | Out-Null
foreach ($file in @($bundle, (Join-Path $out 'forge-ordnance.manifest.json'), $log)) { Copy-Item -LiteralPath $file -Destination $kept -Force }

if ($Install) {
    New-Item -ItemType Directory -Force $Install | Out-Null
    Copy-Item -LiteralPath $bundle -Destination (Join-Path $Install 'forge-ordnance.bundle') -Force
    Write-Host "Installed the bundle into $Install."
}
