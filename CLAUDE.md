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
- Win32 layer (`UsnInterop.cs`): every struct/FSCTL/flag was verified against Windows SDK
  10.0.26100.0 headers. Never edit those from memory; re-verify (microsoft-learn MCP is registered
  in this project, or grep the SDK headers).

## Practical

- `GLOBAL_DATA_ROOT` per the user conventions: no fallback, services get it via the registry
  `Environment` pin (see `tools/install-service.ps1`).
- Journal reads need elevation. Claude never runs elevated steps — write scripts into `tools/` and
  hand them to Yanis (`tools/test-journal-elevated.ps1` is the end-to-end journal test; it uses a
  throwaway VHD volume, never C:'s journal).
- Console mode is the dev loop: scratch `GLOBAL_DATA_ROOT`, `tickSeconds: 2`, non-elevated runs
  degrade to polling by design (error.log says so).
- Unit tests: `dotnet test tests\Argus.Tests.csproj` (InternalsVisibleTo Argus.Tests; a
  ModuleInitializer points GLOBAL_DATA_ROOT at a temp dir).
- Health-Check-HUB heartbeat: **proposed, not wired** — the config row + secret handling are
  Yanis's (see `~/.claude/reference/health-hub.md`; reference client: InboxTriage2 DeadManPinger).
