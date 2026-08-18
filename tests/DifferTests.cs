using Xunit;

namespace Argus.Tests;

/// <summary>Differ is pure: two snapshots in, the change log between them out. Every event shape is
/// asserted field by field, because the log's consumers rely on Added carrying no Prev*, Removed
/// carrying no current size/mtime, and Modified carrying both.</summary>
public class DifferTests
{
    const string RootId = "R1";
    static readonly DateTimeOffset Ts = new(2026, 3, 14, 15, 9, 26, TimeSpan.Zero);
    static readonly DateTime M0 = new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Utc).AddTicks(5_358_979);
    static readonly DateTime M1 = M0.AddMinutes(7);

    static Dictionary<string, SnapshotEntry> Map(params (string Path, SnapshotEntry Entry)[] items)
    {
        var d = new Dictionary<string, SnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        foreach ((string p, SnapshotEntry e) in items) d[p] = e;
        return d;
    }

    static SnapshotEntry E(long size = 100, int attrs = 0x20) => new(size, M0, attrs);

    [Fact]
    public void FileOnlyInCurrent_IsAddedWithCurrentMetadataAndNoPrev()
    {
        var prev = Map((@"a\keep.txt", E()));
        var curr = Map((@"a\keep.txt", E()), (@"a\νέο.txt", new SnapshotEntry(4_096, M1, 0x80)));

        ChangeEvent e = Assert.Single(Differ.Diff(prev, curr, RootId, Ts));

        Assert.Equal(ChangeType.Added, e.Type);
        Assert.Equal(@"a\νέο.txt", e.Path);
        Assert.Equal(Ts, e.Ts);
        Assert.Equal(RootId, e.Root);
        Assert.Equal(4_096L, e.Size);
        Assert.Equal(M1, e.MtimeUtc);
        Assert.Null(e.PrevSize);
        Assert.Null(e.PrevMtimeUtc);
        Assert.Null(e.Files);
    }

    [Fact]
    public void FileOnlyInPrevious_IsRemovedWithPrevMetadataAndNoCurrent()
    {
        var prev = Map((@"a\keep.txt", E()), (@"a\χάθηκε.txt", new SnapshotEntry(512, M1, 0x20)));
        var curr = Map((@"a\keep.txt", E()));

        ChangeEvent e = Assert.Single(Differ.Diff(prev, curr, RootId, Ts));

        Assert.Equal(ChangeType.Removed, e.Type);
        Assert.Equal(@"a\χάθηκε.txt", e.Path);
        Assert.Equal(512L, e.PrevSize);
        Assert.Equal(M1, e.PrevMtimeUtc);
        Assert.Null(e.Size);
        Assert.Null(e.MtimeUtc);
        Assert.Null(e.Files);
    }

    [Fact]
    public void SizeChange_IsModifiedCarryingBothSides()
    {
        var prev = Map(("f.bin", new SnapshotEntry(100, M0, 0x20)));
        var curr = Map(("f.bin", new SnapshotEntry(250, M0, 0x20)));

        ChangeEvent e = Assert.Single(Differ.Diff(prev, curr, RootId, Ts));

        Assert.Equal(ChangeType.Modified, e.Type);
        Assert.Equal("f.bin", e.Path);
        Assert.Equal(250L, e.Size);
        Assert.Equal(M0, e.MtimeUtc);
        Assert.Equal(100L, e.PrevSize);
        Assert.Equal(M0, e.PrevMtimeUtc);
    }

    [Fact]
    public void MtimeChange_IsModifiedCarryingBothSides()
    {
        var prev = Map(("f.bin", new SnapshotEntry(100, M0, 0x20)));
        var curr = Map(("f.bin", new SnapshotEntry(100, M1, 0x20)));

        ChangeEvent e = Assert.Single(Differ.Diff(prev, curr, RootId, Ts));

        Assert.Equal(ChangeType.Modified, e.Type);
        Assert.Equal(100L, e.Size);
        Assert.Equal(M1, e.MtimeUtc);
        Assert.Equal(100L, e.PrevSize);
        Assert.Equal(M0, e.PrevMtimeUtc);
    }

    [Fact]
    public void AttributesOnlyChange_IsModifiedAndStillCarriesBothSizesAndMtimes()
    {
        // Read-only / hidden flips matter to an operator even though bytes and mtime never moved.
        var prev = Map(("f.bin", new SnapshotEntry(100, M0, 0x20)));
        var curr = Map(("f.bin", new SnapshotEntry(100, M0, 0x21)));

        ChangeEvent e = Assert.Single(Differ.Diff(prev, curr, RootId, Ts));

        Assert.Equal(ChangeType.Modified, e.Type);
        Assert.Equal(100L, e.Size);
        Assert.Equal(100L, e.PrevSize);
        Assert.Equal(M0, e.MtimeUtc);
        Assert.Equal(M0, e.PrevMtimeUtc);
    }

    [Fact]
    public void IdenticalSnapshots_ProduceNoEvents()
    {
        var prev = Map(("f.bin", E()), (@"d\g.txt", E(7)));
        var curr = Map(("f.bin", E()), (@"d\g.txt", E(7)));

        Assert.Empty(Differ.Diff(prev, curr, RootId, Ts));
    }

    [Fact]
    public void CaseOnlyKeyDifference_IsNotAChange()
    {
        // NTFS/SMB are case-preserving but case-insensitive: a path that only changed case is the
        // same file, and must not surface as a remove plus an add.
        var prev = Map((@"Docs\Report.TXT", E()));
        var curr = Map((@"docs\report.txt", E()));

        Assert.Empty(Differ.Diff(prev, curr, RootId, Ts));
    }

    [Fact]
    public void EmptyPrevious_MakesEverythingAdded()
    {
        var prev = Map();
        var curr = Map(("a.txt", E(1)), (@"d\b.txt", E(2)), (@"d\e\c.txt", E(3)));

        List<ChangeEvent> events = Differ.Diff(prev, curr, RootId, Ts);

        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(ChangeType.Added, e.Type));
        Assert.All(events, e => Assert.Null(e.PrevSize));
        Assert.Equal(
            new[] { "a.txt", @"d\b.txt", @"d\e\c.txt" },
            events.Select(e => e.Path!).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void EmptyCurrent_MakesEverythingRemoved()
    {
        var prev = Map(("a.txt", E(1)), (@"d\b.txt", E(2)), (@"d\e\c.txt", E(3)));
        var curr = Map();

        List<ChangeEvent> events = Differ.Diff(prev, curr, RootId, Ts);

        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(ChangeType.Removed, e.Type));
        Assert.All(events, e => Assert.Null(e.Size));
        Assert.All(events, e => Assert.NotNull(e.PrevSize));
        Assert.Equal(
            new[] { "a.txt", @"d\b.txt", @"d\e\c.txt" },
            events.Select(e => e.Path!).Order(StringComparer.Ordinal).ToArray());
    }
}
