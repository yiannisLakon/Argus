using System.ComponentModel;
using System.Diagnostics;
using static Argus.UsnInterop;

namespace Argus;

/// <summary>Watches one local NTFS root through the volume's USN change journal. Every tick runs the
/// validation gate (one FSCTL_QUERY_USN_JOURNAL — ~free); a clean gate drains the journal delta, any
/// gate hit persists a resync obligation FIRST and then rebuilds state from a scan. The poller-style
/// scan is deliberately confined to: first-ever baseline, kernel-signaled resync, and a degraded
/// mode for when the journal is unusable (not elevated / non-NTFS / journal being manipulated).
///
/// Commit order everywhere: events flushed to disk → snapshot/dirmap → cursor LAST. A crash at any
/// point replays journal records; duplicates are suppressed by the snapshot compare, and nothing is
/// ever silently lost.</summary>
internal sealed class JournalWatcher : IDisposable
{
    const int ReadBufferSize = 256 * 1024;

    readonly RootConfig _root;
    readonly string _fullPath;   // normalized absolute root, no trailing separator
    readonly string _volume;     // "C:"
    readonly ErrorLog _errors;
    readonly StatsSink _stats;
    readonly EventSink _events;

    UsnJournal? _journal;
    CursorState? _cursor;
    Snapshot? _snapshot;
    DirMap? _dirMap;
    bool _snapDirty;

    volatile bool _busy;         // a resync / degraded poll runs on a task; ticks skip meanwhile
    Task? _busyTask;
    DateTimeOffset _nextDegradedPoll = DateTimeOffset.MinValue;
    bool _openFailureLogged;
    bool _unsupportedLogged;
    bool _rootMissingLogged;
    bool _journalGoneLogged;
    bool _rootDisplaced;        // the watched root itself was renamed/moved/deleted mid-drain
    volatile bool _disposed;    // a busy task must not commit past its watcher's disposal
    int _lastVolumeError;

    // Shutdown-summary counters (read by Worker after the loop stops).
    internal long Drains, Polls, Baselines, Resyncs, Records, Events;

