# In-game acceptance of frame generation (Docs/forge-upscale/PLAN.md §3.3 and §5, stage 4b), on a running development drive session.
#   pwsh Mods/ForgeFrameGen/tools/accept_frame_generation.ps1 -Session <work-dir>/session.forge -Log <file>
# Needs: Windows' hardware-accelerated GPU scheduling on (the owner turns it on and restarts; no tool changes it), the package installed
# with [Frame Generation] Enabled = true and the game in a level (run-drive-session.py start), FORGE_GAME_LOCK_TOKEN set. The game runs in
# the foreground for the counts to mean anything. Changes only ForgeFrameGen's own mode (its config file) and development switches.
param([Parameter(Mandatory)] [string]$Session, [Parameter(Mandatory)] [string]$Log)
$ErrorActionPreference = 'Stop'
$repo = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$cli = Join-Path $repo 'Mods/ForgeDevelopment/tools/drive_cli.py'
function Say($text) { $text | Tee-Object -FilePath $Log -Append }
function Call([string[]]$arguments) { $line = (& python $cli --session $Session call @arguments 2>&1) -join ' '; Say $line; $line }
function Shot($label) { $path = ((& python $cli --session $Session screenshot) | ConvertFrom-Json).screenshot; Say "$label screenshot $path"; $path }
function Numbers($line) {
    $m = [regex]::Match($line, 'state (-?\d+) \((.*)\), frames (\d+), generated (\d+), last present showed (\d+) \(frame (\d+)\)')
    if (-not $m.Success) { return $null }
    [pscustomobject]@{ State = [int]$m.Groups[1].Value; Reason = $m.Groups[2].Value; Frames = [long]$m.Groups[3].Value; Generated = [long]$m.Groups[4].Value; Last = [int]$m.Groups[5].Value; Frame = [long]$m.Groups[6].Value }
}
Remove-Item $Log -ErrorAction SilentlyContinue

# 1. The bridge exists and Streamline runs (state 2 = bridge active).
Call static:ForgeFrameGen.DlssBridge, PresentBridge | Out-Null
Call static:ForgeFrameGen.WorldPass, SetMode, '"Quality"' | Out-Null
Start-Sleep 5

# 2. Generated frames: over 20 seconds, frames presented with frame generation on grow with the game's frames and each present shows 2.
$a = Numbers (Call static:ForgeFrameGen.DlssBridge, FrameGeneration)
Start-Sleep 20
$b = Numbers (Call static:ForgeFrameGen.DlssBridge, FrameGeneration)
if ($a -and $b) {
    $frames = $b.Frame - $a.Frame; $on = $b.Frames - $a.Frames; $generated = $b.Generated - $a.Generated
    Say ("game frames $frames, presented with frame generation on $on, generated $generated, last present showed $($b.Last); " +
        $(if ($b.State -eq 2 -and $b.Last -eq 2 -and $on -ge 0.9 * $frames -and $generated -ge 0.9 * $on) { 'PASS' } else { "FAIL (state $($b.State): $($b.Reason))" }))
}

# 3. Conventions, by eye on the screenshots while turning: rows flipped into swap-chain order (expected) against Unity's order, and the
#    motion sign. The generated frames show doubled or torn edges with the wrong one.
foreach ($flip in 1, 0) {
    Call static:ForgeFrameGen.WorldPass, FlipRows, $flip | Out-Null
    Start-Sleep 2
    & python $cli --session $Session call static:ForgeDevelopment.Native.GameInputDrive Look 1.5 1.0 0 | Out-Null
    Shot "flipRows $flip" | Out-Null
}
Call static:ForgeFrameGen.WorldPass, FlipRows, 1 | Out-Null

# 4. Off again: DLSS Off turns frame generation off with its reason, and back.
Call static:ForgeFrameGen.WorldPass, SetMode, '"Off"' | Out-Null
Start-Sleep 3
Call static:ForgeFrameGen.DlssBridge, FrameGeneration | Out-Null
Call static:ForgeFrameGen.WorldPass, SetMode, '"DLAA"' | Out-Null
Start-Sleep 3
Call static:ForgeFrameGen.DlssBridge, FrameGeneration | Out-Null
Say ((& python $cli --session $Session log) -join "`n")
Say 'done'
