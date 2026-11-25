using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AoiLine.Core.Services;

namespace AoiLine.Server;

/// <summary>Owns one <see cref="LineWorker"/> per line and runs them in the background.</summary>
public sealed class LineManager : BackgroundService
{
    public static readonly Regex ValidId = new(@"^[A-Za-z0-9_-]{1,20}$", RegexOptions.Compiled);

    private readonly SettingsStore _store;
    private readonly EventHub _hub;
    private readonly string _dataRoot;
    private readonly ConcurrentDictionary<string, (LineWorker Worker, CancellationTokenSource Cts, Task Task)> _lines =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationToken _stopping;
    private readonly Dictionary<string, bool> _lastOnline = new(StringComparer.OrdinalIgnoreCase);

    public LineManager(SettingsStore store, EventHub hub, DataRoot dataRoot)
    {
        _store = store;
        _hub = hub;
        _dataRoot = dataRoot.Path;
        AppLog.Written += e =>
        {
            if (e.LineId != null && _lines.TryGetValue(e.LineId, out var l)) l.Worker.AddLog(e);
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        foreach (var line in _store.Current.Lines) StartWorker(line.Id);
        AppLog.Info($"Server started with {_lines.Count} line(s): {string.Join(", ", _lines.Keys)}");

        var lastCleanup = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Online/offline changes are only noticed by time passing, so check them here.
            foreach (var (id, l) in _lines)
            {
                var online = l.Worker.IsAgentOnline();
                if (_lastOnline.TryGetValue(id, out var was) && was && !online)
                    AppLog.Warn("Agent offline (no heartbeat)", id);
                if (!_lastOnline.TryGetValue(id, out was) || was != online) _hub.Publish(id);
                _lastOnline[id] = online;
            }
            if (lastCleanup.Date != DateTime.Today)
            {
                lastCleanup = DateTime.Now;
                foreach (var l in _lines.Values) l.Worker.Cleanup();
            }
            try { await Task.Delay(5000, stoppingToken); } catch (OperationCanceledException) { break; }
        }

        foreach (var l in _lines.Values) l.Cts.Cancel();
        await Task.WhenAll(_lines.Values.Select(l => l.Task.ContinueWith(_ => { })));
    }

    public LineWorker? Get(string id) => _lines.TryGetValue(id, out var l) ? l.Worker : null;

    public IEnumerable<LineWorker> All() =>
        _store.Current.Lines.OrderBy(l => l.Order).ThenBy(l => l.Id, StringComparer.OrdinalIgnoreCase)
            .Select(l => Get(l.Id)).Where(w => w != null).Select(w => w!);

    /// <summary>Returns the line, adding it (and saving settings) the first time its agent connects.</summary>
    public LineWorker? GetOrAdd(string id, string name)
    {
        if (!ValidId.IsMatch(id ?? "")) return null;
        var existing = Get(id!);
        if (existing != null) return existing;

        lock (_lines)
        {
            existing = Get(id!);
            if (existing != null) return existing;
            if (!_store.Current.Lines.Any(l => l.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                _store.Update(s => s.Lines.Add(new LineConfig
                {
                    Id = id!,
                    Name = string.IsNullOrWhiteSpace(name) ? id! : name.Trim(),
                    Order = s.Lines.Count == 0 ? 1 : s.Lines.Max(l => l.Order) + 1,
                }));
                AppLog.Info($"New line registered by its agent: {id} ({name})");
            }
            var w = StartWorker(id!);
            _hub.Publish("*");
            return w;
        }
    }

    /// <summary>Removes the line from the server list. Its data folder is kept.</summary>
    public bool Remove(string id)
    {
        if (!_lines.TryRemove(id, out var l)) return false;
        l.Cts.Cancel();
        _store.Update(s => s.Lines.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
        AppLog.Info($"Line {id} removed from the server list (its files under Lines\\{id} are kept)");
        _hub.Publish("*");
        return true;
    }

    /// <summary>After the settings page saved: start workers for new lines, stop removed ones.</summary>
    public void SyncWithSettings()
    {
        foreach (var line in _store.Current.Lines)
            if (Get(line.Id) == null) StartWorker(line.Id);
        foreach (var id in _lines.Keys.ToList())
        {
            if (_store.Current.Lines.Any(l => l.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
            if (_lines.TryRemove(id, out var l))
            {
                l.Cts.Cancel();
                AppLog.Info($"Line {id} removed on the settings page (its files under Lines\\{id} are kept)");
            }
        }
        _hub.Publish("*");
    }

    private LineWorker StartWorker(string id)
    {
        var worker = new LineWorker(id, _dataRoot, () => _store.Current, _hub.Publish);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        var task = Task.Run(() => worker.RunAsync(cts.Token));
        _lines[id] = (worker, cts, task);
        return worker;
    }
}

/// <summary>Folder that holds settings.json, Lines\ and Logs\.</summary>
public record DataRoot(string Path);
