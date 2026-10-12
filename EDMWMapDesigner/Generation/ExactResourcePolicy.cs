using System.Text.RegularExpressions;

namespace EDMWMapDesigner.Generation;

/// <summary>
/// Competitive exact-location maps use fixed starting resources and one seeded, map-wide
/// neutral request per mine type. Default.dll still owns placement and seeded randomness.
/// </summary>
internal static class ExactResourcePolicy
{
    internal const int GoldPerPlayer = 5;
    internal const int StonePerPlayer = 2;
    internal const int BerryPerPlayer = 1;
    internal const int MaximumNeutralGold = 10;
    internal const int MaximumNeutralStone = 10;

    public static string Apply(string source, bool manualResourcePlacement = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        source = ReplaceResource(source, "ResourcePerPlayer", "Berry", BerryPerPlayer.ToString());
        source = ReplaceResource(source, "ResourcePerNeutralLand", "Berry", "0");
        source = ReplaceResource(source, "ResourcePerPlayer", "Gold", GoldPerPlayer.ToString());
        source = ReplaceResource(source, "ResourcePerNeutralLand", "Gold",
            $"Between(0, {MaximumNeutralGold})");
        source = ReplaceResource(source, "ResourcePerPlayer", "Stone", StonePerPlayer.ToString());
        source = ReplaceResource(source, "ResourcePerNeutralLand", "Stone",
            $"Between(0, {MaximumNeutralStone})");
        return ReplaceScalar(source, "MaxResourceElevation", "2");
    }


    private static string ReplaceResource(string source, string directive, string resource, string value)
    {
        var regex = new Regex(
            $@"^(?<indent>[ \t]*){Regex.Escape(directive)}[ \t]+\([ \t]*{Regex.Escape(resource)}[ \t]*,.*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var matches = regex.Matches(source);
        if (matches.Count != 1)
            throw new InvalidDataException(
                $"The generated map must contain exactly one {directive}({resource}) directive; found {matches.Count}.");
        return regex.Replace(source,
            match => $"{match.Groups["indent"].Value}{directive,-32}({resource}, {value})", 1);
    }

    private static string ReplaceScalar(string source, string directive, string value)
    {
        var regex = new Regex(
            $@"^(?<indent>[ \t]*){Regex.Escape(directive)}[ \t]+[^\r\n]+",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var matches = regex.Matches(source);
        if (matches.Count != 1)
            throw new InvalidDataException(
                $"The generated map must contain exactly one {directive} directive; found {matches.Count}.");
        return regex.Replace(source,
            match => $"{match.Groups["indent"].Value}{directive,-32}{value}", 1);
    }
}
