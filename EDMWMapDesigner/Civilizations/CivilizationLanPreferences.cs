using System.Text.Json;

namespace EDMWMapDesigner.Civilizations;

internal sealed class CivilizationLanPreferences
{
    public bool ReceiveEnabled { get; set; } = true;
    public int Port { get; set; } = CivilizationLanTransfer.DefaultPort;

    public static CivilizationLanPreferences Load()
    {
        try
        {
            if (!File.Exists(PreferencePath)) return new();
            var data = File.ReadAllText(PreferencePath);
            if (data.Length > 4096) return new();
            var settings = JsonSerializer.Deserialize<CivilizationLanPreferences>(data) ?? new();
            if (settings.Port is < 1 or > 65535) settings.Port = CivilizationLanTransfer.DefaultPort;
            return settings;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save()
    {
        var path = PreferencePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string PreferencePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EDMW Map Designer", "civilization-lan.json");
}
