using System.Buffers;
using System.Text.Json;

namespace Argus;

/// <summary>The resource-telemetry stream: one compact JSON object per line appended to
/// <c>$GLOBAL_DATA_ROOT\argus\stats-YYYY-MM.jsonl</c>, rotated monthly (UTC), plus one
/// <c>kind:"summary"</c> line when the service stops.
///
/// A tick line looks like:
///   {"ts":"2026-08-15T10:00:03.1234567+00:00","kind":"drain","root":"docs","elapsedMs":12.4,
///    "records":37,"journalBytes":3700,"events":4,"cpuMs":8.1,"ws":31457280,"priv":22020096,
///    "alloc":65536,"gc0":0,"gc1":0,"gc2":0,"ioR":4096,"ioW":512,"handles":204,"threads":17}
///
/// <c>trigger</c> is omitted when 0; on resync/baseline lines it carries WHICH validation gate
/// (#1–#5) demanded the poll — the alpha metric behind "the poller almost never runs".
///
/// Unlike Apographe's manifest writer this type IS thread-safe (the tick thread and the per-root
/// poller threads all write here) and it flushes to the OS after every line: the stream is
/// low-volume, so durability across a crash beats buffering. A write failure is logged and
/// swallowed — telemetry must never take the watcher down.</summary>
public sealed class StatsSink : IDisposable
{
    enum Level { Off, Summary, Full }

