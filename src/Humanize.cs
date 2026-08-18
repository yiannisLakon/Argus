namespace Argus;

public static class Humanize
{
    public static string Bytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024):0.#} MB",
        _ => $"{b / (1024.0 * 1024 * 1024):0.##} GB",
    };

    public static string Duration(TimeSpan t) => t.TotalSeconds switch
    {
        < 10 => $"{t.TotalSeconds:0.0#}s",
        < 60 => $"{t.TotalSeconds:0}s",
        < 3600 => $"{(int)t.TotalMinutes}m {t.Seconds:00}s",
        _ => $"{(int)t.TotalHours}h {t.Minutes:00}m {t.Seconds:00}s",
    };
}
