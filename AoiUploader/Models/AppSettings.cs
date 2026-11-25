namespace AoiUploader.Models;

public enum ColumnDataType { Text, Number, Date }

public enum SendMode { PerFile, PerRow }

/// <summary>How PCB qty is calculated for a file.</summary>
public enum QtyMode
{
    /// <summary>Every data row = 1 PCB.</summary>
    ByRow,

    /// <summary>Each distinct value of the Qty field (e.g. BarcodeID) = 1 PCB.</summary>
    ByDistinctField,
}

/// <summary>Maps one column of the AOI file to a field name used in the API payload.</summary>
public class ColumnMapping
{
    /// <summary>Name used in the API JSON (e.g. "Serial").</summary>
    public string FieldName { get; set; } = "";

    /// <summary>Header text in the AOI file (e.g. "BarcodeID"). Looked up first.</summary>
    public string HeaderName { get; set; } = "";

    /// <summary>0-based column position, used when the header name is not found. -1 = none.</summary>
    public int ColumnIndex { get; set; } = -1;

    public ColumnDataType DataType { get; set; } = ColumnDataType.Text;

    /// <summary>Include this field in the API payload.</summary>
    public bool Include { get; set; } = true;
}

/// <summary>Where the value entered by the operator (Recipe / Work Order...) is looked for in each file.</summary>
public enum MatchSource
{
    /// <summary>Part of the file name before the separator, e.g. B613351X1105 in B613351X1105_2026....TST</summary>
    FileNamePrefix,

    /// <summary>The recipe line of the file (Recipe line no. on the General tab).</summary>
    RecipeLine,

    /// <summary>A mapped field in the data rows (Column Mapping).</summary>
    DataField,
}

public class InputSettings
{
    /// <summary>Name shown to the operator, e.g. "Product Name".</summary>
    public string Label { get; set; } = "Product Name";

    public MatchSource MatchSource { get; set; } = MatchSource.FileNamePrefix;

    /// <summary>Used with FileNamePrefix.</summary>
    public string FileNameSeparator { get; set; } = "_";

    /// <summary>Used with DataField: API Field Name from Column Mapping.</summary>
    public string MatchField { get; set; } = "Recipe";

    /// <summary>Only process files created / written after Start was pressed.</summary>
    public bool OnlyFilesAfterStart { get; set; } = true;

    /// <summary>Show a warning banner + sound when a file for another value arrives.</summary>
    public bool WarnOnMismatch { get; set; } = true;

    /// <summary>Start automatically with the last entered value when the program opens.</summary>
    public bool AutoStartWithLastValue { get; set; }

    /// <summary>Last value entered by the operator (pre-filled on open).</summary>
    public string LastValue { get; set; } = "";

    /// <summary>
    /// Extra values the operator must enter before Start (e.g. Work Order).
    /// They are not used to match files - they are only sent to the API as {{Key}}.
    /// </summary>
    public List<ExtraInput> ExtraInputs { get; set; } = new()
    {
        new ExtraInput { Key = "WorkOrder", Label = "Work Order", Required = true },
    };
}

/// <summary>An additional value entered by the operator, sent to the API as {{Key}}.</summary>
public class ExtraInput
{
    /// <summary>Placeholder name used in the body template, e.g. WorkOrder → {{WorkOrder}}.</summary>
    public string Key { get; set; } = "";

    /// <summary>Text shown to the operator.</summary>
    public string Label { get; set; } = "";

    /// <summary>Start is disabled until this value is entered.</summary>
    public bool Required { get; set; } = true;

    /// <summary>Pre-fill the last entered value when the program opens.</summary>
    public bool RememberLastValue { get; set; }

    public string LastValue { get; set; } = "";
}

public class HttpHeader
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

public class GeneralSettings
{
    public string WatchFolder { get; set; } = @"D:\AOI RESULT FILE";
    public string FilePattern { get; set; } = "*.TST";
    public bool IncludeSubfolders { get; set; }
    public int ScanIntervalSeconds { get; set; } = 10;

