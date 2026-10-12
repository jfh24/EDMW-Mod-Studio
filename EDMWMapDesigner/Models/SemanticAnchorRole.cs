namespace EDMWMapDesigner.Models;

public enum SemanticAnchorRole : byte
{
    None = 0,
    Center = 1,
    InnerNeutral = 2,
    OuterNeutral = 3,
    PlayerLand = 4
}

public static class SemanticAnchorCatalog
{
    public static readonly SemanticAnchorRole[] Assignable =
    [
        SemanticAnchorRole.Center,
        SemanticAnchorRole.InnerNeutral,
        SemanticAnchorRole.OuterNeutral,
        SemanticAnchorRole.PlayerLand
    ];

    public static string DisplayName(this SemanticAnchorRole role) => role switch
    {
        SemanticAnchorRole.None => "No anchor",
        SemanticAnchorRole.Center => "Center",
        SemanticAnchorRole.InnerNeutral => "Inner neutral",
        SemanticAnchorRole.OuterNeutral => "Outer neutral",
        SemanticAnchorRole.PlayerLand => "Player land",
        _ => role.ToString()
    };

    public static Color Color(this SemanticAnchorRole role) => role switch
    {
        SemanticAnchorRole.Center => System.Drawing.Color.FromArgb(255, 214, 72),
        SemanticAnchorRole.InnerNeutral => System.Drawing.Color.FromArgb(75, 220, 225),
        SemanticAnchorRole.OuterNeutral => System.Drawing.Color.FromArgb(226, 102, 205),
        SemanticAnchorRole.PlayerLand => System.Drawing.Color.FromArgb(109, 238, 118),
        _ => System.Drawing.Color.Transparent
    };
}