    internal JournalWatcher(RootConfig root, ErrorLog errors, StatsSink stats)
    {
        _root = root;
        _fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path));
        string vol = Path.GetPathRoot(_fullPath)?.TrimEnd('\\')
            ?? throw new ArgumentException($"root '{root.Id}': no volume in path {root.Path}");
        if (vol.Length != 2 || vol[1] != ':')
            throw new ArgumentException($"root '{root.Id}': only drive-letter local roots are supported, got '{vol}'");
        _volume = vol;
        _errors = errors;
        _stats = stats;
        _events = new EventSink(root.Id);
        _cursor = CursorStore.Load(root.Id, errors);
        _snapshot = Snapshot.Load(SnapshotPath, errors);
        _dirMap = DirMap.Load(root.Id, errors);
    }

    string SnapshotPath => Path.Combine(StateDir, _root.Id + ".snapshot.jsonl");

    internal void Tick()
    {
        if (_busy) return;

        // Root gone (renamed away, BitLocker-locked, dismounted)? Suspend — never a removal flood.
        // The cursor stays put, so the journal replays the whole gap when the path returns.
        if (!Directory.Exists(_fullPath))
        {
            if (!_rootMissingLogged)
            {
                _rootMissingLogged = true;
                _errors.Log($"journal\t{_root.Id}\twatched root missing/unreachable — suspended (no removal flood); resumes when it returns");
            }
            return;
        }
        _rootMissingLogged = false;

        TelemetrySample before = Telemetry.Sample();
        var sw = Stopwatch.StartNew();
        try
        {
            if (!EnsureJournal()) { DegradedPollIfDue(); return; }

            USN_JOURNAL_DATA_V2 jd;
            try { jd = _journal!.Query(); }
            catch (Win32Exception w) when (w.NativeErrorCode is ERROR_JOURNAL_DELETE_IN_PROGRESS or ERROR_JOURNAL_NOT_ACTIVE)
            {
                PersistResyncFlag(5); // the journal is being manipulated right now — or never existed
                if (!_journalGoneLogged)
                {
                    _journalGoneLogged = true;
                    _errors.Log($"journal\t{_root.Id}\tno usable journal on {_volume} (error {w.NativeErrorCode}) — gate #5 flagged, degraded polling every {_root.PollMinutes}m; if the volume never had one, create it: fsutil usn createjournal m=<bytes> {_volume}");
                }
                DegradedPollIfDue();
                return;
            }
            catch (Win32Exception w) when (w.NativeErrorCode == ERROR_INVALID_FUNCTION)
            {
                if (!_unsupportedLogged)
                {
                    _unsupportedLogged = true;
                    _errors.Log($"journal\t{_root.Id}\tvolume {_volume} has no USN journal support — permanent degraded polling");
                }
                DegradedPollIfDue();
                return;
            }

            _lastVolumeError = 0;
            _journalGoneLogged = false;

            int trigger = Gate(jd);
            if (trigger != 0)
            {
                PersistResyncFlag(trigger);     // obligation on disk BEFORE any work starts
                StartBusy(() => Resync(trigger, jd));
                return;
            }

            Drain(jd, before, sw);
        }
        catch (Win32Exception w)
        {
            // Unexpected volume-level error (dismount, stale handle, ...): drop the handle so the
            // next tick reopens it fresh, and keep watching in degraded mode meanwhile.
            if (_lastVolumeError != w.NativeErrorCode)
            {
                _lastVolumeError = w.NativeErrorCode;
                _errors.Log($"journal\t{_root.Id}\tvolume error {w.NativeErrorCode} ({w.Message}) — handle reset, degraded polling until healthy");
            }
            _journal?.Dispose();
            _journal = null;
            DegradedPollIfDue();
        }
        catch (Exception ex)
        {
            _errors.Log($"tick\t{_root.Id}", ex);
        }
    }

    bool EnsureJournal()
    {
        if (_journal is not null) return true;
        try
        {
            _journal = UsnJournal.Open(_volume);
            _openFailureLogged = false;
            return true;
        }
        catch (Win32Exception w)
        {
            if (!_openFailureLogged)
            {
                _openFailureLogged = true;
                string hint = w.NativeErrorCode == ERROR_ACCESS_DENIED ? "needs elevation; " : "";
                _errors.Log($"journal\t{_root.Id}\tcannot open volume {_volume} ({w.Message}) — {hint}degraded polling");
            }
            return false;
        }
    }

    int Gate(in USN_JOURNAL_DATA_V2 jd)
        => GateCheck(_cursor, haveScanState: _snapshot is not null && _dirMap is not null, in jd);

    /// <summary>The validation gate — the plan's five conditions, in order. 0 ⇒ drain. Pure and
    /// static so the decision table is unit-testable without a live volume.</summary>
    internal static int GateCheck(CursorState? cursor, bool haveScanState, in USN_JOURNAL_DATA_V2 jd)
    {
        if (cursor is null || !haveScanState) return 1;             // #1 no (or incomplete) saved state
        if (cursor.ResyncTrigger != 0) return cursor.ResyncTrigger; // crash mid-resync: obligation survives
        if (jd.UsnJournalID != cursor.JournalId) return 2;          // #2 journal deleted/recreated
        if (cursor.NextUsn < jd.FirstUsn) return 3;                 // #3 fell off the ring
        if (cursor.NextUsn < jd.LowestValidUsn) return 4;           // #4 re-stamped in place, coverage broken
        if (cursor.NextUsn > jd.NextUsn) return 4;                  // #4 variant: journal REWOUND under the same
                                                                    //    ID (image-level volume restore) — reads
                                                                    //    would silently return nothing forever
        return 0;
    }

    void PersistResyncFlag(int trigger)
    {
        _cursor ??= new CursorState { Volume = _volume };
        if (_cursor.ResyncTrigger == 0)
        {
            _cursor.ResyncTrigger = trigger; // first cause wins — that is the telemetry answer to "why"
            CursorStore.Save(_root.Id, _cursor);
        }
    }

    void StartBusy(Action work)
    {
        _busy = true;
        _busyTask = Task.Run(() =>
        {
            try { work(); }
            catch (Exception ex) { _errors.Log($"resync\t{_root.Id}", ex); }
            finally { _busy = false; }
        });
    }

    // ---------------------------------------------------------------- drain

    void Drain(in USN_JOURNAL_DATA_V2 jd, in TelemetrySample before, Stopwatch sw)
    {
        ushort maxMajor = Math.Clamp(jd.MaxSupportedMajorVersion, (ushort)2, (ushort)3);
        long cursor = _cursor!.NextUsn;
        long startCursor = cursor;
        long records = 0, bytesTotal = 0, events = 0;

        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            while (true)
            {
                int bytes;
                long next;
                try { bytes = _journal!.Read(cursor, _cursor.JournalId, maxMajor, buffer, out next); }
                catch (Win32Exception w) when (w.NativeErrorCode == ERROR_JOURNAL_ENTRY_DELETED)
                { PersistResyncFlag(3); return; }   // kernel-confirmed gate #3 — resync next tick
                catch (Win32Exception w) when (w.NativeErrorCode is ERROR_JOURNAL_DELETE_IN_PROGRESS or ERROR_JOURNAL_NOT_ACTIVE)
                { PersistResyncFlag(5); return; }

                if (bytes > 8)
                {
                    bytesTotal += bytes - 8;
                    var reader = new UsnRecordReader(buffer.AsSpan(8, bytes - 8));
                    while (reader.TryNext(out UsnRecord rec))
                    {
                        records++;
                        events += Handle(in rec);
                        if (_rootDisplaced) break;
                    }
                }

                if (_rootDisplaced)
                {
                    // The root itself moved mid-drain: later records would stat dead paths into
                    // spurious removals. Abort WITHOUT committing and discard in-memory mutations;
                    // the tick guard idles until the path returns, then the cursor replays it all.
                    _rootDisplaced = false;
                    RollbackToPersisted();
                    return;
                }

                // bytes == 8 ⇒ caught up, but next may STILL be ahead of cursor (mask-filtered
                // records are skipped kernel-side and the next-USN advances past them) — take it.
                if (bytes <= 8) { cursor = Math.Max(cursor, next); break; }
                if (next <= cursor) { _errors.Log($"drain\t{_root.Id}\tnon-monotonic next USN {next} ≤ {cursor} — stopping"); break; }
                cursor = next;
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }

        if (cursor != startCursor || _snapDirty || _dirMap!.Dirty)
            Commit(cursor);

        Drains++; Records += records; Events += events;
        _stats.Tick("drain", _root.Id, sw.Elapsed, records, bytesTotal, events, 0, before, Telemetry.Sample());
    }

    void Commit(long newCursor)
    {
        if (_disposed) return;
        _events.Flush(toDisk: true);            // events durable BEFORE any state advances
        if (_events.TakeFault() is Exception f)
        {
            // Holding the cursor back is not enough on its own: the in-memory snapshot/dirmap
            // already advanced, and the replay's stat-compare would dedupe the unwritten events
            // away. Roll the in-memory state back to what's on disk so the replay regenerates them.
            _errors.Log($"events\t{_root.Id}\tchange log write failed — cursor NOT advanced, state rolled back, range will replay", f);
            RollbackToPersisted();
            return;
        }
        if (_snapDirty) { _snapshot!.Save(SnapshotPath); _snapDirty = false; }
        if (_dirMap!.Dirty) _dirMap.Save(_root.Id);
        _cursor!.NextUsn = newCursor;
        CursorStore.Save(_root.Id, _cursor);    // cursor LAST — a crash anywhere earlier replays
    }

    /// <summary>Discard in-memory snapshot/dirmap mutations and reload the last committed state.
    /// An unreadable file degrades to null ⇒ gate #1 ⇒ baseline — loud and safe, never silent.</summary>
    void RollbackToPersisted()
    {
        try { _snapshot = Snapshot.Load(SnapshotPath, _errors); }
        catch { _snapshot = null; }
        _dirMap = DirMap.Load(_root.Id, _errors);
        _snapDirty = false;
    }

    // ------------------------------------------------------- record mapping

    int Handle(in UsnRecord rec)
    {
        bool parentKnown = _dirMap!.TryGetPath(rec.ParentFrn, out string parentRel);
        if (rec.IsDirectory) return HandleDirectory(in rec, parentKnown, parentRel);
        if (!parentKnown) return 0;             // outside the watched subtree
        string rel = parentRel.Length == 0 ? rec.Name.ToString() : $"{parentRel}\\{rec.Name}";
        return HandleFile(rel, rec.Reason);
    }

    int HandleFile(string rel, uint reason)
    {
        bool inSnap = _snapshot!.Entries.TryGetValue(rel, out SnapshotEntry prev);

        // Rename = remove(old name) + add(new name), v1 semantics; delete is the same removal
        // shape. Reason bits accumulate per open handle, so a record carrying RENAME_NEW_NAME —
        // including the close record, which re-carries BOTH rename bits — holds the NEW name; only
        // a record with the old-name bit alone names the path being vacated.
        bool renameOldOnly = (reason & ReasonRenameOldName) != 0 && (reason & ReasonRenameNewName) == 0;
        if ((reason & ReasonFileDelete) != 0 || renameOldOnly)
        {
            if (!inSnap) return 0;
            Emit(new ChangeEvent(Now, _root.Id, ChangeType.Removed, rel, PrevSize: prev.Size, PrevMtimeUtc: prev.MtimeUtc));
            _snapshot.Entries.Remove(rel);
            _snapDirty = true;
            return 1;
        }

        // Creation, rename-in, or any modification: reconcile against the live file. The stat-and-
        // compare is also the dedupe — reason bits recur across a handle's records and again on the
        // close record, but an unchanged (size, mtime, attrs) triple emits nothing. It also covers
        // the accumulate-per-handle blind spot: writes continuing into an already-flagged handle
        // journal nothing new until close, and the close record's stat picks up the final state.
        if (TryStat(Path.Join(_fullPath, rel), out SnapshotEntry nowE))
        {
            if (!inSnap)
            {
                Emit(new ChangeEvent(Now, _root.Id, ChangeType.Added, rel, nowE.Size, nowE.MtimeUtc));
                _snapshot.Entries[rel] = nowE;
                _snapDirty = true;
                return 1;
            }
            if (nowE != prev)
            {
                Emit(new ChangeEvent(Now, _root.Id, ChangeType.Modified, rel, nowE.Size, nowE.MtimeUtc, prev.Size, prev.MtimeUtc));
                _snapshot.Entries[rel] = nowE;
                _snapDirty = true;
                return 1;
            }
            return 0;
        }

        if (inSnap) // vanished between the record and the stat; the delete record may replay harmlessly
        {
            Emit(new ChangeEvent(Now, _root.Id, ChangeType.Removed, rel, PrevSize: prev.Size, PrevMtimeUtc: prev.MtimeUtc));
            _snapshot.Entries.Remove(rel);
            _snapDirty = true;
            return 1;
        }
        return 0;
    }

    int HandleDirectory(in UsnRecord rec, bool parentKnown, string parentRel)
    {
        bool known = _dirMap!.TryGetPath(rec.Frn, out string oldRel);
        uint reason = rec.Reason;

        if (known && oldRel.Length == 0 && (reason & (ReasonFileDelete | ReasonRenameOldName | ReasonRenameNewName)) != 0)
        {
            // The watched root ITSELF was renamed, moved or deleted. Never flood removals and never
            // drop the map — flag it so the drain aborts uncommitted; the tick guard idles until the
            // configured path exists again, and the intact cursor then replays the gap (FRNs are
            // path-independent, so the map stays valid throughout).
            _rootDisplaced = true;
            _errors.Log($"journal\t{_root.Id}\twatched root renamed/moved/deleted — drain aborted, state kept");
            return 0;
        }

        if ((reason & ReasonFileDelete) != 0)
            return known ? RemoveSubtree(oldRel, alsoSelf: rec.Frn) : 0;

        if ((reason & ReasonRenameNewName) != 0)
        {
            if (parentKnown)
            {
                string newRel = parentRel.Length == 0 ? rec.Name.ToString() : $"{parentRel}\\{rec.Name}";
                if (known)
                    return newRel.Equals(oldRel, StringComparison.OrdinalIgnoreCase) ? 0 : RemapSubtree(oldRel, newRel);
                return MoveInSubtree(rec.Frn, newRel);      // moved INTO the tree — enumerate it now
            }
            if (known) return RemoveSubtree(oldRel, alsoSelf: rec.Frn); // moved OUT of the tree
            return 0;
        }

        if ((reason & ReasonFileCreate) != 0 && parentKnown && !known)
        {
            // New empty directory; its future children journal their own records in USN order.
            _dirMap.Set(rec.Frn, parentRel.Length == 0 ? rec.Name.ToString() : $"{parentRel}\\{rec.Name}");
        }
        // RENAME_OLD_NAME alone: keep the old mapping until the NEW record arrives (maybe next drain).
        return 0;
    }

    /// <summary>Directory rename/move within the tree: a single dir record, but every descendant
    /// file's path changed. v1 remove+add per file (metadata untouched — the content didn't move).</summary>
    int RemapSubtree(string oldRel, string newRel)
    {
        int emitted = 0;
        List<(string Old, SnapshotEntry E)> moved = [];
        foreach ((string p, SnapshotEntry e) in _snapshot!.Entries)
            if (DirMap.IsUnder(p, oldRel)) moved.Add((p, e));
        foreach ((string p, SnapshotEntry e) in moved)
        {
            string np = string.Concat(newRel, p.AsSpan(oldRel.Length));
            Emit(new ChangeEvent(Now, _root.Id, ChangeType.Removed, p, PrevSize: e.Size, PrevMtimeUtc: e.MtimeUtc));
            Emit(new ChangeEvent(Now, _root.Id, ChangeType.Added, np, e.Size, e.MtimeUtc));
            _snapshot.Entries.Remove(p);
            _snapshot.Entries[np] = e;
            emitted += 2;
        }
        if (moved.Count > 0) _snapDirty = true;
        _dirMap!.RemapSubtree(oldRel, newRel);
        return emitted;
    }

    int RemoveSubtree(string rel, UInt128 alsoSelf)
    {
        int emitted = 0;
        List<string> gone = [];
        foreach ((string p, _) in _snapshot!.Entries)
            if (DirMap.IsUnder(p, rel)) gone.Add(p);
        foreach (string p in gone)
        {
            SnapshotEntry e = _snapshot.Entries[p];
            Emit(new ChangeEvent(Now, _root.Id, ChangeType.Removed, p, PrevSize: e.Size, PrevMtimeUtc: e.MtimeUtc));
            _snapshot.Entries.Remove(p);
            emitted++;
        }
        if (gone.Count > 0) _snapDirty = true;
        foreach (UInt128 frn in _dirMap!.CollectSubtree(rel)) _dirMap.Remove(frn);
        _dirMap.Remove(alsoSelf);
        return emitted;
    }

    int MoveInSubtree(UInt128 frn, string rel)
    {
        _dirMap!.Set(frn, rel);
        string full = Path.Join(_fullPath, rel);
        int emitted = 0;
        foreach (FileEntry fe in new Enumerator(full, _errors).Walk())
        {
            string p = $"{rel}\\{fe.RelPath}";
            if (!_snapshot!.Entries.ContainsKey(p))
            {
                Emit(new ChangeEvent(Now, _root.Id, ChangeType.Added, p, fe.Size, fe.MtimeUtc));
                emitted++;
            }
            _snapshot!.Entries[p] = new SnapshotEntry(fe.Size, fe.MtimeUtc, fe.Attrs);
            _snapDirty = true;
        }
        BuildDirMapUnder(_dirMap, full);
        return emitted;
    }

    void Emit(in ChangeEvent e) => _events.Write(in e);

    static bool TryStat(string fullPath, out SnapshotEntry entry)
    {
        entry = default;
        try
        {
            var fi = new FileInfo(fullPath);
            if (!fi.Exists) return false; // also false when the path is (now) a directory
            entry = new SnapshotEntry(fi.Length, fi.LastWriteTimeUtc, (int)fi.Attributes);
            return true;
        }
        catch { return false; }
    }

    /// <summary>FRNs for every directory under <paramref name="fullPath"/> — the subtree-scoped
    /// alternative to a whole-volume FSCTL_ENUM_USN_DATA pass. One attribute-only handle per
    /// directory; directories ≪ files, so this rides along with the scan for ~free.</summary>
    void BuildDirMapUnder(DirMap map, string fullPath)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(fullPath, "*", opts))
            {
                if (TryGetFileId(dir, out UInt128 id)) map.Set(id, Path.GetRelativePath(_fullPath, dir));
                else _errors.Log($"dirmap\t{_root.Id}\tno file id for {dir} — its events will not resolve");
            }
        }
        catch (Exception ex) { _errors.Log($"dirmap-walk\t{_root.Id}\t{fullPath}", ex); }
    }

    // ------------------------------------------------- resync / degraded poll

    /// <summary>Rebuild from a scan and resume the journal from the PRE-scan USN. Capturing NextUsn
    /// before enumerating is the correctness-critical half: mid-scan changes get re-seen via replay
    /// (duplicate events are fine — at-least-once); capturing after would lose them forever.</summary>
    void Resync(int trigger, USN_JOURNAL_DATA_V2 jd)
    {
        TelemetrySample before = Telemetry.Sample();
        var sw = Stopwatch.StartNew();

        if (!Directory.Exists(_fullPath))
        {
            // Same rule as the poller: an unreachable root reads as "unknown", never as "every
            // file was deleted". The persisted flag keeps the obligation; next tick retries.
            _errors.Log($"resync\t{_root.Id}\troot unreachable — flag kept (no removal flood), will retry");
            return;
        }

        long preUsn = jd.NextUsn;   // BEFORE the scan (jd was queried before this task even started)

        var en = new Enumerator(_fullPath, _errors);
        var curr = Snapshot.FromEntries(en.Walk());
        if (en.FailedRelDirs.Contains("."))
        {
            _errors.Log($"resync\t{_root.Id}\troot enumeration failed — flag kept, will retry");
            return;
        }
        if (_snapshot is not null && en.FailedRelDirs.Count > 0)
        {
            int kept = Differ.PreserveFailedSubtrees(_snapshot.Entries, curr.Entries, en.FailedRelDirs);
            _errors.Log($"resync\t{_root.Id}\t{en.FailedRelDirs.Count} unreadable director(y/ies) — {kept} prior entries preserved, no removals emitted for them");
        }

        var map = new DirMap();
        if (TryGetFileId(_fullPath, out UInt128 rootFrn)) map.Set(rootFrn, "");
        else _errors.Log($"resync\t{_root.Id}\tcannot get root file id — journal events will not resolve");
        BuildDirMapUnder(map, _fullPath);

        int eventCount = 0;
        if (_snapshot is null)
        {
            // Decided with Yanis: the log self-documents where coverage starts — one line, no flood.
            Emit(new ChangeEvent(Now, _root.Id, ChangeType.Baseline, null, Files: curr.Entries.Count));
            eventCount = 1;
        }
        else
        {
            foreach (ChangeEvent e in Differ.Diff(_snapshot.Entries, curr.Entries, _root.Id, Now))
            {
                Emit(in e);
                eventCount++;
            }
        }

        _events.Flush(toDisk: true);
        if (_events.TakeFault() is Exception f)
        {
            _errors.Log($"resync\t{_root.Id}\tchange log write failed — flag kept, will retry", f);
            return;                 // ResyncTrigger still persisted ⇒ the whole resync reruns
        }
        if (_disposed)
        {
            _errors.Log($"resync\t{_root.Id}\tshutdown during resync — flag kept, will rerun");
            return;
        }

        curr.Save(SnapshotPath);
        _snapshot = curr;
        _snapDirty = false;
        map.Save(_root.Id);
        _dirMap = map;

        _cursor = new CursorState { Volume = _volume, JournalId = jd.UsnJournalID, NextUsn = preUsn, ResyncTrigger = 0 };
        CursorStore.Save(_root.Id, _cursor);

        if (trigger == 1) Baselines++; else Resyncs++;
        Events += eventCount;
        _stats.Tick(trigger == 1 ? "baseline" : "resync", _root.Id, sw.Elapsed, 0, 0, eventCount, trigger, before, Telemetry.Sample());
    }

    /// <summary>Snapshot-diff polling while the journal cannot be used (not elevated, non-NTFS
    /// volume, journal mid-manipulation). The cursor is deliberately untouched: a pending resync
    /// flag still forces a true resync — which re-establishes a valid (journalId, USN) pair — the
    /// moment the journal is usable again.</summary>
    void DegradedPollIfDue()
    {
        if (Now < _nextDegradedPoll) return;
        _nextDegradedPoll = Now + Jitter(TimeSpan.FromMinutes(Math.Max(1, _root.PollMinutes)));
        StartBusy(DegradedPoll);
    }

    void DegradedPoll()
    {
        TelemetrySample before = Telemetry.Sample();
        var sw = Stopwatch.StartNew();

        if (!Directory.Exists(_fullPath))
        {
            _errors.Log($"poll\t{_root.Id}\troot unreachable — skipped (never a mass-removal flood)");
            return;
        }

        var en = new Enumerator(_fullPath, _errors);
        var curr = Snapshot.FromEntries(en.Walk());
        if (en.FailedRelDirs.Contains("."))
        {
            _errors.Log($"poll\t{_root.Id}\troot enumeration failed — skipped");
            return;
        }
        bool wasBaseline = _snapshot is null;
        if (!wasBaseline && en.FailedRelDirs.Count > 0)
        {
            int kept = Differ.PreserveFailedSubtrees(_snapshot!.Entries, curr.Entries, en.FailedRelDirs);
            _errors.Log($"poll\t{_root.Id}\t{en.FailedRelDirs.Count} unreadable director(y/ies) — {kept} prior entries preserved, no removals emitted for them");
        }
        int eventCount = 0;
        if (wasBaseline)
        {
            Emit(new ChangeEvent(Now, _root.Id, ChangeType.Baseline, null, Files: curr.Entries.Count));
            eventCount = 1;
        }
        else
        {
            foreach (ChangeEvent e in Differ.Diff(_snapshot!.Entries, curr.Entries, _root.Id, Now))
            {
                Emit(in e);
                eventCount++;
            }
        }

        _events.Flush(toDisk: true);
        if (_events.TakeFault() is Exception f)
        {
            _errors.Log($"poll\t{_root.Id}\tchange log write failed — snapshot NOT advanced", f);
            return;
        }
        if (_disposed) return;

        curr.Save(SnapshotPath);
        _snapshot = curr;
        _snapDirty = false;

        if (wasBaseline) Baselines++; else Polls++;
        Events += eventCount;
        _stats.Tick(wasBaseline ? "baseline" : "poll", _root.Id, sw.Elapsed, 0, 0, eventCount, wasBaseline ? 1 : 0, before, Telemetry.Sample());
    }

    static TimeSpan Jitter(TimeSpan t) => t * (0.9 + 0.2 * Random.Shared.NextDouble());

    public void Dispose()
    {
        // A killed resync is safe by design: the flag is on disk, the busy task checks _disposed
        // before its commit phase, and a Write on the disposed sink faults instead of vanishing —
        // so an overrun task can no longer commit a clean cursor over unlogged events. The bounded
        // wait just avoids the race in the common case (host shutdown budget is ~30 s total).
        _disposed = true;
        try { _busyTask?.Wait(TimeSpan.FromSeconds(5)); } catch { /* surfaced via its own catch */ }
        _events.Dispose();
        _journal?.Dispose();
        if (_events.Fault is Exception f) _errors.Log($"events\t{_root.Id}\tfault at dispose", f);
    }
}
