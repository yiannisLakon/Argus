namespace Argus;

/// <summary>Turns two snapshots of the same root into the change log between them. Purely
/// computational — no I/O, no clock read (the caller supplies the one timestamp every event of a
/// poll shares, so a whole poll's events sort together and can be correlated).
///
/// Renames are NOT detected here: to a poller a rename is indistinguishable from a delete plus a
/// create, and pretending otherwise would need content hashes. The USN journal path reports them
/// properly; the poller reports Removed + Added.</summary>
public static class Differ
{
    public static List<ChangeEvent> Diff(
        IReadOnlyDictionary<string, SnapshotEntry> prev,
        IReadOnlyDictionary<string, SnapshotEntry> curr,
        string rootId,
        DateTimeOffset ts)
    {
        var events = new List<ChangeEvent>();

        // Present now: new, or changed. Lookups use prev's own comparer (OrdinalIgnoreCase), so a
        // case-only difference in the stored path is not mistaken for a different file.
        foreach ((string path, SnapshotEntry now) in curr)
        {
            if (!prev.TryGetValue(path, out SnapshotEntry was))
            {
                events.Add(new ChangeEvent(ts, rootId, ChangeType.Added, path, now.Size, now.MtimeUtc));
                continue;
            }

            // Attributes count as a modification too: a file turned read-only or hidden changed in a
            // way an operator cares about, even though its bytes and mtime did not move.
            if (now.Size != was.Size || now.MtimeUtc != was.MtimeUtc || now.Attrs != was.Attrs)
                events.Add(new ChangeEvent(
                    ts, rootId, ChangeType.Modified, path,
                    now.Size, now.MtimeUtc, was.Size, was.MtimeUtc));
        }

        // Present before, gone now.
        foreach ((string path, SnapshotEntry was) in prev)
        {
            if (!curr.ContainsKey(path))
                events.Add(new ChangeEvent(
                    ts, rootId, ChangeType.Removed, path,
                    PrevSize: was.Size, PrevMtimeUtc: was.MtimeUtc));
        }

        return events;
    }

    /// <summary>Copies the previous snapshot's entries under each failed directory into the fresh
    /// scan, so an unreadable subtree diffs as "unchanged" instead of "everything deleted". Changes
    /// inside it are missed until it enumerates again — logged, honest, and recoverable; a removal
    /// flood is neither. Call BEFORE <see cref="Diff"/>.</summary>
    public static int PreserveFailedSubtrees(
        IReadOnlyDictionary<string, SnapshotEntry> prev,
        Dictionary<string, SnapshotEntry> curr,
        IReadOnlyList<string> failedRelDirs)
    {
        int preserved = 0;
        foreach ((string path, SnapshotEntry e) in prev)
        {
            foreach (string dir in failedRelDirs)
            {
                if (!DirMap.IsUnder(path, dir)) continue;
                if (curr.TryAdd(path, e)) preserved++;
                break;
            }
        }
        return preserved;
    }
}
