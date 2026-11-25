using System.Text.Json.Serialization;
using AoiLine.Core.Services;
using AoiLine.Server;

// Data folder: settings.json, Lines\, Logs\. Default = next to the program; --data <folder> for testing.
string? Arg(string name)
{
    int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var dataRoot = Path.GetFullPath(Arg("--data") ?? AppContext.BaseDirectory);
Directory.CreateDirectory(dataRoot);
AppLog.Folder = Path.Combine(dataRoot, "Logs");

var store = new SettingsStore(Path.Combine(dataRoot, "settings.json"));
AppLog.KeepDays = store.Current.KeepDays;
var port = int.TryParse(Arg("--port"), out var p) ? p : store.Current.Port;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.Host.UseWindowsService(o => o.ServiceName = "AoiLineServer");
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(new DataRoot(dataRoot));
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<LineManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LineManager>());

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Always load the latest page after an update.
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
});
Endpoints.Map(app);

AppLog.Info($"AOI Line Server {typeof(Endpoints).Assembly.GetName().Version?.ToString(3)} listening on port {port}, data folder {dataRoot}");
app.Run();
