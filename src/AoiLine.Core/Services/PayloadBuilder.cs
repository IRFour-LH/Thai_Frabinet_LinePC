using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using AoiLine.Core.Models;

namespace AoiLine.Core.Services;

/// <summary>
/// Builds the API body from a text template with {{Placeholders}}.
/// String placeholders are inserted JSON-escaped WITHOUT quotes, so write "{{FileName}}" in the template.
/// </summary>
public static class PayloadBuilder
{
    public const string DefaultPerFileTemplate =
"""
{
  "line": "{{LineId}}",
  "lineName": "{{LineName}}",
  "workOrder": "{{WorkOrder}}",
  "productName": "{{InputValue}}",
  "fileName": "{{FileName}}",
  "recipe": "{{Recipe}}",
  "pcbQty": {{PcbQty}},
  "rowCount": {{RowCount}},
  "goodQty": {{GoodQty}},
  "ngQty": {{NgQty}},
  "sentTime": "{{SentTime}}",
  "records": {{Records}}
}
""";

    public const string DefaultPerRowTemplate =
"""
{
  "line": "{{LineId}}",
  "lineName": "{{LineName}}",
  "workOrder": "{{WorkOrder}}",
  "productName": "{{InputValue}}",
  "fileName": "{{FileName}}",
  "recipe": "{{Recipe}}",
  "rowNo": {{RowNo}},
  "sentTime": "{{SentTime}}",
  "record": {{Record}}
}
""";

    public const string PlaceholderHelp =
"""
Line:   {{LineId}}  {{LineName}}
Input:  {{InputValue}} = Product Name entered by the operator
        {{WorkOrder}}  = an additional input, by its Key (also {{Input.WorkOrder}})
File:   {{FileName}}  {{Recipe}}  {{MachineName}}  {{SentTime}}
Qty:    {{PcbQty}}  {{GoodQty}}  {{NgQty}}  {{RowCount}}   (numbers - no quotes)
        {{QtyMode}} = "ByRow" / "ByDistinctField"
Data:   {{Records}} = JSON array of all rows   (Per file mode)
        {{Record}}  = JSON object of one row   (Per row mode)
        {{RowNo}}   = row number 1, 2, 3...     (Per row mode)
Field:  {{Field.BarcodeID}} = value of a mapped field (Per row: that row, Per file: first row)
Text placeholders are inserted without quotes, e.g.  "serial": "{{Field.BarcodeID}}"
""";

    /// <summary>Placeholder names reserved by the program (an extra input cannot use these keys).</summary>
    public static readonly string[] BuiltInNames =
    {
        "FileName", "FilePath", "Recipe", "MachineName", "SentTime", "PcbQty", "GoodQty", "NgQty",
        "Records", "Record", "RowNo", "RowCount", "InputValue", "QtyMode", "LineId", "LineName",
    };

    public static bool IsBuiltIn(string key) => BuiltInNames.Contains(key, StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOpt = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions PrettyOpt = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };
    private static readonly Regex Token = new(@"\{\{\s*([A-Za-z0-9_.\-]+)\s*\}\}", RegexOptions.Compiled);

    public static List<string> Build(ProcessingSettings s, ParsedFile pf)
    {
        var sentTime = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        if (s.Api.SendMode == SendMode.PerRow)
            return pf.Records.Select((r, i) => Render(s.Api.PayloadTemplate, pf, r, i, sentTime)).ToList();
        return new List<string> { Render(s.Api.PayloadTemplate, pf, null, -1, sentTime) };
    }

    private static string Render(string template, ParsedFile pf, Dictionary<string, object?>? record, int rowIndex, string sentTime)
    {
        return Token.Replace(template ?? "", m =>
        {
            var key = m.Groups[1].Value;
            if (key.StartsWith("Field.", StringComparison.OrdinalIgnoreCase))
            {
                var field = key[6..];
                var rec = record ?? pf.Records.FirstOrDefault();
                if (rec == null) return "";
                var kv = rec.FirstOrDefault(k => k.Key.Equals(field, StringComparison.OrdinalIgnoreCase));
                return kv.Key == null ? m.Value : RawValue(kv.Value);
            }

            // Extra operator inputs: {{WorkOrder}} or {{Input.WorkOrder}}
            var extraKey = key.StartsWith("Input.", StringComparison.OrdinalIgnoreCase) ? key[6..] : key;
            if (!IsBuiltIn(key) && pf.Extras.TryGetValue(extraKey, out var extra))
                return Escape(extra);

            return key.ToLowerInvariant() switch
            {
                "filename" => Escape(pf.FileName),
                "filepath" => Escape(pf.FileName),
                "recipe" => Escape(pf.Recipe),
                "machinename" => Escape(Environment.MachineName),
                "senttime" => Escape(sentTime),
                "pcbqty" => pf.PcbQty.ToString(CultureInfo.InvariantCulture),
                "goodqty" => pf.GoodQty.ToString(CultureInfo.InvariantCulture),
                "ngqty" => pf.NgQty.ToString(CultureInfo.InvariantCulture),
                "rowcount" => pf.RowCount.ToString(CultureInfo.InvariantCulture),
                "inputvalue" => Escape(pf.InputValue),
                "qtymode" => Escape(pf.QtyMode.ToString()),
                "lineid" => Escape(pf.LineId),
                "linename" => Escape(pf.LineName),
                "records" => JsonSerializer.Serialize(pf.Records, JsonOpt),
                "record" => JsonSerializer.Serialize(record ?? pf.Records.FirstOrDefault() ?? new(), JsonOpt),
                "rowno" => (rowIndex + 1).ToString(CultureInfo.InvariantCulture),
                _ => m.Value, // unknown placeholder left as-is so it is visible in the preview
            };
        });
    }

    private static string Escape(string? s) => JsonSerializer.Serialize(s ?? "", JsonOpt)[1..^1];

    private static string RawValue(object? v) => v switch
    {
        null => "",
        string str => Escape(str),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Escape(v.ToString()),
    };

    /// <summary>Returns null if valid JSON, otherwise the error message.</summary>
    public static string? ValidateJson(string body)
    {
        try { using var _ = JsonDocument.Parse(body); return null; }
        catch (JsonException ex) { return ex.Message; }
    }

    public static string Pretty(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(doc.RootElement, PrettyOpt);
        }
        catch { return body; }
    }
}
