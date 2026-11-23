param(
  [Parameter(Mandatory)][string]$GameRoot,
  [Parameter(Mandatory)][string]$BepInEx,
  [Parameter(Mandatory)][string]$Dump,
  [string]$Evidence = (Join-Path $PSScriptRoot '..\evidence\action-lock-options.json')
)
$ErrorActionPreference = 'Stop'

function File-Row([string]$Name, [string]$Path) {
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing evidence input: $Path" }
  $file = Get-Item -LiteralPath $Path
  $row = [ordered]@{
    name = $Name
    length = $file.Length
    sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
  }
  return $row
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$document = Get-Content -LiteralPath $Evidence -Raw | ConvertFrom-Json -AsHashtable
$files = @(
  @('game/GameAssembly.dll', (Join-Path $GameRoot 'GameAssembly.dll')),
  @('game/global-metadata.dat', (Join-Path $GameRoot 'GTFO_Data\il2cpp_data\Metadata\global-metadata.dat')),
  @('game/GTFO.exe', (Join-Path $GameRoot 'GTFO.exe')),
  @('game/revision.txt', (Join-Path $GameRoot 'revision.txt')),
  @('core/Mono.Cecil.dll', (Join-Path $BepInEx 'core\Mono.Cecil.dll')),
  @('interop/Modules-ASM.dll', (Join-Path $BepInEx 'interop\Modules-ASM.dll')),
  @('interop/GameData-ASM.dll', (Join-Path $BepInEx 'interop\GameData-ASM.dll')),
  @('interop/SNet_ASM.dll', (Join-Path $BepInEx 'interop\SNet_ASM.dll'))
)
. (Join-Path $repo 'Mods\Testing\SourceTools\InteropSignature.ps1') -BepInEx $BepInEx
for ($i = 0; $i -lt $files.Count; $i++) {
  # An interop assembly is generated per profile: only its signature names the game.
  $document.files[$i] = if ($files[$i][0] -like 'interop/*') {
    [ordered]@{ name = $files[$i][0]; signatureSha256 = [ForgeTestInputs.InteropSignature]::Sha256($files[$i][1]) }
  } else { File-Row $files[$i][0] $files[$i][1] }
}

$document.sources.dumpSha256 = (Get-FileHash -LiteralPath $Dump -Algorithm SHA256).Hash
foreach ($relative in @($document.catalogSha256.Keys)) {
  $path = Join-Path $repo ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
  $document.catalogSha256[$relative] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}
$json = ConvertTo-Json -InputObject $document -Depth 100
[IO.File]::WriteAllText((Resolve-Path $Evidence).Path, $json + "`n", [Text.UTF8Encoding]::new($false))
Write-Output "Regenerated action-lock source pins from dump, QA interop, game files, and catalog inputs."
