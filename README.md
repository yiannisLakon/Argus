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

**Telemetry volume.** `full` writes one line per root per tick (~253 B). At `tickSeconds: 10` with
two roots that is ~3.6 MB/day, ~110 MB/month — fine for an alpha, expensive to leave on forever.
`summary` keeps only lines carrying information (any events, and every poll/baseline/resync) plus
one heartbeat per root per hour: ~12 KB/day. Note `records` is NOT a quietness signal — it counts
volume-wide journal records passing the kernel reason mask, so on a live disk it is never zero.

## Config

```json
{
  "tickSeconds": 10,
  "telemetry": "full",                  // full | summary | off
  "ignoreDirPrefixes": [".tmp"],        // directory-name prefixes excluded everywhere; [] watches all
  "roots": [
    { "id": "downloads", "path": "C:\\Users\\Yanis\\Downloads", "pollMinutes": 30 },
    { "id": "worddocs",  "path": "\\\\Mainsrv\\d\\Word Files",  "pollMinutes": 45 }
  ]
}
```

**`ignoreDirPrefixes`** excludes any path with a *directory* segment starting with one of these —
matched per segment, case-insensitively. Excluded subtrees are never descended into, never enter
the snapshot or the FRN map, and (because their directories are unmapped) their journal records
can't resolve a parent and are dropped for free. A file merely *named* `.tmp-notes` is still a real
change: the rule is about folders. Sync clients are the motivating case — Google Drive's
`.tmp.driveupload` churned a file per upload and dominated the change log. Editing the list applies
live and prunes already-recorded state silently — those paths left the watch by configuration, not
by being deleted, so emitting `removed` for them would be a lie.

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
dotnet publish -c Release -o C:\Tools\Published\Services\Argus
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

Measure the raw churn rate before sizing — sample `Next Usn` (a USN *is* a byte offset in `$J`)
twice a minute apart and scale. Measured on the dev PC 2026-08-24: **66 MB/day**, so the 32 MB
default held only 11.6 h; resized to 512 MB ≈ 7.7 days. The resize preserved the journal ID, so no
resync fired. Retention is consumed only while the machine is *running* — downtime costs nothing,
so what must fit in the ring is the longest expected **Argus outage on a live machine**, not the
longest power-off.

## Known v1 limits

- Renames are remove+add (a rename heuristic is a possible v2).
- Poller diffs can't see size+mtime+attrs-preserving edits; neither source sees bit rot.
- No process attribution (that's ETW/Sysmon territory).
- Journal roots must be drive-letter paths (no `\\?\Volume{…}` mounts).
- Renaming/moving/deleting the **watched root itself** suspends that root (logged, no removal
  flood, state kept); when the configured path exists again the intact cursor replays the gap.
- A directory rename/delete record costs one O(snapshot) scan; a bulk reorganization of a very
  large root does that per moved directory, inline in the drain (alpha telemetry will show it).
