using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.Generation;

/// <summary>Creates reproducible terrain using a version-independent integer noise source.</summary>
public static class SeededMapGenerator
{
    public static MapProject Generate(int size, int seed)
    {
        var map = MapProject.CreateDefault(size);
        map.GenerationSeed = seed;
        map.PreserveCoastline = true;
        var random = new SeedRandom(unchecked((uint)seed));
        var noiseSeed = random.Next();
        var layout = (int)(random.Next() % 4);
        var landFraction = layout switch
        {
            0 => 0.46 + random.Unit() * 0.28,
            1 => 0.34 + random.Unit() * 0.22,
            2 => 0.64 + random.Unit() * 0.17,
            _ => 0.42 + random.Unit() * 0.30
        };
        var bodies = CreateLandBodies(layout, ref random);
        var scores = new double[map.Cells.Length];
        var order = Enumerable.Range(0, scores.Length).ToArray();
        var landCount = (int)Math.Round(scores.Length * landFraction);
        var warpScale = 0.055 + random.Unit() * 0.085;
        var coastFrequency = 3.5 + random.Unit() * 4;

        // Bodies can lie beyond the map boundary. The map is a window into a
        // larger landscape, so edges and corners are ordinary terrain locations.
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var u = (x + 0.5) / size;
                var v = (y + 0.5) / size;
                var warpedX = u + warpScale * Noise(u * 3.1, v * 3.1, noiseSeed);
                var warpedY = v + warpScale * Noise(u * 3.1, v * 3.1, noiseSeed ^ 0x8DA6B343u);
                var continent = double.NegativeInfinity;
                foreach (var body in bodies)
                    continent = Math.Max(continent, BodyValue(warpedX, warpedY, body));
                scores[y * size + x] = continent +
                    0.28 * Noise(u * coastFrequency, v * coastFrequency, noiseSeed ^ 0xCB1AB31Fu) +
                    0.11 * Noise(u * 13.7, v * 13.7, noiseSeed ^ 0x165667B1u) +
                    0.04 * Noise(u * 31.3, v * 31.3, noiseSeed ^ 0xD3A2646Cu);
            }
        }

        SortByScore(order, scores);
        var preliminaryLand = order.Take(landCount).ToArray();
        var rivers = new List<Segment>();
        var riverCount = (layout == 2 ? 2 : 1) + (int)(random.Next() % 3);
        for (var river = 0; river < riverCount; river++)
        {
            var anchor = Pick(preliminaryLand, ref random);
            AddRibbon(rivers, (anchor % size + 0.5) / size, (anchor / size + 0.5) / size,
                0.65 + random.Unit() * 1.15, 0.012 + random.Unit() * 0.032,
                0.025 + random.Unit() * 0.095, taper: river > 0, ref random);
        }
        var lakes = new LandBody[(int)(random.Next() % 5)];
        for (var lake = 0; lake < lakes.Length; lake++)
        {
            var anchor = Pick(preliminaryLand, ref random);
            var angle = random.Unit() * Math.Tau;
            lakes[lake] = new LandBody((anchor % size + 0.5) / size, (anchor / size + 0.5) / size,
                0.025 + random.Unit() * 0.09, 0.014 + random.Unit() * 0.055,
                Math.Cos(angle), Math.Sin(angle));
        }
        for (var index = 0; index < scores.Length; index++)
        {
            var u = (index % size + 0.5) / size;
            var v = (index / size + 0.5) / size;
            var waterCut = Math.Max(0, 1 - RibbonDistance(u, v, rivers));
            foreach (var lake in lakes)
                waterCut = Math.Max(waterCut, BodyValue(u, v, lake) +
                    0.14 * Noise(u * 23, v * 23, noiseSeed ^ 0xCA01F9DDu));
            scores[index] -= 5 * Math.Max(0, waterCut);
        }

        SortByScore(order, scores);
        var land = new bool[scores.Length];
        var landOrder = order.Take(landCount).ToArray();
        foreach (var index in landOrder) land[index] = true;
        var distanceFromWater = Distances(size, land, sourceValue: false);
        var distanceFromLand = Distances(size, land, sourceValue: true);

        var ranges = new List<Segment>();
        var rangeCount = 1 + (int)(random.Next() % 4);
        for (var range = 0; range < rangeCount; range++)
        {
            var anchor = Pick(landOrder, ref random);
            AddRibbon(ranges, (anchor % size + 0.5) / size, (anchor / size + 0.5) / size,
                0.40 + random.Unit() * 1.30, 0.012 + random.Unit() * 0.033,
                0.025 + random.Unit() * 0.11, taper: (random.Next() & 1) == 0, ref random);
        }
        var forestBelts = new List<Segment>();
        var beltCount = 1 + (int)(random.Next() % 3);
        for (var belt = 0; belt < beltCount; belt++)
        {
            var anchor = Pick(landOrder, ref random);
            AddRibbon(forestBelts, (anchor % size + 0.5) / size, (anchor / size + 0.5) / size,
                0.50 + random.Unit() * 1.20, 0.026 + random.Unit() * 0.067,
                0.035 + random.Unit() * 0.11, taper: (random.Next() & 1) == 0, ref random);
        }

        var mountainScores = new double[scores.Length];
        var forestScores = new double[scores.Length];
        var hillScores = new double[scores.Length];
        var forestFrequency = 2.8 + random.Unit() * 5.5;
        var forestThreshold = 0.16 + random.Unit() * 0.30;
        var hillSpread = 1.30 + random.Unit() * 0.80;
        foreach (var index in landOrder)
        {
            var u = (index % size + 0.5) / size;
            var v = (index / size + 0.5) / size;
            var rangeDistance = RibbonDistance(u, v, ranges);
            mountainScores[index] = 1 - rangeDistance +
                0.28 * Noise(u * 16.2, v * 16.2, noiseSeed ^ 0x4F1BBCDCu);
            hillScores[index] = hillSpread - rangeDistance +
                0.30 * Noise(u * 9.4, v * 9.4, noiseSeed ^ 0xD6E8FEB9u);
            var forestPatch = 0.76 * Noise(u * forestFrequency, v * forestFrequency, noiseSeed ^ 0xA24BAED5u) +
                0.20 * Noise(u * 14.5, v * 14.5, noiseSeed ^ 0x9FB21C65u) +
                0.04 * Noise(u * 33, v * 33, noiseSeed ^ 0xB7E15163u) - forestThreshold;
            var forestBelt = 1 - RibbonDistance(u, v, forestBelts) +
                0.24 * Noise(u * 11, v * 11, noiseSeed ^ 0xB5297A4Du);
            forestScores[index] = Math.Max(forestPatch, forestBelt);
        }

        // Broad safeguards leave open ground even when several independently
        // placed ranges and belts overlap. Their actual extent comes from the
        // seeded geometry, rather than an exact per-terrain coverage quota.
        var mountainCutoff = CoverageCutoff(landOrder, mountainScores, 0.38);
        var forestCutoff = CoverageCutoff(landOrder, forestScores, 0.57);
        var grassCount = 0;
        foreach (var index in landOrder)
        {
            var terrain = mountainScores[index] > mountainCutoff ? TerrainKind.Mountain :
                forestScores[index] > forestCutoff ? TerrainKind.Forest :
                hillScores[index] > 0 ? TerrainKind.Hills : TerrainKind.Grassland;
            map.Cells[index] = (int)terrain;
            if (terrain == TerrainKind.Grassland) grassCount++;
        }
        var minimumGrass = Math.Max(3, (int)Math.Round(landCount * 0.18));
        if (grassCount < minimumGrass)
        {
            foreach (var index in landOrder)
                scores[index] = -Math.Max(mountainScores[index], Math.Max(forestScores[index], hillScores[index] * 0.35));
            var openGroundOrder = (int[])landOrder.Clone();
            SortByScore(openGroundOrder, scores);
            foreach (var index in openGroundOrder)
            {
                if (grassCount >= minimumGrass) break;
                if (map.Cells[index] == (int)TerrainKind.Grassland) continue;
                map.Cells[index] = (int)TerrainKind.Grassland;
                grassCount++;
            }
        }

        var beachWidth = Math.Max(1, (int)Math.Round(size * (0.003 + random.Unit() * 0.008)));
        var shelfWidth = Math.Max(1, (int)Math.Round(size * (0.007 + random.Unit() * 0.028)));
        var beachThreshold = -0.12 + random.Unit() * 0.32;
        var shelfThreshold = -0.15 + random.Unit() * 0.35;
        for (var index = 0; index < land.Length; index++)
        {
            var u = (index % size + 0.5) / size;
            var v = (index / size + 0.5) / size;
            if (!land[index])
            {
                var shelf = 0.82 * Noise(u * 4.1, v * 4.1, noiseSeed ^ 0xDB4F0B91u) +
                    0.18 * Noise(u * 12.7, v * 12.7, noiseSeed ^ 0xBBE05633u);
                map.Cells[index] = (int)(shelf > shelfThreshold &&
                    distanceFromLand[index] <= shelfWidth * (0.65 + 0.7 * Math.Max(0, shelf))
                    ? TerrainKind.Shallows : TerrainKind.DeepWater);
            }
            else if (map.Cells[index] == (int)TerrainKind.Grassland && grassCount > minimumGrass &&
                distanceFromWater[index] <= beachWidth)
            {
                var beach = 0.85 * Noise(u * 4.7, v * 4.7, noiseSeed ^ 0xC6BC2797u) +
                    0.15 * Noise(u * 15.3, v * 15.3, noiseSeed ^ 0x9E3779B9u);
                if (beach > beachThreshold)
                {
                    map.Cells[index] = (int)TerrainKind.Beach;
                    grassCount--;
                }
            }
        }

        // At tiny resolutions a narrow belt can fall between sample centers.
        // Keep the defining complete-map features using their strongest cells.
        EnsureFeature(map, landOrder, mountainScores, TerrainKind.Mountain);
        EnsureFeature(map, landOrder, forestScores, TerrainKind.Forest);
        foreach (var index in landOrder)
            if (map.Cells[index] == (int)TerrainKind.Mountain)
                map.ElevationMask[index] = MapProject.CliffElevation;
        return map;
    }

    private static LandBody[] CreateLandBodies(int layout, ref SeedRandom random)
    {
        var count = layout == 1 ? 5 + (int)(random.Next() % 5) : 3 + (int)(random.Next() % 5);
        var bodies = new LandBody[count];
        var bearing = random.Unit() * Math.Tau;
        var cosine = Math.Cos(bearing);
        var sine = Math.Sin(bearing);
        var anchorX = -0.10 + random.Unit() * 1.20;
        var anchorY = -0.10 + random.Unit() * 1.20;
        for (var index = 0; index < count; index++)
        {
            var rotation = random.Unit() * Math.Tau;
            double x, y, radiusX, radiusY;
            if (layout == 0 && index == 0)
            {
                x = 0.5 + cosine * 0.62;
                y = 0.5 + sine * 0.62;
                radiusX = 0.55 + random.Unit() * 0.34;
                radiusY = 0.44 + random.Unit() * 0.25;
            }
            else if (layout == 2)
            {
                x = -0.05 + random.Unit() * 1.10;
                y = -0.05 + random.Unit() * 1.10;
                radiusX = 0.32 + random.Unit() * 0.35;
                radiusY = 0.25 + random.Unit() * 0.34;
            }
            else if (layout == 3)
            {
                var along = (random.Unit() - 0.5) * 1.1;
                var across = (random.Unit() - 0.5) * 0.42;
                x = anchorX + along * cosine - across * sine;
                y = anchorY + along * sine + across * cosine;
                radiusX = 0.18 + random.Unit() * 0.29;
                radiusY = 0.12 + random.Unit() * 0.22;
            }
            else
            {
                x = -0.10 + random.Unit() * 1.20;
                y = -0.10 + random.Unit() * 1.20;
                radiusX = 0.09 + random.Unit() * 0.20;
                radiusY = 0.065 + random.Unit() * 0.17;
            }
            bodies[index] = new LandBody(x, y, radiusX, radiusY, Math.Cos(rotation), Math.Sin(rotation));
        }
        return bodies;
    }

    private static void AddRibbon(List<Segment> segments, double centerX, double centerY,
        double length, double width, double bend, bool taper, ref SeedRandom random)
    {
        var angle = random.Unit() * Math.Tau;
        var cosine = Math.Cos(angle);
        var sine = Math.Sin(angle);
        var phase = random.Unit() * Math.Tau;
        var phaseWidth = random.Unit() * Math.Tau;
        var frequency = 0.6 + random.Unit() * 1.25;
        var priorX = 0.0;
        var priorY = 0.0;
        const int subdivisions = 16;
        for (var point = 0; point <= subdivisions; point++)
        {
            var fraction = point / (double)subdivisions;
            var along = (fraction - 0.5) * length;
            var across = bend * (Math.Sin(fraction * Math.Tau * frequency + phase) - Math.Sin(0.5 * Math.Tau * frequency + phase));
            var x = centerX + along * cosine - across * sine;
            var y = centerY + along * sine + across * cosine;
            if (point > 0)
            {
                var segmentWidth = width * (0.85 + 0.30 * Math.Sin(fraction * Math.Tau * 1.7 + phaseWidth));
                if (taper) segmentWidth *= 0.36 + 0.64 * Math.Sin(fraction * Math.PI);
                var dx = x - priorX;
                var dy = y - priorY;
                segments.Add(new Segment(priorX, priorY, dx, dy, 1 / (dx * dx + dy * dy), 1 / (segmentWidth * segmentWidth)));
            }
            priorX = x;
            priorY = y;
        }
    }

    private static double BodyValue(double x, double y, LandBody body)
    {
        var dx = x - body.X;
        var dy = y - body.Y;
        var along = (dx * body.Cosine + dy * body.Sine) / body.RadiusX;
        var across = (-dx * body.Sine + dy * body.Cosine) / body.RadiusY;
        return 1 - Math.Sqrt(along * along + across * across);
    }

    private static double RibbonDistance(double x, double y, List<Segment> segments)
    {
        var distance = double.PositiveInfinity;
        foreach (var segment in segments)
        {
            var deltaX = x - segment.X;
            var deltaY = y - segment.Y;
            var fraction = Math.Clamp((deltaX * segment.Dx + deltaY * segment.Dy) * segment.InverseLengthSquared, 0, 1);
            var offsetX = deltaX - fraction * segment.Dx;
            var offsetY = deltaY - fraction * segment.Dy;
            distance = Math.Min(distance, (offsetX * offsetX + offsetY * offsetY) * segment.InverseWidthSquared);
        }
        return Math.Sqrt(distance);
    }

    private static double CoverageCutoff(int[] landOrder, double[] scores, double maximumFraction)
    {
        var positiveCount = landOrder.Count(index => scores[index] > 0);
        var maximumCount = (int)Math.Round(landOrder.Length * maximumFraction);
        if (positiveCount <= maximumCount) return 0;
        var ranked = (int[])landOrder.Clone();
        SortByScore(ranked, scores);
        return scores[ranked[maximumCount]];
    }

    private static void EnsureFeature(MapProject map, int[] landOrder, double[] scores, TerrainKind terrain)
    {
        if (landOrder.Any(index => map.Cells[index] == (int)terrain)) return;
        var best = -1;
        foreach (var index in landOrder)
        {
            if ((TerrainKind)map.Cells[index] is not (TerrainKind.Grassland or TerrainKind.Hills or TerrainKind.Beach)) continue;
            if (best < 0 || scores[index] > scores[best]) best = index;
        }
        if (best >= 0) map.Cells[best] = (int)terrain;
    }

    private static int Pick(int[] indices, ref SeedRandom random) => indices[(int)(random.Next() % (uint)indices.Length)];

    private static int[] Distances(int size, bool[] mask, bool sourceValue)
    {
        var distances = new int[mask.Length];
        Array.Fill(distances, size * 2);
        var queue = new int[mask.Length];
        var head = 0;
        var tail = 0;
        for (var index = 0; index < mask.Length; index++)
        {
            if (mask[index] != sourceValue) continue;
            distances[index] = 0;
            queue[tail++] = index;
        }
        while (head < tail)
        {
            var index = queue[head++];
            var x = index % size;
            var y = index / size;
            if (x > 0) Visit(index - 1);
            if (x + 1 < size) Visit(index + 1);
            if (y > 0) Visit(index - size);
            if (y + 1 < size) Visit(index + size);

            void Visit(int neighbor)
            {
                if (distances[neighbor] <= distances[index] + 1) return;
                distances[neighbor] = distances[index] + 1;
                queue[tail++] = neighbor;
            }
        }
        return distances;
    }

    private static void SortByScore(int[] order, double[] scores) =>
        Array.Sort(order, (left, right) =>
        {
            var comparison = scores[right].CompareTo(scores[left]);
            return comparison != 0 ? comparison : left.CompareTo(right);
        });

    private static double Noise(double x, double y, uint seed)
    {
        var left = (int)Math.Floor(x);
        var top = (int)Math.Floor(y);
        var fractionX = x - left;
        var fractionY = y - top;
        var blendX = fractionX * fractionX * fractionX * (fractionX * (fractionX * 6 - 15) + 10);
        var blendY = fractionY * fractionY * fractionY * (fractionY * (fractionY * 6 - 15) + 10);
        var a = Value(left, top, seed);
        var b = Value(left + 1, top, seed);
        var c = Value(left, top + 1, seed);
        var d = Value(left + 1, top + 1, seed);
        return (a + (b - a) * blendX) * (1 - blendY) + (c + (d - c) * blendX) * blendY;
    }

    private static double Value(int x, int y, uint seed) =>
        Mix(unchecked((uint)x * 0x9E3779B1u ^ (uint)y * 0x85EBCA77u ^ seed)) / (double)uint.MaxValue * 2 - 1;

    private static uint Mix(uint value)
    {
        value ^= value >> 16;
        value = unchecked(value * 0x7FEB352Du);
        value ^= value >> 15;
        value = unchecked(value * 0x846CA68Bu);
        return value ^ (value >> 16);
    }

    private struct SeedRandom(uint state)
    {
        private uint _state = state;
        public uint Next()
        {
            _state = unchecked(_state + 0x9E3779B9u);
            return Mix(_state);
        }
        public double Unit() => Next() / ((double)uint.MaxValue + 1);
    }

    private readonly record struct LandBody(double X, double Y, double RadiusX, double RadiusY, double Cosine, double Sine);
    private readonly record struct Segment(double X, double Y, double Dx, double Dy, double InverseLengthSquared, double InverseWidthSquared);
}
