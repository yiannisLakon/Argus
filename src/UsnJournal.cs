using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static Argus.UsnInterop;

namespace Argus;

/// <summary>Thin wrapper over one volume's change journal: open, query, one non-blocking read.
/// Interpretation of results (validation gate, event mapping) lives in JournalWatcher.</summary>
internal sealed class UsnJournal : IDisposable
{
    readonly SafeFileHandle _handle;
    internal string Volume { get; }

    UsnJournal(string volume, SafeFileHandle handle) { Volume = volume; _handle = handle; }

    /// <summary>volume is "C:" style. The volume handle requires elevation (the service runs
    /// LocalSystem; console runs need an elevated shell). Throws Win32Exception.</summary>
    internal static UsnJournal Open(string volume)
    {
        SafeFileHandle h = CreateFile(@"\\.\" + volume, GENERIC_READ, FILE_SHARE_ALL, 0,
            OPEN_EXISTING, 0, 0);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            h.Dispose();
            throw new Win32Exception(err, $"open volume {volume}: error {err}");
        }
        return new UsnJournal(volume, h);
    }

    /// <summary>FSCTL_QUERY_USN_JOURNAL — the ~free per-tick validation probe. Throws Win32Exception;
    /// callers map 1178/1179 to gate #5 and ERROR_INVALID_FUNCTION to "no journal on this volume".</summary>
    internal unsafe USN_JOURNAL_DATA_V2 Query()
    {
        USN_JOURNAL_DATA_V2 data = default;
        if (!DeviceIoControl(_handle, FSCTL_QUERY_USN_JOURNAL, null, 0,
                &data, (uint)sizeof(USN_JOURNAL_DATA_V2), out _, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"query journal {Volume}");
        return data;
    }

    /// <summary>One non-blocking FSCTL_READ_USN_JOURNAL from <paramref name="startUsn"/>. The first
    /// 8 bytes of the buffer are the kernel's next StartUsn (returned via <paramref name="nextUsn"/>);
    /// packed records follow, 64-bit aligned, walked by RecordLength. bytesReturned == 8 (or
    /// nextUsn == startUsn) ⇒ caught up. Throws Win32Exception — 1181 is gate #3.</summary>
    internal unsafe int Read(long startUsn, ulong journalId, ushort maxMajorVersion,
        Span<byte> buffer, out long nextUsn)
    {
        var input = new READ_USN_JOURNAL_DATA_V1
        {
            StartUsn = startUsn,
            ReasonMask = WatchedReasons,
            ReturnOnlyOnClose = 0,      // see WatchedReasons doc — rename old-name records must arrive
            Timeout = 0,
            BytesToWaitFor = 0,         // non-blocking tick shape
            UsnJournalID = journalId,   // kernel integrity-checks it (fails instead of serving a different instance)
            MinMajorVersion = 2,
            MaxMajorVersion = maxMajorVersion,
        };
        uint bytes;
        fixed (byte* p = buffer)
        {
            if (!DeviceIoControl(_handle, FSCTL_READ_USN_JOURNAL,
                    &input, (uint)sizeof(READ_USN_JOURNAL_DATA_V1),
                    p, (uint)buffer.Length, out bytes, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"read journal {Volume}");
        }
        nextUsn = bytes >= 8 ? BinaryPrimitives.ReadInt64LittleEndian(buffer) : startUsn;
        return (int)bytes;
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>One parsed USN record. Name aliases the read buffer — consume before the next read.</summary>
internal readonly ref struct UsnRecord
{
    internal readonly UInt128 Frn;
    internal readonly UInt128 ParentFrn;
    internal readonly long Usn;
    internal readonly uint Reason;
    internal readonly uint Attributes;
    internal readonly ReadOnlySpan<char> Name;

    internal bool IsDirectory => (Attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;

    internal UsnRecord(UInt128 frn, UInt128 parent, long usn, uint reason, uint attrs, ReadOnlySpan<char> name)
    { Frn = frn; ParentFrn = parent; Usn = usn; Reason = reason; Attributes = attrs; Name = name; }
}

/// <summary>Walks the packed records of one read's payload (buffer minus the 8-byte next-USN header).
/// Handles V2 (64-bit FRNs) and V3 (128-bit) records; anything else is skipped. Field offsets are the
/// verified header layouts; FileNameOffset is honored at run time as the docs require.</summary>
internal ref struct UsnRecordReader(ReadOnlySpan<byte> payload)
{
    ReadOnlySpan<byte> _rest = payload;

    internal bool TryNext(out UsnRecord record)
    {
        record = default;
        while (_rest.Length >= 8)
        {
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(_rest);
            if (len < 8 || len > (uint)_rest.Length) return false; // truncated/corrupt — stop cleanly
            ReadOnlySpan<byte> body = _rest[..(int)len];
            _rest = _rest[(int)len..];

            ushort major = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
            if (major == 2 && body.Length >= 60)
            {
                record = Parse(body,
                    frn: BinaryPrimitives.ReadUInt64LittleEndian(body[8..]),
                    parent: BinaryPrimitives.ReadUInt64LittleEndian(body[16..]),
                    usnOffset: 24, reasonOffset: 40, attrsOffset: 52, nameFieldsOffset: 56);
                return true;
            }
            if (major == 3 && body.Length >= 76)
            {
                record = Parse(body,
                    frn: MemoryMarshal.Read<UInt128>(body[8..24]),
                    parent: MemoryMarshal.Read<UInt128>(body[24..40]),
                    usnOffset: 40, reasonOffset: 56, attrsOffset: 68, nameFieldsOffset: 72);
                return true;
            }
            // V4 (range tracking) or future — not requested (MaxMajorVersion ≤ 3), skip defensively.
        }
        return false;
    }

    static UsnRecord Parse(ReadOnlySpan<byte> body, UInt128 frn, UInt128 parent,
        int usnOffset, int reasonOffset, int attrsOffset, int nameFieldsOffset)
    {
        long usn = BinaryPrimitives.ReadInt64LittleEndian(body[usnOffset..]);
        uint reason = BinaryPrimitives.ReadUInt32LittleEndian(body[reasonOffset..]);
        uint attrs = BinaryPrimitives.ReadUInt32LittleEndian(body[attrsOffset..]);
        int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(body[nameFieldsOffset..]);
        int nameOff = BinaryPrimitives.ReadUInt16LittleEndian(body[(nameFieldsOffset + 2)..]);

        ReadOnlySpan<char> name = [];
        if (nameOff > 0 && nameLen > 0 && nameOff + nameLen <= body.Length)
            name = MemoryMarshal.Cast<byte, char>(body.Slice(nameOff, nameLen)); // no trailing null — length-delimited
        return new UsnRecord(frn, parent, usn, reason, attrs, name);
    }
}
