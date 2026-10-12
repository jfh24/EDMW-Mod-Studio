using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EDMWMapDesigner.UI;

/// <summary>The generated category artwork, sampled from its embedded transparent atlas.</summary>
internal static class ProgressionIcons
{
    private static readonly Lazy<IReadOnlyList<Image>> Icons = new(Load);
    private static readonly Lazy<IReadOnlyList<Image>> CivilizationIcons = new(() =>
        LoadAtlas("EDMWMapDesigner.Assets.Progression.civilization-icons.png", 2, 3));

    public static Image? ForKind(string kind) => kind.ToLowerInvariant() switch
    {
        "unit" or "units" => Icons.Value[0],
        "building" or "buildings" => Icons.Value[1],
        "technology" or "technologies" => Icons.Value[2],
        "age advance" or "age advances" or "ages" => Icons.Value[3],
        "overview" => CivilizationIcons.Value[0],
        "ability" or "abilities" => CivilizationIcons.Value[1],
        "economy" => CivilizationIcons.Value[2],
        "assets" => CivilizationIcons.Value[3],
        "ai" => CivilizationIcons.Value[4],
        "changes" => CivilizationIcons.Value[5],
        _ => null
    };

    private static IReadOnlyList<Image> Load() => LoadAtlas("EDMWMapDesigner.Assets.Progression.progression-icons.png", 2, 2);

    private static IReadOnlyList<Image> LoadAtlas(string resourceName, int columns, int rows)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            resourceName)
            ?? throw new InvalidOperationException("The progression icon artwork is missing.");
        using var sheet = new Bitmap(stream);
        var width = sheet.Width / columns;
        var height = sheet.Height / rows;
        var pixels = sheet.LockBits(new Rectangle(0, 0, sheet.Width, sheet.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] bytes;
        try
        {
            bytes = new byte[pixels.Stride * pixels.Height];
            Marshal.Copy(pixels.Scan0, bytes, 0, bytes.Length);
        }
        finally { sheet.UnlockBits(pixels); }
        return Enumerable.Range(0, columns * rows).Select(index => (Image)sheet.Clone(
            VisibleSquare(new Rectangle(index % columns * width, index / columns * height, width, height), bytes, pixels.Stride),
            PixelFormat.Format32bppArgb)).ToArray();
    }

    private static Rectangle VisibleSquare(Rectangle tile, byte[] pixels, int stride)
    {
        var left = tile.Right;
        var top = tile.Bottom;
        var right = tile.Left;
        var bottom = tile.Top;
        for (var y = tile.Top; y < tile.Bottom; y++)
            for (var x = tile.Left; x < tile.Right; x++)
                if (pixels[y * stride + x * 4 + 3] >= 32)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x + 1);
                    bottom = Math.Max(bottom, y + 1);
                }
        if (right <= left || bottom <= top) return tile;
        var side = Math.Min(Math.Min(tile.Width, tile.Height), Math.Max(right - left, bottom - top) + 32);
        return new Rectangle(Math.Clamp((left + right - side) / 2, tile.Left, tile.Right - side),
            Math.Clamp((top + bottom - side) / 2, tile.Top, tile.Bottom - side), side, side);
    }
}
