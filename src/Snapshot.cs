using System.Buffers;
using System.Text.Json;

namespace Argus;

/// <summary>The last known state of one root: relative path → size/mtime/attrs. Persisted as JSONL
/// (one compact object per line) so a partially-written file costs at most its tail rather than the
/// whole snapshot, and so a multi-hundred-thousand-file root can be read line by line.
///
/// Keys are compared OrdinalIgnoreCase: NTFS and SMB are case-preserving but case-insensitive, so a
/// rename that only flips case is the same file, not a delete plus an add.
///
/// Saving is atomic — the whole file is written to "<name>.tmp", forced to disk, and only then moved
/// over the live snapshot. A crash mid-save therefore leaves the PREVIOUS snapshot intact; the worst
/// case is one poll's worth of changes reported again, never a truncated snapshot that would make
/// the next diff hallucinate mass deletions.</summary>
public sealed class Snapshot
{
    public Dictionary<string, SnapshotEntry> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Materialises a walk into a snapshot. Later entries win on a duplicate key, which can
    /// only happen if two paths differ by case alone — same file, so either value is correct.</summary>
    public static Snapshot FromEntries(IEnumerable<FileEntry> files)
    {
        var snap = new Snapshot();
        foreach (FileEntry f in files)
            snap.Entries[f.RelPath] = new SnapshotEntry(f.Size, f.MtimeUtc, f.Attrs);
        return snap;
    }

    /// <summary>Reads a snapshot, or null when there is none to read — null means "first ever scan of
    /// this root" and the caller emits a Baseline instead of an Added per file. Never throws: a
    /// corrupt line is logged and skipped, and a file we cannot open at all is reported as null for
    /// the same reason (an empty snapshot would be read as "every file is new").</summary>
    public static Snapshot? Load(string path, ErrorLog errors)
    {
        if (!File.Exists(path)) return null;

        var snap = new Snapshot();
        try
        {
            using var reader = new StreamReader(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16));

            long lineNo = 0;
            while (reader.ReadLine() is string line)
            {
                lineNo++;
                if (line.Length == 0) continue;

                SnapshotLine? rec;
                try
                {
                    rec = JsonSerializer.Deserialize(line, JsonCtx.Default.SnapshotLine);
                }
                catch (Exception ex)
                {
                    // One bad line (torn write, disk corruption) costs one file's history: it will be
                    // reported as Added on the next poll. That beats discarding the whole snapshot.
                    errors.Log($"snapshot-line\t{path}#{lineNo}", ex);
                    continue;
                }

                if (rec is null || rec.P.Length == 0) continue;
                snap.Entries[rec.P] = new SnapshotEntry(rec.S, rec.M, rec.A);
            }
        }
        catch (Exception ex)
        {
            // Unreadable ≠ missing. Returning null here would masquerade as "first run", swallow
            // the gate-#1 baseline into a single line, and silently forget state the journal could
            // have replayed. Fail the construction; the worker retries with backoff.
            errors.Log($"snapshot-read\t{path}", ex);
            throw;
        }

        return snap;
    }

    /// <summary>Writes the snapshot and commits it atomically. Throws on failure — a snapshot that
    /// did not land must not be mistaken for one that did.</summary>
    public void Save(string path)
    {
        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = full + ".tmp";
        var buf = new ArrayBufferWriter<byte>(256);

        using (var file = new FileStream(tmp, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 1 << 20, // 1 MiB write buffer
        }))
        {
            // One reused writer over an in-memory buffer, reset per line — no per-entry reflection
            // or allocation (the same pattern as the change log).
            using var json = new Utf8JsonWriter(buf, Jsonl.WriterOptions);

            foreach ((string rel, SnapshotEntry e) in Entries)
            {
                buf.Clear();
                json.Reset();

                json.WriteStartObject();
                json.WriteString("p"u8, rel);
                json.WriteNumber("s"u8, e.Size);
                json.WriteString("m"u8, e.MtimeUtc); // ISO-8601 round-trip ("O"), reads back exactly
                json.WriteNumber("a"u8, e.Attrs);
                json.WriteEndObject();
                json.Flush();

                file.Write(buf.WrittenSpan);
                file.WriteByte((byte)'\n');
            }

            // To the platter, not just the OS cache: the rename below must not be able to win a race
            // with the data it is committing, or a power cut leaves a valid name over empty content.
            file.Flush(flushToDisk: true);
        }

        // File.Replace is the atomic swap, but it demands an existing destination; the very first
        // save has none. ignoreMetadataErrors keeps a failure to copy ACLs/attributes from aborting
        // an otherwise good commit.
        if (File.Exists(full)) File.Replace(tmp, full, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else File.Move(tmp, full, overwrite: true);
    }
}
