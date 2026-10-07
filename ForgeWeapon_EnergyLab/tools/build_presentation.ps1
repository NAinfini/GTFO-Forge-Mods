#Requires -Version 7
param(
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\2019.4.21f1\Editor\Unity.exe',
    [string]$Blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe',
    # The parts library (not published), the Unity project to stage and a folder for this run's logs and candidate.
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$Evidence
)
$ErrorActionPreference = 'Stop'
$module = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Source = [IO.Path]::GetFullPath($Source)
$Project = [IO.Path]::GetFullPath($Project)
$Evidence = [IO.Path]::GetFullPath($Evidence)
$candidate = Join-Path $Evidence 'candidate'
$modes = @('beam', 'arc', 'plasma-blast', 'plasma-arc', 'blast', 'disc', 'flame', 'hole')
foreach ($path in @($Unity, $Blender, $Source)) {
    if (!(Test-Path -LiteralPath $path)) { throw "Missing required input: $path" }
}
& python (Join-Path $PSScriptRoot 'owned_outputs.py') --project $Project --source $Source --evidence $Evidence
if ($LASTEXITCODE -ne 0) { throw 'Unsafe presentation output path; nothing was cleared.' }
function Copy-Asset([string]$From, [string]$To) {
    # Replace the directory entry: truncating a PNG fails while a concurrent reader maps it.
    $temporary = $To + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::Copy($From, $temporary)
        [IO.File]::Move($temporary, $To, $true)
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}
$oldOutput = $env:FORGE_ENERGY_PRESENTATION_OUTPUT
try {
    & $Blender --background --factory-startup --python-exit-code 1 --python (Join-Path $PSScriptRoot 'export_parts_blender.py') -- --source $Source --out $Project
    if ($LASTEXITCODE -ne 0) { throw 'Part export failed.' }

    # Source meshes/textures are validated before clearing the candidate.
    & python (Join-Path $PSScriptRoot 'owned_outputs.py') --evidence $Evidence --source $Source --clear-candidate
    if ($LASTEXITCODE -ne 0) { throw 'Unsafe candidate output path.' }

    $env:FORGE_ENERGY_PRESENTATION_OUTPUT = $candidate
    $log = Join-Path $Evidence ("unity-build-" + [Guid]::NewGuid().ToString('N') + '.log')
    $proc = Start-Process -FilePath $Unity -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode', '-nographics', '-quit', '-projectPath', "`"$Project`"", '-executeMethod', 'ForgeEnergyModelBuild.Build', '-logFile', "`"$log`"")
    $proc.WaitForExit()
    $proc.Refresh()
    if ($proc.ExitCode -ne 0) { throw "Unity build failed; see $log" }

    # Unity can exit 0 before -executeMethod runs (licensing, compile errors), so require this run's own receipts.
    $manifestPath = Join-Path $candidate 'weapon-models-manifest.json'
    if (!(Test-Path -LiteralPath $log) -or !(Test-Path -LiteralPath $manifestPath)) { throw "Unity did not produce a candidate; see $log" }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.format -ne 4 -or @($manifest.bundles).Count -ne $modes.Count) { throw "Unexpected model manifest; see $log" }
    foreach ($mode in $modes) {
        $name = "forge-energy-model-$mode.bundle"
        $entry = @($manifest.bundles | Where-Object { $_.bundle -ceq $name })
        $bundlePath = Join-Path $candidate $name
        if ($entry.Count -ne 1 -or !(Test-Path -LiteralPath $bundlePath)) { throw "Missing $name; see $log" }
        $length = (Get-Item -LiteralPath $bundlePath).Length
        $hash = (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $receipt = "ENERGY_BUNDLE_VERIFIED $name $length bytes $hash"
        if ($entry[0].sha256 -cne $hash -or $entry[0].bytes -ne $length -or !(Select-String -LiteralPath $log -Pattern $receipt -SimpleMatch -Quiet)) { throw "Unity's current run did not verify $name; see $log" }
    }

    & $Blender --background --factory-startup --python-exit-code 1 --python (Join-Path $PSScriptRoot 'render_icons_blender.py') -- --source $Source --out (Join-Path $candidate 'Icons')
    if ($LASTEXITCODE -ne 0) { throw 'Icon rendering failed.' }
    foreach ($mode in $modes) {
        if (!(Test-Path -LiteralPath (Join-Path $candidate "Icons/$mode.png"))) { throw "Missing icon: $mode" }
    }

    New-Item -ItemType Directory -Force (Join-Path $module 'Assets/Models') | Out-Null
    foreach ($mode in $modes) {
        Copy-Asset (Join-Path $candidate "forge-energy-model-$mode.bundle") (Join-Path $module "Assets/Models/forge-energy-model-$mode.bundle")
        Copy-Asset (Join-Path $candidate "Icons/$mode.png") (Join-Path $module "Assets/Icons/$mode.png")
    }
    Copy-Asset (Join-Path $candidate 'forge-energy-disc.bundle') (Join-Path $module 'Assets/Vfx/forge-energy-disc.bundle')
    Copy-Asset $manifestPath (Join-Path $module 'Assets/Models/weapon-models-manifest.json')
    Write-Host "Verified and copied $($modes.Count) model bundles, the manifest and $($modes.Count) icons into $module/Assets. Evidence: $Evidence."
} finally {
    $env:FORGE_ENERGY_PRESENTATION_OUTPUT = $oldOutput
}
