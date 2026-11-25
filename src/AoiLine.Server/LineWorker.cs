using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AoiLine.Core.Models;
using AoiLine.Core.Services;

namespace AoiLine.Server;

/// <summary>Saved to Lines\{id}\state.json so a run survives a server restart.</summary>
public class LineState
{
    public bool Running { get; set; }
    public string InputValue { get; set; } = "";
    public Dictionary<string, string> Extras { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime? RunStartUtc { get; set; }
    public DateTime? RunStopUtc { get; set; }
    public int RunFiles { get; set; }
    public int RunQty { get; set; }
    public int RunGood { get; set; }
    public int RunNg { get; set; }
    public int RunFailures { get; set; }
    public string LastInputValue { get; set; } = "";
    public Dictionary<string, string> LastExtras { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string WarningText { get; set; } = "";
    public int WarningCount { get; set; }
    public List<FileResult> Recent { get; set; } = new();
}

/// <summary>
/// One production line on the server:
///   Lines\{id}\state.json       - current run (survives restart)
///   Lines\{id}\inbox\           - files received from the agent, waiting to be sent
///   Lines\{id}\archive\{date}\  - files after processing (kept for Resend, cleaned after KeepDays)
///   Lines\{id}\processed\       - daily lists of files sent to the API
/// </summary>
public sealed class LineWorker
{
    private const int MaxRecent = 100;
    private const int MaxLogs = 200;
    private static readonly Regex SafeName = new(@"^[^\\/:*?""<>|]+$", RegexOptions.Compiled);

    private readonly object _lock = new();
    private readonly Func<ServerSettings> _settings;
    private readonly Action<string> _notify;
    private readonly string _dir, _inbox, _archive, _statePath;
    private readonly DailyFileList _processed = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly ConcurrentDictionary<string, (DateTime Next, int Count)> _backoff = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _logs = new();
    private LineState _state;
    private CountTotals _today = new(0, 0, 0, 0);
    private DateTime _todayDate = DateTime.Today;

    // Agent info (memory only - refreshed by every heartbeat)
    private HeartbeatRequest? _agent;
    private DateTime? _agentLastSeenUtc;

    public LineWorker(string id, string dataRoot, Func<ServerSettings> settings, Action<string> notify)
    {
        Id = id;
        _settings = settings;
        _notify = notify;
        _dir = Path.Combine(dataRoot, "Lines", id);
        _inbox = Path.Combine(_dir, "inbox");
        _archive = Path.Combine(_dir, "archive");
        _statePath = Path.Combine(_dir, "state.json");
        Directory.CreateDirectory(_inbox);
        Directory.CreateDirectory(_archive);

        try { _state = JsonFile.Read<LineState>(_statePath) ?? new LineState(); }
        catch (Exception ex)
        {
            AppLog.Error($"Cannot read line state, starting stopped: {ex.Message}", id);
            _state = new LineState();
        }
        _processed.Load(Path.Combine(_dir, "processed"), _settings().KeepDays);
        _today = ToTotals(_processed.GetTodaySummary());
        if (_state.Running)
            AppLog.Info($"Run restored after restart: {InputLabel} '{_state.InputValue}'{ExtrasText(_state.Extras)}, started {ToLocal(_state.RunStartUtc):yyyy-MM-dd HH:mm:ss}", id);
        LoadTodayLogs();
    }

    /// <summary>After a restart, show today's log lines of this line again.</summary>
    private void LoadTodayLogs()
    {
        try
        {
            var file = Path.Combine(AppLog.Folder, $"{DateTime.Now:yyyy-MM-dd}.log");
            if (!File.Exists(file)) return;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var tag = $" [{Id}] ";
            var lines = new List<string>();
            string? l;
            while ((l = sr.ReadLine()) != null)
                if (l.Contains(tag, StringComparison.OrdinalIgnoreCase)) lines.Add(l);
            lock (_logs)
                foreach (var x in lines.TakeLast(MaxLogs)) _logs.AddFirst(x);
        }
        catch { /* logs are only informative */ }
    }

    public string Id { get; }

    public string Name =>
        _settings().Lines.FirstOrDefault(l => l.Id.Equals(Id, StringComparison.OrdinalIgnoreCase))?.Name is { Length: > 0 } n ? n : Id;

    private string InputLabel => _settings().Processing.Input.Label;

    // ================================================================ agent side

    public HeartbeatResponse Heartbeat(HeartbeatRequest req)
    {
        bool wasOnline;
        lock (_lock)
        {
            wasOnline = IsAgentOnline();
            var changed = _agent == null || _agent.Pending != req.Pending || _agent.FolderExists != req.FolderExists ||
                          _agent.LastError != req.LastError || _agent.WatchFolder != req.WatchFolder || _agent.LastForwarded != req.LastForwarded;
            _agent = req;
            _agentLastSeenUtc = DateTime.UtcNow;
            if (!wasOnline) AppLog.Info($"Agent online ({req.Machine}, folder {req.WatchFolder})", Id);
            if (changed || !wasOnline) _notify(Id);

            // Express the run start in the agent's clock, so a clock difference between PCs does not matter.
            var offset = req.AgentNowUtc == default ? TimeSpan.Zero : req.AgentNowUtc - DateTime.UtcNow;
            return new HeartbeatResponse
            {
                Running = _state.Running,
                RunStartAgentUtc = _state.RunStartUtc + offset,
                OnlyFilesAfterStart = _settings().Processing.Input.OnlyFilesAfterStart,
                ServerNowUtc = DateTime.UtcNow,
            };
        }
    }

    public FileUploadResponse Receive(FileUploadRequest req)
    {
        var name = Path.GetFileName(req.FileName ?? "");
        if (name.Length == 0 || name.Length > 200 || !SafeName.IsMatch(name) || name is "." or "..")
            return new FileUploadResponse("rejected", "Invalid file name");

        lock (_lock)
        {
            _agentLastSeenUtc = DateTime.UtcNow;
            if (!_state.Running)
            {
                AppLog.Info($"{name}: received while line is stopped - ignored", Id);
                return new FileUploadResponse("ignored", "Line is not running");
            }
            if (_processed.Contains(name))
                return new FileUploadResponse("duplicate", "Already sent");
            var path = Path.Combine(_inbox, name);
            if (File.Exists(path))
                return new FileUploadResponse("duplicate", "Already queued");

            // Save first: once the agent gets "accepted" the file is safe on the server.
            File.WriteAllText(path + ".part", req.Content ?? "", Encoding.UTF8);
            File.Move(path + ".part", path, overwrite: true);
            AppLog.Info($"{name}: received from agent", Id);
        }
        _signal.Release();
        _notify(Id);
        return new FileUploadResponse("accepted", "Saved on server");
    }

    // ================================================================ operator actions

    public (bool Ok, string Message) Start(StartRequest req)
    {
        var s = _settings().Processing.Input;
        var value = (req.InputValue ?? "").Trim();
        if (value.Length == 0) return (false, $"Please enter {s.Label}.");

        var extras = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var def in s.ExtraInputs.Where(x => !string.IsNullOrWhiteSpace(x.Key)))
        {
            var v = req.Extras?.FirstOrDefault(kv => kv.Key.Equals(def.Key, StringComparison.OrdinalIgnoreCase)).Value?.Trim() ?? "";
            if (def.Required && v.Length == 0) missing.Add(string.IsNullOrWhiteSpace(def.Label) ? def.Key : def.Label);
            extras[def.Key.Trim()] = v;
        }
        if (missing.Count > 0) return (false, "Please enter: " + string.Join(", ", missing));

        lock (_lock)
        {
            if (_state.Running) return (false, "Line is already running. Stop it first.");
            _state.Running = true;
            _state.InputValue = value;
            _state.Extras = extras;
            _state.RunStartUtc = DateTime.UtcNow;
            _state.RunStopUtc = null;
            _state.RunFiles = _state.RunQty = _state.RunGood = _state.RunNg = _state.RunFailures = 0;
            _state.WarningText = "";
            _state.WarningCount = 0;
            _state.LastInputValue = value;
            _state.LastExtras = new Dictionary<string, string>(extras, StringComparer.OrdinalIgnoreCase);
            _backoff.Clear();
            SaveState();
        }
        AppLog.Info($"Started: {s.Label} = '{value}'{ExtrasText(extras)}", Id);
        _notify(Id);
        _signal.Release();
        return (true, "Started");
    }

    public StopResponse Stop(bool force)
    {
        lock (_lock)
        {
            if (!_state.Running) return new StopResponse(true, false, new(), 0, "Already stopped");

            var queued = InboxFiles().Select(Path.GetFileName).Select(n => n!).ToList();
            int agentPending = IsAgentOnline() ? _agent?.Pending ?? 0 : 0;
            if (!force && (queued.Count > 0 || agentPending > 0))
            {
                var msg = $"{queued.Count} file(s) on the server and {agentPending} file(s) on the line PC have not been sent yet.";
                return new StopResponse(false, true, queued, agentPending, msg);
            }

            // Files still waiting are not sent after Stop (only files after Start are processed).
            foreach (var f in InboxFiles()) MoveToArchive(f);
            if (queued.Count > 0)
                AppLog.Warn($"Stopped with {queued.Count} unsent file(s): {string.Join(", ", queued)}", Id);

            _state.Running = false;
            _state.RunStopUtc = DateTime.UtcNow;
            _backoff.Clear();
            SaveState();
        }
        AppLog.Info($"Stopped ({InputLabel} '{_state.InputValue}')", Id);
        _notify(Id);
        return new StopResponse(true, false, new(), 0, "Stopped");
    }

    public (bool Ok, string Message) Resend(string fileName)
    {
        var name = Path.GetFileName(fileName ?? "");
        lock (_lock)
        {
            if (!_state.Running) return (false, "Start the line first.");
            var archived = Directory.Exists(_archive)
                ? Directory.EnumerateFiles(_archive, name, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            if (archived == null) return (false, $"{name} is no longer kept on the server (older than {_settings().KeepDays} days?).");
            _processed.Remove(name);
            _backoff.TryRemove(name, out _);
            File.Copy(archived, Path.Combine(_inbox, name), overwrite: true);
        }
        AppLog.Info($"{name}: resend requested", Id);
        _signal.Release();
        _notify(Id);
        return (true, "Queued for resend");
    }

    public void DismissWarning()
    {
        lock (_lock)
        {
            _state.WarningText = "";
            _state.WarningCount = 0;
            SaveState();
        }
        _notify(Id);
    }

    // ================================================================ processing loop

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTime.Today != _todayDate)
                {
                    _todayDate = DateTime.Today;
                    _today = ToTotals(_processed.GetTodaySummary());
                    _notify(Id);
                }
                if (_state.Running) await ProcessInboxAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                AppLog.Error("Processing error: " + ex.Message, Id);
            }

