namespace Argus;

/// <summary>Two exclusion lists, matched by prefix on a single path SEGMENT, case-insensitively
/// (consistent with NTFS/SMB, which are case-preserving but case-insensitive).
///
/// <para><b>Directory prefixes</b> exclude a whole subtree: it is never descended into during a
/// scan, never enters the snapshot or the FRN map, and — because its directories stay unmapped —
/// its journal records cannot resolve a parent and are dropped for free. Sync-client scratch
/// folders are the motivating case (Google Drive's ".tmp.driveupload" writes a file per upload).
///
/// <para><b>File prefixes</b> exclude individual files wherever they appear. This needs its own
/// check on every record: the parent directory is legitimately watched, so nothing upstream filters
/// them. Office lock files are the motivating case ("~$agreement.docx" exists only while the
/// document is open).
///
/// The two are kept separate because the same string means different things: a folder named ".tmp"
/// is noise, while a file named ".tmp-notes" may well be real work.</summary>
public sealed class IgnoreRules
{
    public static readonly IgnoreRules None = new([], []);

    readonly string[] _dirPrefixes;
    readonly string[] _filePrefixes;

    public IgnoreRules(IEnumerable<string> dirPrefixes, IEnumerable<string> filePrefixes)
    {
        _dirPrefixes = Clean(dirPrefixes);
        _filePrefixes = Clean(filePrefixes);
    }

    // Blank entries are dropped rather than trusted: "" is a prefix of everything and would
    // silently exclude the entire root.
    static string[] Clean(IEnumerable<string> p)
        => [.. p.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim())];

    public bool Any => _dirPrefixes.Length > 0 || _filePrefixes.Length > 0;

    /// <summary>True when one directory NAME (a single segment, not a path) is excluded.</summary>
    public bool IsIgnoredDirName(ReadOnlySpan<char> name) => Matches(name, _dirPrefixes);

    /// <summary>True when one file NAME (a single segment, not a path) is excluded.</summary>
    public bool IsIgnoredFileName(ReadOnlySpan<char> name) => Matches(name, _filePrefixes);

    static bool Matches(ReadOnlySpan<char> name, string[] prefixes)
    {
        foreach (string p in prefixes)
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>True when a root-relative path is excluded: any of its directory components matches
    /// a directory prefix, or its final segment matches. <paramref name="pathIsDirectory"/> selects
    /// which list the final segment is tested against — dir-map paths end in a directory, snapshot
    /// keys end in a file name.</summary>
    public bool IsIgnoredPath(string relPath, bool pathIsDirectory)
    {
        if (!Any || relPath.Length == 0) return false;
        ReadOnlySpan<char> rest = relPath;
        while (true)
        {
            int i = rest.IndexOf('\\');
            if (i < 0)
                return pathIsDirectory ? IsIgnoredDirName(rest) : IsIgnoredFileName(rest);
            if (IsIgnoredDirName(rest[..i])) return true;
            rest = rest[(i + 1)..];
        }
    }
}
