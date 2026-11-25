using System.Globalization;
using System.Text.RegularExpressions;
using AoiUploader.Models;

namespace AoiUploader.Services;

/// <summary>
/// Remembers which files were sent successfully, in one text file per day:
///   Processed\2026-11-25.txt
///   2026-11-25 10:15:02 | B603401X1101_20250724002918001.TST | input=B603401X1101 | WorkOrder=WO1 | rows=7 | qty=5 | good=5 | ng=0 | OK
/// Daily files older than "keep days" are deleted. Only these list files are deleted -
/// AOI result files are never touched.
/// </summary>
public sealed class ProcessedTracker
{
    private const string DateFormat = "yyyy-MM-dd";
    private static readonly Regex DailyName = new(@"^\d{4}-\d{2}-\d{2}\.txt$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly object _lock = new();
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private int _keepDays;
    private DateTime _lastCleanup = DateTime.MinValue;

    /// <summary>Folder holding the daily list files.</summary>
    public string Folder { get; private set; } = "";

    /// <summary>Today's list file.</summary>
    public string TodayFile => Path.Combine(Folder, DateTime.Now.ToString(DateFormat, CultureInfo.InvariantCulture) + ".txt");

    /// <param name="folder">Folder for daily list files.</param>
    /// <param name="keepDays">Delete daily lists older than this. 0 = keep forever.</param>
    /// <param name="legacyFile">Old single processed.txt - read only, if it exists.</param>
    public void Load(string folder, int keepDays, string? legacyFile = null)
    {
        lock (_lock)
        {
            Folder = folder;
            _keepDays = Math.Max(0, keepDays);
            _names.Clear();
            Directory.CreateDirectory(folder);
            CleanupLocked();

            foreach (var file in DailyFiles())
                AddNames(file);
            if (!string.IsNullOrWhiteSpace(legacyFile) && File.Exists(legacyFile))
                AddNames(legacyFile);
        }
    }

    public int Count { get { lock (_lock) return _names.Count; } }

    public bool IsProcessed(string fileName)
    {
        lock (_lock) return _names.Contains(fileName);
    }

    public void MarkProcessed(FileResult r)
    {
        lock (_lock)
        {
            _names.Add(r.FileName);
            Directory.CreateDirectory(Folder);
            var extras = string.Concat(r.Extras.Select(kv => $" | {kv.Key}={kv.Value}"));
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {r.FileName} | input={r.InputValue}{extras} | rows={r.Rows} | qty={r.Qty} | good={r.Good} | ng={r.Ng} | OK";
            File.AppendAllText(TodayFile, line + Environment.NewLine);
            if (_lastCleanup.Date != DateTime.Today) CleanupLocked(); // once a day while running
        }
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

    /// <summary>Deletes daily list files older than keep days. Never touches anything else.</summary>
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
                AppLogger.Info($"Deleted old processed list {Path.GetFileName(file)} (older than {_keepDays} days)");
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Cannot delete old processed list {file}: {ex.Message}");
            }
        }
    }

    /// <summary>Only files named like 2026-11-25.txt in the processed folder.</summary>
    private IEnumerable<string> DailyFiles() =>
        Directory.Exists(Folder)
            ? Directory.EnumerateFiles(Folder, "*.txt").Where(f => DailyName.IsMatch(Path.GetFileName(f))).ToList()
            : Enumerable.Empty<string>();

    private void AddNames(string file)
    {
        foreach (var line in File.ReadLines(file))
        {
            var name = ParseName(line);
            if (name != null) _names.Add(name);
        }
    }

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
