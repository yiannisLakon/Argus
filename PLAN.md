# Argus — directory-change watcher (Windows service, USN-journal-primary)

**Status:** plan only — nothing built yet. This document is the handoff into a fresh session.
**Working name:** *Argus* (Άργος Πανόπτης, the all-seeing watchman; fits Lethe/Charon/Eidolon).
NOTE: a dead repo `github.com/yiannisLakon/Argus` exists — check its scope with Yanis before reusing the name.

## What it is

A **Windows service** that watches configured directory trees (**recursive**) and appends every
detected change to a **JSONL** log. Two sources feed ONE event pipeline:

- **USN change journal** (primary, local NTFS roots): near-real-time, reads only the delta.
- **Poller** (enumerate + snapshot diff): runs ONLY for (a) first-ever baseline per root,
  (b) kernel-signaled resync, (c) UNC roots, where the journal is unreachable over SMB.

Yanis's explicit requirement: **the poller almost never runs.** Poll = first run + signaled failures.
This is achievable because "poll required" is a closed, kernel-signaled set — see the gate below.

## The validation gate (every tick, ~free)

Before each drain, `FSCTL_QUERY_USN_JOURNAL` and check persisted `(UsnJournalID, cursor)`:

| # | Condition | Meaning |
|---|---|---|
| 1 | no saved state | first run for this root/volume → **baseline poll** |
| 2 | `UsnJournalID` ≠ saved | journal deleted/recreated (covers format, chkdsk, volume restore) |
| 3 | `cursor < FirstUsn` | fell off the ring (read also fails `ERROR_JOURNAL_ENTRY_DELETED`) |
| 4 | `cursor < LowestValidUsn` | journal re-stamped in place — ID plausible, coverage broken |
| 5 | `ERROR_JOURNAL_DELETE_IN_PROGRESS` / `_NOT_ACTIVE` | journal being manipulated now |

Any hit ⇒ persist a `resyncRequired` flag **first** (crash mid-resync must not lose it) ⇒ poll.
No hit ⇒ drain. Service restarts/downtime need **no poll**: validation passing means the journal
replays the entire gap. Optional belt (Yanis undecided): treat unclean Windows shutdown as one extra
resync trigger. No scheduled integrity polls otherwise.

**Resync race (correctness-critical):** capture `NextUsn` **BEFORE** enumerating, scan, diff, resume
journal from that pre-scan USN. Mid-scan changes get re-seen via replay (duplicate event = fine,
at-least-once). Capturing after the scan loses mid-scan changes forever.

**Ordering rule (both sources):** write events → flush → commit cursor/snapshot atomically
(`File.Replace`). At-least-once, never silent loss.

**Journal sizing:** trigger #3 is the only "normal" failure and is purely sizing. Measure a week of
churn, then `fsutil usn createjournal m=<~30× daily churn>` (resizes in place, non-disruptive).
Records ≈ 100 bytes. A few hundred MB makes ring regression effectively impossible.

## Journal reader facts (verified against MS Learn in a prior session)

- Volume handle `\\.\C:` + `DeviceIoControl`. First 8 bytes of every read buffer = next `StartUsn`;
  then packed `USN_RECORD_V2/V3` walked by `RecordLength`. `bytesReturned == 8` ⇒ caught up.
- `READ_USN_JOURNAL_DATA_V1`: pass saved `UsnJournalID` (kernel integrity-checks it). `ReasonMask` +
  `ReturnOnlyOnClose=1` filter in-kernel (reasons accumulate per open handle, flush on CLOSE).
  `BytesToWaitFor=0` → non-blocking tick; `>0` → parks in-kernel, no polling at all (v2 option).
- Records carry leaf name (**no trailing null — use `FileNameLength`**) + `ParentFileReferenceNumber`,
  never a full path. Flat roots: filter on parent FRN. Deep roots: need FRN→(name,parent) map via
  `FSCTL_ENUM_USN_DATA` — the expensive part; keep per-watched-subtree, not whole-volume, if possible.
- Per-volume; needs elevation (service = LocalSystem, fine); **impossible over SMB** (documented).
- Full background + refs: `%TEMP%\handoff-2026-08-15-usn-journal-mainsrv.md`.

## Poller (baseline / resync / UNC roots)

Enumerate capturing (relPath, size, lastWriteUtc, attrs) — steal `Apographe/src/Enumerator.cs` idioms
(FIND-cached metadata, no per-file stat; reparse points never followed; hidden/system included;
per-dir errors logged+skipped). Diff vs snapshot ⇒ `added`/`modified`/`removed`.
Limits: renames = remove+add (v2 heuristic possible); size+mtime-preserving edits invisible; no
attribution. Unreachable root ⇒ **skip + error.log, never a mass-removal flood.**
UNC roots: per-root interval (tens of minutes; Mainsrv-scale trees take minutes to enumerate — never
1/min over SMB), jitter, overlap guard (skip tick if previous still running; auto-back-off + log if
scan > ~25% of interval).

