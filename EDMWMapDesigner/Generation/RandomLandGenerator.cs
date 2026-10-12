using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.Generation;

public sealed record RandomLandResult(
    int AddedCells,
    int OverwrittenCells,
    int PatchesAdded,
    int Seed,
    TerrainKind Terrain,
    int OverwritePercentage)
{
    public int ChangedCells => AddedCells + OverwrittenCells;
}

public static class RandomLandGenerator
{
    public static RandomLandResult Add(
        MapProject map,
        TerrainKind terrain,
        int overwritePercentage = 0) =>
        Add(map, terrain, overwritePercentage, Random.Shared.Next());

    public static RandomLandResult Add(
        MapProject map,
        TerrainKind terrain,
        int overwritePercentage,
        int seed)
    {
        ArgumentNullException.ThrowIfNull(map);
        map.Validate();
        if (!terrain.IsLand())
            throw new ArgumentException("Random land must use a land terrain.", nameof(terrain));
        if (overwritePercentage is < 0 or > 100)
            throw new ArgumentOutOfRangeException(
                nameof(overwritePercentage), "Overwrite percentage must be 0-100.");

        var random = new Random(seed);
        var patchCount = random.Next(3, 7);
        var patchesAdded = 0;
        var size = map.GridSize;
        var covered = new bool[map.Cells.Length];
        var protectedPlayerStarts = new bool[map.Cells.Length];
        if (!MapProject.IsSafePlayerStartTerrain(terrain))
            foreach (var start in map.ManualPlayerStarts)
                protectedPlayerStarts[start.Y * size + start.X] = true;

        for (var patch = 0; patch < patchCount; patch++)
        {
            var centerIndex = FindPatchCenter(
                map, covered, random, overwritePercentage > 0);
            if (centerIndex < 0) break;
            patchesAdded++;
            var centerX = centerIndex % size + random.NextDouble() - 0.5;
            var centerY = centerIndex / size + random.NextDouble() - 0.5;
            var radiusX = size * (0.08 + random.NextDouble() * 0.10);
            var radiusY = size * (0.06 + random.NextDouble() * 0.09);
            var rotation = random.NextDouble() * Math.PI;
            var phaseA = random.NextDouble() * Math.PI * 2.0;
            var phaseB = random.NextDouble() * Math.PI * 2.0;
            var cosine = Math.Cos(rotation);
            var sine = Math.Sin(rotation);
            var bound = (int)Math.Ceiling(Math.Max(radiusX, radiusY) * 1.28);
            var minimumX = Math.Max(0, (int)Math.Floor(centerX) - bound);
            var maximumX = Math.Min(size - 1, (int)Math.Ceiling(centerX) + bound);
            var minimumY = Math.Max(0, (int)Math.Floor(centerY) - bound);
            var maximumY = Math.Min(size - 1, (int)Math.Ceiling(centerY) + bound);

            for (var y = minimumY; y <= maximumY; y++)
            {
                for (var x = minimumX; x <= maximumX; x++)
                {
                    var deltaX = x - centerX;
                    var deltaY = y - centerY;
                    var rotatedX = deltaX * cosine + deltaY * sine;
                    var rotatedY = -deltaX * sine + deltaY * cosine;
                    var normalizedX = rotatedX / radiusX;
                    var normalizedY = rotatedY / radiusY;
                    var distance = Math.Sqrt(
                        normalizedX * normalizedX + normalizedY * normalizedY);
                    var angle = Math.Atan2(normalizedY, normalizedX);
                    var coastline = 1.0 +
                        0.14 * Math.Sin(3.0 * angle + phaseA) +
                        0.07 * Math.Sin(5.0 * angle + phaseB) +
                        0.035 * Math.Sin((x + y) * 0.17 + phaseA - phaseB);
                    if (distance > coastline) continue;
                    covered[y * size + x] = true;
                }
            }
        }

        var addedCells = 0;
        var overwriteCandidates = new List<int>();
        for (var index = 0; index < covered.Length; index++)
        {
            if (!covered[index]) continue;
            var existing = (TerrainKind)map.Cells[index];
            if (existing.IsWater())
            {
                map[index % size, index / size] = terrain;
                map.ComponentRoles[index] = (byte)SemanticAnchorRole.None;
                addedCells++;
            }
            else if (existing != terrain && !protectedPlayerStarts[index])
            {
                overwriteCandidates.Add(index);
            }
        }

        for (var index = overwriteCandidates.Count - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (overwriteCandidates[index], overwriteCandidates[swap]) =
                (overwriteCandidates[swap], overwriteCandidates[index]);
        }
        var overwrittenCells = (int)Math.Round(
            overwriteCandidates.Count * (overwritePercentage / 100.0),
            MidpointRounding.AwayFromZero);
        for (var index = 0; index < overwrittenCells; index++)
        {
            var cell = overwriteCandidates[index];
            map[cell % size, cell / size] = terrain;
        }

        return new RandomLandResult(
            addedCells, overwrittenCells, patchesAdded, seed, terrain,
            overwritePercentage);
    }

    private static int FindPatchCenter(
        MapProject map,
        bool[] covered,
        Random random,
        bool allowLand)
    {
        var water = FindCell(map, covered, random, requireWater: true);
        return water >= 0 || !allowLand
            ? water
            : FindCell(map, covered, random, requireWater: false);
    }

    private static int FindCell(
        MapProject map,
        bool[] covered,
        Random random,
        bool requireWater)
    {
        bool Eligible(int index) => !covered[index] &&
            (!requireWater || ((TerrainKind)map.Cells[index]).IsWater());

        for (var attempt = 0; attempt < 64; attempt++)
        {
            var index = random.Next(map.Cells.Length);
            if (Eligible(index)) return index;
        }

        var start = random.Next(map.Cells.Length);
        for (var offset = 0; offset < map.Cells.Length; offset++)
        {
            var index = (start + offset) % map.Cells.Length;
            if (Eligible(index)) return index;
        }
        return -1;
    }
}
