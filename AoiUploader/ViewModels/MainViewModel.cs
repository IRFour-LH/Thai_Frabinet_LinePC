using System.Collections.ObjectModel;
using System.Windows.Media;
using AoiUploader.Models;
using AoiUploader.Services;

namespace AoiUploader.ViewModels;

public class MainViewModel : ObservableObject
{
    private const int MaxResults = 500;
    private const int MaxLogs = 500;

    private static readonly Brush Grey = Freeze(Color.FromRgb(0x9A, 0xA3, 0xAE));
    private static readonly Brush Green = Freeze(Color.FromRgb(0x2E, 0xA0, 0x43));
    private static readonly Brush Orange = Freeze(Color.FromRgb(0xE0, 0x8A, 0x00));
    private static readonly Brush Red = Freeze(Color.FromRgb(0xD0, 0x33, 0x33));

    private bool _isRunning;
    private string _statusText = "Stopped";
    private Brush _statusBrush = Grey;
    private string _watchFolder = "";
    private string _apiInfo = "";
    private string _inputLabel = "Product Name";
    private string _inputValue = "";
    private string _qtyUnit = "by row";
    private string _runInfo = "Not started";
    private int _runFiles, _runQty, _runGood, _runNg, _failures;
    private int _todayFiles, _todayQty, _todayGood, _todayNg;
    private DateTime _counterDate = DateTime.Today;
    private string _warningText = "";
    private int _warningCount;

    public ObservableCollection<FileResult> Results { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();

    /// <summary>Additional inputs shown next to the main input (e.g. Work Order).</summary>
    public ObservableCollection<ExtraInputItem> Extras { get; } = new();

    public void SetExtras(IEnumerable<ExtraInputItem> items)
    {
        foreach (var old in Extras) old.PropertyChanged -= OnExtraChanged;
        Extras.Clear();
        foreach (var i in items)
        {
            i.PropertyChanged += OnExtraChanged;
            Extras.Add(i);
        }
        Raise(nameof(CanStartStop));
    }

    private void OnExtraChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        Raise(nameof(CanStartStop));

    // ------------------------------------------------------------- run / input

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (!Set(ref _isRunning, value)) return;
            Raise(nameof(StartStopText));
            Raise(nameof(IsInputEditable));
            Raise(nameof(CanStartStop));
        }
    }

    public string StartStopText => IsRunning ? "■  Stop" : "▶  Start";
    public bool IsInputEditable => !IsRunning;
    public bool CanStartStop => IsRunning || AllInputsFilled;

    public bool AllInputsFilled => !string.IsNullOrWhiteSpace(InputValue) && Extras.All(x => !x.IsMissing);

    public string InputLabel { get => _inputLabel; set => Set(ref _inputLabel, value); }

    public string InputValue
    {
        get => _inputValue;
        set { if (Set(ref _inputValue, value)) Raise(nameof(CanStartStop)); }
    }

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
    public string WatchFolder { get => _watchFolder; set => Set(ref _watchFolder, value); }
    public string ApiInfo { get => _apiInfo; set => Set(ref _apiInfo, value); }
    public string RunInfo { get => _runInfo; set => Set(ref _runInfo, value); }

    public string QtyUnit
    {
        get => _qtyUnit;
        set { if (Set(ref _qtyUnit, value)) { Raise(nameof(RunQtyLabel)); Raise(nameof(TodayText)); } }
    }

    public string RunQtyLabel => $"PCB qty ({QtyUnit})";

    // ------------------------------------------------------------- counters

    public int RunFiles { get => _runFiles; set => Set(ref _runFiles, value); }
    public int RunQty { get => _runQty; set => Set(ref _runQty, value); }
    public int RunGood { get => _runGood; set => Set(ref _runGood, value); }
    public int RunNg { get => _runNg; set => Set(ref _runNg, value); }
    public int Failures { get => _failures; set => Set(ref _failures, value); }

    public string TodayText =>
        $"Today (all runs): {_todayFiles} file(s) · PCB qty {_todayQty} · G {_todayGood} · NG {_todayNg}";

    // ------------------------------------------------------------- warning banner

    public string WarningText { get => _warningText; set { if (Set(ref _warningText, value)) Raise(nameof(HasWarning)); } }
    public bool HasWarning => !string.IsNullOrEmpty(WarningText);

    public void ShowMismatch(FileResult r, string label)
    {
        _warningCount++;
        var more = _warningCount > 1 ? $"   ({_warningCount} file(s) skipped this run)" : "";
        WarningText = $"⚠  {r.Time:HH:mm:ss}  {r.FileName}: {r.Message}.  Check the {label} entered.{more}";
    }

    public void ClearWarning()
    {
        WarningText = "";
        _warningCount = 0;
    }

    // ------------------------------------------------------------- state

    public void SetState(MonitorState state, string message)
    {
        StatusText = message;
        StatusBrush = state switch
        {
            MonitorState.Watching => Green,
            MonitorState.Warning => Orange,
            MonitorState.Error => Red,
            _ => Grey,
        };
    }

    public void StartRun(string label, string value, DateTime start)
    {
        RunFiles = RunQty = RunGood = RunNg = Failures = 0;
        ClearWarning();
        var extras = string.Concat(Extras.Where(x => !string.IsNullOrWhiteSpace(x.Value))
                                         .Select(x => $"  ·  {x.Label} {x.Value.Trim()}"));
        RunInfo = $"{label} {value}{extras}  ·  started {start:yyyy-MM-dd HH:mm:ss}";
    }

    public void StopRun(DateTime stop)
    {
        RunInfo += $"  ·  stopped {stop:HH:mm:ss}";
    }

    public void LoadToday((int Files, int Qty, int Good, int Ng) t)
    {
        _counterDate = DateTime.Today;
        (_todayFiles, _todayQty, _todayGood, _todayNg) = t;
        Raise(nameof(TodayText));
    }

    public void AddResult(FileResult r)
    {
        if (DateTime.Today != _counterDate) LoadToday((0, 0, 0, 0)); // new day

        Results.Insert(0, r);
        while (Results.Count > MaxResults) Results.RemoveAt(Results.Count - 1);

        switch (r.Kind)
        {
            case ResultKind.Sent:
                RunFiles++;
                RunQty += r.Qty;
                RunGood += r.Good;
                RunNg += r.Ng;
                _todayFiles++;
                _todayQty += r.Qty;
                _todayGood += r.Good;
                _todayNg += r.Ng;
                Raise(nameof(TodayText));
                break;
            case ResultKind.Failed:
                Failures++;
                break;
        }
    }

    public void AddLog(string line)
    {
        Logs.Insert(0, line);
        while (Logs.Count > MaxLogs) Logs.RemoveAt(Logs.Count - 1);
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