    readonly object _gate = new();
    readonly ErrorLog _errors;
    readonly Level _level;
    readonly ArrayBufferWriter<byte> _buf = new(512);
    readonly Utf8JsonWriter _json;
    // Last line kept per root, so summary mode can still emit a periodic heartbeat. Under _gate.
    readonly Dictionary<string, DateTimeOffset> _lastKept = new(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan HeartbeatEvery = TimeSpan.FromHours(1);
    FileStream? _file;
    string _month = "";   // yyyy-MM of the currently open file
    bool _disposed;

    public StatsSink(string mode, ErrorLog errors)
    {
        _errors = errors;
        _level = mode?.Trim().ToLowerInvariant() switch
        {
            "off" => Level.Off,
            "summary" => Level.Summary,
            "full" => Level.Full,
            _ => Unknown(mode, errors),
        };
        _json = new Utf8JsonWriter(_buf, Jsonl.WriterOptions);
    }

    // A typo in the config must not silently blind the alpha run: keep everything, say so once.
    static Level Unknown(string? mode, ErrorLog errors)
    {
        errors.Log($"StatsSink\tunknown telemetry mode '{mode}' — using full");
        return Level.Full;
    }

    /// <summary>One line per unit of work: kind ∈ drain|poll|baseline|resync, <paramref name="trigger"/>
    /// = the validation gate (#1–#5) that forced a resync/baseline, 0 when none.</summary>
    public void Tick(
        string kind, string root, TimeSpan elapsed, long records, long journalBytes, long events,
        int trigger, in TelemetrySample before, in TelemetrySample after)
    {
        if (_level == Level.Off) return;

        DateTimeOffset ts = Now;
        lock (_gate)
        {
            if (_disposed) return;

            // Summary mode keeps every line that carries information — any events at all, and every
            // poll/baseline/resync — and drops the event-less drains, which on a live volume is
            // nearly all of them. It deliberately does NOT test `records`: that counts volume-wide
            // journal records passing the kernel reason mask, NOT records belonging to this root, so
            // on any busy machine it is never zero (measured on C: 2026-08-24: 0 of 87,672 drains
            // had records == 0 — the earlier records-based filter dropped nothing whatsoever, and
            // summary mode cost exactly as much as full). One heartbeat per root per hour still
            // lands, so CPU/memory/handle trends stay visible through quiet stretches.
            if (_level == Level.Summary && kind == "drain" && events == 0 &&
                _lastKept.TryGetValue(root, out DateTimeOffset last) && ts - last < HeartbeatEvery)
                return;
            _lastKept[root] = ts;
            _buf.Clear();
            _json.Reset();

            _json.WriteStartObject();
            _json.WriteString("ts"u8, ts.UtcDateTime); // "O" with the Z suffix, matching the change log
            _json.WriteString("kind"u8, kind);
            _json.WriteString("root"u8, root);
            _json.WriteNumber("elapsedMs"u8, Math.Round(elapsed.TotalMilliseconds, 1));
            _json.WriteNumber("records"u8, records);
            _json.WriteNumber("journalBytes"u8, journalBytes);
            _json.WriteNumber("events"u8, events);
            if (trigger != 0) _json.WriteNumber("trigger"u8, trigger);
            _json.WriteNumber("cpuMs"u8, Math.Round(after.CpuMs - before.CpuMs, 1));
            _json.WriteNumber("ws"u8, after.WorkingSet);
            _json.WriteNumber("priv"u8, after.PrivateBytes);
            _json.WriteNumber("alloc"u8, after.AllocatedBytes - before.AllocatedBytes);
            _json.WriteNumber("gc0"u8, after.Gc0 - before.Gc0);
            _json.WriteNumber("gc1"u8, after.Gc1 - before.Gc1);
            _json.WriteNumber("gc2"u8, after.Gc2 - before.Gc2);
            _json.WriteNumber("ioR"u8, Delta(before.IoReadBytes, after.IoReadBytes));
            _json.WriteNumber("ioW"u8, Delta(before.IoWriteBytes, after.IoWriteBytes));
            _json.WriteNumber("handles"u8, after.Handles);
            _json.WriteNumber("threads"u8, after.Threads);
            _json.WriteEndObject();

            Emit(ts);
        }
    }

    /// <summary>The closing line of a run: absolute totals, not deltas. Written in full and summary
    /// modes — it is the one line an operator reads after a service stop.</summary>
    public void Summary(
        TimeSpan uptime, long drains, long polls, long baselines, long resyncs,
        long totalRecords, long totalEvents, in TelemetrySample last)
    {
        if (_level == Level.Off) return;

        DateTimeOffset ts = Now;
        lock (_gate)
        {
            if (_disposed) return;
            _buf.Clear();
            _json.Reset();

            _json.WriteStartObject();
            _json.WriteString("ts"u8, ts.UtcDateTime);
            _json.WriteString("kind"u8, "summary");
            _json.WriteNumber("uptimeSec"u8, (long)Math.Round(uptime.TotalSeconds));
            _json.WriteNumber("drains"u8, drains);
            _json.WriteNumber("polls"u8, polls);
            _json.WriteNumber("baselines"u8, baselines);
            _json.WriteNumber("resyncs"u8, resyncs);
            _json.WriteNumber("records"u8, totalRecords);
            _json.WriteNumber("events"u8, totalEvents);
            _json.WriteNumber("cpuMs"u8, Math.Round(last.CpuMs, 1));
            _json.WriteNumber("ws"u8, last.WorkingSet);
            _json.WriteNumber("priv"u8, last.PrivateBytes);
            _json.WriteNumber("handles"u8, last.Handles);
            _json.WriteNumber("threads"u8, last.Threads);
            _json.WriteEndObject();

            Emit(ts);
        }
    }

    // The kernel counters are monotonic per process, but clamp anyway: a negative-looking delta
    // would wrap to a nonsense 64-bit number in the log rather than reading as "nothing happened".
    static ulong Delta(ulong before, ulong after) => after > before ? after - before : 0UL;

    // Caller holds _gate.
    void Emit(DateTimeOffset ts)
    {
        _json.Flush();
        try
        {
            _buf.GetSpan(1)[0] = (byte)'\n'; // same buffer ⇒ one write ⇒ no torn line on a fault
            _buf.Advance(1);
            FileStream f = Open(ts);
            f.Write(_buf.WrittenSpan);
            f.Flush(); // to the OS after every line: low volume, so durability wins over buffering
        }
        catch (Exception ex)
        {
            _errors.Log("StatsSink", ex);
            Close(); // drop the handle so the next line reopens (disk filled, path went away, ...)
        }
    }

    // Caller holds _gate. Monthly rotation keyed on the UTC month of the line being written, so the
    // first line of a new month closes the old file and opens the new one — no scheduled rollover.
    FileStream Open(DateTimeOffset ts)
    {
        string month = ts.UtcDateTime.ToString("yyyy-MM");
        if (_file is not null && month == _month) return _file;

        Close();
        Directory.CreateDirectory(ArgusDir);
        _file = new FileStream(Path.Combine(ArgusDir, $"stats-{month}.jsonl"), new FileStreamOptions
        {
            Mode = FileMode.Append,   // a service restart must not truncate the month's history
            Access = FileAccess.Write,
            Share = FileShare.Read,   // tail it while the service runs
            BufferSize = 4096,
        });
        _month = month;
        return _file;
    }

    // Caller holds _gate.
    void Close()
    {
        try { _file?.Flush(); _file?.Dispose(); } catch { /* telemetry must never break the run */ }
        _file = null;
        _month = "";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Close();
            _json.Dispose(); // targets the in-memory buffer, so this cannot fail on disk
        }
    }
}
