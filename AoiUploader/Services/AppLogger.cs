namespace AoiUploader.Services;

/// <summary>Writes a daily log file and notifies the UI.</summary>
public static class AppLogger
{
    private static readonly object Lock = new();

    public static string Folder { get; set; } = "Logs";

    public static event Action<string>? Logged;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, $"{now:yyyy-MM-dd}.log"), line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never crash the program.
        }
        Logged?.Invoke(line);
    }
}
