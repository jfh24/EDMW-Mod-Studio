namespace EDMWMapDesigner.Models;

public sealed class ManualPlayerStart
{
    public const int MinimumSlot = 1;
    public const int MaximumSlot = 8;

    public int PlayerSlot { get; set; } = MinimumSlot;
    public int X { get; set; }
    public int Y { get; set; }

    public ManualPlayerStart Clone() => new()
    {
        PlayerSlot = PlayerSlot,
        X = X,
        Y = Y
    };
}
