using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Argus;

public static class Program
{
    const string Version = "0.1.0";

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8; // required for Greek output on Windows terminals
        GuardNotRunningFromNetworkShare();

        if (args is ["init"]) return InitConfig();
        if (args is ["version" or "--version"]) { Console.WriteLine($"argus {Version}"); return 0; }

        // One instance machine-wide: the service and a stray console run must never interleave
        // cursor/snapshot commits. The kernel releases the mutex if the process dies. Opening a
        // Global\ mutex the LocalSystem service owns throws for a non-elevated user — same answer.
        Mutex mutex;
        bool createdNew;
        try { mutex = new Mutex(initiallyOwned: true, @"Global\Argus_SingleInstance", out createdNew); }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("argus: another instance (the service?) is already running — exiting.");
            return 1;
        }
        using var _mutexScope = mutex;
        if (!createdNew)
        {
            Console.Error.WriteLine("argus: another instance is already running — exiting.");
            return 1;
        }
        try
        {
            _ = DataRoot; // resolve GLOBAL_DATA_ROOT now — fail loudly before the host spins up

            HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddWindowsService(o => o.ServiceName = "Argus");
            // Room for per-root sink flushes at stop (watcher disposal waits up to 5 s each).
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
            builder.Services.AddHostedService<Worker>();
            using IHost host = builder.Build();

            // Console runs also stop gracefully on the Global\ArgusStop event: a script cannot
            // deliver Ctrl+C to a hidden console window, and a hard kill would skip the summary and
            // final flushes. Console mode only — the service is stopped through the SCM, and a
            // world-settable stop event on a LocalSystem process would be a free denial-of-service.
            RegisteredWaitHandle? stopReg = null;
            EventWaitHandle? stopSignal = null;
            if (!WindowsServiceHelpers.IsWindowsService())
            {
                IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
                stopSignal = new EventWaitHandle(false, EventResetMode.ManualReset, @"Global\ArgusStop", out _);
                stopReg = ThreadPool.RegisterWaitForSingleObject(
                    stopSignal, (_, _) => lifetime.StopApplication(), null, Timeout.Infinite, executeOnlyOnce: true);
            }
            try { host.Run(); } // console: Ctrl+C or the stop event; service: SCM lifetime
            finally { stopReg?.Unregister(null); stopSignal?.Dispose(); }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] {ex.Message}");
            return 1;
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { /* not owned */ }
        }
    }

    static int InitConfig()
    {
        string path;
        try { path = ConfigPath; }
        catch (Exception ex) { Console.Error.WriteLine($"[ERROR] {ex.Message}"); return 1; }

        if (File.Exists(path)) { Console.WriteLine($"config already exists: {path}"); return 0; }
        Directory.CreateDirectory(ArgusDir);
        File.WriteAllText(path, """
            {
              // Argus — watched roots. Reread every tick; edits apply live.
              // Local paths are watched via the USN journal; UNC paths via the poller.
              // UNC roots must use \\server\share form, never a mapped drive (services don't see them).
              // ignoreDirPrefixes: any path with a DIRECTORY segment starting with one of these is
              // ignored entirely (sync scratch folders). [] to watch everything.
              "tickSeconds": 10,
              "telemetry": "full",
              "ignoreDirPrefixes": [".tmp"],
              "roots": [
                { "id": "downloads", "path": "C:\\Users\\Yanis\\Downloads", "pollMinutes": 30 }
              ]
            }
            """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"wrote starter config: {path}");
        return 0;
    }

    static void GuardNotRunningFromNetworkShare()
    {
        var path = Environment.ProcessPath ?? AppContext.BaseDirectory;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "ΣΦΑΛΜΑ: Μην εκτελείτε την εφαρμογή απευθείας από το δικτυακό φάκελο.\n" +
                "Αντιγράψτε την πρώτα σε τοπικό φάκελο και εκτελέστε την από εκεί.");
            Environment.Exit(1);
        }
    }
}
