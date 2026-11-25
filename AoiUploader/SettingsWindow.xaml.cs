using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AoiUploader.Models;
using AoiUploader.Services;
using Microsoft.Win32;

namespace AoiUploader;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _edit;
    private readonly ObservableCollection<ColumnMapping> _maps;
    private readonly ObservableCollection<HttpHeader> _headers;
    private readonly ObservableCollection<ExtraInput> _extras;

    public AppSettings? Result { get; private set; }

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();

        _edit = SettingsService.Clone(current);
        _maps = new ObservableCollection<ColumnMapping>(_edit.ColumnMappings);
        _headers = new ObservableCollection<HttpHeader>(_edit.Api.Headers);
        _extras = new ObservableCollection<ExtraInput>(_edit.Input.ExtraInputs);

        DataContext = _edit;
        MappingGrid.ItemsSource = _maps;
        HeadersGrid.ItemsSource = _headers;
        ExtrasGrid.ItemsSource = _extras;
        DataTypeColumn.ItemsSource = Enum.GetValues<ColumnDataType>();
        MethodCombo.ItemsSource = new[] { "POST", "PUT", "PATCH" };
        SendModeCombo.ItemsSource = Enum.GetValues<SendMode>();
        PlaceholderHelpText.Text = PayloadBuilder.PlaceholderHelp;
        SampleFileBox.Text = FindSampleFile(_edit.General) ?? "";

        if (_edit.General.QtyMode == QtyMode.ByDistinctField) QtyByDistinctRadio.IsChecked = true;
        else QtyByRowRadio.IsChecked = true;
        QtyFieldBox.IsEnabled = _edit.General.QtyMode == QtyMode.ByDistinctField;

        switch (_edit.Input.MatchSource)
        {
            case MatchSource.RecipeLine: MatchRecipeLineRadio.IsChecked = true; break;
            case MatchSource.DataField: MatchFieldRadio.IsChecked = true; break;
            default: MatchFileNameRadio.IsChecked = true; break;
        }
        UpdateMatchEnabled();
        TestInputBox.Text = _edit.Input.LastValue;
    }

    private void Match_Changed(object sender, RoutedEventArgs e)
    {
        if (MatchFieldRadio == null || MatchRecipeLineRadio == null || SeparatorBox == null) return; // during InitializeComponent
        _edit.Input.MatchSource = MatchFieldRadio.IsChecked == true ? MatchSource.DataField
            : MatchRecipeLineRadio.IsChecked == true ? MatchSource.RecipeLine
            : MatchSource.FileNamePrefix;
        UpdateMatchEnabled();
    }

    private void UpdateMatchEnabled()
    {
        if (SeparatorBox == null || MatchFieldBox == null) return;
        SeparatorBox.IsEnabled = _edit.Input.MatchSource == MatchSource.FileNamePrefix;
        MatchFieldBox.IsEnabled = _edit.Input.MatchSource == MatchSource.DataField;
    }

    private void CheckMatch_Click(object sender, RoutedEventArgs e)
    {
        var path = GetSampleFile();
        if (path == null) return;
        try
        {
            var s = Snapshot();
            var pf = TstParser.Parse(path, s);
            var values = InputMatcher.GetFileValues(path, pf, s.Input);
            var label = string.IsNullOrWhiteSpace(s.Input.Label) ? "Value" : s.Input.Label;
            var found = values.Count == 0 ? "(nothing)" : string.Join(", ", values);
            if (string.IsNullOrWhiteSpace(TestInputBox.Text))
            {
                MatchResultText.Foreground = System.Windows.Media.Brushes.DimGray;
                MatchResultText.Text = $"{label} found in sample file: {found}";
                return;
            }
            var err = InputMatcher.Check(values, TestInputBox.Text, s.Input);
            MatchResultText.Foreground = err == null ? System.Windows.Media.Brushes.ForestGreen : System.Windows.Media.Brushes.Firebrick;
            MatchResultText.Text = err == null
                ? $"✔ MATCH - this file would be sent.   ({label} in file: {found})"
                : $"✘ NO MATCH - this file would be skipped with a warning.   {err}";
        }
        catch (Exception ex)
        {
            MatchResultText.Foreground = System.Windows.Media.Brushes.Firebrick;
            MatchResultText.Text = "Error: " + ex.Message;
        }
    }

    private void QtyMode_Changed(object sender, RoutedEventArgs e)
    {
        if (QtyFieldBox == null || QtyByDistinctRadio == null) return; // during InitializeComponent
        _edit.General.QtyMode = QtyByDistinctRadio.IsChecked == true ? QtyMode.ByDistinctField : QtyMode.ByRow;
        QtyFieldBox.IsEnabled = _edit.General.QtyMode == QtyMode.ByDistinctField;
    }

    // ---------------------------------------------------------------- helpers

    private static string? FindSampleFile(GeneralSettings g)
    {
        try
        {
            if (!Directory.Exists(g.WatchFolder)) return null;
            return new DirectoryInfo(g.WatchFolder)
                .EnumerateFiles(string.IsNullOrWhiteSpace(g.FilePattern) ? "*.*" : g.FilePattern)
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    private void CommitGrids()
    {
        MappingGrid.CommitEdit(DataGridEditingUnit.Row, true);
        HeadersGrid.CommitEdit(DataGridEditingUnit.Row, true);
        ExtrasGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void AddExtra_Click(object sender, RoutedEventArgs e)
    {
        var x = new ExtraInput { Key = "NewInput", Label = "New Input", Required = true };
        _extras.Add(x);
        ExtrasGrid.SelectedItem = x;
    }

    private void RemoveExtra_Click(object sender, RoutedEventArgs e)
    {
        if (ExtrasGrid.SelectedItem is ExtraInput x) _extras.Remove(x);
    }

    /// <summary>Sample values for the extra inputs so Preview / Test send show something.</summary>
    private static Dictionary<string, string> SampleExtras(AppSettings s) =>
        s.Input.ExtraInputs.Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .GroupBy(x => x.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => "TEST-" + g.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Settings exactly as currently shown in the window.</summary>
    private AppSettings Snapshot()
    {
        CommitGrids();
        _edit.ColumnMappings = _maps.ToList();
        _edit.Api.Headers = _headers.ToList();
        _edit.Input.ExtraInputs = _extras.ToList();
        return SettingsService.Clone(_edit);
    }

    private string? GetSampleFile()
    {
        var path = SampleFileBox.Text.Trim();
        if (File.Exists(path)) return path;
        MessageBox.Show(this, "Please choose a sample file first (top of this window).", "Settings",
            MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    // ---------------------------------------------------------------- browse buttons

    private void BrowseSample_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a sample AOI file",
            Filter = "AOI files (*.TST)|*.TST|All files (*.*)|*.*",
        };
        if (Directory.Exists(_edit.General.WatchFolder)) dlg.InitialDirectory = _edit.General.WatchFolder;
        if (dlg.ShowDialog(this) == true) SampleFileBox.Text = dlg.FileName;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose the folder to watch" };
        if (Directory.Exists(_edit.General.WatchFolder)) dlg.InitialDirectory = _edit.General.WatchFolder;
        if (dlg.ShowDialog(this) != true) return;
        _edit.General.WatchFolder = dlg.FolderName;
        RefreshBindings();
        if (string.IsNullOrWhiteSpace(SampleFileBox.Text) || !File.Exists(SampleFileBox.Text))
            SampleFileBox.Text = FindSampleFile(_edit.General) ?? "";
    }

    private void BrowseProcessed_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose folder for the daily processed lists" };
        var current = SettingsService.ResolvePath(_edit.General.ProcessedFolder);
        if (Directory.Exists(current)) dlg.InitialDirectory = current;
        if (dlg.ShowDialog(this) != true) return;
        _edit.General.ProcessedFolder = dlg.FolderName;
        RefreshBindings();
    }

    private void BrowseLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose log folder" };
        if (dlg.ShowDialog(this) != true) return;
        _edit.General.LogFolder = dlg.FolderName;
        RefreshBindings();
    }

    /// <summary>Plain settings classes do not raise change events, so re-bind after code changes them.</summary>
    private void RefreshBindings()
    {
        DataContext = null;
        DataContext = _edit;
    }

    // ---------------------------------------------------------------- column mapping

    private void AddMapping_Click(object sender, RoutedEventArgs e)
    {
        var m = new ColumnMapping { FieldName = "NewField", ColumnIndex = _maps.Count };
        _maps.Add(m);
        MappingGrid.SelectedItem = m;
        MappingGrid.ScrollIntoView(m);
    }

    private void RemoveMapping_Click(object sender, RoutedEventArgs e)
    {
        if (MappingGrid.SelectedItem is ColumnMapping m) _maps.Remove(m);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        CommitGrids();
        if (MappingGrid.SelectedItem is not ColumnMapping m) return;
        int i = _maps.IndexOf(m), j = i + delta;
        if (j < 0 || j >= _maps.Count) return;
        _maps.Move(i, j);
        MappingGrid.SelectedItem = m;
    }

    private void DetectMapping_Click(object sender, RoutedEventArgs e)
    {
        var path = GetSampleFile();
        if (path == null) return;
        CommitGrids();
        try
        {
            var g = _edit.General;
            var headers = TstParser.ReadHeaders(path, g);
            if (headers.Count == 0)
            {
                MessageBox.Show(this, $"No headers found on line {g.HeaderLineNumber}. Check 'Header line no.' on the General tab.",
                    "Detect", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_maps.Count > 0 && MessageBox.Show(this,
                    $"Found {headers.Count} columns:\n{string.Join(", ", headers)}\n\nReplace the current mapping?",
                    "Detect from sample file", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var first = TstParser.ReadFirstDataRow(path, g);
            _maps.Clear();
            for (int i = 0; i < headers.Count; i++)
            {
                var sample = first != null && i < first.Length ? first[i] : null;
                _maps.Add(new ColumnMapping
                {
                    FieldName = headers[i],
                    HeaderName = headers[i],
                    ColumnIndex = i,
                    DataType = GuessType(sample, g.DateFormat),
                });
            }
            PreviewMapping_Click(sender, e);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Detect", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static ColumnDataType GuessType(string? v, string dateFormat)
    {
        if (string.IsNullOrEmpty(v)) return ColumnDataType.Text;
        if (v.Length == dateFormat.Length &&
            DateTime.TryParseExact(v, dateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return ColumnDataType.Date;
        // Short integers only - long digit strings are usually barcodes and must stay text.
        if (v.Length <= 9 && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return ColumnDataType.Number;
        return ColumnDataType.Text;
    }

    private void PreviewMapping_Click(object sender, RoutedEventArgs e)
    {
        var path = GetSampleFile();
        if (path == null) return;
        try
        {
            var s = Snapshot();
            var pf = TstParser.Parse(path, s);

            var table = new DataTable();
            var fields = s.ColumnMappings.Where(m => m.Include && !string.IsNullOrWhiteSpace(m.FieldName))
                                         .Select(m => m.FieldName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var f in fields) table.Columns.Add(f.Replace(".", "_"), typeof(string));
            foreach (var rec in pf.Records)
                table.Rows.Add(fields.Select(f => rec.TryGetValue(f, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) : "").ToArray<object?>());
            PreviewGrid.ItemsSource = table.DefaultView;

            var sb = new StringBuilder();
            var qtyText = pf.QtyMode == QtyMode.ByDistinctField ? $"distinct {s.General.QtyField}" : "by row";
            sb.Append($"Recipe: {pf.Recipe}    Rows: {pf.RowCount}    PCB qty ({qtyText}): {pf.PcbQty}    G: {pf.GoodQty}    NG: {pf.NgQty}\n");
            sb.Append($"Headers found in file: {string.Join(" | ", pf.Headers.Select((h, i) => $"[{i}] {h}"))}");
            if (pf.Warnings.Count > 0) sb.Append("\n⚠ " + string.Join("\n⚠ ", pf.Warnings));
            PreviewInfo.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            PreviewInfo.Text = "Error: " + ex.Message;
            PreviewGrid.ItemsSource = null;
        }
    }

    // ---------------------------------------------------------------- API

    private void AddHeader_Click(object sender, RoutedEventArgs e) => _headers.Add(new HttpHeader { Name = "X-Api-Key" });

    private void RemoveHeader_Click(object sender, RoutedEventArgs e)
    {
        if (HeadersGrid.SelectedItem is HttpHeader h) _headers.Remove(h);
    }

    private void DefaultTemplate_Click(object sender, RoutedEventArgs e)
    {
        Snapshot();
        if (MessageBox.Show(this, $"Replace the body template with the default for '{_edit.Api.SendMode}' mode?",
                "Template", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _edit.Api.PayloadTemplate = _edit.Api.SendMode == SendMode.PerRow
            ? PayloadBuilder.DefaultPerRowTemplate
            : PayloadBuilder.DefaultPerFileTemplate;
        RefreshBindings();
    }

    private void PreviewBody_Click(object sender, RoutedEventArgs e)
    {
        var path = GetSampleFile();
        if (path == null) return;
        try
        {
            var s = Snapshot();
            var pf = TstParser.Parse(path, s);
            pf.InputValue = InputMatcher.GetFileValues(path, pf, s.Input).FirstOrDefault() ?? "";
            foreach (var kv in SampleExtras(s)) pf.Extras[kv.Key] = kv.Value;
            var payloads = PayloadBuilder.Build(s, pf);
            var sb = new StringBuilder();
            sb.AppendLine($"{s.Api.Method} {s.Api.Url}");
            sb.AppendLine($"Content-Type: {s.Api.ContentType}");
            foreach (var h in s.Api.Headers.Where(h => !string.IsNullOrWhiteSpace(h.Name)))
                sb.AppendLine($"{h.Name}: {h.Value}");
            sb.AppendLine($"Calls for this file: {payloads.Count}");
            sb.AppendLine();
            if (payloads.Count > 0)
            {
                var err = PayloadBuilder.ValidateJson(payloads[0]);
                if (err != null) sb.AppendLine("⚠ NOT VALID JSON: " + err).AppendLine();
                sb.AppendLine(err == null ? PayloadBuilder.Pretty(payloads[0]) : payloads[0]);
                if (payloads.Count > 1) sb.AppendLine($"\n... and {payloads.Count - 1} more call(s), one per row");
            }
            ApiOutputBox.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            ApiOutputBox.Text = "Error: " + ex.Message;
        }
    }

    private async void TestSend_Click(object sender, RoutedEventArgs e)
    {
        var path = GetSampleFile();
        if (path == null) return;
        var s = Snapshot();
        if (!Uri.TryCreate(s.Api.Url, UriKind.Absolute, out _))
        {
            MessageBox.Show(this, "URL is not valid.", "Test send", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this, $"Send '{Path.GetFileName(path)}' to\n{s.Api.Url}\nnow?", "Test send",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        TestSendButton.IsEnabled = false;
        ApiOutputBox.Text = "Sending...";
        try
        {
            var r = await FileProcessor.ProcessAsync(path, s, send: true, inputValue: null, SampleExtras(s), CancellationToken.None);
            var sb = new StringBuilder();
            sb.AppendLine($"Result : {r.Status}");
            if (!string.IsNullOrEmpty(r.Message)) sb.AppendLine($"Message: {r.Message}");
            sb.AppendLine($"HTTP   : {r.HttpStatus}");
            sb.AppendLine("Response:");
            sb.AppendLine(PayloadBuilder.Pretty(r.ResponseBody ?? ""));
            ApiOutputBox.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            ApiOutputBox.Text = "Error: " + ex.Message;
        }
        finally
        {
            TestSendButton.IsEnabled = true;
        }
    }

    // ---------------------------------------------------------------- save

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = Snapshot();
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(s.General.WatchFolder)) errors.Add("Watch folder is empty.");
        else if (!Directory.Exists(s.General.WatchFolder)) errors.Add($"Watch folder does not exist (will keep checking): {s.General.WatchFolder}");

        var names = s.ColumnMappings.Select(m => m.FieldName.Trim()).ToList();
        if (names.Any(string.IsNullOrEmpty)) errors.Add("A column mapping has an empty API Field Name.");
        var dup = names.Where(n => n.Length > 0).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(gr => gr.Count() > 1).Select(gr => gr.Key).ToList();
        if (dup.Count > 0) errors.Add("Duplicate API Field Name: " + string.Join(", ", dup));
        if (s.ColumnMappings.Any(m => string.IsNullOrWhiteSpace(m.HeaderName) && m.ColumnIndex < 0))
            errors.Add("A column mapping has no File Header and no Column Index.");

        if (s.General.QtyMode == QtyMode.ByDistinctField &&
            !names.Contains(s.General.QtyField.Trim(), StringComparer.OrdinalIgnoreCase))
            errors.Add($"PCB qty field '{s.General.QtyField}' is not an API Field Name in Column Mapping (qty would fall back to by row).");

        if (string.IsNullOrWhiteSpace(s.General.ProcessedFolder))
            errors.Add("Processed lists folder is empty.");
        if (s.General.KeepProcessedDays < 0)
            errors.Add("Delete lists older than: use 0 (keep forever) or a positive number of days.");

        if (string.IsNullOrWhiteSpace(s.Input.Label))
            errors.Add("Operator Input: label is empty.");
        var extraKeys = s.Input.ExtraInputs.Select(x => x.Key.Trim()).ToList();
        if (extraKeys.Any(string.IsNullOrEmpty))
            errors.Add("Additional inputs: a Key is empty.");
        if (extraKeys.Any(k => k.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(k, @"^[A-Za-z0-9_]+$")))
            errors.Add("Additional inputs: Key may only contain letters, numbers and _ (e.g. WorkOrder).");
        var dupKeys = extraKeys.Where(k => k.Length > 0).GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(gr => gr.Count() > 1).Select(gr => gr.Key).ToList();
        if (dupKeys.Count > 0) errors.Add("Additional inputs: duplicate Key " + string.Join(", ", dupKeys));
        var reserved = extraKeys.Where(PayloadBuilder.IsBuiltIn).ToList();
        if (reserved.Count > 0) errors.Add("Additional inputs: Key is a reserved placeholder name: " + string.Join(", ", reserved));
        if (s.Input.MatchSource == MatchSource.DataField &&
            !names.Contains(s.Input.MatchField.Trim(), StringComparer.OrdinalIgnoreCase))
            errors.Add($"Operator Input: match field '{s.Input.MatchField}' is not an API Field Name in Column Mapping (no file would match).");

        if (s.Api.Enabled && !Uri.TryCreate(s.Api.Url, UriKind.Absolute, out _))
            errors.Add("API URL is not valid.");

        if (errors.Count > 0 && MessageBox.Show(this, "Please check:\n\n• " + string.Join("\n• ", errors) + "\n\nSave anyway?",
                "Settings", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        foreach (var m in s.ColumnMappings)
        {
            m.FieldName = m.FieldName.Trim();
            m.HeaderName = m.HeaderName.Trim();
        }
        foreach (var x in s.Input.ExtraInputs)
        {
            x.Key = x.Key.Trim();
            x.Label = x.Label.Trim();
        }
        Result = s;
        DialogResult = true;
    }
}
