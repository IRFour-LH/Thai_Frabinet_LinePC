using AoiLine.Core.Services;

namespace AoiLine.Agent;

/// <summary>agent.json - the only settings kept on the line PC.</summary>
public class AgentSettings
{
    /// <summary>Address of the Line PC Server, e.g. http://LINE1-PC:5000</summary>
    public string ServerUrl { get; set; } = "http://localhost:5000";

    /// <summary>Short id of this line, e.g. L3 (letters, numbers, - or _).</summary>
    public string LineId { get; set; } = "L1";

    /// <summary>Display name, e.g. "Line 3". Used when the server first sees this line.</summary>
    public string LineName { get; set; } = "Line 1";

    /// <summary>Folder where the AOI machine writes its result files.</summary>
    public string WatchFolder { get; set; } = @"D:\AOI RESULT FILE";

    public string FilePattern { get; set; } = "*.TST";
    public bool IncludeSubfolders { get; set; }

    /// <summary>File must not have changed for this many seconds before it is forwarded.</summary>
    public int FileStableSeconds { get; set; } = 3;

    /// <summary>Backup scan of the folder (new files are also seen instantly by the folder watcher).</summary>
    public int ScanIntervalSeconds { get; set; } = 5;

    /// <summary>How often the agent reports to the server.</summary>
    public int HeartbeatSeconds { get; set; } = 5;

    /// <summary>Only used if the server's "only files after Start" option is OFF: skip files older than this. 0 = no limit.</summary>
    public int IgnoreFilesOlderThanDays { get; set; } = 1;

    /// <summary>Delete the agent's own daily forwarded lists older than this. AOI files are never deleted. 0 = keep forever.</summary>
    public int KeepDays { get; set; } = 30;

    /// <summary>Port of this agent's status/settings page (http://localhost:PORT). Restart the agent after changing.</summary>
    public int LocalPort { get; set; } = 5001;
}

public sealed class AgentSettingsStore
{
    private readonly object _lock = new();

    public AgentSettingsStore(string path)
    {
        FilePath = path;
        try
        {
            Current = JsonFile.Read<AgentSettings>(path) ?? new AgentSettings();
            if (!File.Exists(path)) JsonFile.Write(path, Current);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Cannot read {path}, using defaults: {ex.Message}");
            Current = new AgentSettings();
        }
    }

    public string FilePath { get; }
    public string Folder => Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
    public AgentSettings Current { get; private set; }
    public event Action? Changed;

    public void Save(AgentSettings s)
    {
        lock (_lock)
        {
            JsonFile.Write(FilePath, s);
            Current = s;
        }
        Changed?.Invoke();
    }
}
