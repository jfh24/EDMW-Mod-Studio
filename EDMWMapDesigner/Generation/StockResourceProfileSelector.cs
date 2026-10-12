using EDMWMapDesigner.Analysis;
using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.Generation;

public enum StockResourceProfile
{
    CaspianSea,
    Channels,
    Coastal,
    Continental,
    Highlands,
    LargeIslands,
    Mediterranean,
    Oceania,
    Peloponnese,
    Plains,
    Rivers,
    SmallIslands,
    TeamIslands
}

public sealed record StockResourceProfileSelection(
    StockResourceProfile Profile,
    string IncludeStem,
    bool UsesAnimalSymbols,
    bool IncludesFish,
    string Reason);

internal readonly record struct StockTopologySignature(
    double LandFraction,
    double RuggedFraction,
    int LandComponents,
    double LargestLandComponentShare,
    bool LandTouchesEdge,
    bool WaterTouchesEdge,
    double CenterWaterFraction,
    int PlayerLandAnchors);

public static class StockResourceProfileSelector
{
    private static readonly IReadOnlyDictionary<StockResourceProfile, (string Stem, bool UsesAnimalSymbols, bool IncludesFish)> Profiles =
        new Dictionary<StockResourceProfile, (string, bool, bool)>
        {
            [StockResourceProfile.CaspianSea] = ("Caspian Sea", true, true),
            [StockResourceProfile.Channels] = ("Channels", true, true),
            [StockResourceProfile.Coastal] = ("Coastal", true, true),
            [StockResourceProfile.Continental] = ("Continental", true, true),
            [StockResourceProfile.Highlands] = ("Highlands", true, false),
            [StockResourceProfile.LargeIslands] = ("Large Islands", true, true),
            [StockResourceProfile.Mediterranean] = ("Mediterranean", true, true),
            [StockResourceProfile.Oceania] = ("Oceania", true, true),
            [StockResourceProfile.Peloponnese] = ("Peloponnese", true, true),
            [StockResourceProfile.Plains] = ("Plains", false, false),
            [StockResourceProfile.Rivers] = ("Rivers", true, true),
            [StockResourceProfile.SmallIslands] = ("Small Islands", true, true),
            [StockResourceProfile.TeamIslands] = ("Team Islands", true, true)
        };

    public static StockResourceProfileSelection Describe(StockResourceProfile profile, string reason = "explicit stock profile")
    {
        var metadata = Profiles[profile];
        return new StockResourceProfileSelection(
            profile, metadata.Stem, metadata.UsesAnimalSymbols, metadata.IncludesFish, reason);
    }

    public static StockResourceProfileSelection Select(
        MapProject map,
        MapAnalysis analysis,
        SemanticAnchorSummary anchors) => Select(CreateSignature(map, analysis, anchors));

