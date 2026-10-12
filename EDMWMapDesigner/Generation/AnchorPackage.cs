using System.Globalization;
using System.Security.Cryptography;
using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.Generation;

public static class AnchorPackage
{
    public const uint IdMask = 0x007FFFFF;
    public const string GeneratorMapPrefix = "EDMW_";
    public const string DataFolderName = "EDMW Anchors";
    public const int HeaderSize = 32;

    public const uint PaintedTreeMaskFlag = 0x00000001;
    public const uint ManualResourcePlacementFlag = 0x00000002;
    public const uint ManualPlayerStartPlacementFlag = 0x00000004;
    public const uint ManualFaunaPlacementFlag = 0x00000008;
    public const uint PreserveCoastlineFlag = 0x00000010;
    public const uint ForcedCliffMaskFlag = 0x00000020;
    private const uint ExistingPlacementFlags = PaintedTreeMaskFlag |
                                                 ManualResourcePlacementFlag |
                                                 ManualPlayerStartPlacementFlag;

    private static ReadOnlySpan<byte> Magic => "EDMWA07\0"u8;

    public static uint MapId(MapProject map)
    {
        ArgumentNullException.ThrowIfNull(map);
        map.Validate();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(map.GridSize));
        hash.AppendData(ToTerrainBytes(map));
        hash.AppendData(map.ComponentRoles);
        hash.AppendData(ToElevationBytes(map));
        hash.AppendData(map.RampMask);
        hash.AppendData(BitConverter.GetBytes(map.Variance));
        hash.AppendData([1]);
        hash.AppendData([1]);
        hash.AppendData(ToResourceBytes(map));
        hash.AppendData([1]);
        hash.AppendData(ToPlayerStartBytes(map));
        // Preserve legacy identities; the new policy changes exported topology, unlike a seed label.
        if (map.PreserveCoastline) hash.AppendData([(byte)PreserveCoastlineFlag]);
        if (map.CliffMask.Any(value => value != 0))
        {
            hash.AppendData([(byte)ForcedCliffMaskFlag]);
            hash.AppendData(map.CliffMask);
        }
        var id = BitConverter.ToUInt32(hash.GetHashAndReset()) & IdMask;
        return id == 0 ? 1u : id;
    }

    public static string TransportThreshold(MapProject map)
    {
        var bits = 0x3F800000u | MapId(map);
        return BitConverter.UInt32BitsToSingle(bits).ToString("R", CultureInfo.InvariantCulture);
    }

    public static string FileName(MapProject map) => $"{MapId(map):X6}.edmwa";

    public static uint PlacementFlagsFor(MapProject map)
    {
        ArgumentNullException.ThrowIfNull(map);
        map.Validate();
        var flags = map.ManualResources.Any(resource =>
            resource.Kind.HasDistinctShippedModel())
            ? ExistingPlacementFlags | ManualFaunaPlacementFlag
            : ExistingPlacementFlags;
        if (map.PreserveCoastline) flags |= PreserveCoastlineFlag;
        if (map.CliffMask.Any(value => value != 0)) flags |= ForcedCliffMaskFlag;
        return flags;
    }

    public static string GeneratorName(uint mapId)
    {
        if (mapId is 0 or > IdMask)
            throw new ArgumentOutOfRangeException(nameof(mapId), mapId, "Map ID is outside the supported range.");
        return $"{GeneratorMapPrefix}{mapId:X6}";
    }

    public static string GeneratorFileNameFor(uint mapId) => $"{GeneratorName(mapId)}.dll";

    public static bool TryParseGeneratorName(string value, out uint mapId)
    {
        mapId = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.StartsWith(GeneratorMapPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var hex = value[GeneratorMapPrefix.Length..];
            if (hex.Length == 6 &&
                uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mapId) &&
                mapId is > 0 and <= IdMask)
                return true;
        }
        mapId = 0;
        return false;
    }

    public static bool TryDecodeTransportThreshold(string value, out uint mapId)
    {
        mapId = 0;
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
            return false;
        var bits = BitConverter.SingleToUInt32Bits(threshold);
        if ((bits & 0x7F800000u) != 0x3F800000u) return false;
        mapId = bits & IdMask;
        return mapId != 0;
    }

    public static string Write(MapProject map, string randomMapScriptsFolder)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentException.ThrowIfNullOrWhiteSpace(randomMapScriptsFolder);
        map.Validate();

        var terrain = ToTerrainBytes(map);
        var elevation = ToElevationBytes(map);
        var resources = ToResourceBytes(map);
        var playerStarts = ToPlayerStartBytes(map);
        var cliffs = ToCliffBytes(map);
        var checksum = ComputeChecksum(terrain, map.ComponentRoles, elevation, map.RampMask,
            cliffs, resources, playerStarts);
        var folder = Path.Combine(Path.GetFullPath(randomMapScriptsFolder), DataFolderName);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, FileName(map));
        if (File.Exists(target))
        {
            try
            {
                ValidateFile(target, map);
                return target;
            }
            catch (InvalidDataException) when (IsMatchingLegacyPayload(target, map, terrain))
            {
                // Upgrade a matching Version 1 payload in place after staging Version 2 below.
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidOperationException(
                    $"A different or damaged terrain payload already uses map ID {MapId(map):X6}. " +
                    "Move that payload aside or make a small map edit before exporting again.", ex);
            }
        }
        var staged = target + $".{Guid.NewGuid():N}.tmp";

        try
        {
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(MapId(map));
                writer.Write((uint)map.GridSize);
                writer.Write((uint)map.GridSize);
                writer.Write((uint)map.Variance);
                writer.Write(PlacementFlagsFor(map));
                writer.Write(checksum);
                writer.Write(terrain);
                writer.Write(map.ComponentRoles);
                writer.Write(elevation);
                writer.Write(map.RampMask);
                writer.Write(cliffs);
                writer.Write((uint)map.ManualResources.Count);
                writer.Write(resources);
                writer.Write((uint)map.ManualPlayerStarts.Count);
                writer.Write(playerStarts);
                writer.Flush();
                stream.Flush(true);
            }

            ValidateFile(staged, map);
            File.Move(staged, target, true);
            return target;
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    public static void ValidateFile(string path, MapProject map)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(map);
        map.Validate();

        var terrain = ToTerrainBytes(map);
        var elevation = ToElevationBytes(map);
        var resources = ToResourceBytes(map);
        var playerStarts = ToPlayerStartBytes(map);
        var cliffs = ToCliffBytes(map);
        var expectedLength = HeaderSize + terrain.Length + map.ComponentRoles.Length +
                             elevation.Length + map.RampMask.Length + cliffs.Length + sizeof(uint) + resources.Length +
                             sizeof(uint) + playerStarts.Length;
        using var stream = File.OpenRead(path);
        if (stream.Length != expectedLength)
            throw new InvalidDataException("The generated terrain payload has an invalid length.");

        using var reader = new BinaryReader(stream);
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) ||
            reader.ReadUInt32() != MapId(map) ||
            reader.ReadUInt32() != map.GridSize ||
            reader.ReadUInt32() != map.GridSize ||
            reader.ReadUInt32() != map.Variance ||
            reader.ReadUInt32() != PlacementFlagsFor(map))
            throw new InvalidDataException("The generated terrain payload header is invalid.");

        var expectedChecksum = reader.ReadUInt32();
        var actualTerrain = reader.ReadBytes(terrain.Length);
        var actualRoles = reader.ReadBytes(map.ComponentRoles.Length);
        var actualElevation = reader.ReadBytes(elevation.Length);
        var actualRamps = reader.ReadBytes(map.RampMask.Length);
        var actualCliffs = reader.ReadBytes(cliffs.Length);
        var resourceCount = reader.ReadUInt32();
        var actualResources = reader.ReadBytes(resources.Length);
        var playerStartCount = reader.ReadUInt32();
        var actualPlayerStarts = reader.ReadBytes(playerStarts.Length);
        if (!actualTerrain.AsSpan().SequenceEqual(terrain) ||
            !actualRoles.AsSpan().SequenceEqual(map.ComponentRoles) ||
            !actualElevation.AsSpan().SequenceEqual(elevation) ||
            !actualRamps.AsSpan().SequenceEqual(map.RampMask) ||
            !actualCliffs.AsSpan().SequenceEqual(cliffs) || resourceCount != map.ManualResources.Count ||
            !actualResources.AsSpan().SequenceEqual(resources) ||
            playerStartCount != map.ManualPlayerStarts.Count ||
            !actualPlayerStarts.AsSpan().SequenceEqual(playerStarts) ||
            expectedChecksum != ComputeChecksum(actualTerrain, actualRoles, actualElevation, actualRamps,
                actualCliffs, actualResources, actualPlayerStarts))
            throw new InvalidDataException("The generated terrain payload failed its integrity check.");
    }

    private static byte[] ToTerrainBytes(MapProject map)
    {
        var terrain = new byte[map.Cells.Length];
        for (var index = 0; index < terrain.Length; index++) terrain[index] = checked((byte)map.Cells[index]);
        return terrain;
    }

    private static byte[] ToElevationBytes(MapProject map)
    {
        var elevation = new byte[map.ElevationMask.Length];
        Buffer.BlockCopy(map.ElevationMask, 0, elevation, 0, elevation.Length);
        return elevation;
    }

    // Version 7 payloads add this array only when its flag is set, retaining old payloads unchanged.
    private static byte[] ToCliffBytes(MapProject map) =>
        map.CliffMask.Any(value => value != 0) ? map.CliffMask : [];

    private static byte[] ToResourceBytes(MapProject map)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var resource in map.ManualResources)
        {
            writer.Write((byte)resource.Kind);
            writer.Write(resource.X);
            writer.Write(resource.Y);
        }
        return stream.ToArray();
    }

    private static byte[] ToPlayerStartBytes(MapProject map)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var start in map.ManualPlayerStarts.OrderBy(start => start.PlayerSlot))
        {
            writer.Write(checked((byte)start.PlayerSlot));
            // Retained as a reserved byte so Version 7 payloads stay binary-compatible.
            // Runtime placement is keyed only by the numbered lobby slot.
            writer.Write((byte)0);
            writer.Write(start.X);
            writer.Write(start.Y);
        }
        return stream.ToArray();
    }

    private static uint ComputeChecksum(ReadOnlySpan<byte> terrain, ReadOnlySpan<byte> roles,
                                        ReadOnlySpan<byte> elevation, ReadOnlySpan<byte> ramps,
                                        ReadOnlySpan<byte> cliffs,
                                        ReadOnlySpan<byte> resources, ReadOnlySpan<byte> playerStarts)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var value in terrain) hash = (hash ^ value) * prime;
        foreach (var value in roles) hash = (hash ^ value) * prime;
        foreach (var value in elevation) hash = (hash ^ value) * prime;
        foreach (var value in ramps) hash = (hash ^ value) * prime;
        foreach (var value in cliffs) hash = (hash ^ value) * prime;
        foreach (var value in resources) hash = (hash ^ value) * prime;
        foreach (var value in playerStarts) hash = (hash ^ value) * prime;
        return hash;
    }

    private static bool IsMatchingLegacyPayload(string path, MapProject map, ReadOnlySpan<byte> terrain)
    {
        const int legacyHeaderSize = 24;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length != legacyHeaderSize + terrain.Length + map.ComponentRoles.Length) return false;
            using var reader = new BinaryReader(stream);
            if (!reader.ReadBytes(8).AsSpan().SequenceEqual("EDMWA01\0"u8) ||
                reader.ReadUInt32() != MapId(map) || reader.ReadUInt32() != map.GridSize ||
                reader.ReadUInt32() != map.GridSize || reader.ReadUInt32() != map.Variance)
                return false;
            return reader.ReadBytes(terrain.Length).AsSpan().SequenceEqual(terrain) &&
                   reader.ReadBytes(map.ComponentRoles.Length).AsSpan().SequenceEqual(map.ComponentRoles);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
