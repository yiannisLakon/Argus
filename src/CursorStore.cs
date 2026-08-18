using System.Text.Json;

namespace Argus;

/// <summary>Atomic load/save of a root's journal cursor. The (UsnJournalID, NextUsn) pair is stored
/// together — a USN without its journal ID is meaningless. Save order in the pipeline is always:
/// events flushed → snapshot/dirmap → cursor LAST, so a crash replays instead of losing.</summary>
internal static class CursorStore
{
    internal static string PathFor(string rootId) => Path.Combine(StateDir, rootId + ".cursor.json");

    internal static CursorState? Load(string rootId, ErrorLog errors)
    {
        string path = PathFor(rootId);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), JsonCtx.Default.CursorState);
        }
        catch (Exception ex)
        {
            // Corrupt cursor ⇒ treated as "no saved state" ⇒ gate #1 baseline. Safe, never silent.
            errors.Log($"cursor-load\t{rootId}", ex);
            return null;
        }
    }

    internal static void Save(string rootId, CursorState state)
    {
        Directory.CreateDirectory(StateDir);
        string path = PathFor(rootId);
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, state, JsonCtx.Default.CursorState);
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
        else File.Move(tmp, path);
    }
}
