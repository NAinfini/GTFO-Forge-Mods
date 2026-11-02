#Requires -Version 7
# Converts the recorded sources into the local AudioKit audio folder (<cue>.ogg + cues.json) and validates it.
param([string]$Library = '<sound library>')
$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$audio = Join-Path $package 'Assets/Audio'
$pipeline = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../ForgeOrdnance/tools/stage_audio.py'))
$converter = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../ForgeAudio/tools/convert_audio.py'))
& python $pipeline --sources (Join-Path $audio 'sources.json') --library $Library --out $audio
if ($LASTEXITCODE -ne 0) { throw 'ForgeOrdnance Arsenal audio staging failed.' }
& python $converter validate $audio
if ($LASTEXITCODE -ne 0) { throw 'ForgeOrdnance Arsenal AudioKit audio folder validation failed.' }
