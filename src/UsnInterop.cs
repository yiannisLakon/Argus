using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Argus;

/// <summary>P/Invoke surface for the USN change journal (LibraryImport — source-generated, AOT-clean).
/// Every struct layout, FSCTL code, flag and error value here was verified against the Windows SDK
/// 10.0.26100.0 headers (winioctl.h, winerror.h, winnt.h, winbase.h, minwinbase.h, fileapi.h).</summary>
internal static partial class UsnInterop
{
    // CTL_CODE(FILE_DEVICE_FILE_SYSTEM=0x9, function, method, FILE_ANY_ACCESS)
    internal const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4; // fn 61, METHOD_BUFFERED
    internal const uint FSCTL_READ_USN_JOURNAL  = 0x000900BB; // fn 46, METHOD_NEITHER

    internal const int ERROR_INVALID_FUNCTION           = 1;    // volume doesn't support the journal (non-NTFS)
    internal const int ERROR_ACCESS_DENIED              = 5;    // volume handle needs elevation
    internal const int ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178; // gate #5
    internal const int ERROR_JOURNAL_NOT_ACTIVE         = 1179; // gate #5
    internal const int ERROR_JOURNAL_ENTRY_DELETED      = 1181; // gate #3: cursor fell off the ring

    // USN_REASON_* — only the ones Argus watches or tests against.
    internal const uint ReasonDataOverwrite       = 0x00000001;
    internal const uint ReasonDataExtend          = 0x00000002;
    internal const uint ReasonDataTruncation      = 0x00000004;
    internal const uint ReasonNamedDataOverwrite  = 0x00000010;
    internal const uint ReasonNamedDataExtend     = 0x00000020;
    internal const uint ReasonNamedDataTruncation = 0x00000040;
    internal const uint ReasonFileCreate          = 0x00000100;
    internal const uint ReasonFileDelete          = 0x00000200;
    internal const uint ReasonRenameOldName       = 0x00001000;
    internal const uint ReasonRenameNewName       = 0x00002000;
    internal const uint ReasonBasicInfoChange     = 0x00008000;
    internal const uint ReasonHardLinkChange      = 0x00010000;
    internal const uint ReasonCompressionChange   = 0x00020000;
    internal const uint ReasonEncryptionChange    = 0x00040000;
    internal const uint ReasonReparsePointChange  = 0x00100000;
    internal const uint ReasonStreamChange        = 0x00200000;
    internal const uint ReasonClose               = 0x80000000;

    /// <summary>Everything content-, name- or attribute-relevant. CLOSE is deliberately absent:
    /// with ReturnOnlyOnClose=0 each reason's first occurrence arrives in its own record, and close
    /// records still match through their accumulated bits — the snapshot compare suppresses the
    /// duplicates. ReturnOnlyOnClose=1 would lose RENAME_OLD_NAME records (the close record carries
    /// only the new name), breaking remove+add rename semantics.</summary>
    internal const uint WatchedReasons =
        ReasonDataOverwrite | ReasonDataExtend | ReasonDataTruncation |
        ReasonNamedDataOverwrite | ReasonNamedDataExtend | ReasonNamedDataTruncation |
        ReasonFileCreate | ReasonFileDelete |
        ReasonRenameOldName | ReasonRenameNewName |
        ReasonBasicInfoChange | ReasonHardLinkChange |
        ReasonCompressionChange | ReasonEncryptionChange |
        ReasonReparsePointChange | ReasonStreamChange;

    internal const uint GENERIC_READ               = 0x80000000;
    internal const uint FILE_READ_ATTRIBUTES       = 0x00000080;
    internal const uint FILE_SHARE_ALL             = 0x00000007; // READ | WRITE | DELETE
    internal const uint OPEN_EXISTING              = 3;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000; // required to open directory handles
    internal const uint FILE_ATTRIBUTE_DIRECTORY   = 0x00000010;
    internal const int  FileIdInfoClass            = 18;         // FILE_INFO_BY_HANDLE_CLASS::FileIdInfo

    [StructLayout(LayoutKind.Sequential)]
    internal struct USN_JOURNAL_DATA_V2
    {
        internal ulong UsnJournalID;
        internal long FirstUsn;
        internal long NextUsn;
        internal long LowestValidUsn;
        internal long MaxUsn;
        internal ulong MaximumSize;
        internal ulong AllocationDelta;
        internal ushort MinSupportedMajorVersion;
        internal ushort MaxSupportedMajorVersion;
        internal uint Flags;
        internal ulong RangeTrackChunkSize;
        internal long RangeTrackFileSizeThreshold;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct READ_USN_JOURNAL_DATA_V1
    {
        internal long StartUsn;
        internal uint ReasonMask;
        internal uint ReturnOnlyOnClose;
        internal ulong Timeout;
        internal ulong BytesToWaitFor;
        internal ulong UsnJournalID;
        internal ushort MinMajorVersion;
        internal ushort MaxMajorVersion;
    }

    // FILE_ID_128 is 16 little-endian bytes, kept as two ulongs: a UInt128 field would give the
    // struct 16-byte alignment — size 32 with FileId at offset 16, while the kernel writes 24 bytes
    // with FileId at offset 8 — and the mismatch reads back a permanent zero (caught in review by
    // an empirical probe, not by the compiler: the call still "succeeds").
    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_ID_INFO
    {
        internal ulong VolumeSerialNumber;
        internal ulong FileIdLow;
        internal ulong FileIdHigh;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode,
        void* inBuffer, uint inBufferSize, void* outBuffer, uint outBufferSize,
        out uint bytesReturned, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetFileInformationByHandleEx(
        SafeFileHandle file, int fileInformationClass, void* fileInformation, uint bufferSize);

    /// <summary>128-bit file id of a directory (or file), for keying journal records to paths.
    /// Attribute-only access; backup semantics so directories open. False on any failure.</summary>
    internal static unsafe bool TryGetFileId(string path, out UInt128 fileId)
    {
        fileId = default;
        using SafeFileHandle h = CreateFile(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, 0,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (h.IsInvalid) return false;
        FILE_ID_INFO info = default;
        if (!GetFileInformationByHandleEx(h, FileIdInfoClass, &info, (uint)sizeof(FILE_ID_INFO)))
            return false;
        fileId = new UInt128(info.FileIdHigh, info.FileIdLow); // little-endian: low 8 bytes first
        return true;
    }
}