            try { await _signal.WaitAsync(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { break; }
            while (_signal.CurrentCount > 0) _signal.Wait(0);
        }
    }

    private async Task ProcessInboxAsync(CancellationToken ct)
    {
        foreach (var path in InboxFiles().OrderBy(File.GetCreationTimeUtc))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (_backoff.TryGetValue(name, out var b) && b.Next > DateTime.UtcNow) continue;

            ServerSettings settings = _settings();
            ProcessRequest req;
            lock (_lock)
            {
                if (!_state.Running || !File.Exists(path)) return;
                req = new ProcessRequest
                {
                    FileName = name,
                    Content = File.ReadAllText(path, Encoding.UTF8),
                    InputValue = _state.InputValue,
                    Extras = new Dictionary<string, string>(_state.Extras, StringComparer.OrdinalIgnoreCase),
                    LineId = Id,
                    LineName = Name,
                };
            }

            var r = await FileProcessor.ProcessAsync(req, settings.Processing, settings.Processing.Api.Enabled, ct);
            HandleResult(path, r, settings);
            _notify(Id);
        }
    }

    private void HandleResult(string path, FileResult r, ServerSettings settings)
    {
        lock (_lock)
        {
            switch (r.Kind)
            {
                case ResultKind.Sent:
                    _processed.AddSent(r);
                    MoveToArchive(path);
                    _backoff.TryRemove(r.FileName, out _);
                    _state.RunFiles++;
                    _state.RunQty += r.Qty;
                    _state.RunGood += r.Good;
                    _state.RunNg += r.Ng;
                    _today = new CountTotals(_today.Files + 1, _today.Qty + r.Qty, _today.Good + r.Good, _today.Ng + r.Ng);
                    AppLog.Info($"{r.FileName}: sent {r.Rows} row(s), PCB qty={r.Qty}, G={r.Good} NG={r.Ng}, HTTP {r.HttpStatus}", Id);
                    break;

                case ResultKind.Mismatch:
                    // Not marked as processed; kept in the archive for traceability.
                    MoveToArchive(path);
                    if (settings.Processing.Input.WarnOnMismatch)
                    {
                        _state.WarningCount++;
                        var more = _state.WarningCount > 1 ? $" ({_state.WarningCount} file(s) skipped this run)" : "";
                        _state.WarningText = $"{DateTime.Now:HH:mm:ss}  {r.FileName}: {r.Message}. Check the {InputLabel} entered.{more}";
                    }
                    AppLog.Warn($"{r.FileName}: {r.Message}", Id);
                    break;

                case ResultKind.ParsedOnly:
                    MoveToArchive(path);
                    AppLog.Info($"{r.FileName}: parsed {r.Rows} row(s), PCB qty={r.Qty} (API off - not sent)", Id);
                    break;

                default:
                    int count = _backoff.TryGetValue(r.FileName, out var b) ? b.Count + 1 : 1;
                    int delay = Math.Max(5, settings.Processing.Api.RetryDelaySeconds);
                    _backoff[r.FileName] = (DateTime.UtcNow.AddSeconds(delay), count);
                    _state.RunFailures++;
                    r.Message += $" (retry #{count} in {delay}s)";
                    AppLog.Error($"{r.FileName}: {r.Status} - {r.Message}", Id);
                    break;
            }

            // One row per file in "recent": a retry replaces the earlier row.
            _state.Recent.RemoveAll(x => x.FileName.Equals(r.FileName, StringComparison.OrdinalIgnoreCase));
            _state.Recent.Insert(0, Trim(r));
            if (_state.Recent.Count > MaxRecent) _state.Recent.RemoveRange(MaxRecent, _state.Recent.Count - MaxRecent);
            SaveState();
        }
    }

    // ================================================================ views

    public LineView GetView() => Fill(new LineView());

    public LineDetail GetDetail()
    {
        var s = _settings().Processing;
        var d = Fill(new LineDetail());
        lock (_lock)
        {
            d.Recent = _state.Recent.ToList();
            d.Logs = _logs.ToList();
            d.Inputs = new InputDefinition
            {
                Label = s.Input.Label,
                Extras = s.Input.ExtraInputs.Where(x => !string.IsNullOrWhiteSpace(x.Key)).ToList(),
                LastInputValue = s.Input.RememberLastValue ? _state.LastInputValue : "",
                LastExtras = s.Input.ExtraInputs
                    .Where(x => x.RememberLastValue && _state.LastExtras.ContainsKey(x.Key))
                    .ToDictionary(x => x.Key, x => _state.LastExtras[x.Key]),
            };
        }
        d.ApiEnabled = s.Api.Enabled;
        d.ApiInfo = s.Api.Enabled
            ? $"{s.Api.Method} {s.Api.Url} ({(s.Api.SendMode == SendMode.PerFile ? "one call per file" : "one call per row")})"
            : "OFF - files are only parsed, not sent";
        d.QtyUnit = s.Parsing.QtyMode == QtyMode.ByDistinctField ? $"distinct {s.Parsing.QtyField}" : "by row";
        d.MatchInfo = s.Input.MatchSource switch
        {
            MatchSource.FileNamePrefix => $"file name before '{s.Input.FileNameSeparator}'",
            MatchSource.RecipeLine => $"recipe line {s.Parsing.RecipeLineNumber}",
            _ => $"field '{s.Input.MatchField}'",
        } + (s.Input.OnlyFilesAfterStart ? " · only files after Start" : "");
        return d;
    }

    private T Fill<T>(T v) where T : LineView
    {
        var api = _settings().Processing.Api;
        lock (_lock)
        {
            v.Id = Id;
            v.Name = Name;
            v.Running = _state.Running;
            v.InputValue = _state.InputValue;
            v.Extras = new Dictionary<string, string>(_state.Extras);
            v.RunStart = ToLocal(_state.RunStartUtc);
            v.RunStop = ToLocal(_state.RunStopUtc);
            v.RunFiles = _state.RunFiles;
            v.RunQty = _state.RunQty;
            v.RunGood = _state.RunGood;
            v.RunNg = _state.RunNg;
            v.RunFailures = _state.RunFailures;
            v.Queued = InboxFiles().Count();
            v.RetryWaiting = _backoff.Count;
            v.WarningText = _state.WarningText;
            v.WarningCount = _state.WarningCount;
            v.Today = _today;
            v.Agent = new AgentView
            {
                Online = IsAgentOnline(),
                LastSeen = ToLocal(_agentLastSeenUtc),
                Machine = _agent?.Machine ?? "",
                Version = _agent?.Version ?? "",
                WatchFolder = _agent?.WatchFolder ?? "",
                FilePattern = _agent?.FilePattern ?? "",
                FolderExists = _agent?.FolderExists ?? false,
                Pending = _agent?.Pending ?? 0,
                LastForwarded = _agent?.LastForwarded ?? "",
                LastError = _agent?.LastError ?? "",
            };
        }

        var label = $"{InputLabel} {v.InputValue}";
        if (!v.Running)
            (v.StatusLevel, v.StatusText) = ("stopped", v.Agent.Online ? "Stopped" : "Stopped · agent offline");
        else if (!v.Agent.Online)
            (v.StatusLevel, v.StatusText) = ("error", $"Running {label} · AGENT OFFLINE - files from this line are not received");
        else if (!v.Agent.FolderExists)
            (v.StatusLevel, v.StatusText) = ("error", $"Running {label} · agent cannot find folder {v.Agent.WatchFolder}");
        else if (!api.Enabled)
            (v.StatusLevel, v.StatusText) = ("warn", $"Running {label} · API OFF");
        else if (v.RetryWaiting > 0)
            (v.StatusLevel, v.StatusText) = ("warn", $"Running {label} · {v.RetryWaiting} file(s) waiting to retry");
        else
            (v.StatusLevel, v.StatusText) = ("ok", $"Running {label}");
        return v;
    }

    public bool IsAgentOnline() =>
        _agentLastSeenUtc.HasValue && (DateTime.UtcNow - _agentLastSeenUtc.Value).TotalSeconds <= Math.Max(5, _settings().AgentOfflineSeconds);

    public void AddLog(LogEntry e)
    {
        lock (_logs)
        {
            _logs.AddFirst(e.Text);
            while (_logs.Count > MaxLogs) _logs.RemoveLast();
        }
    }

    /// <summary>Delete archived files older than KeepDays (processed lists clean themselves).</summary>
    public void Cleanup()
    {
        var keep = _settings().KeepDays;
        _processed.Cleanup();
        if (keep <= 0 || !Directory.Exists(_archive)) return;
        var cutoff = DateTime.Today.AddDays(-keep);
        foreach (var dir in Directory.EnumerateDirectories(_archive))
        {
            if (DateTime.TryParseExact(Path.GetFileName(dir), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d < cutoff)
            {
                try { Directory.Delete(dir, true); }
                catch (Exception ex) { AppLog.Warn($"Cannot delete old archive {dir}: {ex.Message}", Id); }
            }
        }
    }

    // ================================================================ helpers

    private IEnumerable<string> InboxFiles() =>
        Directory.Exists(_inbox) ? Directory.EnumerateFiles(_inbox).Where(f => !f.EndsWith(".part")) : Enumerable.Empty<string>();

    private void MoveToArchive(string path)
    {
        var dayDir = Path.Combine(_archive, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dayDir);
        File.Move(path, Path.Combine(dayDir, Path.GetFileName(path)), overwrite: true);
    }

    private void SaveState()
    {
        try { JsonFile.Write(_statePath, _state); }
        catch (Exception ex) { AppLog.Error("Cannot save line state: " + ex.Message, Id); }
    }

    /// <summary>Keep the saved state small: only the first payload, shortened.</summary>
    private static FileResult Trim(FileResult r)
    {
        r.Payloads = r.Payloads.Take(1).Select(p => p.Length > 4000 ? p[..4000] + " ..." : p).ToList();
        if (r.ResponseBody is { Length: > 1000 }) r.ResponseBody = r.ResponseBody[..1000] + " ...";
        return r;
    }

    private static CountTotals ToTotals((int Files, int Qty, int Good, int Ng) t) => new(t.Files, t.Qty, t.Good, t.Ng);

    private static DateTime? ToLocal(DateTime? utc) =>
        utc.HasValue ? DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime() : null;

    private static string ExtrasText(Dictionary<string, string> extras) =>
        string.Concat(extras.Select(kv => $", {kv.Key} = '{kv.Value}'"));
}
