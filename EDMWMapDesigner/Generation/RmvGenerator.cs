using System.Globalization;
using System.Text;
using EDMWMapDesigner.Analysis;
using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.Generation;

public static class RmvGenerator
{
    private sealed record TerrainDefinition(TerrainKind Kind, int Handle, string Primary, string Beach);

    private static readonly TerrainDefinition[] TerrainDefinitions =
    [
        new(TerrainKind.Beach, 100, "GrassBeach", "GrassBeach2"),
        new(TerrainKind.Grassland, 101, "GrassBase", "GrassBeach2"),
        new(TerrainKind.Forest, 102, "GrassPatch", "GrassBeach2"),
        new(TerrainKind.Hills, 103, "GrassRocks2", "GrassBeach2"),
        new(TerrainKind.Mountain, 104, "GrassBase2", "GrassBeach2"),
        new(TerrainKind.Desert, 105, "SandBase", "SandWet")
    ];

    public static GenerationResult Generate(MapProject map, string? stockScriptsFolder = null)
    {
        var analysis = MapAnalyzer.Analyze(map);
        var anchors = SemanticAnchorAnalyzer.Analyze(map);
        var resourceProfile = StockResourceProfileSelector.Select(map, analysis, anchors);
        var notes = BuildNotes(map, analysis, anchors, resourceProfile);
        if (stockScriptsFolder is not null)
        {
            var script = StockMapTemplateRenderer.Render(stockScriptsFolder, resourceProfile, map);
            return new GenerationResult(script, analysis, notes);
        }

        var terrainRegions = BuildTerrainRegions(analysis);
        var land = Math.Clamp(analysis.LandFraction, 0.05, 0.99);
        var varianceWidth = map.Variance / 100.0 * 0.10;
        var minLand = Math.Clamp(land - varianceWidth / 2, 0.05, 0.99);
        var maxLand = Math.Clamp(land + varianceWidth / 2, minLand, 0.99);
        var rugged = analysis.RuggedFractionOfLand;
        var elevationScale = 1.0 + rugged * 3.0;
        var heightChaos = 2.0 + rugged * 18.0 + analysis.CoastComplexity * 8.0;
        var components = Math.Clamp(analysis.LandComponents, 1, 24);
        var landClumps = Math.Clamp((int)Math.Ceiling(Math.Sqrt(components)), 1, 6);
        var centerAnchors = anchors.ForRole(SemanticAnchorRole.Center).ToArray();
        var innerAnchors = anchors.ForRole(SemanticAnchorRole.InnerNeutral).ToArray();
        var outerAnchors = anchors.ForRole(SemanticAnchorRole.OuterNeutral).ToArray();
        var playerAnchors = anchors.ForRole(SemanticAnchorRole.PlayerLand).ToArray();
        var unassignedAnchors = anchors.ForRole(SemanticAnchorRole.None).ToArray();
        var innerZoneAnchors = centerAnchors.Concat(innerAnchors).ToArray();
        const double neutralRegionSplit = 0.67;
        var inferredInnerAnchors = unassignedAnchors
            .Where(component => component.NormalizedRadius < neutralRegionSplit).ToArray();
        var inferredOuterAnchors = unassignedAnchors
            .Where(component => component.NormalizedRadius >= neutralRegionSplit).ToArray();
        var innerRegionAnchors = innerZoneAnchors.Concat(inferredInnerAnchors).ToArray();
        var outerRegionAnchors = outerAnchors.Concat(inferredOuterAnchors).ToArray();
        var playerRadius = playerAnchors.Length == 0 ? 0.725 : playerAnchors.Average(component => component.NormalizedRadius);
        // Player starts need a broad legal band or Default.dll can generate no starting
        // position at all (which causes immediate defeat). Anchors may bias that band,
        // but never shrink it below the stable envelope used by stock map scripts.
        var playerInnerRadius = playerAnchors.Length == 0 ? 0.55 : Math.Clamp(playerRadius - 0.12, 0.55, 0.75);
        var playerOuterRadius = playerAnchors.Length == 0 ? 0.90 : Math.Clamp(playerRadius + 0.12, 0.90, 0.99);
        var playerLandClumps = playerAnchors.Length == 0 ? landClumps : Math.Clamp(Math.Max(landClumps, playerAnchors.Length), 1, 6);
        var neutralInnerRadius = innerRegionAnchors.Length == 0 ? 0.10 :
            Math.Clamp(innerRegionAnchors.Max(component => component.NormalizedRadius) + 0.08, 0.05, 0.65);
        var neutralOuterRadius = outerRegionAnchors.Length == 0 ? 0.75 :
            Math.Clamp(outerRegionAnchors.Min(component => component.NormalizedRadius) - 0.08, 0.35, 0.95);
        // Neutral counts describe real non-player components. Unassigned components
        // are divided radially, while explicit roles take precedence. Inventing
        // additional regions multiplies every ResourcePerNeutralLand request and can
        // leave Gold and Stone markers with no valid world region to occupy.
        var innerNeutralCount = Math.Clamp(innerRegionAnchors.Length, 0, 12);
        var outerNeutralCount = Math.Clamp(outerRegionAnchors.Length, 0, 16);
        var waterCells = analysis.Counts[TerrainKind.DeepWater] + analysis.Counts[TerrainKind.Shallows];
        var shallowShare = waterCells == 0 ? 0 : analysis.Counts[TerrainKind.Shallows] / (double)waterCells;
        var shallowWidth = Math.Clamp((int)Math.Round(2 + shallowShare * 10), 2, 12);
        var beachShare = analysis.LandFraction == 0 ? 0 :
            analysis.Counts[TerrainKind.Beach] / (analysis.LandFraction * analysis.TotalCells);
        var beachWidth = Math.Clamp(0.5 + beachShare * 4.0, 0.5, 3.0);
        // Exact-location mode needs a stock tree record for every painted forest
        // world cell. The native generator trims and relocates that full-density
        // pool to the painted mask after the stock tree setup has completed.
        var paintedForestShare = 1.0;
        var mapName = SanitizeComment(map.Name);
        var author = SanitizeComment(map.Author);

        var sb = new StringBuilder(12_000);
        sb.AppendLine("//////////////////////////////////////////////////////////////////////");
        sb.AppendLine($"// {mapName}.rmv");
        sb.AppendLine(MapScriptBranding.GeneratedMarker);
        sb.AppendLine($"// Author: {author}");
        sb.AppendLine($"// Painted grid: {map.GridSize}x{map.GridSize}; authoritative native topology");
        if (anchors.HasAny)
            sb.AppendLine($"// Semantic anchors: center {centerAnchors.Length}, inner {innerAnchors.Length}, outer {outerAnchors.Length}, player {playerAnchors.Length}");
        if (map.ManualPlayerStarts.Count > 0)
            sb.AppendLine($"// Manual player starts: {PlayerStartSummary(map)}");
        sb.AppendLine("//////////////////////////////////////////////////////////////////////");
        sb.AppendLine();
        sb.AppendLine("UseMapGenerator                 \"EDMWAnchored\"");
        sb.AppendLine("StringTableID                   27100");
        sb.AppendLine();
        sb.AppendLine("#define                         (kGrassMap, 1)");
        sb.AppendLine("ClimateType                     GrassClimate");
        sb.AppendLine("EnvironmentName                 Grasslands");
        sb.AppendLine();
        sb.AppendLine("#include \"Common\\Common Conditions\"");
        sb.AppendLine("#include \"Common\\Common Initial Units\"");
        sb.AppendLine("#include \"Common\\Common Forests\"");
        sb.AppendLine();
        sb.AppendLine("//////////////////////////////////////////////////////////////////////");
        sb.AppendLine($"// Original {resourceProfile.IncludeStem} resource profile (map size + player count)");
        sb.AppendLine($"// Selected automatically: {SanitizeComment(resourceProfile.Reason)}");
        sb.AppendLine("//////////////////////////////////////////////////////////////////////");
        if (stockScriptsFolder is not null)
            sb.Append(StockResourceProfileImporter.Import(stockScriptsFolder, resourceProfile));
        else
            AppendStockProfileIncludes(sb, resourceProfile);
        sb.AppendLine();
        sb.AppendLine("//////////////////////////////////////////////////////////////////////");
        sb.AppendLine("// Terrain palette derived from painted land pixels");
        sb.AppendLine("//////////////////////////////////////////////////////////////////////");
        foreach (var terrain in TerrainDefinitions)
            sb.AppendLine($"DeclareRandomMapTerrain         ({terrain.Handle}, {terrain.Primary})");
        sb.AppendLine();
        foreach (var region in terrainRegions)
            sb.AppendLine($"TerrainRegion                   ({region.Start}, {region.End}, {region.Handle})");
        sb.AppendLine();
        foreach (var terrain in TerrainDefinitions)
        {
            sb.AppendLine($"RandomMapTerrainRange           ({terrain.Handle}, -1, -1, {terrain.Beach})");
            sb.AppendLine($"RandomMapTerrainRange           ({terrain.Handle}, -3, -2, WaterSeaweed)");
            sb.AppendLine($"RandomMapTerrainRange           ({terrain.Handle}, -10, -4, WaterDeep)");
        }
        sb.AppendLine();
        sb.AppendLine($"TerrainChaos                    {F(1.0 + map.Variance / 20.0)}");
        sb.AppendLine("CliffTerrain                    GrasslandsCliff");
        sb.AppendLine("RoadTerrain                     XSP-Cobblestone");
        sb.AppendLine("ShallowTerrain                  SandShallows");
        sb.AppendLine($"PaintBeaches                    {(map.PreserveCoastline ? "false" : "true")}");
        sb.AppendLine("BeachTerrain                    GrassBeach");
        sb.AppendLine($"BeachWidth                      ({F(beachWidth)}, {F(Math.Min(3.5, beachWidth + 0.5))})");
        sb.AppendLine("BeachTransitionTerrain          GrassBeach");
        sb.AppendLine("BeachTransitionWidth            (0.0, 0.5)");
        sb.AppendLine("PaintAboveCliffs                false");
        sb.AppendLine("PaintBelowCliffs                false");
        sb.AppendLine("ResourceTerrainTexture          (Tree, GrassPatch)");
        sb.AppendLine("ResourceTerrainTexture          (Berry, GrassPatch)");
        sb.AppendLine("ResourceTerrainTexture          (Stone, GrassRocks2)");
        sb.AppendLine("ResourceTerrainTexture          (Gold, GrassRocks2)");
        sb.AppendLine("ResourceTerrainTexture          (Iron, GrassRocks2)");
        sb.AppendLine();
        sb.AppendLine("//////////////////////////////////////////////////////////////////////");
        sb.AppendLine("// Elevation and land form inferred from the painted topology");
        sb.AppendLine("//////////////////////////////////////////////////////////////////////");
        sb.AppendLine("MinIntElevation                 -10");
        sb.AppendLine($"MaxIntElevation                 {Math.Clamp(3 + (int)Math.Round(rugged * 10), 3, 12)}");
        sb.AppendLine("InitialElevation                0");
        sb.AppendLine("FlatElevation                   1");
        sb.AppendLine($"ElevationScale                  {F(elevationScale)}");
        sb.AppendLine($"HeightMapChaos                  {Between(heightChaos, map.Variance, 0.35, 1.0)}");
        sb.AppendLine($"HeightMapSmoothness             {F(Math.Clamp(2.5 - rugged * 2.0, 0.25, 2.5))}");
        sb.AppendLine("LandElevationThreshold          0");
        sb.AppendLine($"PercentLand                     Between({F(minLand)}, {F(maxLand)})");
        sb.AppendLine($"MaxNumLandClumps                {components}");
        sb.AppendLine($"WaterBorder                     {(analysis.Counts[TerrainKind.DeepWater] > 0 ? 1 : 0)}");
        sb.AppendLine("RiverWidth                      0");
        sb.AppendLine("RiverLandFill                   false");
        sb.AppendLine("RiverLandFillWidth              0");
        sb.AppendLine("TeamGrowth                      false");
        sb.AppendLine($"CoastalToDeeperWaterPercentage  Between({F(Math.Clamp(0.2 + shallowShare * 0.4, 0.2, 0.6))}, {F(Math.Clamp(0.5 + shallowShare * 0.4, 0.5, 0.9))})");
        sb.AppendLine("ShallowsElevation               -1");
        sb.AppendLine($"ShallowsWidth                   {shallowWidth}");
        sb.AppendLine("MaxShallowConnectionsPerPlayer  1");
        sb.AppendLine($"GenerateShallows                {(analysis.Counts[TerrainKind.Shallows] > 0 ? "true" : "false")}");
        sb.AppendLine();
        sb.AppendLine("//////////////////////////////////////////////////////////////////////");
        sb.AppendLine("// Player and neutral land generation");
        sb.AppendLine("//////////////////////////////////////////////////////////////////////");
        sb.AppendLine($"PlayerInnerRadius               {F(playerInnerRadius)}");
        sb.AppendLine($"PlayerOuterRadius               {F(playerOuterRadius)}");
        sb.AppendLine("PlayerPercentFlat               0.94");
        sb.AppendLine($"PlayerTreePercentage            {F(paintedForestShare)}");
        sb.AppendLine($"PlayerLandChaos                 {F(Math.Clamp(0.35 + analysis.CoastComplexity, 0.35, 0.9))}");
        sb.AppendLine($"PlayerLandClumps                {playerLandClumps}");
        sb.AppendLine("PlayerFlatChaos                 0.60");
        sb.AppendLine("PlayerFlatClumps                2");
        sb.AppendLine("MaxAngleBetweenTeamMembers      90.0");
        sb.AppendLine("TileDistanceBetweenPlayers      12.0");
        sb.AppendLine("MinimumStartPositionToMapEdge   5");
        sb.AppendLine("OuterPushFromMapCenter          10");
        sb.AppendLine("InnerPushFromMapCenter          0");
        sb.AppendLine($"NeutralInnerRadius              {F(neutralInnerRadius)}");
        sb.AppendLine($"NeutralOuterRadius              {F(neutralOuterRadius)}");
        sb.AppendLine($"NumInnerNeutrals                {innerNeutralCount}");
        sb.AppendLine("InnerNeutralPercentLand         0.55");
        sb.AppendLine("InnerNeutralPercentFlat         0.85");
        sb.AppendLine($"InnerNeutralTreePercentage      {F(paintedForestShare)}");
        sb.AppendLine("InnerNeutralLandChaos           0.65");
        sb.AppendLine($"InnerNeutralLandClumps          {landClumps}");
        sb.AppendLine("InnerNeutralFlatChaos           0.60");
        sb.AppendLine("InnerNeutralFlatClumps          2");
        sb.AppendLine($"NumOuterNeutrals                {outerNeutralCount}");
        sb.AppendLine("OuterNeutralPercentLand         0.45");
        sb.AppendLine("OuterNeutralPercentFlat         0.80");
        sb.AppendLine($"OuterNeutralTreePercentage      {F(paintedForestShare)}");
        sb.AppendLine("OuterNeutralLandChaos           0.70");
        sb.AppendLine($"OuterNeutralLandClumps          {landClumps}");
        sb.AppendLine("OuterNeutralFlatChaos           0.65");
        sb.AppendLine("OuterNeutralFlatClumps          2");
        sb.AppendLine();
        sb.AppendLine("//////////////////////////////////////////////////////////////////////");
        sb.AppendLine($"// Stock {resourceProfile.IncludeStem} resources and forests");
        sb.AppendLine("//////////////////////////////////////////////////////////////////////");
        if (stockScriptsFolder is not null)
            sb.Append(StockResourceProfileImporter.ImportMainAttributes(stockScriptsFolder, resourceProfile));
        else
            AppendStockResourceAndForestAttributes(sb, resourceProfile);
        sb.AppendLine("ConvergentSampleSize            32");
        sb.AppendLine($"ConvergentThreshold             {AnchorPackage.TransportThreshold(map)}");
        sb.AppendLine($"MaxIterationsClumpGrowth        {(resourceProfile.Profile == StockResourceProfile.Plains ? 400 : 200)}");
        sb.AppendLine($"MaxLoopCount                    {resourceProfile.Profile switch
        {
            StockResourceProfile.Plains => 800,
            StockResourceProfile.Continental => 300,
            _ => 200
        }}");
        sb.AppendLine("FilterHeightMap                 false");
        sb.AppendLine("TestingLandDistribution         false");

        var generatedScript = ManualFaunaDirectiveRenderer.Append(
            ExactResourcePolicy.Apply(sb.ToString(), true), map);
        return new GenerationResult(generatedScript, analysis, notes);
    }

