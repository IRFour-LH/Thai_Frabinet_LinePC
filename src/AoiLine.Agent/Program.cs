using System.Text.RegularExpressions;
using AoiLine.Agent;
using AoiLine.Core.Services;

// agent.json next to the program; --config <path> to run several agents on one PC for testing.
string? Arg(string name)
{
    int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var configPath = Path.GetFullPath(Arg("--config") ?? Path.Combine(AppContext.BaseDirectory, "agent.json"));
var configDir = Path.GetDirectoryName(configPath)!;
AppLog.Folder = Path.Combine(configDir, "Logs");
var store = new AgentSettingsStore(configPath);
AppLog.KeepDays = store.Current.KeepDays;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.Host.UseWindowsService(o => o.ServiceName = "AoiLineAgent");
// Status/settings page is only reachable from this PC.
builder.WebHost.UseUrls($"http://localhost:{store.Current.LocalPort}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

builder.Services.AddSingleton(store);
builder.Services.AddSingleton<AgentWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentWorker>());

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });

app.MapGet("/api/status", (AgentWorker w, AgentSettingsStore s) => new
{
    status = w.Status,
    settings = s.Current,
    configFile = s.FilePath,
    lineUrl = s.Current.ServerUrl.TrimEnd('/') + "/line/" + Uri.EscapeDataString(s.Current.LineId),
});

app.MapGet("/api/settings", (AgentSettingsStore s) => s.Current);

app.MapPut("/api/settings", (AgentSettings n, AgentSettingsStore s) =>
{
    var errors = new List<string>();
    if (!Regex.IsMatch(n.LineId ?? "", @"^[A-Za-z0-9_-]{1,20}$")) errors.Add("Line ID: letters, numbers, - or _ (max 20), e.g. L3.");
    if (!Uri.TryCreate(n.ServerUrl, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
        errors.Add("Server address must look like http://SERVER-PC:5000");
    if (string.IsNullOrWhiteSpace(n.WatchFolder)) errors.Add("Watch folder is empty.");
    if (n.LocalPort is < 1 or > 65535) errors.Add("Local port must be 1-65535.");
    if (errors.Count > 0) return Results.BadRequest(new { errors });

    var portChanged = n.LocalPort != s.Current.LocalPort;
    s.Save(n);
    AppLog.KeepDays = n.KeepDays;
    AppLog.Info($"Settings saved: line {n.LineId}, folder '{n.WatchFolder}', server {n.ServerUrl}");
    var warn = Directory.Exists(n.WatchFolder) ? "" : " Warning: the watch folder does not exist (yet).";
    return Results.Ok(new { message = (portChanged ? "Saved. The new page port is used after the agent restarts." : "Saved.") + warn });
});

app.MapPost("/api/test-connection", async (AgentWorker w, AgentSettingsStore s, CancellationToken ct) =>
{
    var (ok, msg) = await w.HeartbeatAsync(s.Current, ct);
    w.TriggerNow();
    return new { ok, message = msg };
});

app.Run();
