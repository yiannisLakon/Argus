using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace Argus.Tests;

/// <summary>Byte-level tests for the packed-record walker. Buffers are built by hand against the
/// documented USN_RECORD_V2/V3 layouts so a layout regression fails here rather than on a live volume.
///
/// V2: RecordLength@0 u32, Major@4 u16, Minor@6 u16, Frn@8 u64, ParentFrn@16 u64, Usn@24 i64,
///     Timestamp@32 i64, Reason@40 u32, SourceInfo@44 u32, SecurityId@48 u32, Attrs@52 u32,
///     NameLen@56 u16, NameOff@58 u16, name@60.
/// V3: identical to @8, then Frn@8 (16B), ParentFrn@24 (16B), Usn@40 i64, Timestamp@48 i64,
///     Reason@56 u32, SourceInfo@60 u32, SecurityId@64 u32, Attrs@68 u32, NameLen@72 u16,
///     NameOff@74 u16, name@76.
/// RecordLength always includes the 8-byte-alignment padding that follows the name.</summary>
public class UsnRecordReaderTests
{
    const long Timestamp = 133_000_000_000_000_000L;

    static int Align8(int n) => (n + 7) & ~7;

    /// <param name="nameGap">Bytes of filler inserted between the fixed header and the name, so the
    /// test can prove FileNameOffset is honored instead of assumed.</param>
    static byte[] V2(ulong frn, ulong parentFrn, long usn, uint reason, uint attrs, string name, int nameGap = 0)
    {
        int nameOff = 60 + nameGap;
        int nameLen = name.Length * 2;
        int len = Align8(nameOff + nameLen);

        var b = new byte[len];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), (uint)len);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), frn);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), parentFrn);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24), usn);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(32), Timestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), reason);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(44), 0);      // SourceInfo
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(48), 0);      // SecurityId
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(52), attrs);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(56), (ushort)nameLen);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(58), (ushort)nameOff);
        b.AsSpan(60, nameGap).Fill(0xEE);                               // garbage the parser must skip
        Encoding.Unicode.GetBytes(name, b.AsSpan(nameOff));
        return b;
    }

    static byte[] V3(UInt128 frn, UInt128 parentFrn, long usn, uint reason, uint attrs, string name)
    {
        const int nameOff = 76;
        int nameLen = name.Length * 2;
        int len = Align8(nameOff + nameLen);

        var b = new byte[len];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), (uint)len);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), (ulong)frn);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), (ulong)(frn >> 64));
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), (ulong)parentFrn);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(32), (ulong)(parentFrn >> 64));
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(40), usn);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(48), Timestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(56), reason);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(60), 0);      // SourceInfo
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(64), 0);      // SecurityId
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(68), attrs);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(72), (ushort)nameLen);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(74), (ushort)nameOff);
        Encoding.Unicode.GetBytes(name, b.AsSpan(nameOff));
        return b;
    }

    /// <summary>A record whose major version the reader does not know (V4 range-tracking shape).</summary>
    static byte[] UnknownMajor(int len = 80)
    {
        var b = new byte[len];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), (uint)len);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 4);
        return b;
    }

    static byte[] Concat(params byte[][] parts)
    {
        var buf = new byte[parts.Sum(p => p.Length)];
        int off = 0;
        foreach (byte[] p in parts) { p.CopyTo(buf, off); off += p.Length; }
        return buf;
    }

    [Fact]
    public void SingleV2Record_ParsesEveryField()
    {
        byte[] buf = V2(frn: 0x0004_0000_0000_1234UL, parentFrn: 0x0005_0000_0000_ABCDUL,
            usn: 0x0000_1122_3344_5566L,
            reason: UsnInterop.ReasonDataExtend | UsnInterop.ReasonClose,
            attrs: 0x20, name: "αρχείο.docx");

        var reader = new UsnRecordReader(buf);
        Assert.True(reader.TryNext(out UsnRecord rec));

        Assert.Equal((UInt128)0x0004_0000_0000_1234UL, rec.Frn);
        Assert.Equal((UInt128)0x0005_0000_0000_ABCDUL, rec.ParentFrn);
        Assert.Equal(0x0000_1122_3344_5566L, rec.Usn);
        Assert.Equal(UsnInterop.ReasonDataExtend | UsnInterop.ReasonClose, rec.Reason);
        Assert.Equal(0x20u, rec.Attributes);
        Assert.Equal("αρχείο.docx", rec.Name.ToString());
        Assert.False(rec.IsDirectory);

        Assert.False(reader.TryNext(out _));
    }

    [Fact]
    public void TwoV2RecordsBackToBack_BothParseAcrossAlignmentPadding()
    {
        // 60 + 22 = 82 → padded to 88, so the second record only starts correctly if RecordLength
        // (padding included) is what advances the walker.
        byte[] first = V2(1, 10, 100, UsnInterop.ReasonFileCreate, 0x20, "αρχείο.docx");
        byte[] second = V2(2, 10, 200, UsnInterop.ReasonFileDelete, 0x10, "υποφάκελος");
        Assert.Equal(88, first.Length);   // 60 header + 22 name = 82, padded
        Assert.Equal(0, first.Length % 8);
        Assert.Equal(0, second.Length % 8);

        var reader = new UsnRecordReader(Concat(first, second));

        Assert.True(reader.TryNext(out UsnRecord a));
        Assert.Equal((UInt128)1, a.Frn);
        Assert.Equal(100L, a.Usn);
        Assert.Equal("αρχείο.docx", a.Name.ToString());
        Assert.False(a.IsDirectory);

        Assert.True(reader.TryNext(out UsnRecord b));
        Assert.Equal((UInt128)2, b.Frn);
        Assert.Equal(200L, b.Usn);
        Assert.Equal(UsnInterop.ReasonFileDelete, b.Reason);
        Assert.Equal("υποφάκελος", b.Name.ToString());
        Assert.True(b.IsDirectory);   // 0x10 = FILE_ATTRIBUTE_DIRECTORY

        Assert.False(reader.TryNext(out _));
    }

    [Fact]
    public void V3Record_Parses128BitFrnsWithHighBitsSet()
    {
        UInt128 frn = new(0xFEDC_BA98_7654_3210UL, 0x0123_4567_89AB_CDEFUL);
        UInt128 parent = new(0x8000_0000_0000_0001UL, 0xFFFF_FFFF_FFFF_FFFFUL);

        var reader = new UsnRecordReader(V3(frn, parent, usn: 777,
            reason: UsnInterop.ReasonRenameNewName, attrs: 0x10, name: "νέο-όνομα"));

        Assert.True(reader.TryNext(out UsnRecord rec));
        Assert.Equal(frn, rec.Frn);
        Assert.Equal(parent, rec.ParentFrn);
        Assert.Equal(777L, rec.Usn);
        Assert.Equal(UsnInterop.ReasonRenameNewName, rec.Reason);
        Assert.Equal(0x10u, rec.Attributes);
        Assert.True(rec.IsDirectory);
        Assert.Equal("νέο-όνομα", rec.Name.ToString());
        Assert.False(reader.TryNext(out _));
    }

    [Fact]
    public void ZeroRecordLength_StopsInsteadOfLooping()
    {
        // A zero length would advance the walker by nothing; the guard must end the walk, and stay
        // ended, rather than spin forever over the same 8 bytes.
        byte[] buf = new byte[64];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 2);

        var reader = new UsnRecordReader(buf);
        Assert.False(reader.TryNext(out _));
        Assert.False(reader.TryNext(out _));
    }

    [Fact]
    public void RecordLengthBeyondBuffer_StopsCleanly()
    {
        byte[] buf = V2(1, 10, 100, UsnInterop.ReasonFileCreate, 0x20, "a.txt");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), (uint)(buf.Length + 8));

        var reader = new UsnRecordReader(buf);
        Assert.False(reader.TryNext(out _));
    }

    [Fact]
    public void UnknownMajorVersion_IsSkippedAndTheNextRecordStillParses()
    {
        byte[] buf = Concat(UnknownMajor(), V2(42, 10, 500, UsnInterop.ReasonBasicInfoChange, 0x80, "μετά.txt"));

        var reader = new UsnRecordReader(buf);
        Assert.True(reader.TryNext(out UsnRecord rec));
        Assert.Equal((UInt128)42, rec.Frn);
        Assert.Equal(500L, rec.Usn);
        Assert.Equal(UsnInterop.ReasonBasicInfoChange, rec.Reason);
        Assert.Equal("μετά.txt", rec.Name.ToString());
        Assert.False(reader.TryNext(out _));
    }

    [Fact]
    public void NameSliceHonorsFileNameOffset()
    {
        // Two bytes of garbage between the header and the name: reading at a hard-coded offset 60
        // would yield a leading U+EEEE, so an exact name match proves the offset is used.
        byte[] buf = V2(7, 10, 900, UsnInterop.ReasonFileCreate, 0x20, "αρχείο.docx", nameGap: 2);
        Assert.Equal(62, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(58)));

        var reader = new UsnRecordReader(buf);
        Assert.True(reader.TryNext(out UsnRecord rec));
        Assert.Equal("αρχείο.docx", rec.Name.ToString());
        Assert.Equal((UInt128)7, rec.Frn);
    }
}
