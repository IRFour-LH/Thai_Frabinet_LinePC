using AoiLine.Core.Models;

namespace AoiLine.Core.Services;

/// <summary>Match → parse → build payload → (optionally) send.</summary>
public static class FileProcessor
{
    public static async Task<FileResult> ProcessAsync(ProcessRequest req, ProcessingSettings s, bool send, CancellationToken ct)
    {
        var r = new FileResult { FileName = req.FileName, LineId = req.LineId, Kind = ResultKind.Failed };
        if (req.Extras != null)
            foreach (var kv in req.Extras) r.Extras[kv.Key] = kv.Value;

        // File-name match needs no parsing, so a wrong file is rejected immediately.
        if (req.InputValue != null && !InputMatcher.NeedsParse(s.Input))
        {
            var values = InputMatcher.GetFileValues(req.FileName, null, s.Input);
            r.FileValue = string.Join(", ", values);
            r.InputValue = req.InputValue;
            var mismatch = InputMatcher.Check(values, req.InputValue, s.Input);
            if (mismatch != null) return Mismatch(r, mismatch, s.Input.Label);
        }

        ParsedFile pf;
        try
        {
            pf = TstParser.Parse(req.FileName, req.Content, s);
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

        var fileValues = InputMatcher.GetFileValues(req.FileName, pf, s.Input);
        r.FileValue = string.Join(", ", fileValues);
        if (req.InputValue != null)
        {
            var mismatch = InputMatcher.Check(fileValues, req.InputValue, s.Input);
            if (mismatch != null) return Mismatch(r, mismatch, s.Input.Label);
            pf.InputValue = req.InputValue.Trim();
        }
        else
        {
            pf.InputValue = fileValues.FirstOrDefault() ?? "";
        }
        r.InputValue = pf.InputValue;
        foreach (var kv in r.Extras) pf.Extras[kv.Key] = kv.Value;
        pf.LineId = req.LineId;
        pf.LineName = req.LineName;

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
