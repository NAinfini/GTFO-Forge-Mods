# Opens a cut file in Blender 5.2 with 战甲开合 loaded from this folder (no install needed).
#   pwsh tools/hinge_tool/open.ps1 Source/mods/frame-cut/M16_cut.blend
param([Parameter(Mandatory = $true)][string]$File)
$blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
$tools = Split-Path -Parent $PSScriptRoot
$load = "import sys; sys.path.insert(0, r'$tools'); import hinge_tool; hinge_tool.register()"
Start-Process -FilePath $blender -ArgumentList @("`"$((Resolve-Path $File).Path)`"", '--python-expr', "`"$load`"")
