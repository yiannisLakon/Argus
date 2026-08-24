using Xunit;

namespace Argus.Tests;

/// <summary>Two separate lists, and the separation is the point: a FOLDER prefix excludes a whole
/// subtree, a FILE prefix excludes individual files. Putting "~$" in the directory list would do
/// nothing for "~$agreement.docx", which is exactly the trap these tests pin down.</summary>
public class IgnoreRulesTests
{
    static readonly IgnoreRules Dirs = new([".tmp"], []);
    static readonly IgnoreRules Files = new([], ["~$"]);
    static readonly IgnoreRules Both = new([".tmp"], ["~$"]);

    [Theory]
    [InlineData(@".tmp.driveupload\31600")]          // the real Google Drive case
    [InlineData(@"sub\.tmpfoo\deep\file.txt")]       // excluded dir anywhere in the chain
    [InlineData(@".tmp\file.txt")]                   // exact prefix as the whole segment
    [InlineData(@"φάκελος\.tmp.sync\αρχείο.docx")]   // Greek segments around it
    [InlineData(@"a\.TMP.Upload\x.bin")]             // case-insensitive, like NTFS
    public void FilesUnderAnIgnoredDirectory_AreIgnored(string rel)
        => Assert.True(Dirs.IsIgnoredPath(rel, pathIsDirectory: false));

    [Theory]
    [InlineData(@"notes.txt")]
    [InlineData(@"sub\notes.txt")]
    [InlineData(@".tmpfile.txt")]        // a FILE named .tmp* is a real change — dir list is folders only
    [InlineData(@"sub\.tmp-notes.md")]   // ditto, one level down
    [InlineData(@"a.tmp\x.txt")]         // prefix must start the segment, not appear inside it
    [InlineData(@"tmp\x.txt")]           // no leading dot ⇒ not the configured prefix
    [InlineData(@"")]
    public void DirectoryRules_DoNotTouchFileNames(string rel)
        => Assert.False(Dirs.IsIgnoredPath(rel, pathIsDirectory: false));

    [Theory]
    [InlineData(@"~$agreement.docx")]                    // Word lock file at the root
    [InlineData(@"Word Files\ΚΤΥΠ21\~$συμφωνητικό.docx")] // ...and nested, with Greek
    [InlineData(@"sub\~$X.XLSX")]                        // case-insensitive
    public void FilePrefixes_IgnoreTheFileItself(string rel)
        => Assert.True(Files.IsIgnoredPath(rel, pathIsDirectory: false));

    [Fact]
    public void FilePrefixes_DoNotExcludeSiblingsOrDirectories()
    {
        Assert.False(Files.IsIgnoredPath(@"Word Files\agreement.docx", pathIsDirectory: false));
        // A directory that happens to start with the FILE prefix is not excluded — the lists are
        // matched against their own kind of segment only.
        Assert.False(Files.IsIgnoredPath(@"~$folder\real.docx", pathIsDirectory: false));
        Assert.False(Files.IsIgnoredDirName("~$folder"));
    }

    [Fact]
    public void FinalSegment_IsTestedAgainstTheRightList()
    {
        // The dir map stores directory paths (last segment IS a directory); snapshot keys are file
        // paths (last segment is a file name). Same string, different question.
        Assert.True(Both.IsIgnoredPath(@"sub\.tmp.driveupload", pathIsDirectory: true));
        Assert.False(Both.IsIgnoredPath(@"sub\.tmp.driveupload", pathIsDirectory: false));
        Assert.True(Both.IsIgnoredPath(@"sub\~$doc.docx", pathIsDirectory: false));
        Assert.False(Both.IsIgnoredPath(@"sub\~$doc.docx", pathIsDirectory: true));
    }

    [Fact]
    public void EmptyRules_IgnoreNothing()
    {
        Assert.False(IgnoreRules.None.Any);
        Assert.False(IgnoreRules.None.IsIgnoredPath(@".tmp.driveupload\x", pathIsDirectory: false));
        Assert.False(IgnoreRules.None.IsIgnoredPath(@"~$doc.docx", pathIsDirectory: false));
    }

    [Fact]
    public void BlankAndPaddedPrefixes_AreCleanedUp()
    {
        // "" is a prefix of everything: trusting it would silently exclude the entire root.
        var rules = new IgnoreRules(["  .tmp  ", "", "   "], ["~$", ""]);
        Assert.True(rules.IsIgnoredDirName(".tmp.driveupload"));
        Assert.True(rules.IsIgnoredFileName("~$doc.docx"));
        Assert.False(rules.IsIgnoredDirName("anything"));
        Assert.False(rules.IsIgnoredFileName("anything.docx"));
    }

    [Fact]
    public void Enumerator_SkipsIgnoredDirectoriesAndFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "argus-ign-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(root, ".tmp.driveupload", "nested"));
        Directory.CreateDirectory(Path.Combine(root, "keep"));
        File.WriteAllText(Path.Combine(root, "top.txt"), "x");
        File.WriteAllText(Path.Combine(root, "~$top.docx"), "x");          // Word lock file
        File.WriteAllText(Path.Combine(root, "keep", "kept.txt"), "x");
        File.WriteAllText(Path.Combine(root, "keep", "~$kept.docx"), "x"); // ...nested
        File.WriteAllText(Path.Combine(root, ".tmp.driveupload", "junk.bin"), "x");
        File.WriteAllText(Path.Combine(root, ".tmp.driveupload", "nested", "deep.bin"), "x");
        try
        {
            var walked = new Enumerator(root, TestEnv.Errors(), Both).Walk()
                .Select(f => f.RelPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            Assert.Equal([@"keep\kept.txt", "top.txt"], walked);

            // Without rules the same tree yields all six, so the walk itself is sound.
            Assert.Equal(6, new Enumerator(root, TestEnv.Errors()).Walk().Count());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
