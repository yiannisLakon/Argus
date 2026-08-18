using System.Text;

namespace Argus;

/// <summary>Thread-safe, append-only error log (operator-facing English). Opened lazily on first
/// error in append mode — a service restart must not truncate history, and a clean run creates
/// nothing. Writing never throws: a failed log write must never take the watcher down.</summary>
public sealed class ErrorLog(string path) : IDisposable
{
    readonly object _gate = new();
    readonly string _path = path;
    StreamWriter? _writer;
    bool _opened;
    long _count;

    public long Count => Interlocked.Read(ref _count);
    public string Path => _path;

    public void Log(string context, Exception ex)
        => Log($"{context}\t{ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}");

    public void Log(string message)
    {
        Interlocked.Increment(ref _count);
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}\t{message}";
        lock (_gate)
        {
            EnsureOpen();
            try { _writer?.WriteLine(line); _writer?.Flush(); } catch { /* logging must never break the run */ }
        }
    }

    // Caller holds _gate.
    void EnsureOpen()
    {
        if (_opened) return;
        _opened = true;
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _writer = new StreamWriter(
                new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = false };
        }
        catch { _writer = null; }
    }

    public void Dispose() { lock (_gate) { try { _writer?.Flush(); _writer?.Dispose(); } catch { } } }
}
