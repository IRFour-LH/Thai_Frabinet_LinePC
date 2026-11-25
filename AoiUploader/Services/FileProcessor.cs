using AoiUploader.Models;

namespace AoiUploader.Services;

/// <summary>Match → parse → build payload → (optionally) send. Shared by the folder monitor and the "Test send" button.</summary>
public static class FileProcessor
{
    /// <param name="inputValue">
    /// Value entered by the operator. The file must match it, otherwise Kind = Mismatch and nothing is sent.
    /// null = no check (Test send): the file's own value is used for {{InputValue}}.
    /// </param>
    /// <param name="extras">Extra operator inputs (e.g. WorkOrder) - sent to the API as {{Key}}.</param>
    public static async Task<FileResult> ProcessAsync(string path, AppSettings s, bool send, string? inputValue,
        IReadOnlyDictionary<string, string>? extras, CancellationToken ct)
    {
        var r = new FileResult { FilePath = path, FileName = Path.GetFileName(path), Kind = ResultKind.Failed };
        if (extras != null)
            foreach (var kv in extras) r.Extras[kv.Key] = kv.Value;

        // File-name match needs no reading, so a wrong file is never even opened.
        if (inputValue != null && !InputMatcher.NeedsParse(s.Input))
        {
            var values = InputMatcher.GetFileValues(path, null, s.Input);
            r.FileValue = string.Join(", ", values);
            r.InputValue = inputValue;
            var mismatch = InputMatcher.Check(values, inputValue, s.Input);
            if (mismatch != null) return Mismatch(r, mismatch, s.Input.Label);
        }

        ParsedFile pf;
        try
        {
            pf = TstParser.Parse(path, s);
        }
        catch (Exception ex)
        {
            r.Status = "Read error";
            r.Message = ex.Message;
            return r;
        }

        r.Parsed = pf;
        r.Recipe = pf.Recipe;
        r.Rows = pf.RowCount;
        r.Qty = pf.PcbQty;
        r.Good = pf.GoodQty;
        r.Ng = pf.NgQty;
        var warnings = string.Join("; ", pf.Warnings);

        var fileValues = InputMatcher.GetFileValues(path, pf, s.Input);
        r.FileValue = string.Join(", ", fileValues);
        if (inputValue != null)
        {
            var mismatch = InputMatcher.Check(fileValues, inputValue, s.Input);
            if (mismatch != null) return Mismatch(r, mismatch, s.Input.Label);
            pf.InputValue = inputValue.Trim();
        }
        else
        {
            pf.InputValue = fileValues.FirstOrDefault() ?? "";
        }
        r.InputValue = pf.InputValue;
        foreach (var kv in r.Extras) pf.Extras[kv.Key] = kv.Value;

        if (pf.Records.Count == 0)
        {
            r.Status = "No data";
            r.Message = "No data rows found" + (warnings.Length > 0 ? "; " + warnings : "");
            return r;
        }

        try
        {
            r.Payloads = PayloadBuilder.Build(s, pf);
        }
        catch (Exception ex)
        {
            r.Status = "Template error";
            r.Message = ex.Message;
            return r;
        }

        if (s.Api.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            var err = r.Payloads.Select(PayloadBuilder.ValidateJson).FirstOrDefault(e => e != null);
            if (err != null)
            {
                r.Status = "Template error";
                r.Message = "Payload is not valid JSON: " + err;
                return r;
            }
        }

        if (!send)
        {
            r.Kind = ResultKind.ParsedOnly;
            r.Status = "Parsed (API off)";
            r.Message = warnings;
            return r;
        }

        for (int i = 0; i < r.Payloads.Count; i++)
        {
            var ar = await ApiSender.SendAsync(s.Api, r.Payloads[i], ct);
            r.HttpStatus = ar.StatusCode;
            r.ResponseBody = ar.ResponseBody;
            if (!ar.Success)
            {
                r.Status = "API failed";
                r.Message = (r.Payloads.Count > 1 ? $"Row {i + 1}: " : "") + ar.Error;
                return r;
            }
        }

        r.Kind = ResultKind.Sent;
        r.Status = r.HttpStatus.HasValue ? $"Sent (HTTP {r.HttpStatus})" : "Sent";
        r.Message = warnings;
        return r;
    }

    private static FileResult Mismatch(FileResult r, string message, string label)
    {
        r.Kind = ResultKind.Mismatch;
        r.Status = "Wrong " + (string.IsNullOrWhiteSpace(label) ? "value" : label);
        r.Message = message + " - not sent";
        return r;
    }
}
