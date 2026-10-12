using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.Analysis;

public sealed record MapAnalysis(
    double LandFraction,
    double ForestFractionOfLand,
    double RuggedFractionOfLand,
    double CoastComplexity,
    int LandComponents,
    IReadOnlyDictionary<TerrainKind, int> Counts)
{
    public int TotalCells => Counts.Values.Sum();
}