    /// <summary>File must not have changed for this many seconds before it is read.</summary>
    public int FileStableSeconds { get; set; } = 3;

    /// <summary>0 = process all files. Otherwise skip files older than N days.</summary>
    public int IgnoreFilesOlderThanDays { get; set; }

    public int RecipeLineNumber { get; set; } = 1;
    public int HeaderLineNumber { get; set; } = 2;
    public int DataStartLineNumber { get; set; } = 4;
    public string Delimiter { get; set; } = ",";
    public string DateFormat { get; set; } = "yyyyMMddHHmmss";
    public string OutputDateFormat { get; set; } = "yyyy-MM-ddTHH:mm:ss";

    /// <summary>Field name (from Column Mapping) that holds the pass/fail result.</summary>
    public string ResultField { get; set; } = "Status";

    /// <summary>Comma separated values that count as good (e.g. "G,PASS").</summary>
    public string GoodValues { get; set; } = "G";

    /// <summary>ByRow = count rows. ByDistinctField = count distinct values of <see cref="QtyField"/>.</summary>
    public QtyMode QtyMode { get; set; } = QtyMode.ByRow;

    /// <summary>Field name (from Column Mapping) used when QtyMode = ByDistinctField.</summary>
    public string QtyField { get; set; } = "BarcodeID";

    /// <summary>
    /// Old single processed list (before daily files). Still read at startup if it exists,
    /// so files sent by the older version are not sent again. Never written or deleted.
    /// </summary>
    public string ProcessedListFile { get; set; } = "processed.txt";

    /// <summary>
    /// Folder for the daily processed lists (yyyy-MM-dd.txt). Relative = next to the program.
    /// Only these list files are cleaned up - AOI result files are never deleted.
    /// </summary>
    public string ProcessedFolder { get; set; } = "Processed";

    /// <summary>Delete daily processed lists older than this many days. 0 = keep forever.</summary>
    public int KeepProcessedDays { get; set; } = 30;

    public string LogFolder { get; set; } = "Logs";
    public bool StartWithWindows { get; set; }
}

public class ApiSettings
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "http://localhost:5000/api/aoi";
    public string Method { get; set; } = "POST";
    public string ContentType { get; set; } = "application/json";
    public List<HttpHeader> Headers { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Immediate retries within one attempt.</summary>
    public int RetryCount { get; set; } = 2;

    /// <summary>Wait before trying a failed file again.</summary>
    public int RetryDelaySeconds { get; set; } = 60;

    public SendMode SendMode { get; set; } = SendMode.PerFile;

    /// <summary>Optional: response body must contain this text to count as success.</summary>
    public string SuccessContains { get; set; } = "";

    public string PayloadTemplate { get; set; } = Services.PayloadBuilder.DefaultPerFileTemplate;
}

public class AppSettings
{
    public GeneralSettings General { get; set; } = new();
    public InputSettings Input { get; set; } = new();
    public List<ColumnMapping> ColumnMappings { get; set; } = new();
    public ApiSettings Api { get; set; } = new();

    public static AppSettings CreateDefault() => new()
    {
        ColumnMappings =
        {
            new() { FieldName = "Recipe",    HeaderName = "Recipe",    ColumnIndex = 0 },
            new() { FieldName = "Tagset",    HeaderName = "Tagset",    ColumnIndex = 1 },
            new() { FieldName = "BoardID",   HeaderName = "BoardID",   ColumnIndex = 2, DataType = ColumnDataType.Number },
            new() { FieldName = "Status",    HeaderName = "Status",    ColumnIndex = 3 },
            new() { FieldName = "TestDate",  HeaderName = "TestDate",  ColumnIndex = 4, DataType = ColumnDataType.Date },
            new() { FieldName = "BarcodeID", HeaderName = "BarcodeID", ColumnIndex = 5 },
            new() { FieldName = "Side",      HeaderName = "Side",      ColumnIndex = 6 },
        }
    };
}
