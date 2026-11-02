#Requires -Version 7
# Builds the exosuit effects bundle with the game's Unity (2019.4.21f1) and rewrites Native/FxBundle.Generated.cs.
# Take artifacts/work/unity-lock.txt first. Run from the repository root:
#   pwsh Mods/ForgeOrdnance_Frames/tools/fx/build_fx.ps1 [-Work artifacts/work/vfx-frames/unity]
# The CC0 sheets are read from -Vfx (the shared Ordnance art folder) and -Labs (the Unity Labs flipbooks); nothing of them is tracked.
param(
    [string]$Work = 'artifacts/work/vfx-frames/unity',
    [string]$Vfx = 'Source/mods/ordnance-assets/vfx',
    [string]$Labs = '<VFX library>\unity-labs-vfx-cc0',
    [string]$Stream = '<VFX library>\asset-store\War FX\unpacked\Assets\JMO Assets\WarFX\Desktop\Textures\Flames\WFX_T_FlameThrower Stream.tga',
    [string]$Unity ='C:\Program Files\Unity\Hub\Editor\2019.4.21f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$tools = $PSScriptRoot
$native = Join-Path (Split-Path (Split-Path $tools -Parent) -Parent) 'Native'
$Work = [IO.Path]::GetFullPath($Work)
$Vfx = [IO.Path]::GetFullPath($Vfx)
if (!(Test-Path -LiteralPath $Unity)) { throw "Missing Unity: $Unity" }
$staged = "$Work.textures"
$Labs = [IO.Path]::GetFullPath($Labs)
python "$tools/make_fx_textures.py" $Vfx $Labs $Stream $staged
if ($LASTEXITCODE -ne 0) { throw 'Staging the textures failed.' }
if (!(Test-Path -LiteralPath (Join-Path $Work 'ProjectSettings'))) {
    New-Item -ItemType Directory -Force (Split-Path $Work -Parent) | Out-Null
    $proc = Start-Process -FilePath $Unity -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode', '-nographics', '-quit', '-createProject', "`"$Work`"", '-logFile', "`"$Work.create.log`"")
    $proc.WaitForExit()
    if ($proc.ExitCode -ne 0) { throw "Unity could not create the project; see $Work.create.log" }
}
New-Item -ItemType Directory -Force (Join-Path $Work 'Assets/Editor') | Out-Null
Copy-Item -LiteralPath "$tools/FrameFxBuild.cs" -Destination (Join-Path $Work 'Assets/Editor/FrameFxBuild.cs') -Force
$env:FORGE_FX_TEXTURES = $staged
$env:FORGE_FX_SHADER = Join-Path (Split-Path (Split-Path (Split-Path $tools -Parent) -Parent) -Parent) 'ForgeVisual/tools/shaders/ForgeVisualParticle.shader'
$env:FORGE_FX_GENERATED = Join-Path $native 'FxBundle.Generated.cs'
$log = "$Work.build.log"
$proc = Start-Process -FilePath $Unity -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode', '-nographics', '-quit', '-projectPath', "`"$Work`"", '-executeMethod', 'FrameFxBuild.Build', '-logFile', "`"$log`"")
$proc.WaitForExit()
if ($proc.ExitCode -ne 0) {
    Select-String -LiteralPath $log -Pattern 'error' | Select-Object -First 15 | ForEach-Object { Write-Host $_.Line }
    throw "Unity build failed; see $log"
}
if (Select-String -LiteralPath $log -Pattern 'Shader error in' -SimpleMatch -Quiet) { Select-String -LiteralPath $log -Pattern 'Shader error in' -SimpleMatch | Select-Object -First 10 | ForEach-Object { Write-Host $_.Line }; throw "The shader has compile errors; see $log" }
if (!(Select-String -LiteralPath $log -Pattern 'FRAMES_FX_BUILT' -SimpleMatch -Quiet)) { throw "Unity did not run the build; see $log" }
Select-String -LiteralPath $log -Pattern 'FRAMES_FX_BUILT' -SimpleMatch | ForEach-Object { Write-Host $_.Line }
