namespace AoiLine.Core.Services;

public record LogEntry(DateTime Time, string Level, string? LineId, string Message)
{
    public string Text => $"{Time:yyyy-MM-dd HH:mm:ss} [{Level}]{(LineId != null ? $" [{LineId}]" : "")} {Message}";
}

/// <summary>Writes a daily log file (Logs\yyyy-MM-dd.log) and notifies listeners.</summary>
public static class AppLog
{
    private static readonly object Lock = new();

    public static string Folder { get; set; } = "Logs";

    /// <summary>Delete log files older than this many days. 0 = keep forever.</summary>
    public static int KeepDays { get; set; } = 30;

    private static DateTime _lastCleanup = DateTime.MinValue;

    public static event Action<LogEntry>? Written;

    public static void Info(string message, string? lineId = null) => Write("INFO", message, lineId);
    public static void Warn(string message, string? lineId = null) => Write("WARN", message, lineId);
    public static void Error(string message, string? lineId = null) => Write("ERROR", message, lineId);

    private static void Write(string level, string message, string? lineId)
    {
        var entry = new LogEntry(DateTime.Now, level, lineId, message);
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, $"{entry.Time:yyyy-MM-dd}.log"), entry.Text + Environment.NewLine);
                if (_lastCleanup.Date != DateTime.Today) Cleanup();
            }
        }
        catch
        {
            // Logging must never crash the program.
        }
        Written?.Invoke(entry);
    }

    private static void Cleanup()
    {
        _lastCleanup = DateTime.Now;
        if (KeepDays <= 0) return;
        var cutoff = DateTime.Today.AddDays(-KeepDays);
        foreach (var f in Directory.EnumerateFiles(Folder, "????-??-??.log"))
        {
            if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
                && d < cutoff)
            {
                try { File.Delete(f); } catch { /* ignore */ }
            }
        }
    }
}
