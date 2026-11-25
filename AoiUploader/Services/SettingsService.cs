using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AoiUploader.Models;
using Microsoft.Win32;

namespace AoiUploader.Services;

public static class SettingsService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AoiUploader";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string AppDir => AppContext.BaseDirectory;
    public static string SettingsPath => Path.Combine(AppDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options);
                if (s != null) return s;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Cannot read settings.json, using defaults: {ex.Message}");
            return AppSettings.CreateDefault();
        }

        var def = AppSettings.CreateDefault();
        Save(def);
        return def;
    }

    public static void Save(AppSettings s) =>
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, Options));

    public static AppSettings Clone(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, Options), Options)!;

    /// <summary>Relative paths are resolved against the program folder.</summary>
    public static string ResolvePath(string path) =>
        string.IsNullOrWhiteSpace(path) ? AppDir
        : Path.IsPathRooted(path) ? path
        : Path.Combine(AppDir, path);

    public static void ApplyStartWithWindows(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable)
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
            else if (key.GetValue(RunValueName) != null)
                key.DeleteValue(RunValueName);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Cannot update 'Start with Windows': {ex.Message}");
        }
    }
}
