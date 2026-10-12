using System.Text;
using System.Text.RegularExpressions;

namespace EDMWMapDesigner.Generation;

/// <summary>
/// Imports only the resource and forest policy from the game's original size profiles.
/// Placement remains entirely owned by Default.dll; custom topology code never creates nodes.
/// </summary>
public static partial class StockResourceProfileImporter
{
    private static readonly string[] GlobalResourceSymbols =
    [
        "kMaxResourceElevation", "kResourceToEdgeDistance", "kResourceToWaterDistance",
        "kAnimalPerPlayer", "kAnimalPerNeutral", "kBerryPerPlayer", "kBerryPerNeutral",
        "kFishPerPlayer", "kFishPerNeutral", "kGoldPerPlayer", "kGoldPerNeutral",
        "kStonePerPlayer", "kStonePerNeutral", "kTreePerPlayer", "kTreePerNeutral",
        "kForestFreeRadius", "kForestsPerPlayer", "kForestChaosLevel", "kMaxClumpsPerForest",
        "kForestToForestDistance", "kForestOuterRadiusMultiplier", "kTreeToEdgeDistance",
        "kMaximumTreesPerClump"
    ];

    private static readonly (string Size, string Condition)[] Sizes =
    [
        ("Tiny", "IsTinyMap"),
        ("Small", "IsSmallMap"),
        ("Medium", "IsMediumMap"),
        ("Large", "IsLargeMap"),
        ("Huge", "IsHugeMap")
    ];

