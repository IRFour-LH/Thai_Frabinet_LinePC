namespace AoiLine.Core.Models;

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

/// <summary>Where the value entered by the operator (Product Name) is looked for in each file.</summary>
public enum MatchSource
{
    /// <summary>Part of the file name before the separator, e.g. B613351X1105 in B613351X1105_2026....TST</summary>
    FileNamePrefix,

    /// <summary>The recipe line of the file.</summary>
    RecipeLine,

    /// <summary>A mapped field in the data rows (Column Mapping).</summary>
    DataField,
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

public class HttpHeader
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>How an AOI result file is read.</summary>
public class ParsingSettings
{
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

    public QtyMode QtyMode { get; set; } = QtyMode.ByRow;

    /// <summary>Field name (from Column Mapping) used when QtyMode = ByDistinctField.</summary>
    public string QtyField { get; set; } = "BarcodeID";
}

/// <summary>What the operator enters on the line page before Start.</summary>
public class InputSettings
{
    /// <summary>Name of the main input shown to the operator (used to match files).</summary>
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

    /// <summary>Pre-fill the last Product Name on the line page.</summary>
    public bool RememberLastValue { get; set; } = true;

    /// <summary>Extra values entered before Start (e.g. Work Order), sent to the API as {{Key}}.</summary>
    public List<ExtraInput> ExtraInputs { get; set; } = new()
    {
        new ExtraInput { Key = "WorkOrder", Label = "Work Order", Required = true },
    };
}

/// <summary>An additional value entered by the operator, sent to the API as {{Key}}.</summary>
public class ExtraInput
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Required { get; set; } = true;
    public bool RememberLastValue { get; set; }
}

public class ApiSettings
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "http://localhost:8080/api/aoi";
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

/// <summary>Everything needed to turn an AOI file into API calls. Same for all lines.</summary>
public class ProcessingSettings
{
    public ParsingSettings Parsing { get; set; } = new();
    public InputSettings Input { get; set; } = new();
    public List<ColumnMapping> ColumnMappings { get; set; } = new();
    public ApiSettings Api { get; set; } = new();

    public static ProcessingSettings CreateDefault() => new()
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
        },
    };
}
