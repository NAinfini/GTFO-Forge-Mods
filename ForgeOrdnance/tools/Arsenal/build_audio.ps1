#Requires -Version 7
# Converts the recorded sources into the local AudioKit audio folder (<cue>.ogg + cues.forge) and validates it.
param([string]$Library = 'F:\音效库')
$ErrorActionPreference = 'Stop'
$audio = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../Assets/Arsenal/Audio'))
$pipeline = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../Carrier/stage_audio.py'))
$converter = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../ForgeCore/Audio/tools/convert_audio.py'))
& python $pipeline --sources (Join-Path $audio 'sources.forge') --library $Library --out $audio
if ($LASTEXITCODE -ne 0) { throw 'ForgeOrdnance Arsenal audio staging failed.' }
& python $converter validate $audio
if ($LASTEXITCODE -ne 0) { throw 'ForgeOrdnance Arsenal AudioKit audio folder validation failed.' }
