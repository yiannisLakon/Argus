#Requires -Version 7
<#
Install (or uninstall) the Argus Windows service. RUN ELEVATED — the SCM and HKLM both require it.

Publish is NOT done here; publish first (framework-dependent AOT native binary):
    dotnet publish -c Release -o C:\Tools\Published\Services\Argus

Usage:
    tools\install-service.ps1                 # install (uses your GLOBAL_DATA_ROOT)
    tools\install-service.ps1 -Relocate       # existing service: point it at $BinDir, re-pin data root
    tools\install-service.ps1 -Uninstall
#>
param(
    [string]$BinDir = 'C:\Tools\Published\Services\Argus',
    [string]$DataRoot = $env:GLOBAL_DATA_ROOT,
    [switch]$Relocate,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$svc = 'Argus'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error 'Run this script elevated.'
}

if ($Uninstall) {
    sc.exe stop $svc | Out-Null   # fails harmlessly when not running
    sc.exe delete $svc
    if ($LASTEXITCODE -ne 0) { throw "sc delete failed ($LASTEXITCODE)" }
    Write-Host "service '$svc' removed in $($sw.Elapsed.TotalSeconds.ToString('0.0'))s"
    return
}

if (-not $DataRoot) {
    Write-Error 'GLOBAL_DATA_ROOT is not set and -DataRoot was not given — no fallback path exists by design.'
}
$exe = Join-Path $BinDir 'argus.exe'
if (-not (Test-Path $exe)) {
    Write-Error "argus.exe not found in $BinDir — publish first: dotnet publish -c Release -o $BinDir"
}
$key = "HKLM:\SYSTEM\CurrentControlSet\Services\$svc"

# Moving an installed service's binary: rewrite binPath (and re-pin the data root) rather than
# uninstall/reinstall, so the service keeps its failure actions, start type and identity. A moved
# folder WITHOUT this leaves a service that simply fails to start.
if ($Relocate) {
    $was = (Get-CimInstance Win32_Service -Filter "Name='$svc'" -ErrorAction SilentlyContinue).PathName
    if (-not $was) { Write-Error "service '$svc' is not installed — run without -Relocate to install it." }
    sc.exe config $svc binPath= "`"$exe`"" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc config failed ($LASTEXITCODE)" }
    Set-ItemProperty -Path $key -Name Environment -Value ([string[]]@("GLOBAL_DATA_ROOT=$DataRoot")) -Type MultiString
    Write-Host "relocated in $($sw.Elapsed.TotalSeconds.ToString('0.0'))s:"
    Write-Host "  was  $was"
    Write-Host "  now  `"$exe`""
    Write-Host "  data root $DataRoot (re-pinned)"
    Write-Host "`nstart it: sc.exe start $svc   — then the old folder is safe to delete"
    return
}

# 1. The service: LocalSystem, delayed auto-start (it watches for hours; boot contention helps no one).
sc.exe create $svc binPath= "`"$exe`"" start= delayed-auto obj= LocalSystem DisplayName= "Argus (directory change watcher)"
if ($LASTEXITCODE -ne 0) { throw "sc create failed ($LASTEXITCODE) — already installed? -Uninstall first." }
sc.exe description $svc "Watches configured directory trees via the NTFS USN change journal (poller for UNC roots) and appends every change to JSONL logs under GLOBAL_DATA_ROOT\argus." | Out-Null
# Restart on crash: 3 restarts, 60 s apart, failure counter resets daily.
sc.exe failure $svc reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

# 2. Pin GLOBAL_DATA_ROOT where LocalSystem can actually see it: the service's own registry
#    Environment MultiString. A machine-scope env var is NOT a substitute — services.exe reads its
#    environment at boot, so services would only see it after a reboot.
Set-ItemProperty -Path $key -Name Environment -Value ([string[]]@("GLOBAL_DATA_ROOT=$DataRoot")) -Type MultiString

Write-Host ''
Write-Host "installed in $($sw.Elapsed.TotalSeconds.ToString('0.0'))s:"
Write-Host "  binary     $exe"
Write-Host "  data root  $DataRoot  (pinned in $key\Environment)"
Write-Host ''
Write-Host 'next steps:'
Write-Host "  1. config   $DataRoot\argus\config.json   (create a starter one with: argus.exe init)"
Write-Host '  2. journal  after a week of alpha telemetry, size it: fsutil usn createjournal m=<~30x daily churn bytes> C:'
Write-Host "  3. start    sc.exe start $svc"
