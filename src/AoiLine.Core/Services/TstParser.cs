using System.Globalization;
using System.Text;
using AoiLine.Core.Models;

namespace AoiLine.Core.Services;

/// <summary>Reads an AOI result file using the line numbers and column mapping in settings.</summary>
public static class TstParser
{
    /// <summary>Reads a file without blocking the AOI machine (FileShare.ReadWrite).</summary>
    public static string ReadText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    public static List<string> SplitLines(string content) =>
        content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    public static string ResolveDelimiter(string d) => d switch
    {
        null or "" => ",",
        "\\t" or "TAB" or "tab" or "Tab" => "\t",
        _ => d,
    };

    /// <summary>Header may use the delimiter or be space-aligned (as in the AOI files).</summary>
    public static List<string> SplitHeader(string line, string delimiter)
    {
        if (line.Contains(delimiter))
            return line.Split(delimiter).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        return line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public static List<string> ReadHeaders(string content, ParsingSettings p)
    {
        var lines = SplitLines(content);
        if (p.HeaderLineNumber < 1 || p.HeaderLineNumber > lines.Count) return new();
        return SplitHeader(lines[p.HeaderLineNumber - 1], ResolveDelimiter(p.Delimiter));
    }

    /// <summary>Returns the first data row split into values (used to guess data types).</summary>
    public static string[]? ReadFirstDataRow(string content, ParsingSettings p)
    {
        var lines = SplitLines(content);
        var delim = ResolveDelimiter(p.Delimiter);
        for (int i = Math.Max(1, p.DataStartLineNumber) - 1; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (IsDataLine(line)) return line.Split(delim).Select(x => x.Trim()).ToArray();
        }
        return null;
    }

    public static ColumnDataType GuessType(string? v, string dateFormat)
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

    private static bool IsDataLine(string line) =>
        line.Length > 0 && !line.StartsWith('|') && !line.StartsWith("--");

    public static ParsedFile Parse(string fileName, string content, ProcessingSettings settings)
    {
        var g = settings.Parsing;
        var lines = SplitLines(content);
        var delim = ResolveDelimiter(g.Delimiter);

        var pf = new ParsedFile { FileName = fileName };

        if (g.RecipeLineNumber >= 1 && g.RecipeLineNumber <= lines.Count)
            pf.Recipe = lines[g.RecipeLineNumber - 1].Trim();

        if (g.HeaderLineNumber >= 1 && g.HeaderLineNumber <= lines.Count)
            pf.Headers = SplitHeader(lines[g.HeaderLineNumber - 1], delim);
        else
            pf.Warnings.Add($"Header line {g.HeaderLineNumber} not found");

        // Resolve every mapping to a column index: header name first, then fixed index.
        var resolved = new List<(ColumnMapping Map, int Index)>();
        foreach (var m in settings.ColumnMappings)
        {
            if (string.IsNullOrWhiteSpace(m.FieldName)) continue;
            int idx = -1;
            if (!string.IsNullOrWhiteSpace(m.HeaderName))
            {
                idx = pf.Headers.FindIndex(h => h.Equals(m.HeaderName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (idx < 0)
                    pf.Warnings.Add($"Header '{m.HeaderName}' not found, using column index {m.ColumnIndex}");
            }
            if (idx < 0) idx = m.ColumnIndex;
            resolved.Add((m, idx));
        }

        var goodSet = (g.GoodValues ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int FieldIndex(string? field) => resolved
            .Where(r => r.Map.FieldName.Equals(field?.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Index)
            .DefaultIfEmpty(-1)
            .First();

        int resultIdx = FieldIndex(g.ResultField);
        if (!string.IsNullOrWhiteSpace(g.ResultField) && resultIdx < 0)
            pf.Warnings.Add($"Result field '{g.ResultField}' is not mapped; G/NG not counted");

        pf.QtyMode = g.QtyMode;
        int qtyIdx = -1;
        if (g.QtyMode == QtyMode.ByDistinctField)
        {
            qtyIdx = FieldIndex(g.QtyField);
            if (qtyIdx < 0)
            {
                pf.Warnings.Add($"Qty field '{g.QtyField}' is not mapped; PCB qty counted by row instead");
                pf.QtyMode = QtyMode.ByRow;
            }
        }

        var rowInfo = new List<(string Key, bool? Good)>();
        int shortRows = 0;
        for (int i = Math.Max(1, g.DataStartLineNumber) - 1; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (!IsDataLine(line)) continue;

            var parts = line.Split(delim).Select(x => x.Trim()).ToArray();
            var rec = new Dictionary<string, object?>();
            var rawRec = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            bool missing = false;
            foreach (var (map, idx) in resolved)
            {
                string? raw = idx >= 0 && idx < parts.Length ? parts[idx] : null;
                if (raw == null) missing = true;
                rawRec[map.FieldName] = raw;
                if (map.Include) rec[map.FieldName] = Convert(raw, map.DataType, g);
            }
            if (missing) shortRows++;

            bool? good = resultIdx >= 0 && resultIdx < parts.Length ? goodSet.Contains(parts[resultIdx]) : null;
            string key = qtyIdx >= 0 && qtyIdx < parts.Length ? parts[qtyIdx] : "";
            rowInfo.Add((key, good));
            pf.Records.Add(rec);
            pf.RawRecords.Add(rawRec);
        }
        if (shortRows > 0)
            pf.Warnings.Add($"{shortRows} row(s) have fewer columns than the mapping expects");

        CalculateQty(pf, rowInfo, g.QtyField);
        return pf;
    }

    /// <summary>
    /// ByRow: each row is one PCB.
    /// ByDistinctField: rows with the same value (e.g. same BarcodeID) are one PCB.
    /// That PCB is NG if ANY of its rows is NG, otherwise Good.
    /// Rows with an empty value are each counted as their own PCB.
    /// </summary>
    private static void CalculateQty(ParsedFile pf, List<(string Key, bool? Good)> rows, string qtyField)
    {
        if (pf.QtyMode == QtyMode.ByRow)
        {
            pf.PcbQty = rows.Count;
            pf.GoodQty = rows.Count(r => r.Good == true);
            pf.NgQty = rows.Count(r => r.Good == false);
            return;
        }

        int empty = 0;
        var groups = new Dictionary<string, bool?>(StringComparer.Ordinal);
        for (int i = 0; i < rows.Count; i++)
        {
            var (key, good) = rows[i];
            if (key.Length == 0)
            {
                empty++;
                key = "\0row" + i;
            }
            if (!groups.TryGetValue(key, out var current))
                groups[key] = good;
            else if (good == false || (current == null && good == true))
                groups[key] = good;   // NG wins over Good; Good wins over unknown
        }
        if (empty > 0)
            pf.Warnings.Add($"{empty} row(s) have an empty {qtyField}; each counted as 1 PCB");

        pf.PcbQty = groups.Count;
        pf.GoodQty = groups.Values.Count(v => v == true);
        pf.NgQty = groups.Values.Count(v => v == false);
    }

    private static object? Convert(string? raw, ColumnDataType type, ParsingSettings g)
    {
        if (raw == null) return null;
        switch (type)
        {
            case ColumnDataType.Number:
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                return raw;
            case ColumnDataType.Date:
                if (DateTime.TryParseExact(raw, g.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    return dt.ToString(string.IsNullOrWhiteSpace(g.OutputDateFormat) ? "yyyy-MM-ddTHH:mm:ss" : g.OutputDateFormat,
                                       CultureInfo.InvariantCulture);
                return raw;
            default:
                return raw;
        }
    }
}
