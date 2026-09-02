using System;
using System.IO;
using System.Text.Json;

namespace BitchatWin.Services;

/// <summary>
/// The handful of preferences worth keeping between runs. Deliberately not the
/// message history: geohash chat is ephemeral, and writing it to disk would
/// undo that.
/// </summary>
public sealed class AppSettings
{
    public string Nickname { get; set; } = "anon";
    public string LastGeohash { get; set; } = "u0nd";

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "bitchat-win",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Corrupt or unreadable settings fall back to defaults.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Preferences are a convenience; failing to persist them is not fatal.
        }
    }
}
