using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AoiLine.Core.Services;

namespace AoiLine.Agent;

/// <summary>What the agent page shows.</summary>
public class AgentStatus
{
    public bool Connected { get; set; }
    public DateTime? LastContact { get; set; }
    public string LastError { get; set; } = "";
    public bool Running { get; set; }
    public DateTime? RunStart { get; set; }
    public bool OnlyFilesAfterStart { get; set; } = true;
    public string LineName { get; set; } = "";
    public string InputLabel { get; set; } = "";
    public string InputValue { get; set; } = "";
    public Dictionary<string, string> Extras { get; set; } = new();
    public bool FolderExists { get; set; }
    public int Pending { get; set; }
    public string LastForwarded { get; set; } = "";
    public int ForwardedToday { get; set; }
    public List<string> Recent { get; set; } = new();
    public string Version { get; set; } = "";
}

/// <summary>
/// Watches the local AOI folder and forwards new files to the server.
/// Only forwards while the line is running on the server (and, by default, only files created after Start).
/// AOI files are only read - never moved, changed or deleted.
/// </summary>
public sealed class AgentWorker : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AgentSettingsStore _store;
    private readonly DailyFileList _forwarded = new();
    private readonly SemaphoreSlim _trigger = new(0, int.MaxValue);
    private readonly object _lock = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private FileSystemWatcher? _watcher;
    private string _watcherKey = "";
    private DateTime _nextHeartbeat = DateTime.MinValue;
    private DateTime? _runStartAgentUtc;

    public AgentWorker(AgentSettingsStore store)
    {
        _store = store;
        _store.Changed += () =>
        {
            _forwarded.Load(Path.Combine(_store.Folder, "Forwarded"), _store.Current.KeepDays);
            _nextHeartbeat = DateTime.MinValue;
            TriggerNow();
        };
        Status.Version = typeof(AgentWorker).Assembly.GetName().Version?.ToString(3) ?? "";
    }

    public AgentStatus Status { get; } = new();

    public void TriggerNow() => _trigger.Release();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _forwarded.Load(Path.Combine(_store.Folder, "Forwarded"), _store.Current.KeepDays);
        Status.ForwardedToday = CountToday();
        var s0 = _store.Current;
        AppLog.Info($"Agent started: line {s0.LineId} ({s0.LineName}), folder '{s0.WatchFolder}', server {s0.ServerUrl}");

        while (!ct.IsCancellationRequested)
        {
            var s = _store.Current;
            bool waiting = false;
            try
            {
                UpdateWatcher(s);
                if (DateTime.UtcNow >= _nextHeartbeat) await HeartbeatAsync(s, ct);
                // While the server is unreachable keep counting waiting files (last known run), but do not send.
                if (Status.Running) waiting = await ForwardAsync(s, send: Status.Connected, ct);
                else Status.Pending = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Status.LastError = ex.Message;
                AppLog.Error("Agent error: " + ex.Message);
            }

            var wait = TimeSpan.FromSeconds(Math.Max(1, waiting ? Math.Min(s.FileStableSeconds, s.ScanIntervalSeconds) : s.ScanIntervalSeconds));
            try { await _trigger.WaitAsync(wait, ct); }
            catch (OperationCanceledException) { break; }
            while (_trigger.CurrentCount > 0) _trigger.Wait(0);
        }
        _watcher?.Dispose();
    }

    // ---------------------------------------------------------------- heartbeat

    public async Task<(bool Ok, string Message)> HeartbeatAsync(AgentSettings s, CancellationToken ct)
    {
        _nextHeartbeat = DateTime.UtcNow.AddSeconds(Math.Max(2, s.HeartbeatSeconds));
        var body = new
        {
            lineId = s.LineId.Trim(),
            lineName = s.LineName.Trim(),
            machine = Environment.MachineName,
            version = Status.Version,
            watchFolder = s.WatchFolder,
            filePattern = s.FilePattern,
            folderExists = Directory.Exists(s.WatchFolder),
            pending = Status.Running ? Status.Pending : 0,
            lastForwarded = Status.LastForwarded,
            lastError = Status.LastError,
            agentNowUtc = DateTime.UtcNow,
        };
        Status.FolderExists = body.folderExists;
        try
        {
            using var resp = await _http.PostAsJsonAsync(Url(s, "/api/agent/heartbeat"), body, Json, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                return Disconnected($"Server answered HTTP {(int)resp.StatusCode}: {err}");
            }
            var hb = await resp.Content.ReadFromJsonAsync<HeartbeatResponse>(Json, ct) ?? new HeartbeatResponse();
            var wasRunning = Status.Running;
            if (!Status.Connected) AppLog.Info($"Connected to server {s.ServerUrl}");
            Status.Connected = true;
            Status.LastContact = DateTime.Now;
            if (Status.LastError.StartsWith("Server")) Status.LastError = "";
            Status.Running = hb.Running;
            _runStartAgentUtc = hb.RunStartAgentUtc;
            Status.RunStart = hb.RunStartAgentUtc?.ToLocalTime();
            Status.OnlyFilesAfterStart = hb.OnlyFilesAfterStart;
            Status.LineName = hb.LineName;
            Status.InputLabel = hb.InputLabel;
            Status.InputValue = hb.InputValue;
            Status.Extras = hb.Extras ?? new();
            if (hb.Running && !wasRunning) AppLog.Info($"Line started on server: {hb.InputLabel} {hb.InputValue}");
            if (!hb.Running && wasRunning) AppLog.Info("Line stopped on server");
            return (true, hb.Running ? $"Connected - line running ({hb.InputLabel} {hb.InputValue})" : "Connected - line stopped");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException or InvalidOperationException)
        {
            return Disconnected($"Server not reachable ({s.ServerUrl}): {ex.Message}");
        }
    }

    private (bool, string) Disconnected(string message)
    {
        if (Status.Connected || Status.LastError != message) AppLog.Warn(message);
        Status.Connected = false;
        Status.LastError = message;
        return (false, message);
    }

    // ---------------------------------------------------------------- forwarding

    /// <returns>true if some files are still being written (check again soon).</returns>
    private async Task<bool> ForwardAsync(AgentSettings s, bool send, CancellationToken ct)
    {
        if (!Directory.Exists(s.WatchFolder))
        {
            Status.FolderExists = false;
            Status.Pending = 0;
            return false;
        }
        Status.FolderExists = true;

        var option = s.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var pattern = string.IsNullOrWhiteSpace(s.FilePattern) ? "*.*" : s.FilePattern;
        var oldest = Status.OnlyFilesAfterStart
            ? _runStartAgentUtc ?? DateTime.MaxValue
            : s.IgnoreFilesOlderThanDays > 0 ? DateTime.UtcNow.AddDays(-s.IgnoreFilesOlderThanDays) : DateTime.MinValue;

        var candidates = new DirectoryInfo(s.WatchFolder).EnumerateFiles(pattern, option)
            .Where(f => !_forwarded.Contains(f.Name) && FileTimeUtc(f) >= oldest)
            .OrderBy(FileTimeUtc)
            .ToList();
        Status.Pending = candidates.Count;
        if (!send) return false;

        bool waiting = false;
        foreach (var fi in candidates)
        {
            ct.ThrowIfCancellationRequested();
            fi.Refresh();
            if (!fi.Exists) { Status.Pending--; continue; }
            if ((DateTime.Now - fi.LastWriteTime).TotalSeconds < s.FileStableSeconds || IsLocked(fi.FullName))
            {
                waiting = true;
                continue;
            }

            var body = new
            {
                lineId = s.LineId.Trim(),
                lineName = s.LineName.Trim(),
                fileName = fi.Name,
                fileTimeUtc = FileTimeUtc(fi),
                content = TstParser.ReadText(fi.FullName),
            };
            HttpResponseMessage resp;
            try
            {
                resp = await _http.PostAsJsonAsync(Url(s, "/api/agent/files"), body, Json, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Disconnected($"Server not reachable while sending {fi.Name}: {ex.Message}");
                return false; // keep the file; it is sent when the server is back
            }

            using (resp)
            {
                var r = await resp.Content.ReadFromJsonAsync<FileUploadResponse>(Json, ct);
                if (r == null || (!resp.IsSuccessStatusCode && r.Status != "rejected"))
                {
                    Status.LastError = $"Server answered HTTP {(int)resp.StatusCode} for {fi.Name}";
                    return false;
                }
                // accepted / duplicate / ignored / rejected: the server has decided - do not send again.
                _forwarded.Add(fi.Name, $"line={s.LineId} | status={r.Status} | {r.Message}");
                Status.Pending--;
                Status.LastForwarded = fi.Name;
                lock (_lock)
                {
                    Status.Recent.Insert(0, $"{DateTime.Now:HH:mm:ss}  {fi.Name}  →  {r.Status} ({r.Message})");
                    if (Status.Recent.Count > 50) Status.Recent.RemoveAt(Status.Recent.Count - 1);
                }
                Status.ForwardedToday = CountToday();
                AppLog.Info($"{fi.Name}: forwarded → {r.Status} ({r.Message})");
                if (r.Status == "ignored")
                {
                    Status.Running = false; // server says the line is stopped
                    return false;
                }
            }
        }
        return waiting;
    }

    // ---------------------------------------------------------------- helpers

    private void UpdateWatcher(AgentSettings s)
    {
        var key = $"{s.WatchFolder}|{s.FilePattern}|{s.IncludeSubfolders}";
        if (_watcher != null && key == _watcherKey) return;
        _watcher?.Dispose();
        _watcher = null;
        _watcherKey = key;
        if (!Directory.Exists(s.WatchFolder)) { _watcherKey = ""; return; } // retried every loop
        try
        {
            var w = new FileSystemWatcher(s.WatchFolder, string.IsNullOrWhiteSpace(s.FilePattern) ? "*.*" : s.FilePattern)
            {
                IncludeSubdirectories = s.IncludeSubfolders,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += (_, _) => TriggerNow();
            w.Changed += (_, _) => TriggerNow();
            w.Renamed += (_, _) => TriggerNow();
            w.Error += (_, _) => { _watcherKey = ""; TriggerNow(); };
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Folder watcher not available (backup scan still works): " + ex.Message);
        }
    }

    private int CountToday()
    {
        try
        {
            var f = _forwarded.TodayFile;
            return File.Exists(f) ? File.ReadLines(f).Count(l => l.Contains('|')) : 0;
        }
        catch { return 0; }
    }

    /// <summary>A file counts as "after Start" if it was created OR last written after Start.</summary>
    private static DateTime FileTimeUtc(FileInfo f) =>
        f.CreationTimeUtc > f.LastWriteTimeUtc ? f.CreationTimeUtc : f.LastWriteTimeUtc;

    private static bool IsLocked(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static string Url(AgentSettings s, string path) => s.ServerUrl.TrimEnd('/') + path;

    private sealed class HeartbeatResponse
    {
        public bool Running { get; set; }
        public DateTime? RunStartAgentUtc { get; set; }
        public bool OnlyFilesAfterStart { get; set; } = true;
        public string LineName { get; set; } = "";
        public string InputLabel { get; set; } = "";
        public string InputValue { get; set; } = "";
        public Dictionary<string, string>? Extras { get; set; }
    }

    private sealed record FileUploadResponse(string Status, string Message);
}
