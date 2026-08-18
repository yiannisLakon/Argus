# Argus — directory-change watcher (Windows service, USN-journal-primary)

Watches configured directory trees (recursive) and appends every detected change to JSONL logs.
Two sources feed one event pipeline:

- **USN change journal** (local NTFS roots): near-real-time, reads only the delta. The primary path.
- **Poller** (enumerate + snapshot diff): runs ONLY for the first-ever baseline, kernel-signaled
  resyncs, UNC roots (the journal is unreachable over SMB — documented), and a degraded mode when
  the journal is unusable (not elevated / non-NTFS / journal being manipulated).

The design goal: **the poller almost never runs.** "Poll required" is a closed, kernel-signaled set.

## The validation gate (every tick, ~free)

Before each drain, `FSCTL_QUERY_USN_JOURNAL` is checked against the persisted `(UsnJournalID, NextUsn)`:

| # | Condition | Meaning |
|---|---|---|
| 1 | no saved state | first run → baseline scan |
| 2 | `UsnJournalID` ≠ saved | journal deleted/recreated |
| 3 | cursor < `FirstUsn` (or read fails `ERROR_JOURNAL_ENTRY_DELETED`) | fell off the ring — a sizing problem |
| 4 | cursor < `LowestValidUsn` | journal re-stamped in place |
| 5 | `ERROR_JOURNAL_DELETE_IN_PROGRESS` / `_NOT_ACTIVE` | journal being manipulated now |

Any hit: a `resyncTrigger` flag is persisted **first** (a crash mid-resync cannot lose the
obligation), then a scan rebuilds state. The pre-scan `NextUsn` is captured **before** enumerating,
so mid-scan changes replay afterwards — duplicates are fine, silent loss is not (at-least-once).
Service restarts need **no** poll: a passing gate means the journal replays the whole gap.

Commit order everywhere: **events flushed to disk → snapshot/dirmap → cursor LAST** (atomic
tmp + `File.Replace`).

## Paths (records → paths without a whole-volume scan)

Journal records carry only a leaf name + parent FRN. Argus keeps a per-root **FRN → directory-path
map**, built during the baseline/resync walk (one attribute-only handle per directory) and
maintained incrementally from directory create/rename/delete records. `FSCTL_ENUM_USN_DATA` is
never used. The map persists beside the snapshot, so restarts resume without polling.

## Data layout (`$GLOBAL_DATA_ROOT\argus\`)

```
config.json                      the watch list (reread every tick; edits apply live)
changes-<rootId>-YYYY-MM.jsonl   change events, monthly rotation, kept forever
stats-YYYY-MM.jsonl              resource telemetry, one line per unit of work + summary on stop
error.log                        operator-facing errors (append-only)
state\<rootId>.cursor.json       (UsnJournalID, NextUsn, resyncTrigger)
state\<rootId>.snapshot.jsonl    last known per-file state (path, size, mtime, attrs)
state\<rootId>.dirmap.jsonl      FRN → directory path map (journal roots only)
```

Event shapes (`UnsafeRelaxedJsonEscaping` — Greek stays literal):

```json
{"ts":"…Z","root":"docs","type":"added","path":"sub\\αρχείο.docx","size":51234,"mtime":"…Z"}
{"ts":"…Z","root":"docs","type":"modified","path":"…","size":51234,"mtime":"…Z","prevSize":50100,"prevMtime":"…Z"}
{"ts":"…Z","root":"docs","type":"removed","path":"…","prevSize":50100,"prevMtime":"…Z"}
{"ts":"…Z","root":"docs","type":"baseline","files":1234}
```

Renames are `removed` + `added` (v1). A stats line carries elapsed, records read, journal bytes,
events, CPU/alloc/GC/IO deltas, and — on resync/baseline lines — **`trigger` (#1–#5)**: the alpha
metric that proves (or disproves) "the poller never runs" and why.

## Config

```json
{
  "tickSeconds": 10,
  "telemetry": "full",            // full | summary | off
  "roots": [
    { "id": "downloads", "path": "C:\\Users\\Yanis\\Downloads", "pollMinutes": 30 },
    { "id": "worddocs",  "path": "\\\\Mainsrv\\d\\Word Files",  "pollMinutes": 45 }
  ]
}
```

Local paths → journal; UNC paths → poller (**always `\\server\share`, never a mapped drive** —
services don't see user drive mappings). `pollMinutes` is the poller cadence (±10% jitter, overlap
guard, auto-back-off when a scan exceeds ~25% of the interval) and the degraded-mode cadence for
journal roots.

## Running

```
argus.exe init        # write a starter config
argus.exe             # console mode — same code path as the service; Ctrl+C to stop
```

Console runs also stop gracefully on the `Global\ArgusStop` event (scripts can't deliver Ctrl+C to
a hidden console): `[Threading.EventWaitHandle]::OpenExisting('Global\ArgusStop').Set()`. Service
mode deliberately ignores it — the SCM owns service stops.

Journal reads need an **elevated** console (the service runs LocalSystem, which is enough);
non-elevated runs degrade to polling and say so in error.log.

Install as a service (run elevated; publishes are separate):

```
dotnet publish -c Release -o C:\Tools\Published\Argus
tools\install-service.ps1
```

The installer creates the service (LocalSystem, delayed-auto, restart-on-crash) and pins
`GLOBAL_DATA_ROOT` into the service's registry `Environment` MultiString — machine-scope env vars
are not visible to services until a reboot; the registry pin is.

## Testing

- `dotnet test tests\Argus.Tests.csproj` — pure logic: the gate decision table, USN record buffer
  parsing (V2/V3), differ, dirmap subtree ops, snapshot/event-sink round-trips.
- `tools\test-journal-elevated.ps1` — end-to-end on a throwaway NTFS VHD (never touches C:'s
  journal): baseline, live drains, Greek filenames, dir moves, kill-mid-tick replay, forced gate #2
  (journal recreate) and gate #3 (undersized ring) resyncs. Run elevated.

## Journal sizing

Gate #3 is the only "normal" failure and is purely sizing. After a week of telemetry, size the
journal to ~30× daily churn (records ≈ 100 bytes; a few hundred MB makes ring regression
effectively impossible): `fsutil usn createjournal m=<bytes> C:` (resizes in place, non-disruptive).
NTFS rounds small requests up — a `m=65536` request measured out at ~2.25 MB retained.

## Known v1 limits

- Renames are remove+add (a rename heuristic is a possible v2).
- Poller diffs can't see size+mtime+attrs-preserving edits; neither source sees bit rot.
- No process attribution (that's ETW/Sysmon territory).
- Journal roots must be drive-letter paths (no `\\?\Volume{…}` mounts).
- Renaming/moving/deleting the **watched root itself** suspends that root (logged, no removal
  flood, state kept); when the configured path exists again the intact cursor replays the gap.
- A directory rename/delete record costs one O(snapshot) scan; a bulk reorganization of a very
  large root does that per moved directory, inline in the drain (alpha telemetry will show it).
