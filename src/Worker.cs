using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace Argus;

/// <summary>The tick loop. Journal watchers tick inline (validation + drain are milliseconds);
/// pollers and resyncs dispatch their own background scans. Config is reread every tick, so the
/// interval, telemetry mode and root list all adjust live — a broken edit is logged and the last
/// good config keeps running.</summary>
internal sealed class Worker : BackgroundService
{
    readonly ErrorLog _errors = new(ErrorLogPath);

    ArgusConfig _config = new();
    DateTime _configStamp;
    bool _configMissingLogged;

    StatsSink? _stats;
    string _telemetryMode = "";

    readonly Dictionary<string, JournalWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Poller> _pollers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _rootKeys = new(StringComparer.OrdinalIgnoreCase); // id → config identity
    readonly Dictionary<string, DateTimeOffset> _retryAt = new(StringComparer.OrdinalIgnoreCase); // failed roots → next attempt

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        DateTimeOffset started = Now;
        Directory.CreateDirectory(ArgusDir);
        Directory.CreateDirectory(StateDir);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                ReloadConfig();
                Apply(_config); // every tick: cheap for unchanged roots, and it retries failed ones
                foreach (JournalWatcher w in _watchers.Values) w.Tick();
                foreach (Poller p in _pollers.Values) p.Tick();
            }
            catch (Exception ex) { _errors.Log("worker-tick", ex); }

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_config.TickSeconds, 1, 3600)), ct); }
            catch (OperationCanceledException) { break; }
        }

        Shutdown(started);
    }

    void Shutdown(DateTimeOffset started)
    {
        long drains = 0, polls = 0, baselines = 0, resyncs = 0, records = 0, events = 0;
        foreach (JournalWatcher w in _watchers.Values)
        {
            drains += w.Drains; polls += w.Polls; baselines += w.Baselines;
            resyncs += w.Resyncs; records += w.Records; events += w.Events;
            w.Dispose();
        }
        foreach (Poller p in _pollers.Values)
        {
            polls += p.Polls; baselines += p.Baselines; events += p.Events;
            p.Dispose();
        }
        _stats?.Summary(Now - started, drains, polls, baselines, resyncs, records, events, Telemetry.Sample());
        _stats?.Dispose();
        _errors.Dispose();
    }

    void ReloadConfig()
    {
        if (!File.Exists(ConfigPath))
        {
            if (!_configMissingLogged)
            {
                _configMissingLogged = true;
                _errors.Log($"config\tmissing {ConfigPath} — run 'argus init'; watching nothing until it exists");
            }
            return;
        }
        _configMissingLogged = false;

        DateTime stamp = File.GetLastWriteTimeUtc(ConfigPath);
        if (stamp == _configStamp) return;

        ArgusConfig cfg;
        try
        {
            cfg = JsonSerializer.Deserialize(File.ReadAllBytes(ConfigPath), JsonCtx.Default.ArgusConfig)
                ?? throw new InvalidDataException("config parsed to null");
        }
        catch (Exception ex)
        {
            _errors.Log("config\tparse failed — keeping previous config", ex);
            _configStamp = stamp; // don't re-log every tick for the same broken save
            return;
        }
        _configStamp = stamp;
        _config = cfg; // applied (and failed roots retried) by the per-tick Apply call
    }

    void Apply(ArgusConfig cfg)
    {
        // Telemetry mode is baked into the sink, and the sink reference is baked into every watcher:
        // a mode change rebuilds everything. Cheap (all state is on disk) and only happens on edits.
        if (_stats is null || _telemetryMode != cfg.Telemetry)
        {
            foreach (JournalWatcher w in _watchers.Values) w.Dispose();
            foreach (Poller p in _pollers.Values) p.Dispose();
            _watchers.Clear();
            _pollers.Clear();
            _rootKeys.Clear();
            _stats?.Dispose();
            _stats = new StatsSink(cfg.Telemetry, _errors);
            _telemetryMode = cfg.Telemetry;
        }

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RootConfig r in cfg.Roots)
        {
            if (string.IsNullOrWhiteSpace(r.Id) || string.IsNullOrWhiteSpace(r.Path))
            { _errors.Log("config\troot with empty id/path skipped"); continue; }
            if (!r.IsUnc && !Path.IsPathRooted(r.Path))
            { _errors.Log($"config\troot '{r.Id}': path must be absolute (service cwd is system32), got '{r.Path}' — skipped"); continue; }
            if (!wanted.Add(r.Id))
            { _errors.Log($"config\tduplicate root id '{r.Id}' — later entry skipped"); continue; }

            string key = $"{r.Path}|{r.PollMinutes}";
            if (_rootKeys.TryGetValue(r.Id, out string? oldKey) && oldKey == key) continue;
            // Failed construction (e.g. transiently unreadable snapshot) retries with backoff —
            // not only on the next config edit, which might never come.
            if (_retryAt.TryGetValue(r.Id, out DateTimeOffset at) && Now < at) continue;

            if (_watchers.Remove(r.Id, out JournalWatcher? ow)) ow.Dispose();
            if (_pollers.Remove(r.Id, out Poller? op)) op.Dispose();
            try
            {
                if (r.IsUnc) _pollers[r.Id] = new Poller(r, _errors, _stats!);
                else _watchers[r.Id] = new JournalWatcher(r, _errors, _stats!);
                _rootKeys[r.Id] = key;
                _retryAt.Remove(r.Id);
            }
            catch (Exception ex)
            {
                _errors.Log($"config\troot '{r.Id}' rejected — retrying in 60s", ex);
                _rootKeys.Remove(r.Id);
                _retryAt[r.Id] = Now + TimeSpan.FromSeconds(60);
            }
        }

        foreach (string id in _watchers.Keys.Where(k => !wanted.Contains(k)).ToList())
        { _watchers.Remove(id, out JournalWatcher? w); w!.Dispose(); _rootKeys.Remove(id); }
        foreach (string id in _pollers.Keys.Where(k => !wanted.Contains(k)).ToList())
        { _pollers.Remove(id, out Poller? p); p!.Dispose(); _rootKeys.Remove(id); }
    }
}
