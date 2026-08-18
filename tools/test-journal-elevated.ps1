#Requires -Version 7
<#
Argus end-to-end journal test. RUN ELEVATED (volume handle + diskpart + fsutil).

Creates a 200 MB NTFS VHD mounted as a dedicated test volume, points a scratch GLOBAL_DATA_ROOT at
%LOCALAPPDATA%\ArgusTest, and drives argus.exe (console mode) through:

  phase 1  baseline + add / append / rename / delete / dir-move / Greek filenames  (journal drains)
  phase 2  hard-kill mid-activity → restart → replay (at-least-once: no loss, duplicates allowed)
  phase 3  journal delete+recreate while stopped → gate #2 resync (trigger=2 in stats)
  phase 4  tiny journal + churn while stopped → cursor falls off the ring → gate #3 (trigger=3)

Nothing touches C:'s journal — all journal manipulation happens on the throwaway VHD volume.
Prints a PASS/FAIL table and detaches the VHD (unless -KeepArtifacts).
#>
param(
    [string]$ArgusExe = (Join-Path $PSScriptRoot '..\bin\Debug\net11.0\win-x64\argus.exe'),
    [char]$DriveLetter = 'T',
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$total = [System.Diagnostics.Stopwatch]::StartNew()

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error 'Run this script elevated.'
}

$ArgusExe = (Resolve-Path $ArgusExe).Path
if (-not (Test-Path $ArgusExe)) { Write-Error "argus.exe not found at $ArgusExe — dotnet build first." }

$drive    = "$($DriveLetter):"
$watch    = "$drive\watch"
$dataRoot = Join-Path $env:LOCALAPPDATA 'ArgusTest'
$vhd      = Join-Path $dataRoot 'argus-test.vhdx'
$argusDir = Join-Path $dataRoot 'argus'
$results  = [System.Collections.Generic.List[object]]::new()
$script:proc = $null

function Assert([string]$name, [bool]$ok, [string]$detail = '') {
    $results.Add([pscustomobject]@{ Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Test = $name; Detail = $detail })
    Write-Host ("  [{0}] {1}  {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}

function Get-Events {
    Get-ChildItem $argusDir -Filter 'changes-t-*.jsonl' -ErrorAction SilentlyContinue |
        Get-Content | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json }
}
function Get-Stats {
    Get-ChildItem $argusDir -Filter 'stats-*.jsonl' -ErrorAction SilentlyContinue |
        Get-Content | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json }
}

function Start-Argus {
    $script:proc = Start-Process -FilePath $ArgusExe -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $dataRoot "argus-out-$([guid]::NewGuid().ToString('n').Substring(0,6)).log") `
        -RedirectStandardError  (Join-Path $dataRoot "argus-err-$([guid]::NewGuid().ToString('n').Substring(0,6)).log")
    Start-Sleep -Seconds 2
    if ($script:proc.HasExited) { throw "argus exited immediately (code $($script:proc.ExitCode)) — check argus-err-*.log in $dataRoot" }
}

function Stop-Argus([switch]$Force) {
    if (-not $script:proc -or $script:proc.HasExited) { return }
    if (-not $Force) {
        # Console argus listens on Global\ArgusStop (taskkill cannot reach a hidden console window).
        # Dispose our handle right away so the NEXT argus run creates a fresh, unset event.
        try {
            $ev = [System.Threading.EventWaitHandle]::OpenExisting('Global\ArgusStop')
            try { [void]$ev.Set() } finally { $ev.Dispose() }
        } catch { Write-Warning "stop event unavailable ($_) — killing instead" }
        if ($script:proc.WaitForExit(10000)) { $script:proc.WaitForExit(); return }
        Write-Warning 'graceful stop timed out — killing'
    }
    Stop-Process -Id $script:proc.Id -Force -Confirm:$false
    $script:proc.WaitForExit()
}

# Evidence dump after every phase, so a failed run diagnoses itself in one paste.
function Dump-State([string]$label) {
    Write-Host "-- state after ${label}:"
    $err = Join-Path $argusDir 'error.log'
    if (Test-Path $err) { Get-Content $err | ForEach-Object { Write-Host "   err| $_" } }
    else { Write-Host '   err| (no error.log)' }
    @(Get-Stats) | ForEach-Object {
        Write-Host ("   st | {0}  {1,-8} trig={2} rec={3} bytes={4} ev={5}" -f
            $_.ts, $_.kind, $_.trigger, $_.records, $_.journalBytes, $_.events)
    }
    $all = @(Get-Events)
    $byType = (@($all | Group-Object type | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ' ')
    Write-Host "   ev | $($all.Count) total: $byType"
}

function Invoke-DiskPart([string]$script) {
    $f = Join-Path $dataRoot 'diskpart.txt'
    Set-Content -Path $f -Value $script -Encoding ascii
    $out = diskpart /s $f 2>&1
    if ($LASTEXITCODE -ne 0) { throw "diskpart failed:`n$out" }
    return $out
}