## Event shape (v1)

Compact JSONL, `UnsafeRelaxedJsonEscaping` (Greek literal — writer pattern: `Apographe/src/ManifestWriter.cs`):

```json
{"ts":"2026-08-15T10:00:03.1234567Z","root":"docs","type":"modified","path":"sub\\αρχείο.docx","size":51234,"mtime":"...","prevSize":50100,"prevMtime":"..."}
```

`type` ∈ `added|modified|removed`; `prev*` on modified only; journal-sourced events fill the same shape.
Change logs: `$GLOBAL_DATA_ROOT\argus\changes-<rootId>-YYYY-MM.jsonl` (monthly rotation).

## Resource telemetry (alpha/beta requirement from Yanis)

Separate stream `$GLOBAL_DATA_ROOT\argus\stats-YYYY-MM.jsonl` — one line per tick + summary on stop.
Zero deps: `Process.TotalProcessorTime`/`WorkingSet64`/`PrivateMemorySize64`/`HandleCount`/`ThreadCount`,
`GC.GetGCMemoryInfo`/`GetTotalAllocatedBytes`/`CollectionCount`, I/O via `GetProcessIoCounters`
(one `LibraryImport`). NO PerformanceCounter, NO OpenTelemetry.

Each line: `kind: drain|resync|baseline`, root, elapsed, records read, journal bytes, events emitted,
cpuMs Δ, workingSet, alloc Δ, gc0/1/2, ioRead/ioWrite Δ, handles. **On resync: which gate trigger
(#1–#5) fired** — this is THE alpha metric: proves (or disproves) "poller never runs" and why.
Config `telemetry: full|summary|off`; `full` during alpha/beta.

## Service conventions (binding)

Workspace + user CLAUDE.md in full (net11.0, AOT — worker services publish AOT cleanly; Release
pdb-free; publish `C:\Tools\Published\Argus`; startup network-share guard; Greek only for
end-user-facing text — service logs are developer English). Service-specific:

- **UNC paths in config, never `Z:`** — services don't see user drive mappings.
- `GLOBAL_DATA_ROOT` pinned in `HKLM\SYSTEM\CurrentControlSet\Services\Argus` `Environment`
  MultiString (LocalSystem sees no user env). **No fallback path — fail loudly.**
- Nothing ever written to `\\Mainsrv` (reading/watching it is fine).
- `Microsoft.Extensions.Hosting` + `UseWindowsService()`; console mode for dev; single-instance mutex
  (`Lethe/src/Program.cs` → `WithSingleInstance`). Config reread each tick (interval adjustable live).
- **Health Check HUB:** always-on service ⇒ propose heartbeat (read `~/.claude/reference/health-hub.md`
  first; config row + secrets are Yanis's — never wire silently).

## Plan of action

1. Scaffold `Workspace\CSharp\Argus` (csproj worker+AOT, Directory.*.props + app.manifest from
   Apographe); in the folder run: `claude mcp add --transport http microsoft-learn https://learn.microsoft.com/api/mcp`
2. Core lib (pure, unit-testable): snapshot model+serializer, enumerator, differ, event/stats sinks.
3. Journal reader: P/Invoke layer (`LibraryImport`, AOT-clean), validation gate, drain loop, cursor
   store `(UsnJournalID, NextUsn)` under `$GLOBAL_DATA_ROOT\argus\`. **Verify structs/FSCTLs against
   microsoft-learn MCP, not memory.**
4. Resync orchestration: gate → flag → pre-scan `NextUsn` → poll → diff → resume. Baseline = same path
   minus event emission (decide with Yanis: silent baseline vs single `baseline` event).
5. Service host + telemetry + install script (`tools/install-service.ps1`: sc create, registry
   `Environment` pinning, delayed-auto start; run elevated).
6. Test (console mode, Downloads, 10s tick): add/modify/delete/rename; kill mid-tick → replay;
   journal-vs-poller event parity; force triggers #2/#3 (delete/undersize journal) → verify resync +
   correct trigger in stats; Greek filenames; then install as service, re-verify, check telemetry.
7. Propose HUB heartbeat (Yanis's call). Ask for real watch targets + intervals; size the journal
   from a week of measured churn.

## Subagent policy — explicit authorization from Yanis

**Freely use Opus subagents** (`Agent` tool, `model: "opus"`) for independent chunks: core-lib vs
journal P/Invoke vs service host in parallel; adversarial review of differ + gate logic; test
authoring. Terse prompts per the token-budget convention.

## Open items (ask, don't assume)

- Final name (dead Argus repo!), real watch targets, dirty-shutdown resync trigger yes/no,
  baseline event vs silent, rename detection (v2), change-log retention, HUB row.
