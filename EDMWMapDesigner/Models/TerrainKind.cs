using System.Drawing;

namespace EDMWMapDesigner.Models;

public enum TerrainKind
{
    DeepWater,
    Shallows,
    Beach,
    Grassland,
    Forest,
    Hills,
    Mountain,
    Desert
}

public static class TerrainCatalog
{
    public static readonly TerrainKind[] All = Enum.GetValues<TerrainKind>();

    public static string DisplayName(this TerrainKind kind) => kind switch
    {
        TerrainKind.DeepWater => "Deep water",
        TerrainKind.Shallows => "Shallows",
        _ => kind.ToString()
    };

    public static Color Color(this TerrainKind kind) => kind switch
    {
        TerrainKind.DeepWater => System.Drawing.Color.FromArgb(16, 37, 54),
        TerrainKind.Shallows => System.Drawing.Color.FromArgb(48, 132, 168),
        TerrainKind.Beach => System.Drawing.Color.FromArgb(220, 194, 126),
        TerrainKind.Grassland => System.Drawing.Color.FromArgb(89, 139, 70),
        TerrainKind.Forest => System.Drawing.Color.FromArgb(37, 91, 51),
        TerrainKind.Hills => System.Drawing.Color.FromArgb(126, 124, 75),
        TerrainKind.Mountain => System.Drawing.Color.FromArgb(105, 106, 107),
        TerrainKind.Desert => System.Drawing.Color.FromArgb(190, 139, 73),
        _ => System.Drawing.Color.Magenta
    };

    public static bool IsWater(this TerrainKind kind) =>
        kind is TerrainKind.DeepWater or TerrainKind.Shallows;

    public static bool IsLand(this TerrainKind kind) => !kind.IsWater();
}