    public static bool IsStockScriptsFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        var resolved = Path.GetFullPath(folder);
        return File.Exists(Path.Combine(resolved, "Common", "Common Conditions.rmv")) &&
               File.Exists(Path.Combine(resolved, "Small Islands", "Small Islands Tiny.rmv"));
    }

    public static string Import(string scriptsFolder, StockResourceProfileSelection profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptsFolder);
        ArgumentNullException.ThrowIfNull(profile);

        var resolvedRoot = Path.GetFullPath(scriptsFolder);
        if (!IsStockScriptsFolder(resolvedRoot))
            throw new InvalidOperationException(
                $"'{resolvedRoot}' is not an Empires DMW Random Map Scripts folder.");

        var output = new StringBuilder(48_000);
        for (var index = 0; index < Sizes.Length; index++)
        {
            var (size, condition) = Sizes[index];
            output.AppendLine(index == 0 ? $"#if {condition}" : $"#elif {condition}");
            output.AppendLine($"// Original {profile.IncludeStem} {size} resource/forest policy (expanded by EDMW Mod Studio)");
            var path = Path.Combine(resolvedRoot, profile.IncludeStem,
                $"{profile.IncludeStem} {size}.rmv");
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"The original {profile.IncludeStem} {size} resource profile is missing.", path);
            output.Append(ExtractSizeProfile(File.ReadAllLines(path), path));
        }
        output.AppendLine("#endif");
        return output.ToString();
    }

    public static string ImportMainAttributes(string scriptsFolder, StockResourceProfileSelection profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptsFolder);
        ArgumentNullException.ThrowIfNull(profile);

        var resolvedRoot = Path.GetFullPath(scriptsFolder);
        if (!IsStockScriptsFolder(resolvedRoot))
            throw new InvalidOperationException(
                $"'{resolvedRoot}' is not an Empires DMW Random Map Scripts folder.");

        var path = Path.Combine(resolvedRoot, $"{profile.IncludeStem}.rmv");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"The original {profile.IncludeStem} main resource profile is missing.", path);
        var lines = File.ReadAllLines(path);
        var resourceMarker = Array.FindIndex(lines, line => ResourceAttributesMarkerRegex().IsMatch(line));
        var forestMarker = Array.FindIndex(lines, line => ForestAttributesMarkerRegex().IsMatch(line));
        var miscMarker = Array.FindIndex(lines, line => MiscAttributesMarkerRegex().IsMatch(line));
        if (resourceMarker < 0 || forestMarker <= resourceMarker || miscMarker <= forestMarker)
            throw new InvalidDataException(
                $"'{path}' has incomplete main resource/forest attribute blocks.");

        var output = new StringBuilder(3_000);
        AppendTrimmedRange(output, lines, resourceMarker + 1, forestMarker);
        AppendTrimmedRange(output, lines, forestMarker + 1, miscMarker);
        var result = output.ToString();
        foreach (var required in new[]
                 {
                     "MaxResourceElevation", "ResourcePerPlayer", "(Berry", "(Gold", "(Stone",
                     "ForestFreeRadius", "ForestsPerPlayer"
                 })
        {
            if (!result.Contains(required, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"'{path}' does not contain required stock attribute {required}.");
        }
        return result;
    }

    internal static string ExtractSizeProfile(IReadOnlyList<string> lines, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        var branchStarts = new List<(int Index, int Players)>();
        for (var index = 0; index < lines.Count; index++)
        {
            var match = PlayerBranchRegex().Match(lines[index]);
            if (match.Success)
                branchStarts.Add((index, int.Parse(match.Groups[1].Value)));
        }

        if (branchStarts.Count != 7 || !branchStarts.Select(branch => branch.Players).SequenceEqual(Enumerable.Range(2, 7)))
            throw new InvalidDataException(
                $"'{sourceName}' does not contain one ordered resource branch for each player count from 2 through 8.");

        var output = new StringBuilder(9_000);
        AppendGlobalResourcePolicy(output, lines, branchStarts[0].Index);
        output.AppendLine();

        for (var branchIndex = 0; branchIndex < branchStarts.Count; branchIndex++)
        {
            var (start, players) = branchStarts[branchIndex];
            var end = branchIndex + 1 < branchStarts.Count ? branchStarts[branchIndex + 1].Index : lines.Count;
            var branch = lines.Skip(start + 1).Take(end - start - 1).ToList();

            var placementDirectives = branch.TakeWhile(line => !ResourceDefinitionsMarkerRegex().IsMatch(line))
                .Where(line => ResourceDirectiveRegex().IsMatch(line))
                .Select(line => line.Trim())
                .ToArray();
            var resourceMarker = branch.FindIndex(line => ResourceDefinitionsMarkerRegex().IsMatch(line));
            var forestMarker = branch.FindIndex(line => ForestDefinitionsMarkerRegex().IsMatch(line));
            if (resourceMarker < 0 || forestMarker <= resourceMarker)
                throw new InvalidDataException(
                    $"'{sourceName}' has an incomplete {players}-player resource/forest block.");

            var forestEnd = branch.Count;
            if (branchIndex == branchStarts.Count - 1)
            {
                forestEnd = branch.FindIndex(forestMarker + 1, line => EndIfRegex().IsMatch(line));
                if (forestEnd < 0)
                    throw new InvalidDataException(
                        $"'{sourceName}' has no closing directive after its 8-player block.");
            }

            output.AppendLine(players == 2 ? "#if Is2Players" : $"#elif Is{players}Players");
            foreach (var line in placementDirectives) output.AppendLine(line);
            AppendTrimmedRange(output, branch, resourceMarker + 1, forestMarker);
            AppendTrimmedRange(output, branch, forestMarker + 1, forestEnd);
        }
        output.AppendLine("#endif");

        var result = output.ToString();
        foreach (var required in new[]
                 {
                     "kMaxResourceElevation", "kResourceToEdgeDistance", "kResourceToWaterDistance",
                     "kBerryPerPlayer", "kGoldPerPlayer", "kStonePerPlayer", "kTreePerPlayer",
                     "kForestFreeRadius", "kForestsPerPlayer", "kForestChaosLevel", "kMaxClumpsPerForest"
                 })
        {
            if (!result.Contains(required, StringComparison.Ordinal))
                throw new InvalidDataException($"'{sourceName}' does not define required stock symbol {required}.");
        }

        return result;
    }

    private static void AppendGlobalResourcePolicy(
        StringBuilder output,
        IReadOnlyList<string> lines,
        int end)
    {
        var conditionalDepth = 0;
        for (var index = 0; index < end; index++)
        {
            var line = lines[index];
            if (IfDirectiveRegex().IsMatch(line))
            {
                conditionalDepth++;
                if (conditionalDepth > 1) output.AppendLine(line.TrimEnd());
                continue;
            }
            if (ElseDirectiveRegex().IsMatch(line))
            {
                if (conditionalDepth > 1) output.AppendLine(line.TrimEnd());
                continue;
            }
            if (EndIfRegex().IsMatch(line))
            {
                if (conditionalDepth > 1) output.AppendLine(line.TrimEnd());
                conditionalDepth--;
                continue;
            }

            if (ResourceDirectiveRegex().IsMatch(line) ||
                GlobalResourceSymbols.Any(symbol => line.Contains(symbol, StringComparison.Ordinal)))
                output.AppendLine(line.TrimEnd());
        }
    }

    private static void AppendTrimmedRange(StringBuilder output, IReadOnlyList<string> lines, int start, int end)
    {
        while (start < end && string.IsNullOrWhiteSpace(lines[start])) start++;
        while (end > start && string.IsNullOrWhiteSpace(lines[end - 1])) end--;
        for (var index = start; index < end; index++) output.AppendLine(lines[index].TrimEnd());
        output.AppendLine();
    }

    [GeneratedRegex(@"^\s*#(?:if|elif)\s+\(?Is([2-8])Players\)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PlayerBranchRegex();

    [GeneratedRegex(@"^\s*(?:ResourcePlacementLimit|ResourceSeperation|PlaceResourceInRing)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceDirectiveRegex();

    [GeneratedRegex(@"^\s*//\s*resource definitions\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceDefinitionsMarkerRegex();

    [GeneratedRegex(@"^\s*//\s*forest definitions\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ForestDefinitionsMarkerRegex();

    [GeneratedRegex(@"^\s*//\s*resource attributes\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceAttributesMarkerRegex();

    [GeneratedRegex(@"^\s*//\s*forest attributes\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ForestAttributesMarkerRegex();

    [GeneratedRegex(@"^\s*//\s*misc\. attributes\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex MiscAttributesMarkerRegex();

    [GeneratedRegex(@"^\s*#endif\b", RegexOptions.IgnoreCase)]
    private static partial Regex EndIfRegex();

    [GeneratedRegex(@"^\s*#if\b", RegexOptions.IgnoreCase)]
    private static partial Regex IfDirectiveRegex();

    [GeneratedRegex(@"^\s*#(?:elif|else)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ElseDirectiveRegex();
}
