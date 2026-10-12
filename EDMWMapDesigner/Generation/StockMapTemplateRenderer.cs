using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.Generation;

/// <summary>
/// Renders an EDMW map from an original game RMV without reconstructing its parser contract.
/// The stock script remains authoritative except for generator selection, custom-map identity,
/// disabling stock river land-fill, and preserving explicitly authored coastlines.
/// </summary>
public static partial class StockMapTemplateRenderer
{
    public static string Render(
        string scriptsFolder,
        StockResourceProfileSelection profile,
        MapProject map)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptsFolder);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(map);
        map.Validate();

        var resolvedRoot = Path.GetFullPath(scriptsFolder);
        if (!StockResourceProfileImporter.IsStockScriptsFolder(resolvedRoot))
            throw new InvalidOperationException(
                $"'{resolvedRoot}' is not an Empires DMW Random Map Scripts folder.");

        var rendered = RenderTemplateBody(
            resolvedRoot, profile, AnchorPackage.GeneratorName(AnchorPackage.MapId(map)));
        rendered = ApplyPaintedForestPercentages(rendered, map);
        rendered = ExactResourcePolicy.Apply(rendered, true);
        rendered = ManualFaunaDirectiveRenderer.Append(rendered, map);
        if (map.PreserveCoastline)
        {
            // This must follow Common Terrain Painting and any profile overrides.
            // Its elevation -1 ranges still paint authored beaches; the global shore overlay is disabled.
            rendered = rendered.TrimEnd() + Environment.NewLine + Environment.NewLine +
                "// Preserve the authored mixture of shoreline terrain." + Environment.NewLine +
                "PaintBeaches                    false" + Environment.NewLine;
        }

        var header = new StringBuilder(512);
        header.AppendLine("//////////////////////////////////////////////////////////////////////");
        header.AppendLine($"// {SanitizeComment(map.Name)}.rmv");
        header.AppendLine(MapScriptBranding.GeneratedMarker);
        header.AppendLine($"// Author: {SanitizeComment(map.Author)}");
        header.AppendLine($"// Painted grid: {map.GridSize}x{map.GridSize}; authoritative native topology");
        if (map.ManualPlayerStarts.Count > 0)
            header.AppendLine($"// Manual player starts: {RmvGenerator.PlayerStartSummary(map)}");
        header.AppendLine($"// Original RMV template: {profile.IncludeStem}.rmv");
        header.AppendLine("// Original gameplay includes, directives, attributes, and ordering are preserved.");
        header.AppendLine("// Stock RiverLandFill is disabled so it cannot replace the authoritative painted heights.");
        header.AppendLine("//////////////////////////////////////////////////////////////////////");
        header.AppendLine();
        header.Append(rendered);
        return header.ToString();
    }

    internal static string RenderWithMapId(
        string scriptsFolder,
        StockResourceProfileSelection profile,
        uint mapId,
        string mapName)
    {
        var rendered = RenderTemplateBody(
            scriptsFolder, profile, AnchorPackage.GeneratorName(mapId));
        rendered = ExactResourcePolicy.Apply(rendered);

        return $"{MapScriptBranding.UpgradedMarker} from the original {profile.IncludeStem}.rmv template.{Environment.NewLine}" +
               $"// Map: {SanitizeComment(mapName)}{Environment.NewLine}" + rendered;
    }

    internal static string RenderTemplateBody(
        string scriptsFolder,
        StockResourceProfileSelection profile,
        string generatorName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptsFolder);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatorName);
        if (!AnchorPackage.TryParseGeneratorName(generatorName, out _))
            throw new ArgumentException("The generator name does not contain a valid map ID.", nameof(generatorName));

        var resolvedRoot = Path.GetFullPath(scriptsFolder);
        if (!StockResourceProfileImporter.IsStockScriptsFolder(resolvedRoot))
            throw new InvalidOperationException(
                $"'{resolvedRoot}' is not an Empires DMW Random Map Scripts folder.");

        var sourcePath = Path.Combine(resolvedRoot, $"{profile.IncludeStem}.rmv");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(
                $"The original {profile.IncludeStem} map script is missing.", sourcePath);

        var source = File.ReadAllText(sourcePath);
        var rendered = ReplaceSingle(
            source,
            GeneratorRegex(),
            match => $"{match.Groups["prefix"].Value}\"{generatorName}\"{match.Groups["suffix"].Value}",
            sourcePath,
            "UseMapGenerator");
        rendered = ReplaceSingle(
            rendered,
            StringTableIdRegex(),
            match => $"{match.Groups["prefix"].Value}27100{match.Groups["suffix"].Value}",
            sourcePath,
            "StringTableID");
        return DisableRiverLandFill(rendered, sourcePath);
    }

    private static string DisableRiverLandFill(string source, string sourcePath)
    {
        var matches = RiverLandFillRegex().Matches(source);
        if (matches.Count > 1)
            throw new InvalidDataException(
                $"'{sourcePath}' must contain at most one RiverLandFill directive; found {matches.Count}.");
        if (matches.Count == 1)
        {
            var match = matches[0];
            return string.Concat(
                source.AsSpan(0, match.Index),
                $"{match.Groups["prefix"].Value}false{match.Groups["suffix"].Value}",
                source.AsSpan(match.Index + match.Length));
        }

        var anchors = TeamGrowthRegex().Matches(source);
        if (anchors.Count != 1)
            throw new InvalidDataException(
                $"'{sourcePath}' must contain exactly one TeamGrowth directive when RiverLandFill is absent; found {anchors.Count}.");
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return string.Concat(
            source.AsSpan(0, anchors[0].Index),
            "RiverLandFill                  false", newline,
            source.AsSpan(anchors[0].Index));
    }

    private static string ApplyPaintedForestPercentages(string source, MapProject map)
    {
        source = ReplaceTreePercentage(source, "PlayerTreePercentage", 1.0);
        source = ReplaceTreePercentage(source, "InnerNeutralTreePercentage", 1.0);
        return ReplaceTreePercentage(source, "OuterNeutralTreePercentage", 1.0);
    }

    private static double? ForestShare(MapProject map, SemanticAnchorRole? role)
    {
        var land = 0;
        var forest = 0;
        for (var index = 0; index < map.Cells.Length; index++)
        {
            if (role.HasValue && map.ComponentRoles[index] != (byte)role.Value) continue;
            var terrain = (TerrainKind)map.Cells[index];
            if (!terrain.IsLand()) continue;
            land++;
            if (terrain == TerrainKind.Forest) forest++;
        }
        return land == 0 ? null : forest / (double)land;
    }

    private static string ReplaceTreePercentage(string source, string directive, double? share)
    {
        var value = (share ?? 0).ToString("0.######", CultureInfo.InvariantCulture);
        var regex = new Regex(
            $"^(?<prefix>\\s*{Regex.Escape(directive)}\\s+)\\S+(?<suffix>[^\\r\\n]*)(?=\\r?$)",
            RegexOptions.Multiline);
        var matches = regex.Matches(source);
        if (matches.Count != 1)
            throw new InvalidDataException(
                $"The original map must contain exactly one {directive} directive; found {matches.Count}.");
        return ReplaceSingle(source, regex,
            match => $"{match.Groups["prefix"].Value}{value}{match.Groups["suffix"].Value}",
            "original map template", directive);
    }

    private static string ReplaceSingle(
        string source,
        Regex regex,
        Func<Match, string> replacement,
        string sourcePath,
        string directive)
    {
        var matches = regex.Matches(source);
        if (matches.Count != 1)
            throw new InvalidDataException(
                $"'{sourcePath}' must contain exactly one {directive} directive; found {matches.Count}.");

        var match = matches[0];
        return string.Concat(
            source.AsSpan(0, match.Index),
            replacement(match),
            source.AsSpan(match.Index + match.Length));
    }

    private static string SanitizeComment(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "Untitled"
            : value.Replace("\r", " ").Replace("\n", " ").Replace("//", "/").Trim();

    [GeneratedRegex("^(?<prefix>\\s*UseMapGenerator\\s+)\"[^\"\\r\\n]+\"(?<suffix>[^\\r\\n]*)(?=\\r?$)", RegexOptions.Multiline)]
    private static partial Regex GeneratorRegex();

    [GeneratedRegex("^(?<prefix>\\s*StringTableID\\s+)\\d+(?<suffix>[^\\r\\n]*)(?=\\r?$)", RegexOptions.Multiline)]
    private static partial Regex StringTableIdRegex();

    [GeneratedRegex("^(?<prefix>\\s*RiverLandFill\\s+)\\S+(?<suffix>[^\\r\\n]*)(?=\\r?$)", RegexOptions.Multiline)]
    private static partial Regex RiverLandFillRegex();

    [GeneratedRegex("^\\s*TeamGrowth\\s+\\S+[^\\r\\n]*(?=\\r?$)", RegexOptions.Multiline)]
    private static partial Regex TeamGrowthRegex();
}
