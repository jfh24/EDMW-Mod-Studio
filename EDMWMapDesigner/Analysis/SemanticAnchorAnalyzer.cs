using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.Analysis;

public sealed record SemanticComponent(
    SemanticAnchorRole Role,
    int PixelCount,
    double CentroidX,
    double CentroidY,
    double NormalizedRadius);

public sealed class SemanticAnchorSummary
{
    public required IReadOnlyList<SemanticComponent> Components { get; init; }
    public bool HasAny => Components.Any(component => component.Role != SemanticAnchorRole.None);
    public IEnumerable<SemanticComponent> ForRole(SemanticAnchorRole role) =>
        Components.Where(component => component.Role == role);
    public int Count(SemanticAnchorRole role) => ForRole(role).Count();
}

public static class SemanticAnchorAnalyzer
{
    private static readonly (int X, int Y)[] Directions = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    public static SemanticAnchorSummary Analyze(MapProject map)
    {
        map.Validate();
        var seen = new bool[map.Cells.Length];
        var components = new List<SemanticComponent>();
        var queue = new Queue<int>();
        var center = (map.GridSize - 1) / 2.0;
        var maxRadius = Math.Max(1.0, Math.Sqrt(2.0) * center);

        for (var start = 0; start < map.Cells.Length; start++)
        {
            if (seen[start] || ((TerrainKind)map.Cells[start]).IsWater()) continue;
            var roleCounts = new int[Enum.GetValues<SemanticAnchorRole>().Length];
            var pixels = 0;
            double sumX = 0, sumY = 0;
            seen[start] = true;
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                var x = index % map.GridSize;
                var y = index / map.GridSize;
                pixels++;
                sumX += x;
                sumY += y;
                var pixelRole = (SemanticAnchorRole)map.ComponentRoles[index];
                if (pixelRole != SemanticAnchorRole.None) roleCounts[(int)pixelRole]++;

                foreach (var direction in Directions)
                {
                    var nx = x + direction.X;
                    var ny = y + direction.Y;
                    if (nx < 0 || ny < 0 || nx >= map.GridSize || ny >= map.GridSize) continue;
                    var next = ny * map.GridSize + nx;
                    if (seen[next] || ((TerrainKind)map.Cells[next]).IsWater()) continue;
                    seen[next] = true;
                    queue.Enqueue(next);
                }
            }

            var role = SemanticAnchorRole.None;
            var best = 0;
            foreach (var candidate in SemanticAnchorCatalog.Assignable)
                if (roleCounts[(int)candidate] > best)
                {
                    role = candidate;
                    best = roleCounts[(int)candidate];
                }

            var centroidX = sumX / pixels;
            var centroidY = sumY / pixels;
            var radius = Math.Clamp(Math.Sqrt(Math.Pow(centroidX - center, 2) + Math.Pow(centroidY - center, 2)) / maxRadius, 0, 1);
            components.Add(new SemanticComponent(role, pixels, centroidX, centroidY, radius));
        }

        return new SemanticAnchorSummary { Components = components };
    }
}
