using System.Text;
using System.Text.Json;
using Xunit;

namespace Argus.Tests;

/// <summary>The change log is the product's only durable output, so its exact JSON shape is a
/// contract: type names are lower-case, only the fields that carry meaning for an event type are
/// written, and Greek paths stay readable rather than turning into \uXXXX escapes.</summary>
public class EventSinkTests
{
    static readonly DateTimeOffset Ts = new(2026, 3, 14, 15, 9, 26, TimeSpan.Zero);
    const string Month = "2026-03";

    static readonly DateTime Mtime = new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Utc).AddTicks(5_358_979);
    static readonly DateTime PrevMtime = new DateTime(2026, 2, 1, 8, 30, 0, DateTimeKind.Utc);

    const string GreekPath = @"φάκελος\αρχείο.txt";

    static string LogPath(string rootId) => Path.Combine(Common.ArgusDir, $"changes-{rootId}-{Month}.jsonl");

    /// <summary>Writes the given events through a sink, disposes it, and returns the log's raw text.</summary>
    static string WriteAndRead(string rootId, params ChangeEvent[] events)
    {
        using (var sink = new EventSink(rootId))
        {
            foreach (ChangeEvent e in events) sink.Write(in e);
            Assert.Null(sink.Fault);
        }
        return File.ReadAllText(LogPath(rootId), Encoding.UTF8);
    }

    static JsonElement[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .ToArray();

    static bool Has(JsonElement o, string name) => o.TryGetProperty(name, out _);

    [Fact]
    public void EveryEventType_LandsInTheMonthlyLogWithTheRightFields()
    {
        string rootId = TestEnv.RootId("evt-all");
        string text = WriteAndRead(rootId,
            new ChangeEvent(Ts, rootId, ChangeType.Added, GreekPath, 4_096, Mtime),
            new ChangeEvent(Ts, rootId, ChangeType.Modified, GreekPath, 8_192, Mtime, 4_096, PrevMtime),
            new ChangeEvent(Ts, rootId, ChangeType.Removed, GreekPath, PrevSize: 8_192, PrevMtimeUtc: PrevMtime),
            new ChangeEvent(Ts, rootId, ChangeType.Baseline, null, Files: 4_211));

        JsonElement[] lines = Lines(text);
        Assert.Equal(4, lines.Length);

        // --- added: current metadata only.
        JsonElement added = lines[0];
        Assert.Equal("added", added.GetProperty("type").GetString());
        Assert.Equal(Ts, added.GetProperty("ts").GetDateTimeOffset());
        Assert.Equal(rootId, added.GetProperty("root").GetString());
        Assert.Equal(GreekPath, added.GetProperty("path").GetString());
        Assert.Equal(4_096L, added.GetProperty("size").GetInt64());
        Assert.Equal(Mtime, added.GetProperty("mtime").GetDateTime());
        Assert.False(Has(added, "prevSize"));
        Assert.False(Has(added, "prevMtime"));
        Assert.False(Has(added, "files"));

        // --- modified: both sides.
        JsonElement modified = lines[1];
        Assert.Equal("modified", modified.GetProperty("type").GetString());
        Assert.Equal(GreekPath, modified.GetProperty("path").GetString());
        Assert.Equal(8_192L, modified.GetProperty("size").GetInt64());
        Assert.Equal(Mtime, modified.GetProperty("mtime").GetDateTime());
        Assert.Equal(4_096L, modified.GetProperty("prevSize").GetInt64());
        Assert.Equal(PrevMtime, modified.GetProperty("prevMtime").GetDateTime());
        Assert.False(Has(modified, "files"));

        // --- removed: previous metadata only.
        JsonElement removed = lines[2];
        Assert.Equal("removed", removed.GetProperty("type").GetString());
        Assert.Equal(GreekPath, removed.GetProperty("path").GetString());
        Assert.Equal(8_192L, removed.GetProperty("prevSize").GetInt64());
        Assert.Equal(PrevMtime, removed.GetProperty("prevMtime").GetDateTime());
        Assert.False(Has(removed, "size"));
        Assert.False(Has(removed, "mtime"));
        Assert.False(Has(removed, "files"));

        // --- baseline: a count, and deliberately no path.
        JsonElement baseline = lines[3];
        Assert.Equal("baseline", baseline.GetProperty("type").GetString());
        Assert.Equal(4_211, baseline.GetProperty("files").GetInt32());
        Assert.False(Has(baseline, "path"));
        Assert.False(Has(baseline, "size"));
        Assert.False(Has(baseline, "mtime"));
        Assert.False(Has(baseline, "prevSize"));
        Assert.False(Has(baseline, "prevMtime"));
        Assert.Equal(rootId, baseline.GetProperty("root").GetString());
        Assert.Equal(Ts, baseline.GetProperty("ts").GetDateTimeOffset());
    }

    [Fact]
    public void GreekPaths_AreWrittenLiterallyNotEscaped()
    {
        string rootId = TestEnv.RootId("evt-greek");
        string text = WriteAndRead(rootId,
            new ChangeEvent(Ts, rootId, ChangeType.Added, GreekPath, 1, Mtime));

        Assert.Contains(@"φάκελος\\αρχείο.txt", text);   // JSON escapes the separator, not the Greek
        Assert.Contains("αρχείο", text);
        Assert.DoesNotContain("\\u03", text);
        Assert.DoesNotContain("\\u00", text);
    }

    [Theory]
    [InlineData(ChangeType.Added, "added")]
    [InlineData(ChangeType.Modified, "modified")]
    [InlineData(ChangeType.Removed, "removed")]
    [InlineData(ChangeType.Baseline, "baseline")]
    public void TypeNames_AreTheExactLowerCaseStrings(ChangeType type, string expected)
    {
        string rootId = TestEnv.RootId("evt-type");
        string text = WriteAndRead(rootId, new ChangeEvent(Ts, rootId, type, "p.txt", 1, Mtime, 2, PrevMtime, 3));

        JsonElement line = Assert.Single(Lines(text));
        Assert.Equal(expected, line.GetProperty("type").GetString());
    }

    [Fact]
    public void TheLogIsAppendedTo_NotTruncated_WhenASinkIsReopened()
    {
        string rootId = TestEnv.RootId("evt-append");
        WriteAndRead(rootId, new ChangeEvent(Ts, rootId, ChangeType.Added, "first.txt", 1, Mtime));
        string text = WriteAndRead(rootId, new ChangeEvent(Ts, rootId, ChangeType.Added, "second.txt", 2, Mtime));

        JsonElement[] lines = Lines(text);
        Assert.Equal(2, lines.Length);
        Assert.Equal("first.txt", lines[0].GetProperty("path").GetString());
        Assert.Equal("second.txt", lines[1].GetProperty("path").GetString());
    }

    [Fact]
    public void EventsAreFiledUnderTheirOwnMonth_NotTheWallClock()
    {
        string rootId = TestEnv.RootId("evt-month");
        var december = new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero);
        var january = new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero);

        using (var sink = new EventSink(rootId))
        {
            var a = new ChangeEvent(december, rootId, ChangeType.Added, "dec.txt", 1, Mtime);
            var b = new ChangeEvent(january, rootId, ChangeType.Added, "jan.txt", 2, Mtime);
            sink.Write(in a);
            sink.Write(in b);
            Assert.Null(sink.Fault);
        }

        string dec = Path.Combine(Common.ArgusDir, $"changes-{rootId}-2025-12.jsonl");
        string jan = Path.Combine(Common.ArgusDir, $"changes-{rootId}-2026-01.jsonl");
        Assert.Equal("dec.txt", Assert.Single(Lines(File.ReadAllText(dec))).GetProperty("path").GetString());
        Assert.Equal("jan.txt", Assert.Single(Lines(File.ReadAllText(jan))).GetProperty("path").GetString());
    }
}
