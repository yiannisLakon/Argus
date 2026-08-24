# Argus — project conventions & decided items

Workspace + user CLAUDE.md bind in full. This file records what is *decided* — don't relitigate.

## Decisions (with Yanis, 2026-08-15)

- **Name stays Argus** (the dead github repo is his to clean up; no local collision).
- **No dirty-shutdown resync trigger** — the gate's five conditions are the complete set; USN
  writes are transactional with NTFS metadata, and a journal damaged by a crash re-stamps → #2/#4.
- **Baseline emits a single `baseline` event** into the changes log (files count) — never a
  per-file flood, never fully silent.
- **Change logs are kept forever** (monthly files, no deletion code). Revisit only with data.
- Renames = `removed`+`added` in v1; a rename heuristic is v2 (not built, don't half-build it).

## Design invariants (correctness — treat as load-bearing)

- At-least-once, never silent loss. Commit order: **events flush(disk) → snapshot/dirmap →
  cursor LAST**; all state saves are tmp + `File.Replace`.
- Resync persists its `resyncTrigger` flag **before** any work, and captures the pre-scan
  `NextUsn` **before** enumerating. Both orderings are crash-safety; do not "optimize" them.
- `ReturnOnlyOnClose=0` is deliberate: close-only mode loses `RENAME_OLD_NAME` records (the close
  record carries only the new name). Dedupe of repeated reason bits is the stat-vs-snapshot
  compare, not record filtering.
- FRN→path map is built from the scan (per-subtree) and maintained from dir records —
  `FSCTL_ENUM_USN_DATA` is deliberately unused. The map + snapshot + cursor persist so a restart
  never polls.
- **Exclusions are two lists, never one.** `ignoreDirPrefixes` matches FOLDER segments (excludes the
  whole subtree — unmapped dirs mean their records can't resolve a parent, so it costs nothing at
  drain time); `ignoreFilePrefixes` matches FILE names (needs its own check, since the parent dir
  is watched). Merging them makes `~$` silently useless against `~$agreement.docx`. Editing either
  prunes persisted state **silently**: those paths left by configuration, not deletion, so a
  `removed` event would be a lie.
- Telemetry: **`records` is not a quietness signal.** It counts volume-wide journal records that
  passed the kernel reason mask — never zero on a live disk (measured 2026-08-24: 0 of 87,672
  drains). Summary mode filters on `events == 0` plus an hourly per-root heartbeat; do not
  reintroduce a records-based test, it drops nothing.
- Win32 layer (`UsnInterop.cs`): every struct/FSCTL/flag was verified against Windows SDK
  10.0.26100.0 headers. Never edit those from memory; re-verify (microsoft-learn MCP is registered
  in this project, or grep the SDK headers).

## Practical

- `GLOBAL_DATA_ROOT` per the user conventions: no fallback, services get it via the registry
  `Environment` pin (see `tools/install-service.ps1`).
- Journal reads need elevation. Elevated work belongs in `tools/` as a reviewable script, never as
  ad-hoc commands — Yanis runs it, or Claude may when he has deliberately started an elevated
  session and asked. `tools/test-journal-elevated.ps1` is the end-to-end journal test (throwaway
  VHD volume, never C:'s journal); `tools/install-service.ps1` installs, `-Relocate`s or removes
  the service.
- Publishing: services go to `C:\Tools\Published\Services\{Name}` (see the user-level CLAUDE.md).
  A moved folder without `sc.exe config <svc> binPath= ...` leaves a service that won't start.
- Console mode is the dev loop: scratch `GLOBAL_DATA_ROOT`, `tickSeconds: 2`, non-elevated runs
  degrade to polling by design (error.log says so).
- Unit tests: `dotnet test tests\Argus.Tests.csproj` (InternalsVisibleTo Argus.Tests; a
  ModuleInitializer points GLOBAL_DATA_ROOT at a temp dir).
- Health-Check-HUB heartbeat: **proposed, not wired** — the config row + secret handling are
  Yanis's (see `~/.claude/reference/health-hub.md`; reference client: InboxTriage2 DeadManPinger).
