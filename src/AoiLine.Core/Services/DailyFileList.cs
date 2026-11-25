using System.Globalization;
using System.Text.RegularExpressions;
using AoiLine.Core.Models;

namespace AoiLine.Core.Services;

/// <summary>
/// Remembers which files were handled, in one text file per day (e.g. Processed\L3\2026-11-25.txt):
///   2026-11-25 10:15:02 | B603401X1101_20250724002918001.TST | line=L3 | input=B603401X1101 | WorkOrder=WO1 | rows=7 | qty=5 | good=5 | ng=0 | OK
/// Daily files older than "keep days" are deleted. Only these list files (yyyy-MM-dd.txt) are
/// deleted - AOI result files are never touched.
/// Used by the server (files sent to the API) and by the agent (files forwarded to the server).
/// </summary>
public sealed class DailyFileList
{
    private const string DateFormat = "yyyy-MM-dd";
    private static readonly Regex DailyName = new(@"^\d{4}-\d{2}-\d{2}\.txt$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly object _lock = new();
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private int _keepDays;
    private DateTime _lastCleanup = DateTime.MinValue;

    public string Folder { get; private set; } = "";

    public string TodayFile => Path.Combine(Folder, DateTime.Now.ToString(DateFormat, CultureInfo.InvariantCulture) + ".txt");

    /// <param name="keepDays">Delete daily lists older than this. 0 = keep forever.</param>
    public void Load(string folder, int keepDays)
    {
        lock (_lock)
        {
            Folder = folder;
            _keepDays = Math.Max(0, keepDays);
            _names.Clear();
            Directory.CreateDirectory(folder);
            CleanupLocked();
            foreach (var file in DailyFiles())
            {
                foreach (var line in File.ReadLines(file))
                {
                    var name = ParseName(line);
                    if (name != null) _names.Add(name);
                }
            }
        }
    }

    public int Count { get { lock (_lock) return _names.Count; } }

    public bool Contains(string fileName)
    {
        lock (_lock) return _names.Contains(fileName);
    }

    /// <summary>Records a file with free-text details (e.g. "status=accepted").</summary>
    public void Add(string fileName, string details)
    {
        lock (_lock)
        {
            _names.Add(fileName);
            Directory.CreateDirectory(Folder);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {fileName}{(details.Length > 0 ? " | " + details : "")}";
            File.AppendAllText(TodayFile, line + Environment.NewLine);
            if (_lastCleanup.Date != DateTime.Today) CleanupLocked();
        }
    }

    /// <summary>Records a file sent to the API, with line, inputs and quantities.</summary>
    public void AddSent(FileResult r)
    {
        var extras = string.Concat(r.Extras.Select(kv => $" | {kv.Key}={kv.Value}"));
        Add(r.FileName, $"line={r.LineId} | input={r.InputValue}{extras} | rows={r.Rows} | qty={r.Qty} | good={r.Good} | ng={r.Ng} | OK");
    }

    /// <summary>Forget a file (used by Resend). Removes its line from whichever daily list has it.</summary>
    public void Remove(string fileName)
    {
        lock (_lock)
        {
            if (!_names.Remove(fileName)) return;
            foreach (var file in DailyFiles())
            {
                var lines = File.ReadAllLines(file);
                var keep = lines.Where(l => !string.Equals(ParseName(l), fileName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (keep.Count != lines.Length) File.WriteAllLines(file, keep);
            }
        }
    }

    /// <summary>Totals of today's sent files (used for counters after a restart).</summary>
    public (int Files, int Qty, int Good, int Ng) GetTodaySummary()
    {
        lock (_lock)
        {
            int files = 0, qty = 0, good = 0, ng = 0;
            if (!File.Exists(TodayFile)) return (0, 0, 0, 0);
            foreach (var line in File.ReadLines(TodayFile))
            {
                if (ParseName(line) == null) continue;
                files++;
                qty += ReadInt(line, "qty") ?? ReadInt(line, "rows") ?? 0;
                good += ReadInt(line, "good") ?? 0;
                ng += ReadInt(line, "ng") ?? 0;
            }
            return (files, qty, good, ng);
        }
    }

    public void Cleanup()
    {
        lock (_lock) CleanupLocked();
    }

    private void CleanupLocked()
    {
        _lastCleanup = DateTime.Now;
        if (_keepDays <= 0 || !Directory.Exists(Folder)) return;
        var cutoff = DateTime.Today.AddDays(-_keepDays);
        foreach (var file in DailyFiles())
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!DateTime.TryParseExact(name, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            if (day >= cutoff) continue;
            try
            {
                File.Delete(file);
                AppLog.Info($"Deleted old list {file} (older than {_keepDays} days)");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Cannot delete old list {file}: {ex.Message}");
            }
        }
    }

    /// <summary>Only files named like 2026-11-25.txt.</summary>
    private IEnumerable<string> DailyFiles() =>
        Directory.Exists(Folder)
            ? Directory.EnumerateFiles(Folder, "*.txt").Where(f => DailyName.IsMatch(Path.GetFileName(f))).ToList()
            : Enumerable.Empty<string>();

    private static int? ReadInt(string line, string key)
    {
        var m = Regex.Match(line, $@"\b{key}=(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static string? ParseName(string line)
    {
        var parts = line.Split('|');
        if (parts.Length < 2) return null;
        var name = parts[1].Trim();
        return name.Length == 0 ? null : name;
    }
}
