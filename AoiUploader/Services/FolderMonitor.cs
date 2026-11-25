using System.Collections.Concurrent;
using AoiUploader.Models;

namespace AoiUploader.Services;

public enum MonitorState { Stopped, Watching, Warning, Error }

/// <summary>
/// Watches the folder (FileSystemWatcher + periodic scan as a safety net),
/// waits until each file is fully written, checks it belongs to the value the
/// operator entered (Recipe / Work Order...), then processes it once.
/// Original files are never moved or changed.
/// </summary>
public sealed class FolderMonitor : IDisposable
{
    private readonly ProcessedTracker _tracker;
    private AppSettings _settings;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly SemaphoreSlim _trigger = new(0, int.MaxValue);
    private readonly SemaphoreSlim _processLock = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTime Next, int Count)> _backoff = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _seenThisRun = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _mismatched = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _forced = new();
    private MonitorState _state = MonitorState.Stopped;
    private string _stateMessage = "";

    public FolderMonitor(ProcessedTracker tracker, AppSettings settings)
    {
        _tracker = tracker;
        _settings = settings;
    }

    public event Action<FileResult>? FileProcessed;
    public event Action<MonitorState, string>? StateChanged;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Value entered by the operator for the current run.</summary>
    public string InputValue { get; private set; } = "";

    /// <summary>Time Start was pressed.</summary>
    public DateTime RunStart { get; private set; }

    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        _seenThisRun.Clear();
        _mismatched.Clear();
        _backoff.Clear();
        if (IsRunning)
        {
            SetupWatcher();
            TriggerScan();
        }
    }

    /// <summary>Extra values entered by the operator for the current run (e.g. WorkOrder).</summary>
    public IReadOnlyDictionary<string, string> Extras { get; private set; } = new Dictionary<string, string>();

    public void Start(string inputValue, IReadOnlyDictionary<string, string>? extras = null)
    {
        if (IsRunning) return;
        if (string.IsNullOrWhiteSpace(inputValue))
            throw new ArgumentException("Input value is required", nameof(inputValue));

        InputValue = inputValue.Trim();
        Extras = new Dictionary<string, string>(extras ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        RunStart = DateTime.Now;
        _cts = new CancellationTokenSource();
        _backoff.Clear();
        _seenThisRun.Clear();
        _mismatched.Clear();
        SetupWatcher();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token));
        var only = _settings.Input.OnlyFilesAfterStart ? ", only files after start" : "";
        var extraText = string.Concat(Extras.Select(kv => $", {kv.Key} = '{kv.Value}'"));
        AppLogger.Info($"Started: {_settings.Input.Label} = '{InputValue}'{extraText}, folder '{_settings.General.WatchFolder}' ({_settings.General.FilePattern}{only})");
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        DisposeWatcher();
        try { if (_loop != null) await _loop; } catch { /* ignore */ }
        _loop = null;
        _cts.Dispose();
        _cts = null;
        SetState(MonitorState.Stopped, "Stopped");
        AppLogger.Info($"Stopped ({_settings.Input.Label} '{InputValue}')");
    }

    public void TriggerScan() => _trigger.Release();

    /// <summary>Files of this run that failed to send and are waiting to retry.</summary>
    public IReadOnlyList<string> PendingRetryFiles => _backoff.Keys.OrderBy(k => k).ToList();

    /// <summary>Forget that a file was sent and send it again (only while running).</summary>
    public void Resend(string filePath)
    {
        if (!IsRunning) return;
        var name = Path.GetFileName(filePath);
        _tracker.Remove(name);
        _seenThisRun.TryRemove(name, out _);
        _mismatched.TryRemove(name, out _);
        _backoff.TryRemove(name, out _);
        _forced.Enqueue(filePath);
        AppLogger.Info($"Resend requested: {name}");
        TriggerScan();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var g = _settings.General;
            var wait = TimeSpan.FromSeconds(Math.Max(1, g.ScanIntervalSeconds));
            try
            {
                bool waitingFiles = await ScanOnceAsync(ct);
                if (waitingFiles)
                {
                    var quick = TimeSpan.FromSeconds(Math.Max(1, g.FileStableSeconds));
                    if (quick < wait) wait = quick;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                SetState(MonitorState.Error, "Scan error: " + ex.Message);
                AppLogger.Error("Scan error: " + ex.Message);
            }

            try { await _trigger.WaitAsync(wait, ct); }
            catch (OperationCanceledException) { break; }
            while (_trigger.CurrentCount > 0) _trigger.Wait(0); // collapse bursts of watcher events
        }
    }

    /// <returns>true if some files are still being written (scan again soon).</returns>
    private async Task<bool> ScanOnceAsync(CancellationToken ct)
    {
        await ProcessForcedAsync(ct);

        var s = _settings;
        var g = s.General;
        if (string.IsNullOrWhiteSpace(g.WatchFolder) || !Directory.Exists(g.WatchFolder))
        {
            DisposeWatcher();
            SetState(MonitorState.Error, $"Folder not found: {g.WatchFolder}");
            return false;
        }
        if (_watcher == null) SetupWatcher();

        var now = DateTime.Now;
        var option = g.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var pattern = string.IsNullOrWhiteSpace(g.FilePattern) ? "*.*" : g.FilePattern;
        var candidates = new List<FileInfo>();

        foreach (var fi in new DirectoryInfo(g.WatchFolder).EnumerateFiles(pattern, option))
        {
            var name = fi.Name;
            if (_tracker.IsProcessed(name)) continue;
            if (_mismatched.ContainsKey(name)) continue;
            if (!s.Api.Enabled && _seenThisRun.ContainsKey(name)) continue;
            if (_backoff.TryGetValue(name, out var b) && b.Next > now) continue;
            if (s.Input.OnlyFilesAfterStart && FileTime(fi) < RunStart) continue;
            if (g.IgnoreFilesOlderThanDays > 0 && fi.LastWriteTime < now.AddDays(-g.IgnoreFilesOlderThanDays)) continue;
            candidates.Add(fi);
        }

        bool waiting = false;
        foreach (var fi in candidates.OrderBy(f => f.LastWriteTime))
        {
            ct.ThrowIfCancellationRequested();
            fi.Refresh();
            if (!fi.Exists) continue;
            if ((DateTime.Now - fi.LastWriteTime).TotalSeconds < g.FileStableSeconds || IsLocked(fi.FullName))
            {
                waiting = true;
                continue;
            }
            await ProcessOneAsync(fi.FullName, s, ct);
        }

        var label = $"{s.Input.Label}: {InputValue}";
        var failing = _backoff.Count;
        if (!s.Api.Enabled)
            SetState(MonitorState.Warning, $"Watching {label} · API OFF");
        else if (failing > 0)
            SetState(MonitorState.Warning, $"Watching {label} · {failing} file(s) waiting to retry");
        else
            SetState(MonitorState.Watching, $"Watching {label}");
        return waiting;
    }

    /// <summary>A file counts as "after Start" if it was created OR last written after Start.</summary>
    private static DateTime FileTime(FileInfo fi) =>
        fi.CreationTime > fi.LastWriteTime ? fi.CreationTime : fi.LastWriteTime;

    private async Task ProcessForcedAsync(CancellationToken ct)
    {
        while (_forced.TryDequeue(out var path))
        {
            if (File.Exists(path)) await ProcessOneAsync(path, _settings, ct);
            else AppLogger.Warn($"Resend: file not found {path}");
        }
    }

    private async Task ProcessOneAsync(string path, AppSettings s, CancellationToken ct)
    {
        await _processLock.WaitAsync(ct);
        try
        {
            var name = Path.GetFileName(path);
            if (s.Api.Enabled && _tracker.IsProcessed(name)) return;

            var r = await FileProcessor.ProcessAsync(path, s, s.Api.Enabled, InputValue, Extras, ct);

            switch (r.Kind)
            {
                case ResultKind.Mismatch:
                    // Not marked as processed: it can still be sent later in a run for its own value.
                    _mismatched[name] = 0;
                    AppLogger.Warn($"{name}: {r.Message}");
                    break;
                case ResultKind.ParsedOnly:
                    _seenThisRun[name] = 0;
                    AppLogger.Info($"{name}: parsed {r.Rows} row(s), PCB qty={r.Qty} (API off)");
                    break;
                case ResultKind.Sent:
                    _tracker.MarkProcessed(r);
                    _backoff.TryRemove(name, out _);
                    AppLogger.Info($"{name}: sent {r.Rows} row(s), PCB qty={r.Qty}, G={r.Good} NG={r.Ng}, HTTP {r.HttpStatus}");
                    break;
                default:
                    int count = _backoff.TryGetValue(name, out var b) ? b.Count + 1 : 1;
                    int delay = Math.Max(5, s.Api.RetryDelaySeconds);
                    _backoff[name] = (DateTime.Now.AddSeconds(delay), count);
                    r.Message += $" (retry #{count} in {delay}s)";
                    AppLogger.Error($"{name}: {r.Status} - {r.Message}");
                    break;
            }
            FileProcessed?.Invoke(r);
        }
        finally
        {
            _processLock.Release();
        }
    }

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

    private void SetupWatcher()
    {
        DisposeWatcher();
        var g = _settings.General;
        if (string.IsNullOrWhiteSpace(g.WatchFolder) || !Directory.Exists(g.WatchFolder)) return;
        try
        {
            var w = new FileSystemWatcher(g.WatchFolder, string.IsNullOrWhiteSpace(g.FilePattern) ? "*.*" : g.FilePattern)
            {
                IncludeSubdirectories = g.IncludeSubfolders,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += (_, _) => TriggerScan();
            w.Changed += (_, _) => TriggerScan();
            w.Renamed += (_, _) => TriggerScan();
            w.Error += (_, e) =>
            {
                AppLogger.Warn("Folder watcher error, falling back to scan: " + e.GetException().Message);
                DisposeWatcher(); // re-created on next scan
                TriggerScan();
            };
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Cannot start folder watcher (scan still works): " + ex.Message);
        }
    }

    private void DisposeWatcher()
    {
        var w = Interlocked.Exchange(ref _watcher, null);
        if (w == null) return;
        w.EnableRaisingEvents = false;
        w.Dispose();
    }

    private void SetState(MonitorState state, string message)
    {
        if (state == _state && message == _stateMessage) return;
        _state = state;
        _stateMessage = message;
        StateChanged?.Invoke(state, message);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        DisposeWatcher();
    }
}
