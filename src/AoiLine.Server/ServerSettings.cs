using AoiLine.Core.Models;
using AoiLine.Core.Services;

namespace AoiLine.Server;

/// <summary>All server settings (settings.json next to the server program).</summary>
public class ServerSettings
{
    /// <summary>Web / agent port. Changing it needs a server restart.</summary>
    public int Port { get; set; } = 5000;

    /// <summary>Delete processed lists, archived files and logs older than this. 0 = keep forever.</summary>
    public int KeepDays { get; set; } = 30;

    /// <summary>An agent not heard from for this long is shown as offline.</summary>
    public int AgentOfflineSeconds { get; set; } = 30;

    /// <summary>Lines known to the server. A line is added automatically when its agent first connects.</summary>
    public List<LineConfig> Lines { get; set; } = new();

    /// <summary>Column mapping, API, qty mode, operator inputs - the same for all lines.</summary>
    public ProcessingSettings Processing { get; set; } = ProcessingSettings.CreateDefault();
}

public class LineConfig
{
    /// <summary>Short id used in URLs and sent to the API as {{LineId}}, e.g. "L3".</summary>
    public string Id { get; set; } = "";

    /// <summary>Display name sent to the API as {{LineName}}, e.g. "Line 3".</summary>
    public string Name { get; set; } = "";

    /// <summary>Sort order on the overview page.</summary>
    public int Order { get; set; }
}

/// <summary>Loads and saves settings.json; other parts read <see cref="Current"/>.</summary>
public sealed class SettingsStore
{
    private readonly object _lock = new();

    public SettingsStore(string path)
    {
        FilePath = path;
        try
        {
            Current = JsonFile.Read<ServerSettings>(path) ?? new ServerSettings();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Cannot read {path}, using defaults: {ex.Message}");
            Current = new ServerSettings();
            return; // do not overwrite a broken file - let the admin fix it
        }
        if (!File.Exists(path)) JsonFile.Write(path, Current);
    }

    public string FilePath { get; }

    /// <summary>Always replaced as a whole, so readers can keep a reference safely.</summary>
    public ServerSettings Current { get; private set; }

    public event Action? Changed;

    public void Save(ServerSettings settings)
    {
        lock (_lock)
        {
            JsonFile.Write(FilePath, settings);
            Current = settings;
        }
        Changed?.Invoke();
    }

    /// <summary>Change a copy of the current settings and save it.</summary>
    public void Update(Action<ServerSettings> change)
    {
        lock (_lock)
        {
            var copy = JsonFile.Clone(Current);
            change(copy);
            JsonFile.Write(FilePath, copy);
            Current = copy;
        }
        Changed?.Invoke();
    }
}
