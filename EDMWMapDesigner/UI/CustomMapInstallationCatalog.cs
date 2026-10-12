using System.Text.RegularExpressions;
using EDMWMapDesigner.Generation;

namespace EDMWMapDesigner.UI;

internal sealed record CustomMapInstallation(string Name, string MapId, string? ScriptPath,
    string GeneratorPath, string PayloadPath)
{
    internal bool IsComplete => ScriptPath is not null && File.Exists(ScriptPath) &&
                                File.Exists(GeneratorPath) && File.Exists(PayloadPath);
}

internal static class CustomMapInstallationCatalog
{
    private static readonly Regex GeneratorPattern = new(
        @"\bUseMapGenerator\s+""(?<generator>EDMW_[0-9A-F]{6})""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex GeneratorFilePattern = new(
        @"^EDMW_(?<id>[0-9A-F]{6})\.dll$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PayloadFilePattern = new(
        @"^(?<id>[0-9A-F]{6})\.edmwa$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DiagnosticFilePattern = new(
        @"^EDMW_(?<id>[0-9A-F]{6})\.(?:trace(?:\.log)?|trees\.bin|fauna\.\d+\.jsonl)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static IReadOnlyList<CustomMapInstallation> Discover(string gamePath)
    {
        if (!GameInstallationLocator.IsValid(gamePath)) return [];
        var root = Path.GetFullPath(gamePath);
        var scripts = Path.Combine(root, "Data", "Random Map Scripts");
        if (!Directory.Exists(scripts)) return [];
        var payloads = Path.Combine(scripts, AnchorPackage.DataFolderName);
        var result = new List<CustomMapInstallation>();
        var referencedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var script in Directory.EnumerateFiles(scripts, "*.rmv", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryReadMapId(script, out var id)) continue;
            referencedIds.Add(id);
            result.Add(new CustomMapInstallation(
                Path.GetFileNameWithoutExtension(script),
                id,
                Path.GetFullPath(script),
                Path.Combine(root, $"EDMW_{id}.dll"),
                Path.Combine(payloads, $"{id}.edmwa")));
        }

        // A failed uninstall from an older build may already have removed the RMV before
        // discovering that the loaded generator DLL could not be deleted. Keep those
        // unusable leftovers visible on the home screen so the user can remove them.
        foreach (var id in FindArtifactMapIds(root, scripts).Where(id => !referencedIds.Contains(id))
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            result.Add(new CustomMapInstallation(
                $"Orphaned map {id}",
                id,
                null,
                Path.Combine(root, $"EDMW_{id}.dll"),
                Path.Combine(payloads, $"{id}.edmwa")));
        }

        return result;
    }

    internal static void Uninstall(CustomMapInstallation map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var targets = FindInstalledArtifacts(map)
            .Where(File.Exists)
            .OrderBy(path => RemovalPriority(path, map.MapId))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Check access to the complete package before deleting anything. The generator
        // is removed before the RMV so any runtime lock leaves the map visible for retry.
        foreach (var path in targets)
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        foreach (var path in targets) File.Delete(path);

        var remaining = targets.Where(File.Exists).ToArray();
        if (remaining.Length > 0)
            throw new IOException($"The map package could not be fully removed: {string.Join(", ", remaining.Select(Path.GetFileName))}.");

        var payloadFolder = Path.GetDirectoryName(map.PayloadPath);
        if (!string.IsNullOrWhiteSpace(payloadFolder) && Directory.Exists(payloadFolder) &&
            !Directory.EnumerateFileSystemEntries(payloadFolder).Any())
            Directory.Delete(payloadFolder);
    }