    internal static StockResourceProfileSelection Select(StockTopologySignature topology)
    {
        StockResourceProfile profile;
        string reason;

        if (topology.LandFraction >= 0.98)
        {
            profile = topology.RuggedFraction >= 0.18
                ? StockResourceProfile.Highlands
                : StockResourceProfile.Plains;
            reason = topology.RuggedFraction >= 0.18
                ? "waterless, rugged painted terrain"
                : "waterless, predominantly flat painted terrain";
        }
        else if (topology.LandComponents == 1 &&
                 topology.CenterWaterFraction >= 0.55 &&
                 !topology.WaterTouchesEdge)
        {
            profile = StockResourceProfile.CaspianSea;
            reason = "one land ring surrounding enclosed central water";
        }
        else if (topology.PlayerLandAnchors >= 2 && topology.LandComponents >= 2)
        {
            profile = StockResourceProfile.TeamIslands;
            reason = "multiple painted player-land components";
        }
        else if (topology.LandComponents >= 8 ||
                 (topology.LandFraction <= 0.32 && topology.LandComponents >= 4))
        {
            profile = StockResourceProfile.Oceania;
            reason = "low-land archipelago with many separate components";
        }
        else if (topology.LandComponents >= 5)
        {
            profile = StockResourceProfile.SmallIslands;
            reason = "five or more separate island components";
        }
        else if (topology.LandComponents >= 3)
        {
            if (topology.LandFraction <= 0.62 || topology.LargestLandComponentShare >= 0.72)
            {
                profile = StockResourceProfile.LargeIslands;
                reason = "a few islands dominated by one or two large landmasses";
            }
            else if (topology.LandFraction >= 0.84)
            {
                profile = StockResourceProfile.Channels;
                reason = "high land coverage divided by narrow water channels";
            }
            else
            {
                profile = StockResourceProfile.Mediterranean;
                reason = "several substantial landmasses around connected water";
            }
        }
        else if (topology.LandComponents == 2)
        {
            if (topology.LandFraction <= 0.62)
            {
                profile = StockResourceProfile.LargeIslands;
                reason = "two large islands with substantial surrounding water";
            }
            else if (topology.LandFraction >= 0.85)
            {
                profile = StockResourceProfile.Channels;
                reason = "two land regions separated by a narrow channel";
            }
            else
            {
                profile = StockResourceProfile.Peloponnese;
                reason = "two high-land coastal or peninsular regions";
            }
        }
        else if (topology.LandComponents == 1 && topology.LandFraction >= 0.90)
        {
            profile = StockResourceProfile.Rivers;
            reason = "one nearly continuous landmass cut by limited water";
        }
        else if (topology.LandComponents == 1 && !topology.LandTouchesEdge)
        {
            if (topology.LandFraction >= 0.66)
            {
                profile = StockResourceProfile.Continental;
                reason = "one large continent surrounded by an ocean border";
            }
            else
            {
                profile = StockResourceProfile.LargeIslands;
                reason = "one large island surrounded by substantial water";
            }
        }
        else if (topology.LandComponents == 1 && topology.LandFraction >= 0.80)
        {
            profile = StockResourceProfile.Coastal;
            reason = "one dominant landmass sharing the map edge with open water";
        }
        else if (topology.LandComponents == 1 && topology.CenterWaterFraction >= 0.35)
        {
            profile = StockResourceProfile.Mediterranean;
            reason = "one connected coastal landmass around central water";
        }
        else if (topology.LandComponents == 1 && topology.LandFraction >= 0.66)
        {
            profile = StockResourceProfile.Continental;
            reason = "one dominant continent with moderate water coverage";
        }
        else
        {
            profile = StockResourceProfile.LargeIslands;
            reason = "a dominant island with broad surrounding water";
        }

        return Describe(profile, reason);
    }

    private static StockTopologySignature CreateSignature(
        MapProject map,
        MapAnalysis analysis,
        SemanticAnchorSummary anchors)
    {
        var landCells = Math.Max(1, analysis.Counts.Where(pair => pair.Key.IsLand()).Sum(pair => pair.Value));
        var largestLandComponentShare = anchors.Components.Count == 0
            ? 0
            : anchors.Components.Max(component => component.PixelCount) / (double)landCells;

        var landTouchesEdge = false;
        var waterTouchesEdge = false;
        for (var coordinate = 0; coordinate < map.GridSize; coordinate++)
        {
            ClassifyEdgeCell(map[coordinate, 0], ref landTouchesEdge, ref waterTouchesEdge);
            ClassifyEdgeCell(map[coordinate, map.GridSize - 1], ref landTouchesEdge, ref waterTouchesEdge);
            ClassifyEdgeCell(map[0, coordinate], ref landTouchesEdge, ref waterTouchesEdge);
            ClassifyEdgeCell(map[map.GridSize - 1, coordinate], ref landTouchesEdge, ref waterTouchesEdge);
        }

        var centerRadius = Math.Max(1, map.GridSize / 20);
        var center = (map.GridSize - 1) / 2;
        var centerWater = 0;
        var centerCells = 0;
        for (var y = Math.Max(0, center - centerRadius); y <= Math.Min(map.GridSize - 1, center + centerRadius); y++)
            for (var x = Math.Max(0, center - centerRadius); x <= Math.Min(map.GridSize - 1, center + centerRadius); x++)
            {
                centerCells++;
                if (map[x, y].IsWater()) centerWater++;
            }

        return new StockTopologySignature(
            analysis.LandFraction,
            analysis.RuggedFractionOfLand,
            analysis.LandComponents,
            largestLandComponentShare,
            landTouchesEdge,
            waterTouchesEdge,
            centerWater / (double)Math.Max(1, centerCells),
            anchors.Count(SemanticAnchorRole.PlayerLand));
    }

    private static void ClassifyEdgeCell(
        TerrainKind terrain,
        ref bool landTouchesEdge,
        ref bool waterTouchesEdge)
    {
        if (terrain.IsWater()) waterTouchesEdge = true;
        else landTouchesEdge = true;
    }
}
