# Records one WPR CPU trace (sampling, context switches, ready-thread) for a fixed time and saves it, then exits.
# Kernel CPU sampling needs an elevated shell; start this with Start-Process powershell -Verb RunAs and nothing else
# elevated. Read the trace with tools/TraceReport. It refuses to start while another WPR session is recording.
param([Parameter(Mandatory)][string]$Out, [int]$Seconds = 60)
$ErrorActionPreference = 'Stop'
$log = "$Out.log"
"start $(Get-Date -Format o)" | Set-Content $log
try {
    $status = wpr -status 2>&1 | Out-String
    if ($status -notmatch 'WPR is not recording') { throw "another WPR session is recording: $status" }
    wpr -start CPU -filemode 2>&1 | Add-Content $log
    if ($LASTEXITCODE) { throw "wpr -start exit $LASTEXITCODE" }
    "recording $(Get-Date -Format o)" | Add-Content $log
    Start-Sleep -Seconds $Seconds
    wpr -stop $Out 2>&1 | Add-Content $log
    "stop exit $LASTEXITCODE $(Get-Date -Format o)" | Add-Content $log
}
catch {
    "error $_" | Add-Content $log
    wpr -cancel 2>&1 | Add-Content $log
}
finally { "done $(Get-Date -Format o)" | Add-Content $log }
