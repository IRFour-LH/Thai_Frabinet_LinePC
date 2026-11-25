using System.Text.Json;
using AoiLine.Core.Models;
using AoiLine.Core.Services;

namespace AoiLine.Core.Tests;

public class CoreTests
{
    /// <summary>Builds a file in the same format as the AOI machine.</summary>
    private static string Tst(string recipe, params (int Board, string Status, string Barcode)[] rows)
    {
        var lines = new List<string>
        {
            recipe,
            " Recipe              Tagset   BoardID  Status   TestDate        BarcodeID            Side     ",
            "|-------------------|--------|--------|--------|---------------|--------------------|--------|",
        };
        lines.AddRange(rows.Select(r => $"{recipe},Default,{r.Board},{r.Status},20261125100000,{r.Barcode},Top"));
        lines.Add("");
        return string.Join("\r\n", lines);
    }

    private static readonly string SevenBoards = Tst("B603401X1101",
        (1, "G", "AAA"), (2, "G", "AAA"), (3, "NG", "BBB"), (4, "G", "BBB"), (5, "NG", "CCC"), (6, "G", "CCC"), (7, "G", "DDD"));

    [Fact]
    public void Parse_reads_recipe_rows_and_converts_types()
    {
        var pf = TstParser.Parse("B603401X1101_1.TST", SevenBoards, ProcessingSettings.CreateDefault());
        Assert.Equal("B603401X1101", pf.Recipe);
        Assert.Equal(7, pf.RowCount);
        Assert.Equal(1L, pf.Records[0]["BoardID"]);
        Assert.Equal("2026-11-25T10:00:00", pf.Records[0]["TestDate"]);
        Assert.Empty(pf.Warnings);
    }

    [Fact]
    public void Qty_by_row_counts_every_row()
    {
        var pf = TstParser.Parse("x.TST", SevenBoards, ProcessingSettings.CreateDefault());
        Assert.Equal((7, 5, 2), (pf.PcbQty, pf.GoodQty, pf.NgQty));
    }

    [Fact]
    public void Qty_by_distinct_barcode_and_NG_if_any_row_NG()
    {
        var s = ProcessingSettings.CreateDefault();
        s.Parsing.QtyMode = QtyMode.ByDistinctField;
        var pf = TstParser.Parse("x.TST", SevenBoards, s);
        // AAA good, BBB NG, CCC NG, DDD good
        Assert.Equal((4, 2, 2), (pf.PcbQty, pf.GoodQty, pf.NgQty));
    }

    [Fact]
    public void Empty_barcode_counts_as_its_own_pcb_with_warning()
    {
        var s = ProcessingSettings.CreateDefault();
        s.Parsing.QtyMode = QtyMode.ByDistinctField;
        var pf = TstParser.Parse("x.TST", Tst("R", (1, "G", "A"), (2, "G", ""), (3, "G", "")), s);
        Assert.Equal(3, pf.PcbQty);
        Assert.Contains(pf.Warnings, w => w.Contains("empty"));
    }

    [Fact]
    public void Header_name_wins_and_index_is_fallback()
    {
        var s = ProcessingSettings.CreateDefault();
        var barcode = s.ColumnMappings.Single(m => m.FieldName == "BarcodeID");
        barcode.ColumnIndex = 0; // wrong index, header still found
        Assert.Equal("AAA", TstParser.Parse("x.TST", SevenBoards, s).Records[0]["BarcodeID"]);

        barcode.ColumnIndex = 5;
        barcode.HeaderName = "NoSuchHeader"; // header missing -> index used
        var pf = TstParser.Parse("x.TST", SevenBoards, s);
        Assert.Equal("AAA", pf.Records[0]["BarcodeID"]);
        Assert.Contains(pf.Warnings, w => w.Contains("NoSuchHeader"));
    }

    [Theory]
    [InlineData(MatchSource.FileNamePrefix)]
    [InlineData(MatchSource.RecipeLine)]
    [InlineData(MatchSource.DataField)]
    public void Matching_works_for_all_sources_and_ignores_case(MatchSource source)
    {
        var s = ProcessingSettings.CreateDefault();
        s.Input.MatchSource = source;
        var pf = TstParser.Parse("B603401X1101_2026.TST", SevenBoards, s);
        var values = InputMatcher.GetFileValues("B603401X1101_2026.TST", pf, s.Input);
        Assert.Null(InputMatcher.Check(values, "b603401x1101", s.Input));
        Assert.NotNull(InputMatcher.Check(values, "B613351X1105", s.Input));
    }

