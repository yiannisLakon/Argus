using Xunit;

namespace Argus.Tests;

/// <summary>The FRN → directory-path map. Its prefix arithmetic is what keeps a directory rename from
/// silently rewriting a sibling whose name merely starts with the same characters, and its persistence
/// has to survive 128-bit FRNs with the high bits set (V3 records) and Greek directory names.</summary>
public class DirMapTests
{
    static readonly UInt128 FrnRoot = new(0x8000_0000_0000_0000UL, 0x0000_0000_0000_0001UL);
    static readonly UInt128 FrnA = new(0xFEDC_BA98_7654_3210UL, 0x0123_4567_89AB_CDEFUL);
    static readonly UInt128 FrnB = new(0x0000_0000_0000_0000UL, 0x0004_0000_0000_1234UL);
    static readonly UInt128 FrnC = new(0xFFFF_FFFF_FFFF_FFFFUL, 0xFFFF_FFFF_FFFF_FFFFUL);
    static readonly UInt128 FrnMissing = new(0x1UL, 0x2UL);

    [Fact]
    public void SetTryGetPathRemove_DriveTheDirtyFlag()
    {
        var map = new DirMap();
        Assert.False(map.Dirty);
        Assert.Equal(0, map.Count);

        map.Set(FrnA, "docs");
        Assert.True(map.Dirty);
        Assert.Equal(1, map.Count);

        map.Dirty = false;
        Assert.True(map.TryGetPath(FrnA, out string path));
        Assert.Equal("docs", path);
        Assert.True(map.Contains(FrnA));
        Assert.False(map.Dirty);                    // pure reads never dirty the map

        map.Set(FrnA, "docs-renamed");              // overwrite still dirties
        Assert.True(map.Dirty);
        Assert.True(map.TryGetPath(FrnA, out path));
        Assert.Equal("docs-renamed", path);

        map.Dirty = false;
        map.Remove(FrnMissing);                     // removing what was never there changes nothing
        Assert.False(map.Dirty);
        Assert.Equal(1, map.Count);

        map.Remove(FrnA);
        Assert.True(map.Dirty);
        Assert.Equal(0, map.Count);
        Assert.False(map.TryGetPath(FrnA, out _));
        Assert.False(map.Contains(FrnA));
    }

    [Fact]
    public void RemapSubtree_RewritesSelfAndDescendantsButNotAPrefixSharingSibling()
    {
        var map = new DirMap();
        map.Set(FrnA, "docs");
        map.Set(FrnB, @"docs\a\b");
        map.Set(FrnC, @"docs2\x");                  // shares the "docs" prefix, is NOT under it
        map.Dirty = false;

        map.RemapSubtree("docs", "docs2-new");

        Assert.True(map.Dirty);
        Assert.True(map.TryGetPath(FrnA, out string a));
        Assert.Equal("docs2-new", a);
        Assert.True(map.TryGetPath(FrnB, out string b));
        Assert.Equal(@"docs2-new\a\b", b);
        Assert.True(map.TryGetPath(FrnC, out string c));
        Assert.Equal(@"docs2\x", c);
    }

    [Fact]
    public void RemapSubtree_WithNoMatches_LeavesTheMapClean()
    {
        var map = new DirMap();
        map.Set(FrnC, @"docs2\x");
        map.Dirty = false;

        map.RemapSubtree("docs", "docs2-new");

        Assert.False(map.Dirty);
        Assert.True(map.TryGetPath(FrnC, out string c));
        Assert.Equal(@"docs2\x", c);
    }

    [Fact]
    public void CollectSubtree_ReturnsSelfAndDescendantsOnly()
    {
        var map = new DirMap();
        map.Set(FrnRoot, "");
        map.Set(FrnA, "docs");
        map.Set(FrnB, @"docs\a\b");
        map.Set(FrnC, @"docs2\x");

        List<UInt128> got = map.CollectSubtree("docs");

        Assert.Equal(2, got.Count);
        Assert.Contains(FrnA, got);
        Assert.Contains(FrnB, got);
        Assert.DoesNotContain(FrnC, got);
        Assert.DoesNotContain(FrnRoot, got);
    }

    [Theory]
    [InlineData(@"docs\a", "docs", true)]
    [InlineData(@"docs\a\b", "docs", true)]
    [InlineData(@"DOCS\a", "docs", true)]           // case-insensitive, like the filesystem
    [InlineData("docs2", "docs", false)]            // prefix guard: no separator at the boundary
    [InlineData(@"docs2\x", "docs", false)]
    [InlineData("docs", "docs", false)]             // a directory is not under itself
    [InlineData("do", "docs", false)]
    [InlineData(@"docs\", "docs", false)]           // trailing separator only — nothing below it
    public void IsUnder_EdgeCases(string path, string prefix, bool expected)
        => Assert.Equal(expected, DirMap.IsUnder(path, prefix));

    [Fact]
    public void SaveLoadRoundTrip_PreservesHighBitFrnsAndGreekDirectoryNames()
    {
        Directory.CreateDirectory(Common.StateDir);
        string rootId = TestEnv.RootId("dirmap");

        var map = new DirMap();
        map.Set(FrnRoot, "");                       // the root itself maps to the empty path
        map.Set(FrnA, "φάκελος");
        map.Set(FrnB, @"φάκελος\υποφάκελος");
        map.Set(FrnC, @"φάκελος\υποφάκελος\βαθύτερα");
        map.Save(rootId);

        Assert.False(map.Dirty);                    // a committed save is not pending any more
        Assert.True(File.Exists(DirMap.PathFor(rootId)));

        using ErrorLog errors = TestEnv.Errors();
        DirMap? loaded = DirMap.Load(rootId, errors);

        Assert.NotNull(loaded);
        Assert.Equal(4, loaded.Count);
        Assert.False(loaded.Dirty);

        Assert.True(loaded.TryGetPath(FrnRoot, out string root));
        Assert.Equal("", root);
        Assert.True(loaded.TryGetPath(FrnA, out string a));
        Assert.Equal("φάκελος", a);
        Assert.True(loaded.TryGetPath(FrnB, out string b));
        Assert.Equal(@"φάκελος\υποφάκελος", b);
        Assert.True(loaded.TryGetPath(FrnC, out string c));
        Assert.Equal(@"φάκελος\υποφάκελος\βαθύτερα", c);

        Assert.Equal(0, errors.Count);
    }

    [Fact]
    public void Load_OfAnAbsentMap_IsNull()
    {
        Directory.CreateDirectory(Common.StateDir);
        using ErrorLog errors = TestEnv.Errors();
        Assert.Null(DirMap.Load(TestEnv.RootId("dirmap-absent"), errors));
    }
}
