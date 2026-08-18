using System.Text.Json.Serialization;

namespace Argus;

public enum ChangeType { Added, Modified, Removed, Baseline }

/// <summary>Per-file state kept in a root's snapshot; the dictionary key is the relative path
/// (OrdinalIgnoreCase — NTFS/SMB are case-preserving but case-insensitive).</summary>
public readonly record struct SnapshotEntry(long Size, DateTime MtimeUtc, int Attrs);

/// <summary>A file seen by enumeration, metadata served from the directory scan's cached FIND data.</summary>
public readonly record struct FileEntry(string RelPath, long Size, DateTime MtimeUtc, int Attrs);

/// <summary>One line of a change log. Added carries Size/Mtime; Removed carries Prev*; Modified
/// carries both; Baseline carries only Files (count present at first-ever scan of the root).</summary>
public readonly record struct ChangeEvent(
    DateTimeOffset Ts,
    string Root,
    ChangeType Type,
    string? Path,
    long? Size = null,
    DateTime? MtimeUtc = null,
    long? PrevSize = null,
    DateTime? PrevMtimeUtc = null,
    int? Files = null);

public sealed class ArgusConfig
{
    public int TickSeconds { get; set; } = 10;
    /// <summary>full | summary | off (full during alpha/beta).</summary>
    public string Telemetry { get; set; } = "full";
    public List<RootConfig> Roots { get; set; } = [];
}

public sealed class RootConfig
{
    public string Id { get; set; } = "";
    /// <summary>Absolute path. UNC paths only for network roots (services don't see user drive
    /// mappings) — a UNC root is watched by the poller, a local one by the USN journal.</summary>
    public string Path { get; set; } = "";
    /// <summary>Poller cadence for UNC roots (also the degraded-mode cadence when a local
    /// volume's journal is unusable). Journal roots ignore this during normal drains.</summary>
    public int PollMinutes { get; set; } = 30;

    [JsonIgnore] public bool IsUnc => Path.StartsWith(@"\\", StringComparison.Ordinal);
}

/// <summary>Persisted journal cursor for one root. A USN without its journal ID is meaningless —
/// they are stored and validated as a pair. ResyncTrigger != 0 records WHICH validation-gate
/// condition (#1–#5) demanded a resync, persisted BEFORE the resync starts so a crash mid-resync
/// cannot lose the obligation.</summary>
public sealed class CursorState
{
    public string Volume { get; set; } = "";
    public ulong JournalId { get; set; }
    public long NextUsn { get; set; }
    public int ResyncTrigger { get; set; }
}

/// <summary>One snapshot JSONL line: p=relative path, s=size, m=last-write UTC, a=attributes.</summary>
public sealed class SnapshotLine
{
    [JsonPropertyName("p")] public string P { get; set; } = "";
    [JsonPropertyName("s")] public long S { get; set; }
    [JsonPropertyName("m")] public DateTime M { get; set; }
    [JsonPropertyName("a")] public int A { get; set; }
}

/// <summary>One directory-map JSONL line: f=FRN as hex, p=directory path relative to the root
/// ("" for the root itself). The map lets journal records (leaf name + parent FRN, never a full
/// path) be resolved to paths without ever enumerating the whole volume.</summary>
public sealed class DirMapLine
{
    [JsonPropertyName("f")] public string F { get; set; } = "";
    [JsonPropertyName("p")] public string P { get; set; } = "";
}
