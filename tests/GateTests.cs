using Xunit;
using static Argus.UsnInterop;

namespace Argus.Tests;

/// <summary>JournalWatcher.GateCheck is the whole validation gate as a pure function — the decision
/// table below is the plan's five conditions, exercised without a live volume.</summary>
public class GateTests
{
    const ulong JournalId = 0xABCD_1234_5678_9ABCUL;

    static USN_JOURNAL_DATA_V2 Jd(
        ulong journalId = JournalId, long firstUsn = 1_000, long nextUsn = 9_000, long lowestValidUsn = 1_000)
        => new()
        {
            UsnJournalID = journalId,
            FirstUsn = firstUsn,
            NextUsn = nextUsn,
            LowestValidUsn = lowestValidUsn,
            MaxUsn = long.MaxValue,
            MaximumSize = 32UL << 20,
            AllocationDelta = 8UL << 20,
            MinSupportedMajorVersion = 2,
            MaxSupportedMajorVersion = 3,
            Flags = 0,
            RangeTrackChunkSize = 0,
            RangeTrackFileSizeThreshold = 0,
        };

    static CursorState Cursor(ulong journalId = JournalId, long nextUsn = 5_000, int trigger = 0)
        => new() { Volume = "C:", JournalId = journalId, NextUsn = nextUsn, ResyncTrigger = trigger };

    [Fact]
    public void NullCursor_IsGate1()
    {
        USN_JOURNAL_DATA_V2 jd = Jd();
        Assert.Equal(1, JournalWatcher.GateCheck(null, haveScanState: true, in jd));
    }

    [Fact]
    public void CursorWithoutScanState_IsGate1()
    {
        USN_JOURNAL_DATA_V2 jd = Jd();
        Assert.Equal(1, JournalWatcher.GateCheck(Cursor(), haveScanState: false, in jd));
    }

    [Fact]
    public void PendingResyncTrigger_WinsOverEveryOtherCondition()
    {
        // Journal id mismatched (#2) AND cursor below FirstUsn (#3) — the persisted obligation still
        // has to be what comes back, or a crash mid-resync would silently change the recorded cause.
        USN_JOURNAL_DATA_V2 jd = Jd(journalId: 0xDEAD_BEEFUL, firstUsn: 8_000, lowestValidUsn: 8_000);
        CursorState cursor = Cursor(journalId: JournalId, nextUsn: 10, trigger: 4);
        Assert.Equal(4, JournalWatcher.GateCheck(cursor, haveScanState: true, in jd));
    }

    [Fact]
    public void JournalIdMismatch_IsGate2()
    {
        USN_JOURNAL_DATA_V2 jd = Jd(journalId: 0x1111_2222_3333_4444UL);
        Assert.Equal(2, JournalWatcher.GateCheck(Cursor(journalId: JournalId), haveScanState: true, in jd));
    }

    [Fact]
    public void CursorBelowFirstUsn_IsGate3()
    {
        USN_JOURNAL_DATA_V2 jd = Jd(firstUsn: 5_000, lowestValidUsn: 5_000);
        Assert.Equal(3, JournalWatcher.GateCheck(Cursor(nextUsn: 4_999), haveScanState: true, in jd));
    }

    [Fact]
    public void CursorAboveFirstUsnButBelowLowestValid_IsGate4()
    {
        USN_JOURNAL_DATA_V2 jd = Jd(firstUsn: 1_000, lowestValidUsn: 3_000);
        Assert.Equal(4, JournalWatcher.GateCheck(Cursor(nextUsn: 2_000), haveScanState: true, in jd));
    }

    [Fact]
    public void EverythingValid_IsClean()
    {
        USN_JOURNAL_DATA_V2 jd = Jd(firstUsn: 1_000, nextUsn: 9_000, lowestValidUsn: 1_000);
        Assert.Equal(0, JournalWatcher.GateCheck(Cursor(nextUsn: 5_000), haveScanState: true, in jd));
    }

    [Fact]
    public void CursorExactlyOnFirstAndLowestValidUsn_IsClean()
    {
        // Both comparisons are strict "<" — sitting exactly on the boundary is still covered.
        USN_JOURNAL_DATA_V2 jd = Jd(firstUsn: 1_000, nextUsn: 9_000, lowestValidUsn: 1_000);
        Assert.Equal(0, JournalWatcher.GateCheck(Cursor(nextUsn: 1_000), haveScanState: true, in jd));
    }

    [Fact]
    public void CursorExactlyOnLowestValidUsnAboveFirstUsn_IsClean()
    {
        USN_JOURNAL_DATA_V2 jd = Jd(firstUsn: 1_000, nextUsn: 9_000, lowestValidUsn: 3_000);
        Assert.Equal(0, JournalWatcher.GateCheck(Cursor(nextUsn: 3_000), haveScanState: true, in jd));
    }

    [Fact]
    public void CursorAheadOfJournalNextUsn_IsGate4()
    {
        // Image-level volume restore: same journal id, but the journal itself was rewound below the
        // saved cursor. Reads would return "nothing new" forever — the gate must force a resync.
        USN_JOURNAL_DATA_V2 jd = Jd(firstUsn: 1_000, nextUsn: 4_000, lowestValidUsn: 1_000);
        Assert.Equal(4, JournalWatcher.GateCheck(Cursor(nextUsn: 5_000), haveScanState: true, in jd));
    }

    [Fact]
    public void CursorExactlyOnJournalNextUsn_IsClean()
    {
        USN_JOURNAL_DATA_V2 jd = Jd(firstUsn: 1_000, nextUsn: 5_000, lowestValidUsn: 1_000);
        Assert.Equal(0, JournalWatcher.GateCheck(Cursor(nextUsn: 5_000), haveScanState: true, in jd));
    }
}