try {
    # ---------------------------------------------------------------- setup
    Write-Host "== setup: VHD volume $drive + scratch data root =="
    if (Test-Path $drive) { Write-Error "$drive already exists — pass a free -DriveLetter." }
    if (Test-Path $dataRoot) { Remove-Item $dataRoot -Recurse -Force -Confirm:$false }
    New-Item -ItemType Directory -Path $dataRoot | Out-Null

    # diskpart only creates and attaches the (uninitialized) vdisk; partitioning happens through the
    # Storage cmdlets so the volume is formatted BEFORE it ever gets a drive letter. Otherwise the
    # raw partition auto-mounts at the next free letter for a moment and Explorer pops a scary
    # "you need to format disk X:" dialog at the operator (seen in run #2 as drive D:).
    Invoke-DiskPart @"
create vdisk file="$vhd" maximum=200 type=expandable
select vdisk file="$vhd"
attach vdisk
"@ | Out-Null

    $disk = Get-Disk | Where-Object Location -EQ $vhd
    if (-not $disk) { throw "attached VHD not found among disks ($vhd)" }
    Initialize-Disk -Number $disk.Number -PartitionStyle MBR
    $part = New-Partition -DiskNumber $disk.Number -UseMaximumSize   # deliberately NO letter yet
    Format-Volume -Partition $part -FileSystem NTFS -NewFileSystemLabel ArgusTest -Confirm:$false | Out-Null
    $part | Set-Partition -NewDriveLetter $DriveLetter               # letter arrives already-NTFS
    if (-not (Test-Path $drive)) { throw "VHD volume $drive did not appear" }

    New-Item -ItemType Directory -Path $watch | Out-Null

    # A freshly formatted volume has NO change journal (learned in run #1: every query returned
    # ERROR_JOURNAL_NOT_ACTIVE and phases 1–2 silently exercised only the 30-minute degraded
    # poller). Production volumes always have one; the test volume gets one explicitly.
    fsutil usn createjournal m=8388608 a=1048576 $drive | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "fsutil createjournal failed on $drive ($LASTEXITCODE)" }

    $env:GLOBAL_DATA_ROOT = $dataRoot
    New-Item -ItemType Directory -Path $argusDir | Out-Null
    @"
{ "tickSeconds": 2, "telemetry": "full",
  "roots": [ { "id": "t", "path": "$($watch.Replace('\', '\\'))", "pollMinutes": 30 } ] }
