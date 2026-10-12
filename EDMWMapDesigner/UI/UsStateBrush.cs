using System.IO.Compression;
using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.UI;

/// <summary>A north-up, aspect-preserving state silhouette in the square [-1, 1].</summary>
internal sealed class UsStateBrush
{
    internal const int MaskSize = 1024;
    private readonly Lazy<byte[]> _mask;
    private readonly Lazy<byte[]> _mountains;
    private readonly Lazy<byte[]> _water;
    private readonly Lazy<byte[]> _forests;

    internal UsStateBrush(string name, string compressedMask)
    {
        Name = name;
        _mask = new Lazy<byte[]>(() => DecodeMask(compressedMask));
        _mountains = new Lazy<byte[]>(() => DecodeMask(UsStateFeatureCatalog.Mountains(name)));
        _water = new Lazy<byte[]>(() => DecodeMask(UsStateFeatureCatalog.Water(name)));
        _forests = new Lazy<byte[]>(() => DecodeMask(UsStateFeatureCatalog.Forests(name)));
    }

    public string Name { get; }

    /// <summary>Tests normalized brush coordinates; negative y points north.</summary>
    public bool Contains(double x, double y)
        => Contains(_mask, x, y);

    public TerrainKind? GetFeature(double x, double y, bool mountains, bool water, bool forests = false)
    {
        if (water && Contains(_water, x, y)) return TerrainKind.Shallows;
        if (mountains && Contains(x, y) && Contains(_mountains, x, y)) return TerrainKind.Mountain;
        if (forests && Contains(x, y) && Contains(_forests, x, y)) return TerrainKind.Forest;
        return null;
    }

    private static bool Contains(Lazy<byte[]> mask, double x, double y)
    {
        // The positive comparisons also reject NaN and infinity before conversion.
        if (!(x >= -1d && x <= 1d && y >= -1d && y <= 1d))
        {
            return false;
        }

        int column = Math.Min((int)((x + 1d) * (MaskSize / 2d)), MaskSize - 1);
        int row = Math.Min((int)((y + 1d) * (MaskSize / 2d)), MaskSize - 1);
        int bit = row * MaskSize + column;
        return (mask.Value[bit >> 3] & (1 << (bit & 7))) != 0;
    }

    public override string ToString() => Name;

    private static byte[] DecodeMask(string compressedMask)
    {
        using var source = new MemoryStream(Convert.FromBase64String(compressedMask));
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        byte[] mask = new byte[MaskSize * MaskSize / 8];
        gzip.ReadExactly(mask);
        if (gzip.ReadByte() != -1)
        {
            throw new InvalidDataException("Unexpected US state brush mask length.");
        }

        return mask;
    }
}

internal static partial class UsStateBrushCatalog
{
    public static IReadOnlyList<UsStateBrush> All { get; }
        = Array.AsReadOnly(CreateBrushes());
}

internal static partial class UsStateFeatureCatalog
{
    private static readonly Dictionary<string, string> MountainMasks = CreateMountainMasks();
    private static readonly Dictionary<string, string> WaterMasks = CreateWaterMasks();
    private static readonly Dictionary<string, string> ForestMasks = CreateForestMasks();

    internal static string Mountains(string name) => MountainMasks[name];
    internal static string Water(string name) => WaterMasks[name];
    internal static string Forests(string name) => ForestMasks[name];
}
