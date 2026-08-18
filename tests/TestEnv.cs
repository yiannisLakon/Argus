using System.Runtime.CompilerServices;

namespace Argus.Tests;

/// <summary>Common.DataRoot resolves GLOBAL_DATA_ROOT once and caches it, and it throws when the
/// variable is unset or points at a missing directory. The module initializer therefore has to run
/// before ANY test (or xunit discovery) touches Common — it points the whole test assembly at a
/// fresh, unique temp directory that it creates itself.</summary>
internal static class TestEnv
{
    /// <summary>The temp directory GLOBAL_DATA_ROOT points at for this test run.</summary>
    internal static string Root { get; private set; } = "";

    [ModuleInitializer]
    internal static void Init()
    {
        Root = Path.Combine(Path.GetTempPath(), "argus-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("GLOBAL_DATA_ROOT", Root);
    }

    /// <summary>A throwaway error log; the product types all take one and must never throw through it.</summary>
    internal static ErrorLog Errors() => new(Path.Combine(Root, "test-errors.log"));

    /// <summary>Unique id so tests that write files named after a root never collide, even in parallel.</summary>
    internal static string RootId(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];
}