"@ | Set-Content -Path (Join-Path $argusDir 'config.json') -Encoding utf8NoBOM

    # ---------------------------------------------------------------- phase 1
    Write-Host "`n== phase 1: baseline + basic events (journal drains) == $(Get-Date -Format HH:mm:ss)"
    Start-Argus
    Start-Sleep -Seconds 6      # baseline on first tick

    Set-Content "$watch\plain.txt" 'hello'
    Set-Content "$watch\αρχείο δοκιμής.txt" 'γειά σου'
    New-Item -ItemType Directory "$watch\sub" | Out-Null
    Set-Content "$watch\sub\nested.txt" 'nest'
    Start-Sleep -Seconds 6

    Add-Content "$watch\plain.txt" ' world'
    Start-Sleep -Seconds 6
    Rename-Item "$watch\plain.txt" 'renamed.txt'
    Start-Sleep -Seconds 6
    Remove-Item "$watch\αρχείο δοκιμής.txt" -Confirm:$false
    Start-Sleep -Seconds 6
    New-Item -ItemType Directory "$watch\dir1" | Out-Null
    Set-Content "$watch\dir1\inner.txt" 'inner'
    Start-Sleep -Seconds 6
    Move-Item "$watch\dir1" "$watch\dir2"
    Start-Sleep -Seconds 6
    Stop-Argus

    $ev = @(Get-Events)
    $st = @(Get-Stats)
    Assert 'baseline event, exactly one'      (@($ev | Where-Object type -EQ 'baseline').Count -eq 1)
    Assert 'baseline trigger=1 in stats'      (@($st | Where-Object { $_.kind -eq 'baseline' -and $_.trigger -eq 1 }).Count -eq 1)
    Assert 'added: plain.txt'                 (@($ev | Where-Object { $_.type -eq 'added' -and $_.path -eq 'plain.txt' }).Count -ge 1)
    Assert 'added: Greek filename, literal'   (@($ev | Where-Object { $_.type -eq 'added' -and $_.path -eq 'αρχείο δοκιμής.txt' }).Count -ge 1)
    Assert 'added: sub\nested.txt'            (@($ev | Where-Object { $_.type -eq 'added' -and $_.path -eq 'sub\nested.txt' }).Count -ge 1)
    Assert 'modified: plain.txt (append)'     (@($ev | Where-Object { $_.type -eq 'modified' -and $_.path -eq 'plain.txt' }).Count -ge 1)
    Assert 'rename = removed old + added new' ((@($ev | Where-Object { $_.type -eq 'removed' -and $_.path -eq 'plain.txt' }).Count -ge 1) -and (@($ev | Where-Object { $_.type -eq 'added' -and $_.path -eq 'renamed.txt' }).Count -ge 1))
    Assert 'removed: Greek filename'          (@($ev | Where-Object { $_.type -eq 'removed' -and $_.path -eq 'αρχείο δοκιμής.txt' }).Count -ge 1)
    Assert 'dir move: removed dir1\inner.txt' (@($ev | Where-Object { $_.type -eq 'removed' -and $_.path -eq 'dir1\inner.txt' }).Count -ge 1)
    Assert 'dir move: added dir2\inner.txt'   (@($ev | Where-Object { $_.type -eq 'added' -and $_.path -eq 'dir2\inner.txt' }).Count -ge 1)
    Assert 'drains ran, no resync'            ((@($st | Where-Object kind -EQ 'drain').Count -ge 3) -and (@($st | Where-Object kind -EQ 'resync').Count -eq 0))
    Assert 'graceful stop wrote summary'      (@($st | Where-Object kind -EQ 'summary').Count -ge 1) '(via Global\ArgusStop)'
    Dump-State 'phase 1'

    # ---------------------------------------------------------------- phase 2
    Write-Host "`n== phase 2: hard kill mid-activity → replay == $(Get-Date -Format HH:mm:ss)"
    Start-Argus
    Start-Sleep -Seconds 3
    1..5 | ForEach-Object { Set-Content "$watch\kill-$_.txt" "k$_" }
    Stop-Argus -Force            # mid-tick kill: cursor may be behind the events just written
    Start-Argus
    Start-Sleep -Seconds 8       # replay drains the gap
    Stop-Argus

    $ev = @(Get-Events)
    # foreach, not nested Where-Object: an inner pipeline's $_ shadows the outer one, which made
    # run #2 report five perfectly-replayed files as missing.
    $missing = @(foreach ($n in 1..5) {
        if (@($ev | Where-Object { $_.type -eq 'added' -and $_.path -eq "kill-$n.txt" }).Count -lt 1) { $n }
    })
    Assert 'kill/replay: all 5 files seen (dupes allowed)' ($missing.Count -eq 0) $(if ($missing) { "missing: kill-$($missing -join ',kill-').txt" })
    Dump-State 'phase 2'

    # ---------------------------------------------------------------- phase 3
    Write-Host "`n== phase 3: journal delete+recreate while stopped → gate #2 == $(Get-Date -Format HH:mm:ss)"
    fsutil usn deletejournal /d $drive | Out-Null
    fsutil usn createjournal m=33554432 a=8388608 $drive | Out-Null
    Set-Content "$watch\while-stopped.txt" 'created while argus was down, under a NEW journal id'
    Start-Argus
    Start-Sleep -Seconds 10      # gate #2 fires → resync scan diffs it in
    Stop-Argus

    $st = @(Get-Stats)
    $ev = @(Get-Events)
    Assert 'resync with trigger=2 in stats'  (@($st | Where-Object { $_.kind -eq 'resync' -and $_.trigger -eq 2 }).Count -ge 1)
    Assert 'resync diff caught the new file' (@($ev | Where-Object { $_.type -eq 'added' -and $_.path -eq 'while-stopped.txt' }).Count -ge 1)
    Dump-State 'phase 3'

    # ---------------------------------------------------------------- phase 4
    Write-Host "`n== phase 4: tiny journal + churn while stopped → gate #3 == $(Get-Date -Format HH:mm:ss)"
    fsutil usn deletejournal /d $drive | Out-Null
    fsutil usn createjournal m=65536 a=32768 $drive | Out-Null
    Start-Argus                  # adopt the tiny journal (this is a trigger=2 resync)
    Start-Sleep -Seconds 8
    Stop-Argus
    # Churn until the saved cursor actually falls off the ring. NTFS rounds the "tiny" journal up
    # (run #2: 600 iterations ≈ 1.05 MB of records were ALL still retained), so a fixed count is a
    # guess — instead read the persisted cursor and keep churning until fsutil reports FirstUsn
    # beyond it. Long filenames fatten each record (~100 B + 2 B/char of name).
    $cursor = (Get-Content (Join-Path $argusDir 'state\t.cursor.json') -Raw | ConvertFrom-Json).nextUsn
    $fat = 'churn-' + ('χ' * 170)
    $firstUsn = -1
    for ($batch = 1; $batch -le 20; $batch++) {
        1..400 | ForEach-Object {
            Set-Content "$watch\$fat-$_.txt" 'x'
            Remove-Item "$watch\$fat-$_.txt" -Confirm:$false
        }
        # Locale-tolerant parse: the hex values appear in a fixed order (journal id, then FirstUsn).
        $hex = @([regex]::Matches((fsutil usn queryjournal $drive | Out-String), '0x[0-9a-fA-F]+') |
            ForEach-Object { [Convert]::ToInt64($_.Value.Substring(2), 16) })
        $firstUsn = if ($hex.Count -ge 2) { $hex[1] } else { -1 }
        Write-Host "   churn batch $batch — FirstUsn=$firstUsn vs saved cursor=$cursor"
        if ($firstUsn -lt 0 -or $firstUsn -gt $cursor) { break }
    }
    if ($firstUsn -lt 0) { Write-Warning 'could not parse fsutil queryjournal — churned blind; trigger=3 may not fire' }
    Set-Content "$watch\after-churn.txt" 'survivor'
    Start-Argus
    Start-Sleep -Seconds 10
    Stop-Argus

    $st = @(Get-Stats)
    $ev = @(Get-Events)
    Assert 'resync with trigger=3 in stats'   (@($st | Where-Object { $_.kind -eq 'resync' -and $_.trigger -eq 3 }).Count -ge 1) '(if the tiny journal was rounded up, churn may not wrap — see script comment)'
    Assert 'post-churn file caught by resync' (@($ev | Where-Object { $_.type -eq 'added' -and $_.path -eq 'after-churn.txt' }).Count -ge 1)
    Dump-State 'phase 4'
}
finally {
    # ---------------------------------------------------------------- teardown
    Stop-Argus -Force
    try { Invoke-DiskPart "select vdisk file=`"$vhd`"`ndetach vdisk" | Out-Null } catch { Write-Warning "VHD detach failed: $_" }
    # Any failure keeps the artifacts automatically — they ARE the diagnosis.
    $keep = $KeepArtifacts -or (@($results | Where-Object Result -EQ 'FAIL').Count -gt 0)
    if (-not $keep -and (Test-Path $dataRoot)) {
        try { Remove-Item $dataRoot -Recurse -Force -Confirm:$false } catch { Write-Warning "cleanup failed: $_" }
    } elseif ($keep -and (Test-Path $dataRoot)) {
        Write-Host "`nartifacts kept in $dataRoot (argus-out/err logs, error.log, stats, changes, state)"
    }
}

Write-Host "`n== results =="
$results | Format-Table -AutoSize | Out-String | Write-Host
$fails = @($results | Where-Object Result -EQ 'FAIL').Count
Write-Host ("{0}/{1} passed in {2}" -f ($results.Count - $fails), $results.Count,
    ("{0}m {1:00}s" -f [int]$total.Elapsed.TotalMinutes, $total.Elapsed.Seconds))
exit $(if ($fails) { 1 } else { 0 })
