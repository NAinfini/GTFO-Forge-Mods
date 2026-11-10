#Requires -Version 7
# Converts the recorded sources into the local AudioKit audio folder (<cue>.ogg + cues.forge) and validates it.
param([string]$Library = 'F:\音效库')
$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$audio = Join-Path $package 'Assets/Audio'
$converter = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../ForgeAudio/tools/convert_audio.py'))
& python (Join-Path $PSScriptRoot 'stage_audio.py') --sources (Join-Path $audio 'sources.forge') --library $Library --out $audio
if ($LASTEXITCODE -ne 0) { throw 'ForgeOrdnance audio staging failed.' }
& python $converter validate $audio
if ($LASTEXITCODE -ne 0) { throw 'ForgeOrdnance AudioKit audio folder validation failed.' }
