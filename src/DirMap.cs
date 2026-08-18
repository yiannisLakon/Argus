using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace Argus;

/// <summary>FRN → directory-path map for one watched root ("" is the root itself). Journal records
/// carry only a leaf name and a parent FRN, never a path; this map is what turns them into paths.
/// It is built for free during baseline/resync enumeration (one attribute-only handle per directory
/// — directories ≪ files, so no FSCTL_ENUM_USN_DATA whole-volume pass is ever needed) and then
/// maintained incrementally from directory create/rename/delete records. Persisted beside the
/// snapshot so a service restart replays the journal gap without any poll.</summary>
internal sealed class DirMap
{
    readonly Dictionary<UInt128, string> _byFrn = [];

    internal int Count => _byFrn.Count;
    internal bool Dirty { get; set; }

    internal bool TryGetPath(UInt128 frn, out string relPath) => _byFrn.TryGetValue(frn, out relPath!);
    internal bool Contains(UInt128 frn) => _byFrn.ContainsKey(frn);

    internal void Set(UInt128 frn, string relPath) { _byFrn[frn] = relPath; Dirty = true; }

    internal void Remove(UInt128 frn) { if (_byFrn.Remove(frn)) Dirty = true; }

    /// <summary>Directory renamed/moved within the tree: rewrite the entry itself and every
    /// descendant directory's path prefix.</summary>
    internal void RemapSubtree(string oldPrefix, string newPrefix)
    {
        List<(UInt128 Frn, string NewPath)> changes = [];
        foreach ((UInt128 frn, string path) in _byFrn)
        {
            if (path.Equals(oldPrefix, StringComparison.OrdinalIgnoreCase))
                changes.Add((frn, newPrefix));
            else if (IsUnder(path, oldPrefix))
                changes.Add((frn, string.Concat(newPrefix, path.AsSpan(oldPrefix.Length))));
        }
        foreach ((UInt128 frn, string path) in changes) _byFrn[frn] = path;
        if (changes.Count > 0) Dirty = true;
    }

    /// <summary>FRNs of a directory and every descendant directory (for move-out/delete cleanup).</summary>
    internal List<UInt128> CollectSubtree(string prefix)
    {
        List<UInt128> frns = [];
        foreach ((UInt128 frn, string path) in _byFrn)
            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || IsUnder(path, prefix))
                frns.Add(frn);
        return frns;
    }

    internal static bool IsUnder(string path, string prefix) =>
        path.Length > prefix.Length + 1 &&
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        path[prefix.Length] == '\\';

    internal static string PathFor(string rootId) => Path.Combine(StateDir, rootId + ".dirmap.jsonl");

    internal static DirMap? Load(string rootId, ErrorLog errors)
    {
        string path = PathFor(rootId);
        if (!File.Exists(path)) return null;
        var map = new DirMap();
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;
                DirMapLine? l;
                try { l = JsonSerializer.Deserialize(line, JsonCtx.Default.DirMapLine); }
                catch (Exception ex) { errors.Log($"dirmap-load\t{rootId}\tbad line", ex); continue; }
                if (l is null || !UInt128.TryParse(l.F, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out UInt128 frn))
                    continue;
                map._byFrn[frn] = l.P;
            }
            return map;
        }
        catch (Exception ex)
        {
            // Unreadable map ⇒ records can't be resolved ⇒ caller treats as gate #1 (baseline).
            errors.Log($"dirmap-load\t{rootId}", ex);
            return null;
        }
    }

    internal void Save(string rootId)
    {
        Directory.CreateDirectory(StateDir);
        string path = PathFor(rootId);
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var buf = new ArrayBufferWriter<byte>(256);
            using var json = new Utf8JsonWriter(buf, Jsonl.WriterOptions);
            foreach ((UInt128 frn, string p) in _byFrn)
            {
                buf.Clear();
                json.Reset();
                json.WriteStartObject();
                json.WriteString("f"u8, frn.ToString("x", CultureInfo.InvariantCulture));
                json.WriteString("p"u8, p);
                json.WriteEndObject();
                json.Flush();
                fs.Write(buf.WrittenSpan);
                fs.WriteByte((byte)'\n');
            }
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
        else File.Move(tmp, path);
        Dirty = false;
    }
}
