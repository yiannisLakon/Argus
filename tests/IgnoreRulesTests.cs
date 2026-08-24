using Xunit;

namespace Argus.Tests;

/// <summary>The exclusion rule is "any DIRECTORY component of the path starts with a prefix" —
/// deliberately not "the file name starts with it", and deliberately not a substring match.</summary>
public class IgnoreRulesTests
{
    static readonly IgnoreRules Tmp = new([".tmp"]);

    [Theory]
    [InlineData(@".tmp.driveupload\31600")]          // the real Google Drive case
    [InlineData(@"sub\.tmpfoo\deep\file.txt")]       // excluded dir anywhere in the chain
    [InlineData(@".tmp\file.txt")]                   // exact prefix as the whole segment
    [InlineData(@"φάκελος\.tmp.sync\αρχείο.docx")]   // Greek segments around it
    [InlineData(@"a\.TMP.Upload\x.bin")]             // case-insensitive, like NTFS
    public void FilesUnderAnIgnoredDirectory_AreIgnored(string rel)
        => Assert.True(Tmp.HasIgnoredDir(rel, pathIsDirectory: false));

    [Theory]
    [InlineData(@"notes.txt")]
    [InlineData(@"sub\notes.txt")]
    [InlineData(@".tmpfile.txt")]        // a FILE named .tmp* is a real change — folders only
    [InlineData(@"sub\.tmp-notes.md")]   // ditto, one level down
    [InlineData(@"a.tmp\x.txt")]         // prefix must start the segment, not appear inside it
    [InlineData(@"tmp\x.txt")]           // no leading dot ⇒ not the configured prefix
    [InlineData(@"")]
    public void OtherPaths_AreNotIgnored(string rel)
        => Assert.False(Tmp.HasIgnoredDir(rel, pathIsDirectory: false));

    [Fact]
    public void FinalSegment_CountsOnlyForDirectoryPaths()
    {
        // The dir-map stores directory paths, where the last segment IS a directory; snapshot keys
        // are file paths, where it is a file name. Same string, opposite answers.
        Assert.True(Tmp.HasIgnoredDir(@"sub\.tmp.driveupload", pathIsDirectory: true));
        Assert.False(Tmp.HasIgnoredDir(@"sub\.tmp.driveupload", pathIsDirectory: false));
    }

    [Fact]
    public void DirNameTest_MatchesPrefixOnly()
    {
        Assert.True(Tmp.IsIgnoredDirName(".tmp"));
        Assert.True(Tmp.IsIgnoredDirName(".tmp.driveupload"));
        Assert.False(Tmp.IsIgnoredDirName("tmp"));
        Assert.False(Tmp.IsIgnoredDirName("a.tmp"));
    }

    [Fact]
    public void EmptyRules_IgnoreNothing()
    {
        Assert.False(IgnoreRules.None.Any);
        Assert.False(IgnoreRules.None.HasIgnoredDir(@".tmp.driveupload\x", pathIsDirectory: false));
        Assert.False(IgnoreRules.None.IsIgnoredDirName(".tmp"));
    }

    [Fact]
    public void BlankAndPaddedPrefixes_AreCleanedUp()
    {
        var rules = new IgnoreRules(["  .tmp  ", "", "   ", "~$"]);
        Assert.True(rules.IsIgnoredDirName(".tmp.driveupload"));
        Assert.True(rules.IsIgnoredDirName("~$doc"));
        Assert.False(rules.IsIgnoredDirName("anything"));  // blank entries must not match everything
    }

    [Fact]
    public void Enumerator_DoesNotDescendIntoIgnoredDirectories()
    {
        string root = Path.Combine(Path.GetTempPath(), "argus-ign-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(root, ".tmp.driveupload", "nested"));
        Directory.CreateDirectory(Path.Combine(root, "keep"));
        File.WriteAllText(Path.Combine(root, "top.txt"), "x");
        File.WriteAllText(Path.Combine(root, "keep", "kept.txt"), "x");
        File.WriteAllText(Path.Combine(root, ".tmp.driveupload", "junk.bin"), "x");
        File.WriteAllText(Path.Combine(root, ".tmp.driveupload", "nested", "deep.bin"), "x");
        try
        {
            var walked = new Enumerator(root, TestEnv.Errors(), Tmp).Walk()
                .Select(f => f.RelPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            Assert.Equal([@"keep\kept.txt", "top.txt"], walked);

            // ...and without rules the same tree yields everything, so the walk itself is sound.
            Assert.Equal(4, new Enumerator(root, TestEnv.Errors()).Walk().Count());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
