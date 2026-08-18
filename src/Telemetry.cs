using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Argus;

/// <summary>One resource reading of THIS process, taken before and after a unit of work; the stats
/// stream stores the deltas (CPU, allocations, GC counts, I/O bytes) and the absolute levels
/// (working set, private bytes, handles, threads). CpuMs is total processor time across all cores,
/// so it can exceed the wall clock.</summary>
public readonly record struct TelemetrySample(
    double CpuMs,
    long WorkingSet,
    long PrivateBytes,
    long AllocatedBytes,
    int Gc0,
    int Gc1,
    int Gc2,
    ulong IoReadBytes,
    ulong IoWriteBytes,
    int Handles,
    int Threads);

/// <summary>Zero-dependency process telemetry: everything comes from the BCL plus one kernel32
/// import for the I/O byte counters (deliberately NO PerformanceCounter, NO OpenTelemetry).
/// Sampling must never be the thing that breaks a tick — a failed counter read reports zeros.</summary>
public static partial class Telemetry
{
    // One Process object for the whole run: constructing it per sample opens a fresh handle every
    // tick. Refresh() is what invalidates its cached counters — without it every sample after the
    // first would repeat the values captured when the object was created.
    static readonly Process Self = Process.GetCurrentProcess();

    // Pseudo-handle for the current process (constant -1, not a real handle): never closed, and
    // valid from any thread. Cheaper and safer than Process.Handle, which materialises a real one.
    static readonly nint SelfHandle = GetCurrentProcess();

    // Refresh() mutates the shared Process object, so concurrent samplers (tick thread + poller
    // threads) would otherwise read counters torn across two refreshes.
    static readonly object Gate = new();

    public static TelemetrySample Sample()
    {
        double cpuMs;
        long ws, priv;
        int handles, threads;
        lock (Gate)
        {
            Self.Refresh();
            cpuMs = Self.TotalProcessorTime.TotalMilliseconds;
            ws = Self.WorkingSet64;
            priv = Self.PrivateMemorySize64;
            handles = Self.HandleCount;
            threads = Self.Threads.Count;
        }

        (ulong read, ulong write) = ReadIo();

        return new TelemetrySample(
            cpuMs, ws, priv,
            // precise:false = the cheap read of the per-thread allocation counters; the small
            // undercount of the current allocation contexts is irrelevant at tick scale.
            GC.GetTotalAllocatedBytes(precise: false),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            read, write, handles, threads);
    }

    /// <summary>Bytes transferred by this process, disk and everything else the kernel counts.
    /// On failure returns zeros rather than throwing: a missing I/O number must not cost a tick.</summary>
    static (ulong Read, ulong Write) ReadIo()
        => GetProcessIoCounters(SelfHandle, out IO_COUNTERS c)
            ? (c.ReadTransferCount, c.WriteTransferCount)
            : (0UL, 0UL);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessIoCounters(nint hProcess, out IO_COUNTERS counters);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    // Populated by the kernel through the P/Invoke above, never assigned in managed code — hence
    // the suppression. Field order IS the ABI; the operation/Other counts are unused but must stay.
#pragma warning disable CS0649
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }
#pragma warning restore CS0649
}