    private static void AppendStockResourceAndForestAttributes(
        StringBuilder output,
        StockResourceProfileSelection resourceProfile)
    {
        output.AppendLine("MaxResourceElevation            kMaxResourceElevation");
        output.AppendLine("ResourceToEdgeDistance          kResourceToEdgeDistance");
        output.AppendLine("ResourceToWaterDistance         kResourceToWaterDistance");
        if (resourceProfile.UsesAnimalSymbols)
        {
            output.AppendLine("ResourcePerPlayer               (Animal, kAnimalPerPlayer)");
            output.AppendLine("ResourcePerNeutralLand          (Animal, kAnimalPerNeutral)");
        }
        else
        {
            output.AppendLine("ResourcePerPlayer               (Animal, 0)");
            output.AppendLine("ResourcePerNeutralLand          (Animal, 0)");
        }
        output.AppendLine("ResourcePerPlayer               (Berry, kBerryPerPlayer)");
        output.AppendLine("ResourcePerNeutralLand          (Berry, kBerryPerNeutral)");
        if (resourceProfile.IncludesFish)
        {
            output.AppendLine("ResourcePerPlayer               (Fish, kFishPerPlayer)");
            output.AppendLine("ResourcePerNeutralLand          (Fish, kFishPerNeutral)");
        }
        output.AppendLine("ResourcePerPlayer               (Gold, kGoldPerPlayer)");
        output.AppendLine("ResourcePerNeutralLand          (Gold, kGoldPerNeutral)");
        output.AppendLine("ResourcePerPlayer               (Stone, kStonePerPlayer)");
        output.AppendLine("ResourcePerNeutralLand          (Stone, kStonePerNeutral)");
        output.AppendLine("ResourcePerPlayer               (Tree, kTreePerPlayer)");
        output.AppendLine("ResourcePerNeutralLand          (Tree, kTreePerNeutral)");
        foreach (var directive in StockForestDirectives(resourceProfile.Profile))
            output.AppendLine(directive);
    }

