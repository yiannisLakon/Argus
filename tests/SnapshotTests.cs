using System.Text;
using Xunit;

namespace Argus.Tests;

/// <summary>The per-root snapshot: JSONL on disk, atomically committed, tolerant of one torn line.
/// Round-tripping mtime EXACTLY matters — an mtime that drifts by a tick would make the next diff
/// report every file as modified.</summary>
public class SnapshotTests
{
    static readonly DateTime M0 = new DateTime(2026, 3, 14, 15, 9, 26, DateTimeKind.Utc).AddTicks(5_358_979);
    static readonly DateTime M1 = new DateTime(2025, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9_999_999);

    static string TempPath(string prefix)
    {
        Directory.CreateDirectory(Common.StateDir);
        return Path.Combine(Common.StateDir, TestEnv.RootId(prefix) + ".snapshot.jsonl");
    }

    [Fact]
    public void FromEntries_ThenSaveLoad_RoundTripsGreekPathsAndMetadataExactly()
    {
        string path = TempPath("snap");
        var snap = Snapshot.FromEntries(
        [
            new FileEntry(@"φάκελος\αρχείο.txt", 123_456_789L, M0, 0x20),
            new FileEntry("root.bin", 0L, M1, 0x2020),
        ]);

        snap.Save(path);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));   // the temp file is moved, never left behind

        using ErrorLog errors = TestEnv.Errors();
        Snapshot? loaded = Snapshot.Load(path, errors);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Entries.Count);

        SnapshotEntry greek = loaded.Entries[@"φάκελος\αρχείο.txt"];
        Assert.Equal(123_456_789L, greek.Size);
        Assert.Equal(M0, greek.MtimeUtc);
        Assert.Equal(M0.Ticks, greek.MtimeUtc.Ticks);
        Assert.Equal(DateTimeKind.Utc, greek.MtimeUtc.Kind);
        Assert.Equal(0x20, greek.Attrs);

        SnapshotEntry bin = loaded.Entries["root.bin"];
        Assert.Equal(0L, bin.Size);
        Assert.Equal(M1, bin.MtimeUtc);
        Assert.Equal(0x2020, bin.Attrs);

        Assert.Equal(0, errors.Count);
    }

    [Fact]
    public void SavedFile_KeepsGreekPathsLiteral()
    {
        string path = TempPath("snap-literal");
        Snapshot.FromEntries([new FileEntry(@"φάκελος\αρχείο.txt", 1, M0, 0x20)]).Save(path);

        string text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("αρχείο", text);
        Assert.DoesNotContain("\\u03", text);
    }

    [Fact]
    public void Load_OfAMissingFile_IsNull()
    {
        // null means "first ever scan" — the caller emits one Baseline instead of an Added per file,
        // so an empty snapshot must never be substituted here.
        using ErrorLog errors = TestEnv.Errors();
        Assert.Null(Snapshot.Load(TempPath("snap-missing"), errors));
        Assert.Equal(0, errors.Count);
    }

    [Fact]
    public void Load_SkipsOneCorruptLineAndKeepsTheRest()
    {
        string path = TempPath("snap-corrupt");
        File.WriteAllLines(path,
        [
            """{"p":"a.txt","s":11,"m":"2026-03-14T15:09:26.5358979Z","a":32}""",
            """{"p":"broken.txt","s":,,,"m":not-json""",
            """{"p":"b.txt","s":22,"m":"2026-03-14T15:09:26.5358979Z","a":33}""",
        ], Encoding.UTF8);

        using ErrorLog errors = TestEnv.Errors();
        Snapshot? loaded = Snapshot.Load(path, errors);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Entries.Count);
        Assert.Equal(11L, loaded.Entries["a.txt"].Size);
        Assert.Equal(22L, loaded.Entries["b.txt"].Size);
        Assert.False(loaded.Entries.ContainsKey("broken.txt"));
        Assert.Equal(1, errors.Count);              // the bad line is logged, not swallowed
    }

    [Fact]
    public void Keys_AreOrdinalIgnoreCase()
    {
        string path = TempPath("snap-case");
        Snapshot.FromEntries([new FileEntry(@"Docs\Report.TXT", 5, M0, 0x20)]).Save(path);

        using ErrorLog errors = TestEnv.Errors();
        Snapshot? loaded = Snapshot.Load(path, errors);

        Assert.NotNull(loaded);
        Assert.True(loaded.Entries.ContainsKey(@"docs\report.txt"));
        Assert.True(loaded.Entries.ContainsKey(@"DOCS\REPORT.TXT"));
        Assert.Equal(5L, loaded.Entries[@"dOcS\rEpOrT.tXt"].Size);
    }

    [Fact]
    public void FromEntries_LetsALaterCaseOnlyDuplicateWin()
    {
        Snapshot snap = Snapshot.FromEntries(
        [
            new FileEntry(@"Docs\Report.TXT", 1, M0, 0x20),
            new FileEntry(@"docs\report.txt", 2, M1, 0x21),
        ]);

        SnapshotEntry only = Assert.Single(snap.Entries).Value;
        Assert.Equal(2L, only.Size);
        Assert.Equal(M1, only.MtimeUtc);
    }

    [Fact]
    public void Save_OverAnExistingSnapshot_ReplacesItAtomically()
    {
        string path = TempPath("snap-replace");
        Snapshot.FromEntries([new FileEntry("old.txt", 1, M0, 0x20)]).Save(path);
        Snapshot.FromEntries([new FileEntry("new.txt", 2, M1, 0x20)]).Save(path);

        using ErrorLog errors = TestEnv.Errors();
        Snapshot? loaded = Snapshot.Load(path, errors);

        Assert.NotNull(loaded);
        Assert.Equal(2L, Assert.Single(loaded.Entries).Value.Size);
        Assert.True(loaded.Entries.ContainsKey("new.txt"));
        Assert.False(File.Exists(path + ".tmp"));
    }
}
