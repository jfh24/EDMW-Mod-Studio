using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.Analysis;

public static class MapAnalyzer
{
    public static MapAnalysis Analyze(MapProject map)
    {
        map.Validate();
        var counts = TerrainCatalog.All.ToDictionary(k => k, _ => 0);
        foreach (var value in map.Cells) counts[(TerrainKind)value]++;

        var total = map.Cells.Length;
        var land = counts.Where(p => p.Key.IsLand()).Sum(p => p.Value);
        var forest = counts[TerrainKind.Forest];
        var rugged = counts[TerrainKind.Hills] + counts[TerrainKind.Mountain];
        var coastEdges = CountCoastEdges(map);
        var maxEdges = Math.Max(1, 2 * map.GridSize * (map.GridSize - 1));

        return new MapAnalysis(
            land / (double)total,
            land == 0 ? 0 : forest / (double)land,
            land == 0 ? 0 : rugged / (double)land,
            coastEdges / (double)maxEdges,
            CountLandComponents(map),
            counts);
    }

    private static int CountCoastEdges(MapProject map)
    {
        var edges = 0;
        for (var y = 0; y < map.GridSize; y++)
            for (var x = 0; x < map.GridSize; x++)
            {
                if (x + 1 < map.GridSize && map[x, y].IsWater() != map[x + 1, y].IsWater()) edges++;
                if (y + 1 < map.GridSize && map[x, y].IsWater() != map[x, y + 1].IsWater()) edges++;
            }
        return edges;
    }

    private static int CountLandComponents(MapProject map)
    {
        var seen = new bool[map.Cells.Length];
        var count = 0;
        var queue = new Queue<(int X, int Y)>();
        ReadOnlySpan<(int X, int Y)> directions = [(1, 0), (-1, 0), (0, 1), (0, -1)];

        for (var y = 0; y < map.GridSize; y++)
            for (var x = 0; x < map.GridSize; x++)
            {
                var index = y * map.GridSize + x;
                if (seen[index] || map[x, y].IsWater()) continue;
                count++;
                seen[index] = true;
                queue.Enqueue((x, y));
                while (queue.Count > 0)
                {
                    var cell = queue.Dequeue();
                    foreach (var direction in directions)
                    {
                        var nx = cell.X + direction.X;
                        var ny = cell.Y + direction.Y;
                        if (nx < 0 || ny < 0 || nx >= map.GridSize || ny >= map.GridSize) continue;
                        var ni = ny * map.GridSize + nx;
                        if (seen[ni] || map[nx, ny].IsWater()) continue;
                        seen[ni] = true;
                        queue.Enqueue((nx, ny));
                    }
                }
            }
        return count;
    }
}