    private static IReadOnlyList<string> StockForestDirectives(StockResourceProfile profile) => profile switch
    {
        StockResourceProfile.CaspianSea or StockResourceProfile.Mediterranean =>
        [
            "ForestFreeRadius                kForestFreeRadius",
            "ForestsPerPlayer                kForestsPerPlayer",
            "ForestChaosLevel                kForestChaosLevel",
            "MaxClumpsPerForest              kMaxClumpsPerForest",
            "MaximumTreesPerClump            10000",
            "MaxTreeLoopCount                200",
            "TreeToEdgeDistance              0",
            "ForestToForestDistance          3"
        ],
        StockResourceProfile.Channels =>
        [
            "ForestFreeRadius                kForestFreeRadius",
            "ForestsPerPlayer                kForestsPerPlayer",
            "ForestChaosLevel                kForestChaosLevel",
            "MaxClumpsPerForest              kMaxClumpsPerForest",
            "MaximumTreesPerClump            10000",
            "MaxTreeLoopCount                200",
            "ForestToForestDistance          kForestToForestDistance",
            "TreeToEdgeDistance              kTreeToEdgeDistance",
            "#if DEFINED(kForestOuterRadiusMultiplier)",
            "ForestOuterRadiusMultiplier     kForestOuterRadiusMultiplier",
            "#else",
            "ForestOuterRadiusMultiplier     2.0",
            "#endif"
        ],
        StockResourceProfile.Coastal or StockResourceProfile.Rivers =>
        [
            "ForestFreeRadius                kForestFreeRadius",
            "ForestsPerPlayer                kForestsPerPlayer",
            "ForestChaosLevel                kForestChaosLevel",
            "ForestToForestDistance          kForestToForestDistance",
            "MaxClumpsPerForest              kMaxClumpsPerForest",
            "MaximumTreesPerClump            kMaximumTreesPerClump",
            "MaxTreeLoopCount                1000",
            "ForestOuterRadiusMultiplier     kForestOuterRadiusMultiplier",
            "TreeToEdgeDistance              0"
        ],
        StockResourceProfile.Highlands or StockResourceProfile.SmallIslands =>
        [
            "ForestFreeRadius                kForestFreeRadius",
            "ForestsPerPlayer                kForestsPerPlayer",
            "ForestChaosLevel                kForestChaosLevel",
            "MaxClumpsPerForest              kMaxClumpsPerForest",
            "MaximumTreesPerClump            10000",
            "MaxTreeLoopCount                200",
            "TreeToEdgeDistance              0"
        ],
        StockResourceProfile.Plains =>
        [
            "ForestFreeRadius                kForestFreeRadius",
            "ForestsPerPlayer                kForestsPerPlayer",
            "ForestChaosLevel                kForestChaosLevel",
            "MaxClumpsPerForest              kMaxClumpsPerForest",
            "MaximumTreesPerClump            20",
            "MaxTreeLoopCount                800",
            "ForestToForestDistance          kForestToForestDistance",
            "TreeToEdgeDistance              0"
        ],
        _ =>
        [
            "ForestFreeRadius                kForestFreeRadius",
            "ForestsPerPlayer                kForestsPerPlayer",
            "ForestChaosLevel                kForestChaosLevel",
            "MaxClumpsPerForest              kMaxClumpsPerForest",
            "MaximumTreesPerClump            10000",
            "MaxTreeLoopCount                200"
        ]
    };

