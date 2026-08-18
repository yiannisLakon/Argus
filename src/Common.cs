global using static Argus.Common;

namespace Argus;

internal static class Common
{
    internal static DateTimeOffset Now => DateTimeOffset.UtcNow;

    static string? _dataRoot;

    /// <summary>Root of all shared service data, from GLOBAL_DATA_ROOT. There is deliberately no
    /// fallback path: a guessed default silently forks the data. Services don't see user env vars —
    /// the installer pins the variable in the service's registry Environment MultiString.</summary>
    internal static string DataRoot => _dataRoot ??= ResolveDataRoot();

    internal static string ArgusDir => Path.Combine(DataRoot, "argus");
    internal static string StateDir => Path.Combine(ArgusDir, "state");
    internal static string ConfigPath => Path.Combine(ArgusDir, "config.json");
    internal static string ErrorLogPath => Path.Combine(ArgusDir, "error.log");

    static string ResolveDataRoot()
    {
        string? root = Environment.GetEnvironmentVariable("GLOBAL_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException(
                "GLOBAL_DATA_ROOT is not set — no fallback path exists by design. Set it at User " +
                "scope for console runs; for the service it must be pinned in HKLM\\SYSTEM\\" +
                "CurrentControlSet\\Services\\Argus\\Environment (see tools/install-service.ps1).");
        if (!Directory.Exists(root))
            throw new InvalidOperationException($"GLOBAL_DATA_ROOT points to a missing directory: {root}");
        return root;
    }
}
