using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace Argus;

/// <summary>Appends change events for one root to a monthly JSONL file under the Argus data
/// directory: "changes-{rootId}-{yyyy-MM}.jsonl", the month taken from the event's own UTC timestamp
/// (never the wall clock, so a batch that straddles midnight on the 1st still files each event under
/// the month it belongs to). Rotation is by month because that is the unit an operator asks for and
/// the unit that can be archived or deleted whole.
///
/// Opened in Append mode and never truncated: a service restart must extend the log, not replace it.
/// Records are written with a reused <see cref="Utf8JsonWriter"/> over an in-memory buffer — no
/// per-event reflection or allocation, which matters because a resync can emit one line per file.
///
/// Only fields that carry meaning for the event type are written (Added has no prev*, Removed has no
/// current size/mtime, Baseline has only a count), keeping the log both smaller and unambiguous.
///
/// This type is NOT thread-safe: it has a single owner — the watcher task for its root.</summary>
public sealed class EventSink(string rootId) : IDisposable
{
    readonly ArrayBufferWriter<byte> _buf = new(256);
    Utf8JsonWriter? _json;
    FileStream? _file;
    int _year = -1, _month = -1; // UTC year/month of the currently open file
    bool _disposed;

    /// <summary>Set if a write or flush failed (e.g. the data disk filled, the directory vanished).
    /// When non-null the change log has a hole — the caller reports it rather than letting a silently
    /// truncated log be read as "nothing changed". The first failure is kept; later ones do not
    /// overwrite the original cause, and writing continues so a transient failure self-heals.</summary>
    public Exception? Fault { get; private set; }

    /// <summary>Returns and clears the recorded fault. The commit path calls this after flushing:
    /// a fault means the change log may have a hole, so the cursor/snapshot must NOT advance — the
    /// range replays next drain (at-least-once), and clearing lets a healed disk commit again.</summary>
    public Exception? TakeFault() { Exception? f = Fault; Fault = null; return f; }

    public void Write(in ChangeEvent e)
    {
        // A write after dispose must FAULT, not vanish: a resync task outliving its watcher's
        // disposal would otherwise commit a clean cursor over events that were never logged.
        if (_disposed) { Fault ??= new ObjectDisposedException(nameof(EventSink)); return; }

        DateTime utc = e.Ts.UtcDateTime;
        try
        {
            if (_file is null || utc.Year != _year || utc.Month != _month) Open(utc);

            _buf.Clear();
            _json!.Reset();

            _json.WriteStartObject();
            _json.WriteString("ts"u8, utc); // UTC DateTime ⇒ "O" with the Z suffix, per the planned shape
            _json.WriteString("root"u8, e.Root);
            _json.WriteString("type"u8, TypeName(e.Type));

            if (e.Path is not null) _json.WriteString("path"u8, e.Path);
            if (e.Size is long size) _json.WriteNumber("size"u8, size);
            if (e.MtimeUtc is DateTime mtime) _json.WriteString("mtime"u8, mtime);
            if (e.PrevSize is long prevSize) _json.WriteNumber("prevSize"u8, prevSize);
            if (e.PrevMtimeUtc is DateTime prevMtime) _json.WriteString("prevMtime"u8, prevMtime);
            if (e.Files is int files) _json.WriteNumber("files"u8, files);

            _json.WriteEndObject();
            _json.Flush();

            // Newline goes into the same buffer so the line lands in ONE write — a fault between
            // two writes would leave a truncated line the reader cannot parse.
            _buf.GetSpan(1)[0] = (byte)'\n';
            _buf.Advance(1);
            _file!.Write(_buf.WrittenSpan);
        }
        catch (Exception ex)
        {
            Fault ??= ex;
        }
    }

    /// <summary>Pushes buffered lines out: to the OS (cheap, survives a process crash) or all the way
    /// to disk (survives a power cut). No-op when nothing is open.</summary>
    public void Flush(bool toDisk)
    {
        try { _file?.Flush(toDisk); }
        catch (Exception ex) { Fault ??= ex; }
    }

    // Lower-case invariant names, written as UTF-8 literals so the enum is never reflected over.
    static ReadOnlySpan<byte> TypeName(ChangeType type) => type switch
    {
        ChangeType.Added => "added"u8,
        ChangeType.Modified => "modified"u8,
        ChangeType.Removed => "removed"u8,
        _ => "baseline"u8,
    };

    void Open(DateTime utcMonth)
    {
        Close();
        Directory.CreateDirectory(ArgusDir);

        string name = $"changes-{rootId}-{utcMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture)}.jsonl";
        _file = new FileStream(Path.Combine(ArgusDir, name), new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.Read, // an operator tailing the log must not break the watcher
            BufferSize = 1 << 16,
        });
        _year = utcMonth.Year;
        _month = utcMonth.Month;

        // The writer targets the in-memory buffer, not the file, so it survives rotation untouched.
        _json ??= new Utf8JsonWriter(_buf, Jsonl.WriterOptions);
    }

    void Close()
    {
        if (_file is null) return;
        // To disk, not just the OS: month rotation retires this file while the cursor may go on to
        // claim its tail as delivered — the old month's lines must survive a power cut too.
        try { _file.Flush(flushToDisk: true); _file.Dispose(); }
        catch (Exception ex) { Fault ??= ex; }
        _file = null;
        _year = _month = -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Shutdown is the one moment worth paying for a real disk flush: whatever is still in the
        // OS cache is the tail of the log, and the service may be stopping because the box is.
        Flush(toDisk: true);
        Close();
        try { _json?.Dispose(); } catch (Exception ex) { Fault ??= ex; }
    }
}
