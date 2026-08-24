using System.Diagnostics;

namespace Argus;

/// <summary>Watches one UNC root by scheduled snapshot-diff polls — FSCTL_READ_USN_JOURNAL is
/// documented as unsupported over SMB, so for network roots the poller IS the design, not a
/// fallback. Cadence is tens of minutes with ±10% jitter (Mainsrv-scale trees take minutes to walk;
/// never poll a share once a minute); an overlapping tick is skipped; a scan that eats more than
/// ~25% of the interval widens the effective interval and says so.</summary>
internal sealed class Poller : IDisposable
{
    readonly RootConfig _root;
    readonly string _fullPath;
    readonly ErrorLog _errors;
    readonly StatsSink _stats;
    readonly EventSink _events;
    readonly IgnoreRules _ignore;

    Snapshot? _snapshot;
    volatile bool _busy;
    volatile bool _disposed;
    Task? _busyTask;
    DateTimeOffset _nextDue = DateTimeOffset.MinValue; // first poll immediately: baseline or catch-up

    internal long Polls, Baselines, Events;

    internal Poller(RootConfig root, ErrorLog errors, StatsSink stats, IgnoreRules? ignore = null)
    {
        _ignore = ignore ?? IgnoreRules.None;
        _root = root;
        _fullPath = Path.TrimEndingDirectorySeparator(root.Path);
        _errors = errors;
        _stats = stats;
        _events = new EventSink(root.Id);
        _snapshot = Snapshot.Load(SnapshotPath, errors);

        // Converge loaded state with the rules at once (see JournalWatcher.PurgeIgnored): silent,
        // because those paths left the watch by configuration, not by being deleted.
        if (_ignore.Any && _snapshot is not null)
        {
            List<string> drop = [];
            foreach ((string p, _) in _snapshot.Entries)
                if (_ignore.HasIgnoredDir(p, pathIsDirectory: false)) drop.Add(p);
            foreach (string p in drop) _snapshot.Entries.Remove(p);
        }
    }

    string SnapshotPath => Path.Combine(StateDir, _root.Id + ".snapshot.jsonl");

    internal void Tick()
    {
        if (_busy || Now < _nextDue) return;
        _busy = true;
        _busyTask = Task.Run(() =>
        {
            try { Poll(); }
            catch (Exception ex)
            {
                _errors.Log($"poll\t{_root.Id}", ex);
                // Reschedule even on failure — a stale _nextDue would re-walk the share every tick.
                _nextDue = Now + Jitter(TimeSpan.FromMinutes(Math.Max(1, _root.PollMinutes)));
            }
            finally { _busy = false; }
        });
    }

    void Poll()
    {
        TelemetrySample before = Telemetry.Sample();
        var sw = Stopwatch.StartNew();
        TimeSpan interval = TimeSpan.FromMinutes(Math.Max(1, _root.PollMinutes));

        if (!Directory.Exists(_fullPath))
        {
            // A dead share must read as "unknown", never as "everything was deleted".
            _errors.Log($"poll\t{_root.Id}\troot unreachable — skipped (never a mass-removal flood)");
            _nextDue = Now + Jitter(interval);
            return;
        }

        var en = new Enumerator(_fullPath, _errors, _ignore);
        var curr = Snapshot.FromEntries(en.Walk());
        if (en.FailedRelDirs.Contains("."))
        {
            _errors.Log($"poll\t{_root.Id}\troot enumeration failed — skipped (never a mass-removal flood)");
            _nextDue = Now + Jitter(interval);
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
            _events.Write(new ChangeEvent(Now, _root.Id, ChangeType.Baseline, null, Files: curr.Entries.Count));
            eventCount = 1;
        }
        else
        {
            foreach (ChangeEvent e in Differ.Diff(_snapshot!.Entries, curr.Entries, _root.Id, Now))
            {
                _events.Write(in e);
                eventCount++;
            }
        }

        _events.Flush(toDisk: true);
        if (_events.TakeFault() is Exception f)
        {
            _errors.Log($"poll\t{_root.Id}\tchange log write failed — snapshot NOT advanced, changes re-report next poll", f);
            _nextDue = Now + Jitter(interval);
            return;
        }
        if (_disposed) return; // shutdown mid-poll: don't advance state past a disposed sink

        curr.Save(SnapshotPath);
        _snapshot = curr;

        TimeSpan effective = interval;
        if (sw.Elapsed > interval / 4)
        {
            effective = TimeSpan.FromTicks(Math.Max(interval.Ticks, sw.Elapsed.Ticks * 4));
            _errors.Log($"poll\t{_root.Id}\tscan took {Humanize.Duration(sw.Elapsed)} (>25% of {Humanize.Duration(interval)}) — backing off to {Humanize.Duration(effective)}");
        }
        _nextDue = Now + Jitter(effective);

        if (wasBaseline) Baselines++; else Polls++;
        Events += eventCount;
        _stats.Tick(wasBaseline ? "baseline" : "poll", _root.Id, sw.Elapsed, 0, 0, eventCount, wasBaseline ? 1 : 0, before, Telemetry.Sample());
    }

    static TimeSpan Jitter(TimeSpan t) => t * (0.9 + 0.2 * Random.Shared.NextDouble());

    public void Dispose()
    {
        _disposed = true;
        try { _busyTask?.Wait(TimeSpan.FromSeconds(5)); } catch { /* surfaced via its own catch */ }
        _events.Dispose();
        if (_events.Fault is Exception f) _errors.Log($"events\t{_root.Id}\tfault at dispose", f);
    }
}