    private static List<string> BuildNotes(
        MapProject map,
        MapAnalysis analysis,
        SemanticAnchorSummary anchors,
        StockResourceProfileSelection resourceProfile)
    {
        var notes = new List<string>();
        if (analysis.LandFraction < 0.12)
            notes.Add("Very little painted land: player starts require enough painted space for initial units and nearby resources.");
        if (analysis.LandFraction > 0.95)
            notes.Add("Almost all land: water details may disappear on some generated maps.");
        if (analysis.LandComponents > 12)
            notes.Add("Many separate islands are compressed to the engine's practical clump range.");
        if (analysis.Counts[TerrainKind.Mountain] > analysis.TotalCells / 3)
            notes.Add("Heavy mountain coverage can reduce buildable starting areas.");
        if (anchors.HasAny)
            notes.Add("Semantic anchors were translated into stock center, inner-neutral, outer-neutral, and player radial zones. DMW retains native angular placement and variation.");
        if (anchors.ForRole(SemanticAnchorRole.PlayerLand).Any(component => component.NormalizedRadius < 0.67))
            notes.Add("A Player Land component is close to map center, so its start radius was widened to DMW's safe player-placement band.");
        if (map.ManualPlayerStarts.Count == 0)
            notes.Add("No manual player starts are placed; all active players will use stock placement.");
        else
        {
            var slots = string.Join(", ", map.ManualPlayerStarts.OrderBy(start => start.PlayerSlot)
                .Select(start => $"P{start.PlayerSlot}"));
            notes.Add($"Manual starts lock configured lobby slots ({slots}). Keep the lobby occupants in those slots; unconfigured or inactive slots remain automatic. At runtime a marker snaps to the nearest stock-eligible cell if its painted cell lacks unit or resource clearance.");
        }
        if (map.ManualResources.Count == 0)
            notes.Add("No editor-authored manual resources are placed; the selected stock profile still controls normal resources.");
        if (map.ManualResources.Any(resource => resource.Kind.IsLegacyUnsupportedFauna()))
            notes.Add("Removed fauna markers are preserved as inert compatibility data and are never placed by the native DLL.");
        if (map.ManualResources.Any(resource => resource.Kind.HasDistinctShippedModel()))
            notes.Add("Manual water fauna (Fish and Dolphin) transactionally replace only stock Fish; manual land fauna transactionally replace only stock Animal. A category that is absent or cannot be fully validated keeps its complete stock behavior.");
        notes.Add($"Stock resource profile: {resourceProfile.IncludeStem} ({resourceProfile.Reason}). Its original settings remain responsible for stock Fish and Animal generation.");
        return notes;
    }

