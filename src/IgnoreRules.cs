namespace Argus;

/// <summary>Directory-name prefixes whose subtrees are not watched at all — no snapshot entries, no
/// FRN map entries, no events, and not even descended into during a scan. Sync clients scribble
/// constantly in scratch folders (Google Drive's ".tmp.driveupload" churns a file per upload), and
/// that noise otherwise dominates both the change log and the drain work.
///
/// The test is per path SEGMENT and applies to DIRECTORIES only: a file merely named ".tmp-notes"
/// is still a real change, while any file under a ".tmp*" directory is not. Matching is
/// case-insensitive, consistent with the rest of the path handling (NTFS/SMB are case-preserving
/// but case-insensitive).</summary>
public sealed class IgnoreRules
{
    public static readonly IgnoreRules None = new([]);

    readonly string[] _prefixes;

    public IgnoreRules(IEnumerable<string> prefixes)
        => _prefixes = [.. prefixes.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim())];

    public bool Any => _prefixes.Length > 0;

    /// <summary>True when one directory NAME (a single segment, not a path) is excluded.</summary>
    public bool IsIgnoredDirName(ReadOnlySpan<char> name)
    {
        foreach (string p in _prefixes)
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>True when any directory component of a root-relative path is excluded.
    /// <paramref name="pathIsDirectory"/> says whether the final segment is itself a directory
    /// (dir-map paths) or a file name that must NOT be prefix-tested (snapshot keys).</summary>
    public bool HasIgnoredDir(string relPath, bool pathIsDirectory)
    {
        if (_prefixes.Length == 0 || relPath.Length == 0) return false;
        ReadOnlySpan<char> rest = relPath;
        while (true)
        {
            int i = rest.IndexOf('\\');
            if (i < 0) return pathIsDirectory && IsIgnoredDirName(rest);
            if (IsIgnoredDirName(rest[..i])) return true;
            rest = rest[(i + 1)..];
        }
    }
}
