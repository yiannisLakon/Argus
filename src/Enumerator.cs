namespace Argus;

/// <summary>Iterative depth-first walk of one root, capturing each file's cheap metadata from the
/// directory scan itself (size, last-write time and attributes are served from the enumeration's
/// cached FIND data, so no extra per-file stat is issued — that is what keeps a full poll of a
/// large share affordable). Reparse points (junctions / symlinks / mount points) are never followed:
/// they could loop forever or escape the tree, and the poller would report the same file twice under
/// two paths. Hidden and system files ARE included — a change hidden from the shell is still a
/// change. Per-directory access errors are logged and the directory skipped, so one unreadable
/// folder never aborts the walk; the rest of the root still diffs correctly.</summary>
public sealed class Enumerator(string rootFullPath, ErrorLog errors, IgnoreRules? ignore = null)
{
    readonly IgnoreRules _ignore = ignore ?? IgnoreRules.None;

    // Include hidden/system (the default EnumerationOptions would skip them); never surface reparse
    // points. IgnoreInaccessible=false so a denied directory raises — we want it logged, not silently
    // treated as empty, which would look exactly like "everything under it was deleted".
    static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Root-relative paths of directories that could not be enumerated ("." = the root
    /// itself). Callers MUST consult this after a walk: a failed directory's files are absent from
    /// the result, and diffing that absence as "everything under it was deleted" is exactly the
    /// removal flood the design forbids — preserve the prior snapshot state for these instead.</summary>
    public List<string> FailedRelDirs { get; } = [];

    public IEnumerable<FileEntry> Walk()
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(rootFullPath));

        while (stack.Count > 0)
        {
            DirectoryInfo dir = stack.Pop();

            FileSystemInfo[] children;
            try
            {
                // Eager per-directory: either we get the whole directory or (on error) none of it,
                // which gives clean error semantics vs. a lazy enumerator throwing mid-iteration.
                children = dir.GetFileSystemInfos("*", Options);
            }
            catch (Exception ex)
            {
                errors.Log($"enumerate\t{dir.FullName}", ex);
                FailedRelDirs.Add(Path.GetRelativePath(rootFullPath, dir.FullName));
                continue;
            }

            foreach (FileSystemInfo child in children)
            {
                if (child is DirectoryInfo sub)
                {
                    // Excluded subtrees are never descended into — that is where the I/O saving is
                    // on a big tree, not merely filtering the results afterwards.
                    if (!_ignore.IsIgnoredDirName(sub.Name)) stack.Push(sub);
                    continue;
                }

                if (TryMakeEntry((FileInfo)child, out FileEntry entry))
                    yield return entry;
            }
        }
    }

    bool TryMakeEntry(FileInfo fi, out FileEntry entry)
    {
        try
        {
            // All cached from the directory scan — no additional I/O. (A file deleted between the
            // scan and here still yields its cached values rather than throwing; the next poll
            // reports the removal.)
            entry = new FileEntry(
                Path.GetRelativePath(rootFullPath, fi.FullName),
                fi.Length,
                fi.LastWriteTimeUtc,
                (int)fi.Attributes);
            return true;
        }
        catch (Exception ex)
        {
            errors.Log($"stat\t{fi.FullName}", ex);
            entry = default;
            return false;
        }
    }
}