    private static void AppendStockProfileIncludes(StringBuilder output, StockResourceProfileSelection resourceProfile)
    {
        output.AppendLine("#if IsTinyMap");
        output.AppendLine($"#include \"{resourceProfile.IncludeStem}\\{resourceProfile.IncludeStem} Tiny\"");
        output.AppendLine("#elif IsSmallMap");
        output.AppendLine($"#include \"{resourceProfile.IncludeStem}\\{resourceProfile.IncludeStem} Small\"");
        output.AppendLine("#elif IsMediumMap");
        output.AppendLine($"#include \"{resourceProfile.IncludeStem}\\{resourceProfile.IncludeStem} Medium\"");
        output.AppendLine("#elif IsLargeMap");
        output.AppendLine($"#include \"{resourceProfile.IncludeStem}\\{resourceProfile.IncludeStem} Large\"");
        output.AppendLine("#elif IsHugeMap");
        output.AppendLine($"#include \"{resourceProfile.IncludeStem}\\{resourceProfile.IncludeStem} Huge\"");
        output.AppendLine("#endif");
    }

    private static List<(int Start, int End, int Handle)> BuildTerrainRegions(MapAnalysis analysis)
    {
        var present = TerrainDefinitions
            .Select(t => (Definition: t, Count: analysis.Counts[t.Kind]))
            .Where(x => x.Count > 0)
            .ToList();
        if (present.Count == 0)
            present.Add((TerrainDefinitions.First(t => t.Kind == TerrainKind.Grassland), 1));

        var total = present.Sum(x => x.Count);
        var exact = present.Select(x => 101.0 * x.Count / total).ToArray();
        var allocations = exact.Select(Math.Floor).Select(v => (int)v).ToArray();
        var remaining = 101 - allocations.Sum();
        foreach (var index in exact.Select((v, i) => (Fraction: v - Math.Floor(v), Index: i))
                     .OrderByDescending(x => x.Fraction).Take(remaining).Select(x => x.Index))
            allocations[index]++;

        var regions = new List<(int Start, int End, int Handle)>();
        var start = 0;
        for (var i = 0; i < present.Count; i++)
        {
            if (allocations[i] == 0) continue;
            var end = start + allocations[i] - 1;
            regions.Add((start, end, present[i].Definition.Handle));
            start = end + 1;
        }
        return regions;
    }

    private static string Between(double center, int variance, double relativeWidth, double minimum)
    {
        var delta = center * relativeWidth * variance / 100.0;
        return $"Between({F(Math.Max(minimum, center - delta))}, {F(center + delta)})";
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string SanitizeComment(string value) =>
        string.IsNullOrWhiteSpace(value) ? "Untitled" : value.Replace("\r", " ").Replace("\n", " ").Replace("//", "/").Trim();

    internal static string PlayerStartSummary(MapProject map) => string.Join(", ",
        map.ManualPlayerStarts.OrderBy(start => start.PlayerSlot)
            .Select(start => $"P{start.PlayerSlot}"));
}