    [Fact]
    public async Task Wrong_product_name_is_mismatch_and_not_sent()
    {
        var s = ProcessingSettings.CreateDefault();
        var r = await FileProcessor.ProcessAsync(new ProcessRequest
        {
            FileName = "B603401X1101_2026.TST", Content = SevenBoards, InputValue = "B613351X1105", LineId = "L3",
        }, s, send: true, CancellationToken.None);
        Assert.Equal(ResultKind.Mismatch, r.Kind);
        Assert.Empty(r.Payloads);
    }

    [Fact]
    public async Task Payload_contains_line_work_order_and_product_name()
    {
        var s = ProcessingSettings.CreateDefault();
        var r = await FileProcessor.ProcessAsync(new ProcessRequest
        {
            FileName = "B603401X1101_2026.TST", Content = SevenBoards, InputValue = "B603401X1101",
            Extras = new Dictionary<string, string> { ["WorkOrder"] = "WO-1" }, LineId = "L3", LineName = "Line 3",
        }, s, send: false, CancellationToken.None);

        Assert.Equal(ResultKind.ParsedOnly, r.Kind);
        using var doc = JsonDocument.Parse(r.Payloads.Single());
        var root = doc.RootElement;
        Assert.Equal("L3", root.GetProperty("line").GetString());
        Assert.Equal("Line 3", root.GetProperty("lineName").GetString());
        Assert.Equal("WO-1", root.GetProperty("workOrder").GetString());
        Assert.Equal("B603401X1101", root.GetProperty("productName").GetString());
        Assert.Equal(7, root.GetProperty("pcbQty").GetInt32());
        Assert.Equal(7, root.GetProperty("records").GetArrayLength());
    }

    [Fact]
    public void Extra_input_cannot_override_built_in_placeholder()
    {
        var s = ProcessingSettings.CreateDefault();
        s.Api.PayloadTemplate = "{\"a\":\"{{Input.WorkOrder}}\",\"b\":\"{{Recipe}}\"}";
        var pf = TstParser.Parse("x.TST", SevenBoards, s);
        pf.Extras["WorkOrder"] = "WO-X";
        pf.Extras["Recipe"] = "HACK";
        Assert.Equal("{\"a\":\"WO-X\",\"b\":\"B603401X1101\"}", PayloadBuilder.Build(s, pf)[0]);
    }

    [Fact]
    public void Per_row_mode_makes_one_payload_per_row()
    {
        var s = ProcessingSettings.CreateDefault();
        s.Api.SendMode = SendMode.PerRow;
        s.Api.PayloadTemplate = PayloadBuilder.DefaultPerRowTemplate;
        var pf = TstParser.Parse("x.TST", SevenBoards, s);
        Assert.Equal(7, PayloadBuilder.Build(s, pf).Count);
    }

    [Fact]
    public void Daily_list_cleanup_deletes_only_old_daily_lists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aoiline-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string Day(int ago) => DateTime.Today.AddDays(-ago).ToString("yyyy-MM-dd");
            File.WriteAllText(Path.Combine(dir, Day(31) + ".txt"), $"{Day(31)} 10:00:00 | OLD.TST | OK\n");
            File.WriteAllText(Path.Combine(dir, Day(30) + ".txt"), $"{Day(30)} 10:00:00 | KEEP.TST | OK\n");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "keep me");
            File.WriteAllText(Path.Combine(dir, "B613_OLD.TST"), "keep me");
            File.SetLastWriteTime(Path.Combine(dir, "B613_OLD.TST"), DateTime.Now.AddDays(-100));

            var list = new DailyFileList();
            list.Load(dir, keepDays: 30);

            Assert.False(File.Exists(Path.Combine(dir, Day(31) + ".txt")));
            Assert.True(File.Exists(Path.Combine(dir, Day(30) + ".txt")));
            Assert.True(File.Exists(Path.Combine(dir, "notes.txt")));
            Assert.True(File.Exists(Path.Combine(dir, "B613_OLD.TST")));
            Assert.True(list.Contains("KEEP.TST"));
            Assert.False(list.Contains("OLD.TST"));

            list.AddSent(new FileResult { FileName = "NEW.TST", LineId = "L3", Rows = 7, Qty = 4, Good = 2, Ng = 2 });
            Assert.Equal((1, 4, 2, 2), list.GetTodaySummary());
            list.Remove("NEW.TST");
            Assert.False(list.Contains("NEW.TST"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
