namespace AoiUploader.Models;

public enum ResultKind { Sent, Failed, ParsedOnly, Mismatch }

public class ParsedFile
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Recipe { get; set; } = "";
    public List<string> Headers { get; set; } = new();
    public List<Dictionary<string, object?>> Records { get; } = new();

    /// <summary>Raw text of ALL mapped fields per row (also fields not sent to the API).</summary>
    public List<Dictionary<string, string?>> RawRecords { get; } = new();

    /// <summary>Value entered by the operator (Recipe / Work Order...), for {{InputValue}}.</summary>
    public string InputValue { get; set; } = "";

    /// <summary>Extra operator inputs (e.g. WorkOrder), for {{WorkOrder}}.</summary>
    public Dictionary<string, string> Extras { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; } = new();
    /// <summary>Number of data rows in the file.</summary>
    public int RowCount => Records.Count;

    /// <summary>PCB qty according to the selected QtyMode (rows or distinct barcodes).</summary>
    public int PcbQty { get; set; }

    /// <summary>Good / NG in the same unit as PcbQty.</summary>
    public int GoodQty { get; set; }
    public int NgQty { get; set; }

    public QtyMode QtyMode { get; set; }
}

public class FileResult
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Recipe { get; set; } = "";

    /// <summary>Value found in the file that was compared with the operator input.</summary>
    public string FileValue { get; set; } = "";
    public string InputValue { get; set; } = "";

    /// <summary>Extra operator inputs used for this file (e.g. WorkOrder).</summary>
    public Dictionary<string, string> Extras { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extra input values for display, e.g. "WO-12345".</summary>
    public string ExtrasText => string.Join(" · ", Extras.Values);
    public int Rows { get; set; }

    /// <summary>PCB qty (by row or by distinct barcode, depending on settings).</summary>
    public int Qty { get; set; }
    public int Good { get; set; }
    public int Ng { get; set; }
    public ResultKind Kind { get; set; }
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public int? HttpStatus { get; set; }
    public string? ResponseBody { get; set; }
    public List<string> Payloads { get; set; } = new();
    public ParsedFile? Parsed { get; set; }
}
