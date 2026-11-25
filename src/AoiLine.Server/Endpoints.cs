using System.Text.RegularExpressions;
using AoiLine.Core.Models;
using AoiLine.Core.Services;

namespace AoiLine.Server;

public static class Endpoints
{
    private static readonly Regex KeyPattern = new(@"^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    public static void Map(WebApplication app)
    {
        // ------------------------------------------------------------ pages
        app.MapGet("/line/{id}", (string id, IWebHostEnvironment env) =>
            Results.File(Path.Combine(env.WebRootPath, "line.html"), "text/html"));
        app.MapGet("/settings", (IWebHostEnvironment env) =>
            Results.File(Path.Combine(env.WebRootPath, "settings.html"), "text/html"));

        // ------------------------------------------------------------ agent API
        app.MapPost("/api/agent/heartbeat", (HeartbeatRequest req, LineManager lines) =>
        {
            var w = lines.GetOrAdd(req.LineId, req.LineName);
            return w == null
                ? Results.BadRequest(new { error = "Invalid Line ID (use letters, numbers, - or _, max 20)" })
                : Results.Ok(w.Heartbeat(req));
        });

        app.MapPost("/api/agent/files", (FileUploadRequest req, LineManager lines) =>
        {
            var w = lines.GetOrAdd(req.LineId, req.LineName);
            if (w == null) return Results.BadRequest(new { error = "Invalid Line ID" });
            var r = w.Receive(req);
            return r.Status == "rejected" ? Results.BadRequest(r) : Results.Ok(r);
        });

        // ------------------------------------------------------------ lines (web pages)
        app.MapGet("/api/lines", (LineManager lines) => lines.All().Select(w => w.GetView()));

        app.MapGet("/api/lines/{id}", (string id, LineManager lines) =>
            lines.Get(id) is { } w ? Results.Ok(w.GetDetail()) : Results.NotFound());

        app.MapPost("/api/lines/{id}/start", (string id, StartRequest req, LineManager lines) =>
        {
            if (lines.Get(id) is not { } w) return Results.NotFound();
            var (ok, msg) = w.Start(req);
            return ok ? Results.Ok(new { message = msg }) : Results.BadRequest(new { error = msg });
        });

        app.MapPost("/api/lines/{id}/stop", (string id, StopRequest req, LineManager lines) =>
            lines.Get(id) is { } w ? Results.Ok(w.Stop(req.Force)) : Results.NotFound());

        app.MapPost("/api/lines/{id}/dismiss", (string id, LineManager lines) =>
        {
            if (lines.Get(id) is not { } w) return Results.NotFound();
            w.DismissWarning();
            return Results.Ok();
        });

        app.MapPost("/api/lines/{id}/resend", (string id, ResendRequest req, LineManager lines) =>
        {
            if (lines.Get(id) is not { } w) return Results.NotFound();
            var (ok, msg) = w.Resend(req.FileName);
            return ok ? Results.Ok(new { message = msg }) : Results.BadRequest(new { error = msg });
        });

        app.MapDelete("/api/lines/{id}", (string id, LineManager lines) =>
            lines.Remove(id) ? Results.Ok() : Results.NotFound());

        // ------------------------------------------------------------ live updates (Server-Sent Events)
        app.MapGet("/api/events", async (HttpContext ctx, EventHub hub) =>
        {
            var ct = ctx.RequestAborted;
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            var ch = hub.Subscribe();
            try
            {
                await ctx.Response.WriteAsync(": connected\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
                while (!ct.IsCancellationRequested)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wait.CancelAfter(TimeSpan.FromSeconds(15));
                    try
                    {
                        if (!await ch.Reader.WaitToReadAsync(wait.Token)) break;
                        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        while (ch.Reader.TryRead(out var id)) ids.Add(id);
                        foreach (var id in ids) await ctx.Response.WriteAsync($"data: {id}\n\n", ct);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await ctx.Response.WriteAsync(": ping\n\n", ct); // keeps the connection open
                    }
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* page closed */ }
            finally { hub.Unsubscribe(ch); }
        });

        // ------------------------------------------------------------ settings
        app.MapGet("/api/settings", (SettingsStore store) => store.Current);

        app.MapPut("/api/settings", (ServerSettings s, SettingsStore store, LineManager lines, EventHub hub) =>
        {
            var errors = Validate(s);
            if (errors.Count > 0) return Results.BadRequest(new { errors });
            var portChanged = s.Port != store.Current.Port;
            store.Save(s);
            AppLog.KeepDays = s.KeepDays;
            lines.SyncWithSettings();
            AppLog.Info("Settings saved");
            hub.Publish("*");
            return Results.Ok(new { message = portChanged ? "Saved. The new port is used after the server restarts." : "Saved." });
        });

        app.MapGet("/api/info", (SettingsStore store, DataRoot root) => new
        {
            version = typeof(Endpoints).Assembly.GetName().Version?.ToString(3),
            machine = Environment.MachineName,
            port = store.Current.Port,
            dataFolder = root.Path,
            placeholderHelp = PayloadBuilder.PlaceholderHelp,
            defaultPerFileTemplate = PayloadBuilder.DefaultPerFileTemplate,
            defaultPerRowTemplate = PayloadBuilder.DefaultPerRowTemplate,
        });

        // ------------------------------------------------------------ settings tools (sample file)
        app.MapPost("/api/tools/detect", (DetectRequest req) =>
        {
            var headers = TstParser.ReadHeaders(req.Content, req.Parsing);
            var first = TstParser.ReadFirstDataRow(req.Content, req.Parsing);
            var mappings = headers.Select((h, i) => new ColumnMapping
            {
                FieldName = h,
                HeaderName = h,
                ColumnIndex = i,
                DataType = TstParser.GuessType(first != null && i < first.Length ? first[i] : null, req.Parsing.DateFormat),
            }).ToList();
            return new { headers, firstRow = first, mappings };
        });

        app.MapPost("/api/tools/preview", (PreviewRequest req) =>
        {
            var s = req.Processing;
            var pf = TstParser.Parse(req.FileName, req.Content, s);
            var values = InputMatcher.GetFileValues(req.FileName, pf, s.Input);
            pf.InputValue = values.FirstOrDefault() ?? "";
            foreach (var x in s.Input.ExtraInputs.Where(x => !string.IsNullOrWhiteSpace(x.Key)))
                pf.Extras[x.Key] = "TEST-" + x.Key;
            pf.LineId = "L1";
            pf.LineName = "Line 1";
            List<string> payloads;
            string? payloadError = null;
            try { payloads = PayloadBuilder.Build(s, pf); }
            catch (Exception ex) { payloads = new(); payloadError = ex.Message; }
            var first = payloads.FirstOrDefault() ?? "";
            var jsonError = first.Length > 0 && s.Api.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                ? PayloadBuilder.ValidateJson(first) : null;

            var fields = s.ColumnMappings.Where(m => m.Include && !string.IsNullOrWhiteSpace(m.FieldName))
                                         .Select(m => m.FieldName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new
            {
                recipe = pf.Recipe,
                headers = pf.Headers,
                rowCount = pf.RowCount,
                pcbQty = pf.PcbQty,
                goodQty = pf.GoodQty,
                ngQty = pf.NgQty,
                qtyMode = pf.QtyMode.ToString(),
                warnings = pf.Warnings,
                columns = fields,
                rows = pf.Records.Select(r => fields.Select(f => r.TryGetValue(f, out var v) ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : "").ToList()),
                matchValues = values,
                matchResult = req.InputValue is { Length: > 0 } iv ? InputMatcher.Check(values, iv, s.Input) ?? "MATCH" : null,
                payloadCount = payloads.Count,
                payload = jsonError == null && first.Length > 0 ? PayloadBuilder.Pretty(first) : first,
                payloadError = payloadError ?? jsonError,
            };
        });

        app.MapPost("/api/tools/test-send", async (PreviewRequest req) =>
        {
            var extras = req.Processing.Input.ExtraInputs.Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .ToDictionary(x => x.Key, x => "TEST-" + x.Key);
            var r = await FileProcessor.ProcessAsync(new ProcessRequest
            {
                FileName = req.FileName, Content = req.Content, InputValue = null, Extras = extras,
                LineId = "TEST", LineName = "Test send",
            }, req.Processing, send: true, CancellationToken.None);
            AppLog.Info($"Test send of {req.FileName}: {r.Status} {r.Message}");
            return new { status = r.Status, kind = r.Kind.ToString(), message = r.Message, httpStatus = r.HttpStatus, response = r.ResponseBody };
        });
    }

    private static List<string> Validate(ServerSettings s)
    {
        var e = new List<string>();
        if (s.Port is < 1 or > 65535) e.Add("Port must be 1-65535.");
        if (s.KeepDays < 0) e.Add("Keep days must be 0 or more.");
        foreach (var l in s.Lines)
        {
            if (!LineManager.ValidId.IsMatch(l.Id ?? "")) e.Add($"Line ID '{l.Id}' is invalid (letters, numbers, - or _, max 20).");
        }
        foreach (var dup in s.Lines.GroupBy(l => l.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            e.Add($"Duplicate Line ID '{dup.Key}'.");

        var p = s.Processing;
        if (string.IsNullOrWhiteSpace(p.Input.Label)) e.Add("Operator input label is empty.");
        var names = p.ColumnMappings.Select(m => m.FieldName?.Trim() ?? "").ToList();
        if (names.Any(string.IsNullOrEmpty)) e.Add("A column mapping has an empty API Field Name.");
        foreach (var dup in names.Where(n => n.Length > 0).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            e.Add($"Duplicate API Field Name '{dup.Key}'.");
        foreach (var x in p.Input.ExtraInputs)
        {
            if (!KeyPattern.IsMatch(x.Key ?? "")) e.Add($"Additional input key '{x.Key}' may only contain letters, numbers and _.");
            else if (PayloadBuilder.IsBuiltIn(x.Key!)) e.Add($"Additional input key '{x.Key}' is a reserved placeholder name.");
        }
        foreach (var dup in p.Input.ExtraInputs.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            e.Add($"Duplicate additional input key '{dup.Key}'.");
        if (p.Input.MatchSource == MatchSource.DataField && !names.Contains(p.Input.MatchField?.Trim() ?? "", StringComparer.OrdinalIgnoreCase))
            e.Add($"Match field '{p.Input.MatchField}' is not an API Field Name in Column Mapping.");
        if (p.Api.Enabled && !Uri.TryCreate(p.Api.Url, UriKind.Absolute, out _)) e.Add("API URL is not valid.");
        return e;
    }
}
