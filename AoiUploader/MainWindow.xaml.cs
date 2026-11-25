using System.Diagnostics;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AoiUploader.Models;
using AoiUploader.Services;
using AoiUploader.ViewModels;

namespace AoiUploader;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly ProcessedTracker _tracker = new();
    private readonly FolderMonitor _monitor;
    private AppSettings _settings;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        AppLogger.Logged += line => Dispatcher.InvokeAsync(() => _vm.AddLog(line));

        _settings = SettingsService.Load();
        ApplyPaths();
        AppLogger.Info($"AOI Uploader started. Settings: {SettingsService.SettingsPath}");

        _monitor = new FolderMonitor(_tracker, _settings);
        _monitor.FileProcessed += r => Dispatcher.InvokeAsync(() => OnFileProcessed(r));
        _monitor.StateChanged += (state, msg) => Dispatcher.InvokeAsync(() => _vm.SetState(state, msg));

        RefreshInfo();
        BuildExtras(keepValues: false);
        _vm.LoadToday(_tracker.GetTodaySummary());
        _vm.InputValue = _settings.Input.LastValue;

        if (_settings.Input.AutoStartWithLastValue && _vm.AllInputsFilled)
            StartMonitor();

        Loaded += (_, _) => FocusFirstInput();
        Closing += async (_, _) =>
        {
            await _monitor.StopAsync();
            _monitor.Dispose();
        };
    }

    private void ApplyPaths()
    {
        AppLogger.Folder = SettingsService.ResolvePath(_settings.General.LogFolder);
        _tracker.Load(SettingsService.ResolvePath(_settings.General.ProcessedFolder),
                      _settings.General.KeepProcessedDays,
                      SettingsService.ResolvePath(_settings.General.ProcessedListFile));
    }

    private void RefreshInfo()
    {
        var g = _settings.General;
        var input = _settings.Input;
        _vm.InputLabel = string.IsNullOrWhiteSpace(input.Label) ? "Value" : input.Label;
        var match = input.MatchSource switch
        {
            MatchSource.FileNamePrefix => $"file name before '{input.FileNameSeparator}'",
            MatchSource.RecipeLine => $"recipe line {g.RecipeLineNumber}",
            _ => $"field '{input.MatchField}'",
        };
        _vm.WatchFolder = $"{g.WatchFolder}   ({g.FilePattern}{(g.IncludeSubfolders ? ", incl. subfolders" : "")})   " +
                          $"·  {_vm.InputLabel} matched with {match}" +
                          (input.OnlyFilesAfterStart ? "  ·  only files after Start" : "");
        _vm.QtyUnit = g.QtyMode == QtyMode.ByDistinctField ? $"distinct {g.QtyField}" : "by row";
        var a = _settings.Api;
        _vm.ApiInfo = a.Enabled
            ? $"{a.Method} {a.Url}   ({(a.SendMode == SendMode.PerFile ? "one call per file" : "one call per row")})"
            : "OFF - files are only parsed and shown, not sent";
    }

    /// <summary>Creates the additional input boxes (e.g. Work Order) from settings.</summary>
    private void BuildExtras(bool keepValues)
    {
        var current = _vm.Extras.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        var items = _settings.Input.ExtraInputs
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .Select(x => new ExtraInputItem
            {
                Key = x.Key.Trim(),
                Label = string.IsNullOrWhiteSpace(x.Label) ? x.Key.Trim() : x.Label.Trim(),
                Required = x.Required,
                Value = keepValues && current.TryGetValue(x.Key.Trim(), out var v) ? v
                      : x.RememberLastValue ? x.LastValue : "",
            });
        _vm.SetExtras(items);
    }

    private void OnFileProcessed(FileResult r)
    {
        _vm.AddResult(r);
        if (r.Kind == ResultKind.Mismatch && _settings.Input.WarnOnMismatch)
        {
            _vm.ShowMismatch(r, _vm.InputLabel);
            SystemSounds.Exclamation.Play();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
    }

    private void StartMonitor()
    {
        var value = _vm.InputValue.Trim();
        if (value.Length == 0)
        {
            MessageBox.Show(this, $"Please enter the {_vm.InputLabel} first.", "AOI Uploader",
                MessageBoxButton.OK, MessageBoxImage.Information);
            InputBox.Focus();
            return;
        }
        _vm.InputValue = value;

        var missing = _vm.Extras.Where(x => x.IsMissing).Select(x => x.Label).ToList();
        if (missing.Count > 0)
        {
            MessageBox.Show(this, $"Please enter: {string.Join(", ", missing)}", "AOI Uploader",
                MessageBoxButton.OK, MessageBoxImage.Information);
            FocusInput(InputBoxes().FirstOrDefault(b => b.DataContext is ExtraInputItem { IsMissing: true }));
            return;
        }
        foreach (var x in _vm.Extras) x.Value = x.Value.Trim();
        var extras = _vm.Extras.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

        // Remember the last values
        bool changed = _settings.Input.LastValue != value;
        _settings.Input.LastValue = value;
        foreach (var def in _settings.Input.ExtraInputs)
        {
            if (extras.TryGetValue(def.Key.Trim(), out var v) && def.LastValue != v)
            {
                def.LastValue = v;
                changed = true;
            }
        }
        if (changed)
        {
            try { SettingsService.Save(_settings); } catch (Exception ex) { AppLogger.Warn("Cannot save last values: " + ex.Message); }
        }

        _monitor.Start(value, extras);
        _vm.IsRunning = true;
        _vm.StartRun(_vm.InputLabel, value, _monitor.RunStart);
        _vm.SetState(MonitorState.Watching, $"Watching {_vm.InputLabel}: {value}");
    }

    private async Task StopMonitorAsync()
    {
        await _monitor.StopAsync();
        _vm.IsRunning = false;
        _vm.StopRun(DateTime.Now);
    }

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_monitor.IsRunning)
        {
            var pending = _monitor.PendingRetryFiles;
            if (pending.Count > 0)
            {
                var list = string.Join("\n", pending.Take(10).Select(p => "  • " + p)) +
                           (pending.Count > 10 ? $"\n  ... and {pending.Count - 10} more" : "");
                var afterStart = _settings.Input.OnlyFilesAfterStart
                    ? "If you stop now they will NOT be sent later (only files after Start are processed)."
                    : "If you stop now they will be retried after the next Start.";
                if (MessageBox.Show(this,
                        $"⚠ {pending.Count} file(s) of this run have NOT been sent yet (API failed, waiting to retry):\n\n{list}\n\n" +
                        $"{afterStart}\n\nStop anyway?\n\nYes = stop now      No = keep running and retrying",
                        "Files not sent", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    _monitor.TriggerScan();
                    return;
                }
                AppLogger.Warn($"Stopped with {pending.Count} unsent file(s): {string.Join(", ", pending)}");
            }
            else if (MessageBox.Show(this, $"Stop {_vm.InputLabel} {_monitor.InputValue}?\n\nFiles arriving while stopped will not be sent " +
                                           "(only files after Start are processed).", "Stop",
                         MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await StopMonitorAsync();
            FocusFirstInput();
        }
        else
        {
            StartMonitor();
        }
    }

    /// <summary>
    /// Barcode scanners send Enter after the value:
    /// Enter in a box moves to the next box; Enter in the last box presses Start.
    /// </summary>
    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _monitor.IsRunning) return;
        e.Handled = true;

        var boxes = InputBoxes();
        int i = boxes.IndexOf((TextBox)sender);
        if (i >= 0 && i < boxes.Count - 1)
            FocusInput(boxes[i + 1]);
        else
            StartMonitor();
    }

    /// <summary>All input boxes in screen order: additional inputs (Work Order...) first, then the main input.</summary>
    private List<TextBox> InputBoxes()
    {
        var list = new List<TextBox>();
        for (int i = 0; i < ExtrasControl.Items.Count; i++)
        {
            if (ExtrasControl.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject c)
            {
                var box = FindChild<TextBox>(c);
                if (box != null) list.Add(box);
            }
        }
        list.Add(InputBox);
        return list;
    }

    /// <summary>Puts the cursor in the first box (Work Order), after the boxes are drawn.</summary>
    private void FocusFirstInput() =>
        Dispatcher.InvokeAsync(() => FocusInput(InputBoxes().FirstOrDefault()),
                               System.Windows.Threading.DispatcherPriority.ContextIdle);

    private static void FocusInput(TextBox? box)
    {
        if (box == null) return;
        box.Focus();
        box.SelectAll();
    }

    private static T? FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            var r = FindChild<T>(c);
            if (r != null) return r;
        }
        return null;
    }

    private void DismissWarning_Click(object sender, RoutedEventArgs e) => _vm.ClearWarning();

    private void ScanNow_Click(object sender, RoutedEventArgs e)
    {
        if (!_monitor.IsRunning)
        {
            MessageBox.Show(this, $"Enter the {_vm.InputLabel} and press Start first.", "AOI Uploader",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _monitor.TriggerScan();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        dlg.Result.Input.LastValue = _settings.Input.LastValue;
        foreach (var x in dlg.Result.Input.ExtraInputs)
        {
            var old = _settings.Input.ExtraInputs.FirstOrDefault(o => o.Key.Equals(x.Key, StringComparison.OrdinalIgnoreCase));
            if (old != null) x.LastValue = old.LastValue;
        }
        _settings = dlg.Result;
        SettingsService.Save(_settings);
        SettingsService.ApplyStartWithWindows(_settings.General.StartWithWindows);
        ApplyPaths();
        _monitor.UpdateSettings(_settings);
        RefreshInfo();
        BuildExtras(keepValues: true);
        _vm.LoadToday(_tracker.GetTodaySummary());
        AppLogger.Info("Settings saved");
    }

    private void Resend_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not FileResult r) return;
        if (!_monitor.IsRunning)
        {
            MessageBox.Show(this, $"Enter the {_vm.InputLabel} and press Start first.", "AOI Uploader",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_settings.Api.Enabled)
        {
            MessageBox.Show(this, "API is OFF. Turn it on in Settings > API first.", "AOI Uploader",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"Send this file to the API again?\n\n{r.FileName}\n\n" +
                                  $"It must match the current {_vm.InputLabel} ({_monitor.InputValue}) and is sent with " +
                                  $"the current run's inputs{string.Concat(_monitor.Extras.Select(kv => $", {kv.Key} {kv.Value}"))}.", "Resend",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _monitor.Resend(r.FilePath);
    }

    private void ResultsGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not FileResult r) return;
        var payload = r.Payloads.Count > 0 ? PayloadBuilder.Pretty(r.Payloads[0]) : "(none)";
        var more = r.Payloads.Count > 1 ? $"\n... and {r.Payloads.Count - 1} more payload(s)" : "";
        var extras = string.Concat(r.Extras.Select(kv => $"{kv.Key}: {kv.Value}\n"));
        var text = $"File: {r.FilePath}\n{_vm.InputLabel} in file: {r.FileValue}   Entered: {r.InputValue}\n{extras}" +
                   $"Result: {r.Status}\nMessage: {r.Message}\nHTTP: {r.HttpStatus}\n" +
                   $"Response: {Truncate(r.ResponseBody, 1000)}\n\nPayload:\n{Truncate(payload, 3000)}{more}";
        MessageBox.Show(this, text, "File details");
    }

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + " ...";

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppLogger.Folder);
        Process.Start(new ProcessStartInfo(AppLogger.Folder) { UseShellExecute = true });
    }

    private void OpenProcessed_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_tracker.Folder);
        if (File.Exists(_tracker.TodayFile))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_tracker.TodayFile}\"") { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo(_tracker.Folder) { UseShellExecute = true });
    }
}
