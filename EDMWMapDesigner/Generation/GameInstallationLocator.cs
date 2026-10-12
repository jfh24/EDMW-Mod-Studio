using System.Text;
using Microsoft.Win32;

namespace EDMWMapDesigner.Generation;

public static class GameInstallationLocator
{
    public const string GameExecutableName = "Empires_DMW.exe";

    public static bool IsValid(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return File.Exists(Path.Combine(Path.GetFullPath(path), GameExecutableName));
        }
        catch
        {
            return false;
        }
    }

    public static string? Locate(string? selectedPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(selectedPath)) candidates.Add(selectedPath);
        try
        {
            var registryPath = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\SSSI\Empires", "Install Directory", null) as string;
            if (!string.IsNullOrWhiteSpace(registryPath)) candidates.Add(registryPath);
        }
        catch
        {
            // Registry discovery is optional because the user can select a folder.
        }
        candidates.AddRange([
            @"C:\GAMES\EMPIRES DAWN OF THE MODERN WORLD",
            @"C:\GOG Games\Empires - Dawn of the Modern World",
            @"C:\Program Files (x86)\Steam\steamapps\common\Empires Dawn of the Modern World"
        ]);

        foreach (var candidate in candidates)
        {
            try
            {
                var resolved = Path.GetFullPath(candidate);
                if (IsValid(resolved)) return resolved;
            }
            catch
            {
                // Ignore malformed discovery candidates and continue.
            }
        }
        return null;
    }

    public static string? LoadSelectedPath()
    {
        try
        {
            var settingsPath = SettingsPath();
            if (!File.Exists(settingsPath)) return null;
            var selectedPath = File.ReadAllText(settingsPath, Encoding.UTF8).Trim();
            return string.IsNullOrWhiteSpace(selectedPath)
                ? null
                : Path.GetFullPath(selectedPath);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveSelectedPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var resolved = Path.GetFullPath(path);
        if (!IsValid(resolved))
            throw new InvalidDataException(
                $"The selected folder does not contain {GameExecutableName}.");
        var settingsPath = SettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, resolved, new UTF8Encoding(false));
    }

    private static string SettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EDMW Map Designer", "game-install-path.txt");
}
