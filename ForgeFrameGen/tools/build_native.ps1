# Builds the ForgeFrameGen native bridge.
#   pwsh Mods/ForgeFrameGen/tools/build_native.ps1
# 1. Fetches the NVIDIA DLSS SDK files pinned in Mods/ForgeFrameGen/dlss-sdk.json and the AMD FSR SDK files pinned in
#    Mods/ForgeFrameGen/fidelityfx-sdk.json from the vendors' public GitHub repositories at the pinned commits into
#    artifacts/cache/tools/dlss-sdk/<tag> and artifacts/cache/tools/fidelityfx-sdk/<tag>, and checks every file against its SHA-256 (a
#    file already there with the right hash is reused). The download is anonymous; if the server ever answers with a login or licence
#    page instead of the file, the hash check fails and the build stops.
#    The Streamline SDK pinned in Mods/ForgeFrameGen/streamline-sdk.json comes as one release archive (about 276 MB): it is downloaded
#    anonymously into artifacts/cache/tools/streamline/<tag> once, checked against its SHA-256, and only the pinned files (production
#    DLLs, headers, the two licences) are taken out of it and checked one by one. The development DLLs in the archive are never taken.
# 2. Configures and builds native/ with CMake (Visual Studio generator, x64, Release) into artifacts/native/ForgeFrameGen.
# 3. Copies the packaged NVIDIA files (nvngx_dlss.dll, nvngx_dlssg.dll, the Streamline DLLs, the licences) and AMD files (the FSR loader,
#    upscaler and frame generation DLLs, the SDK licence) beside the bridge.
# Prints that folder; ForgeFrameGen.csproj copies everything in it into the plugin output.
$ErrorActionPreference = 'Stop'
$package = Split-Path -Parent $PSScriptRoot
$repo = (& git -C $package rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Not inside a Git checkout: ' + $package }

# Fetches the files a pin file names from its GitHub repository at its commit; returns the folder they are in.
function Get-PinnedFiles([string]$pinFile, [string]$cacheName) {
    $pins = Get-Content -LiteralPath (Join-Path $package $pinFile) -Raw | ConvertFrom-Json
    $root = Join-Path $repo ('artifacts/cache/tools/' + $cacheName + '/' + $pins.tag)
    $raw = $pins.repository -replace '^https://github.com/', 'https://raw.githubusercontent.com/'
    foreach ($entry in $pins.files.PSObject.Properties) {
        $target = Join-Path $root $entry.Name
        if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -eq $entry.Value) { continue }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        $url = $raw + '/' + $pins.commit + '/' + $entry.Name
        & curl.exe -sSfL --retry 3 -o $target $url
        if ($LASTEXITCODE -ne 0) { throw 'Download failed: ' + $url }
        $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $entry.Value) {
            Remove-Item -LiteralPath $target
            throw 'SHA-256 mismatch for ' + $entry.Name + ' (got ' + $hash + '); the pinned file was not served. Check the pin; do not accept any licence prompt on the owner''s behalf.'
        }
    }
    $root
}
$sdk = Get-PinnedFiles 'dlss-sdk.json' 'dlss-sdk'
$ffx = Get-PinnedFiles 'fidelityfx-sdk.json' 'fidelityfx-sdk'

$sl = Get-Content -LiteralPath (Join-Path $package 'streamline-sdk.json') -Raw | ConvertFrom-Json
$slRoot = Join-Path $repo ('artifacts/cache/tools/streamline/' + $sl.tag)
$slFiles = Join-Path $slRoot 'files'
function Test-Pinned([string]$path, [string]$hash) { (Test-Path -LiteralPath $path) -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $hash }
$missing = @($sl.files.PSObject.Properties | Where-Object { -not (Test-Pinned (Join-Path $slFiles $_.Name) $_.Value) })
if ($missing.Count -gt 0) {
    $archive = Join-Path $slRoot (Split-Path -Leaf $sl.archive)
    if (-not (Test-Pinned $archive $sl.archiveSha256)) {
        New-Item -ItemType Directory -Force -Path $slRoot | Out-Null
        & curl.exe -sSfL --retry 3 -o $archive $sl.archive
        if ($LASTEXITCODE -ne 0) { throw 'Download failed: ' + $sl.archive }
        if (-not (Test-Pinned $archive $sl.archiveSha256)) {
            Remove-Item -LiteralPath $archive
            throw 'SHA-256 mismatch for ' + $sl.archive + '; the pinned archive was not served. Check the pin; do not accept any licence prompt on the owner''s behalf.'
        }
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $missing) {
            $item = $zip.GetEntry($entry.Name)
            if ($null -eq $item) { throw 'The Streamline archive has no ' + $entry.Name + '.' }
            $target = Join-Path $slFiles $entry.Name
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($item, $target, $true)
            if (-not (Test-Pinned $target $entry.Value)) { Remove-Item -LiteralPath $target; throw 'SHA-256 mismatch for Streamline ' + $entry.Name + '.' }
        }
    }
    finally { $zip.Dispose() }
}

$build = Join-Path $repo 'artifacts/native/ForgeFrameGen'
& cmake -S (Join-Path $package 'native') -B $build -G 'Visual Studio 18 2026' -A x64 ('-DDLSS_SDK=' + $sdk) ('-DSTREAMLINE_SDK=' + $slFiles) ('-DFFX_SDK=' + $ffx)
if ($LASTEXITCODE -ne 0) { throw 'CMake configure failed.' }
& cmake --build $build --config Release
if ($LASTEXITCODE -ne 0) { throw 'CMake build failed.' }
# NVIDIA's and AMD's files go beside the bridge unchanged (byte for byte, signatures intact); the project copies this one folder into the plugin.
$out = Join-Path $build 'Release'
Copy-Item -LiteralPath (Join-Path $sdk 'lib/Windows_x86_64/rel/nvngx_dlss.dll'), (Join-Path $sdk 'lib/Windows_x86_64/rel/nvngx_dlssg.dll') -Destination $out
Copy-Item -LiteralPath (Join-Path $sdk 'LICENSE.txt') -Destination (Join-Path $out 'NVIDIA-RTX-SDKs-LICENSE.txt')
Copy-Item -LiteralPath ($sl.files.PSObject.Properties.Name | Where-Object { $_ -like 'bin/x64/*.dll' } | ForEach-Object { Join-Path $slFiles $_ }) -Destination $out
Copy-Item -LiteralPath (Join-Path $slFiles 'license.txt') -Destination (Join-Path $out 'NVIDIA-Streamline-LICENSE.txt')
# sl.reflex.dll carries NVIDIA's Reflex SDK under the licence NVIDIA ships beside it; it goes into the package with the library.
Copy-Item -LiteralPath (Join-Path $slFiles 'bin/x64/reflex.license.txt') -Destination (Join-Path $out 'NVIDIA-Reflex-LICENSE.txt')
# Every pinned AMD DLL (the loader, the upscaler and frame generation) goes into the package.
Copy-Item -LiteralPath ((Get-Content -LiteralPath (Join-Path $package 'fidelityfx-sdk.json') -Raw | ConvertFrom-Json).files.PSObject.Properties.Name |
        Where-Object { $_ -like 'Kits/FidelityFX/signedbin/*.dll' } | ForEach-Object { Join-Path $ffx $_ }) -Destination $out
Copy-Item -LiteralPath (Join-Path $ffx 'Kits/FidelityFX/docs/license.md') -Destination (Join-Path $out 'AMD-FSR-SDK-LICENSE.txt')
$out
