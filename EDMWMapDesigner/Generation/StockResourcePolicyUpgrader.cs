using System.Text;
using System.Text.RegularExpressions;

namespace EDMWMapDesigner.Generation;

public static class StockResourcePolicyUpgrader
{
    public static string Upgrade(string scriptPath, string stockScriptsFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stockScriptsFolder);

        var resolvedScript = Path.GetFullPath(scriptPath);
        if (!File.Exists(resolvedScript))
            throw new FileNotFoundException("The generated RMV to upgrade was not found.", resolvedScript);
        var original = File.ReadAllText(resolvedScript);
        var profile = Enum.GetValues<StockResourceProfile>()
            .Select(candidate => StockResourceProfileSelector.Describe(candidate))
            .FirstOrDefault(candidate =>
                original.Contains($"#include \"{candidate.IncludeStem}\\{candidate.IncludeStem} Tiny\"",
                    StringComparison.Ordinal) ||
                original.Contains($"Original {candidate.IncludeStem} resource profile",
                    StringComparison.Ordinal) ||
                original.Contains($"Original {candidate.IncludeStem} Tiny resource/forest policy",
                    StringComparison.Ordinal) ||
                original.Contains($"Original RMV template: {candidate.IncludeStem}.rmv",
                    StringComparison.Ordinal))
            ?? throw new InvalidDataException(
                "The RMV does not identify a supported original stock map profile.");

        var generatorMatch = Regex.Match(original,
            "(?m)^\\s*UseMapGenerator\\s+\"(?<name>[^\"\\r\\n]+)\"");
        uint mapId = 0;
        var hasGeneratorId = generatorMatch.Success &&
                             AnchorPackage.TryParseGeneratorName(
                                 generatorMatch.Groups["name"].Value, out mapId);
        if (!hasGeneratorId)
        {
            var thresholdMatch = Regex.Match(original,
                @"(?m)^\s*ConvergentThreshold\s+(?<value>[^\s/]+)");
            if (!thresholdMatch.Success ||
                !AnchorPackage.TryDecodeTransportThreshold(
                    thresholdMatch.Groups["value"].Value, out mapId))
                throw new InvalidDataException("The RMV does not contain a valid payload map ID.");
        }

        var payloadPath = Path.Combine(Path.GetFullPath(stockScriptsFolder),
            AnchorPackage.DataFolderName, $"{mapId:X6}.edmwa");
        if (!File.Exists(payloadPath))
            throw new FileNotFoundException(
                $"The terrain payload for map ID {mapId:X6} is missing.", payloadPath);

        var upgraded = StockMapTemplateRenderer.RenderWithMapId(
            stockScriptsFolder,
            profile,
            mapId,
            Path.GetFileNameWithoutExtension(resolvedScript));
        var backup = resolvedScript + ".pre-v206.bak";
        var staged = resolvedScript + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(resolvedScript, backup, true);
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(upgraded);
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(staged, resolvedScript, true);
            return backup;
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

}
