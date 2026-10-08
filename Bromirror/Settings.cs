using System.IO;
using System.Text.Json;

namespace Bromirror;

public sealed class Settings
{
    public string Name { get; set; } = "Bromirror";
    public bool AutoStartServer { get; set; } = true;
    public bool Fullscreen { get; set; }
    public bool ShowIntro { get; set; } = true;

    // PC -> TV
    public int TvMonitor { get; set; }
    public int TvHeight { get; set; } = 1080;
    public int TvFps { get; set; } = 24;
    public bool TvCursor { get; set; } = true;

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bromirror");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Kaputte Einstellungen sollen die App nicht blockieren -> Standardwerte.
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