    private static IEnumerable<string> FindInstalledArtifacts(CustomMapInstallation map)
    {
        if (!TryNormalizeMapId(map.MapId, out var id))
            throw new InvalidDataException("The installed map identity is invalid.");

        var generatorPath = Path.GetFullPath(map.GeneratorPath);
        var gameRoot = Path.GetDirectoryName(generatorPath);
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            throw new DirectoryNotFoundException("The selected game folder was not found.");

        var scriptsRoot = Path.GetFullPath(Path.Combine(gameRoot, "Data", "Random Map Scripts"));
        var expectedGenerator = Path.GetFullPath(Path.Combine(gameRoot, $"EDMW_{id}.dll"));
        if (!string.Equals(generatorPath, expectedGenerator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installed map generator path is invalid.");

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (map.ScriptPath is not null)
        {
            var scriptPath = Path.GetFullPath(map.ScriptPath);
            if (!string.Equals(Path.GetDirectoryName(scriptPath), scriptsRoot, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(scriptPath), ".rmv", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installed map script path is invalid.");
            targets.Add(scriptPath);
        }

        // Different RMV names can legitimately reference the same content-addressed map
        // package. Remove shared DLL/payload files only when this is the final reference.
        if (HasOtherScriptReference(scriptsRoot, map.ScriptPath, id)) return targets;

        targets.Add(expectedGenerator);
        foreach (var artifact in Directory.EnumerateFiles(gameRoot, $"EDMW_{id}.*", SearchOption.TopDirectoryOnly))
        {
            if (IsOwnedRootArtifact(Path.GetFileName(artifact), id))
                targets.Add(Path.GetFullPath(artifact));
        }

        if (!Directory.Exists(scriptsRoot)) return targets;
        foreach (var payload in Directory.EnumerateFiles(scriptsRoot, $"{id}.edmwa", SearchOption.AllDirectories))
            targets.Add(Path.GetFullPath(payload));

        return targets;
    }

    private static IEnumerable<string> FindArtifactMapIds(string gameRoot, string scriptsRoot)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(gameRoot, "EDMW_*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            var match = GeneratorFilePattern.Match(name);
            if (!match.Success) match = DiagnosticFilePattern.Match(name);
            if (match.Success) ids.Add(match.Groups["id"].Value.ToUpperInvariant());
        }

        foreach (var file in Directory.EnumerateFiles(scriptsRoot, "*.edmwa", SearchOption.AllDirectories))
        {
            var match = PayloadFilePattern.Match(Path.GetFileName(file));
            if (match.Success) ids.Add(match.Groups["id"].Value.ToUpperInvariant());
        }
        return ids;
    }

    private static bool HasOtherScriptReference(string scriptsRoot, string? selectedScript, string id)
    {
        if (!Directory.Exists(scriptsRoot)) return false;
        foreach (var script in Directory.EnumerateFiles(scriptsRoot, "*.rmv", SearchOption.TopDirectoryOnly))
        {
            if (selectedScript is not null && string.Equals(Path.GetFullPath(script), Path.GetFullPath(selectedScript),
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (TryReadMapId(script, out var otherId) && string.Equals(otherId, id, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool TryReadMapId(string scriptPath, out string id)
    {
        id = string.Empty;
        string contents;
        try { contents = File.ReadAllText(scriptPath); }
        catch { return false; }
        var match = GeneratorPattern.Match(contents);
        if (!match.Success || !AnchorPackage.TryParseGeneratorName(match.Groups["generator"].Value, out var mapId))
            return false;
        id = mapId.ToString("X6");
        return true;
    }

    private static bool TryNormalizeMapId(string value, out string id)
    {
        id = string.Empty;
        if (!AnchorPackage.TryParseGeneratorName($"EDMW_{value}", out var mapId)) return false;
        id = mapId.ToString("X6");
        return true;
    }

    private static bool IsOwnedRootArtifact(string fileName, string id) =>
        string.Equals(fileName, $"EDMW_{id}.dll", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, $"EDMW_{id}.trace", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, $"EDMW_{id}.trace.log", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileName, $"EDMW_{id}.trees.bin", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(fileName, $@"^EDMW_{Regex.Escape(id)}\.fauna\.\d+\.jsonl$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsScript(string path) =>
        string.Equals(Path.GetExtension(path), ".rmv", StringComparison.OrdinalIgnoreCase);

    private static int RemovalPriority(string path, string id)
    {
        if (string.Equals(Path.GetFileName(path), $"EDMW_{id}.dll", StringComparison.OrdinalIgnoreCase)) return 0;
        return IsScript(path) ? 2 : 1;
    }
}
