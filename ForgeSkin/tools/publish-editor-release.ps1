#Requires -Version 7.0
# Publishes a built editor release to the public mirror in the one order that keeps its tag on the exported source:
#   pwsh Mods/ForgeSkin/tools/publish-editor-release.ps1 -Mirror <mirror clone> [-Upload <dir>] [-PluginZip <the Thunderstore NAinfini-ForgeSkin zip>]
# Run it after build-editor-release.ps1 and after the source of this version was exported (Mods/Release/export-mirror.ps1) and pushed
# from the mirror clone. It refuses unless the clone is clean, level with origin/main and holds this editor version, then
# 1. creates the release ForgeSkin-v<version> with its tag on that mirror commit (never on whatever main is later),
# 2. adds the new nupkg to ForgeSkin-update and only then replaces its RELEASES (the one asset that may be replaced),
# 3. sets the version in that release's text. No existing release or asset is deleted or changed otherwise.
# -PluginZip: the plugin package as uploaded to Thunderstore (the build script makes its own copy); default is the upload folder's.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Mirror,
    [string]$Upload,
    [string]$PluginZip,
    [string]$Repo = 'NAinfini/GTFO-Forge-Mods'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$Mirror = (Resolve-Path $Mirror).Path
$Upload = if ($Upload) { (Resolve-Path $Upload).Path } else { Join-Path $root 'artifacts/forge-skin-release/upload' }
function Check([string]$what) { if ($LASTEXITCODE) { throw "$what failed (exit $LASTEXITCODE)" } }

$version = (Get-Content (Join-Path $root 'Mods/ForgeSkin/Editor/package.json') -Raw | ConvertFrom-Json).version
$tag = "ForgeSkin-v$version"
$files = @("ForgeSkin-$version Setup.exe", "ForgeSkin-$version-full.nupkg", 'RELEASES', "ForgeSkin-$version-portable.zip") | ForEach-Object { Join-Path $Upload $_ }
$plugin = if ($PluginZip) { (Resolve-Path $PluginZip).Path } else { Get-ChildItem $Upload -Filter 'NAinfini-ForgeSkin-*.zip' | Select-Object -First 1 -ExpandProperty FullName }
foreach ($f in $files + $plugin) { if (-not $f -or -not (Test-Path $f)) { throw "missing upload file: $f" } }

# The build's own list of sha256 must match the files about to go up.
foreach ($line in Get-Content (Join-Path $Upload 'upload.sha256')) {
    $hash, $name = ($line -split '  ', 3)[0, 1]
    if ($name -like 'NAinfini-ForgeSkin-*') { continue }
    if ((Get-FileHash (Join-Path $Upload $name) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw "$name does not match upload.sha256" }
}

git -C $Mirror fetch origin; Check 'mirror fetch'
if (git -C $Mirror status --porcelain) { throw 'the mirror clone has uncommitted changes' }
$head = (git -C $Mirror rev-parse HEAD).Trim()
if ($head -ne (git -C $Mirror rev-parse origin/main).Trim()) { throw 'the mirror clone is not level with origin/main: commit, push and fetch the export first' }
$mirrored = (Get-Content (Join-Path $Mirror 'ForgeSkin/Editor/package.json') -Raw | ConvertFrom-Json).version
if ($mirrored -ne $version) { throw "the mirror holds editor $mirrored, not ${version}: export this version first" }
gh release view $tag --repo $Repo 2>$null | Out-Null
if (-not $LASTEXITCODE) { throw "$tag already exists" }

$changelog = Get-Content (Join-Path $root 'Mods/ForgeSkin/CHANGELOG.md') -Raw
$notes = [regex]::Match($changelog, "(?ms)^## Editor $([regex]::Escape($version))\s*\r?\n(.*?)(?=^## |\z)").Groups[1].Value.Trim()
if (-not $notes) { throw "CHANGELOG.md has no 'Editor $version' section" }
$notesFile = Join-Path ([IO.Path]::GetTempPath()) "$tag-notes.md"
Set-Content $notesFile "## ForgeSkin Editor $version`n`n$notes`n" -Encoding utf8NoBOM

gh release create $tag --repo $Repo --target $head --title "ForgeSkin $version" --notes-file $notesFile @files $plugin; Check 'release create'
gh release upload ForgeSkin-update --repo $Repo (Join-Path $Upload "ForgeSkin-$version-full.nupkg"); Check 'feed nupkg'
gh release upload ForgeSkin-update --repo $Repo (Join-Path $Upload 'RELEASES') --clobber; Check 'feed RELEASES'
$body = gh release view ForgeSkin-update --repo $Repo --json body --jq .body
($body -replace '当前：[\d.]+', "当前：$version") | gh release edit ForgeSkin-update --repo $Repo --notes-file -; Check 'feed text'
Remove-Item $notesFile
Write-Host "published $tag on mirror commit $head"
gh release view $tag --repo $Repo --json assets --jq '.assets[] | "\(.name) \(.size)"'
gh release view ForgeSkin-update --repo $Repo --json assets --jq '.assets[] | "\(.name) \(.size)"'
