namespace EDMWMapDesigner.Models;

public enum ManualResourceKind : byte
{
    // Values 0-5 are retained for project/payload compatibility with earlier maps.
    Wildlife = 0,
    BerryBush = 1,
    Fish = 3,
    Gold = 4,
    Stone = 5,

    // Existing resource-animal values retain their Version 7 payload and project
    // identities. Several of these database names are aliases for other models;
    // they remain readable but are not offered as placement tools.
    Deer = 16,
    Goat = 17,
    BighornSheep = 18,
    Tiger = 19,
    Bear = 20,
    Shark = 21,
    Squirrel = 22,
    Sheep = 23,
    BlueBird = 24,
    Walrus = 25,
    Wolf = 26,
    Chicken = 27,
    Horse = 28,
    Dolphin = 29,
    WildPig = 30,
    Elk = 31
}

public sealed class ManualResource
{
    public ManualResourceKind Kind { get; set; }
    public int X { get; set; }
    public int Y { get; set; }

    public ManualResource Clone() => new() { Kind = Kind, X = X, Y = Y };
}

public enum ManualFaunaCategory : byte
{
    None = 0,
    Water = 1,
    Land = 2
}

public static class ManualResourceKindExtensions
{
    public static IReadOnlyList<ManualResourceKind> PaletteKinds { get; } =
    [
        ManualResourceKind.Gold,
        ManualResourceKind.Stone,
        ManualResourceKind.BerryBush,
        ManualResourceKind.Fish,
        ManualResourceKind.Dolphin,
        ManualResourceKind.Deer,
        ManualResourceKind.BighornSheep,
        ManualResourceKind.Bear,
        ManualResourceKind.Squirrel,
        ManualResourceKind.Sheep,
        ManualResourceKind.Chicken,
        ManualResourceKind.WildPig,
        ManualResourceKind.Elk
    ];

    public static string DisplayName(this ManualResourceKind kind) => kind switch
    {
        ManualResourceKind.Wildlife => "Random wildlife",
        ManualResourceKind.BerryBush => "Berry bush",
        ManualResourceKind.Fish => "Fish",
        ManualResourceKind.Gold => "Gold",
        ManualResourceKind.Stone => "Stone",
        ManualResourceKind.Deer => "Deer",
        ManualResourceKind.Goat => "Goat",
        ManualResourceKind.BighornSheep => "Bighorn sheep",
        ManualResourceKind.Tiger => "Tiger",
        ManualResourceKind.Bear => "Bear",
        ManualResourceKind.Shark => "Shark",
        ManualResourceKind.Squirrel => "Squirrel",
        ManualResourceKind.Sheep => "Sheep",
        ManualResourceKind.BlueBird => "Blue bird",
        ManualResourceKind.Walrus => "Walrus",
        ManualResourceKind.Wolf => "Wolf",
        ManualResourceKind.Chicken => "Chicken",
        ManualResourceKind.Horse => "Horse",
        ManualResourceKind.Dolphin => "Dolphin",
        ManualResourceKind.WildPig => "Wild pig",
        ManualResourceKind.Elk => "Elk",
        _ => kind.ToString()
    };

    public static bool RequiresWater(this ManualResourceKind kind) =>
        kind.IsWaterFauna() || kind == ManualResourceKind.Shark;

    public static bool IsNamedFauna(this ManualResourceKind kind) =>
        kind is ManualResourceKind.Deer or ManualResourceKind.BighornSheep or
            ManualResourceKind.Bear or ManualResourceKind.Squirrel or
            ManualResourceKind.Sheep or ManualResourceKind.Chicken or
            ManualResourceKind.Dolphin or
            ManualResourceKind.WildPig or ManualResourceKind.Elk;

    public static bool IsWaterFauna(this ManualResourceKind kind) =>
        kind is ManualResourceKind.Fish or ManualResourceKind.Dolphin;

    public static bool IsLandFauna(this ManualResourceKind kind) =>
        kind.IsNamedFauna() && !kind.IsWaterFauna();

    public static bool IsLegacyUnsupportedFauna(this ManualResourceKind kind) =>
        kind is ManualResourceKind.Wildlife or ManualResourceKind.Goat or
            ManualResourceKind.Tiger or ManualResourceKind.Shark or
            ManualResourceKind.BlueBird or ManualResourceKind.Walrus or
            ManualResourceKind.Wolf or ManualResourceKind.Horse;

    // This is the complete user-approved fauna palette. Other stable enum values
    // exist only so old numeric project/payload data can be read without mutation.
    public static bool HasDistinctShippedModel(this ManualResourceKind kind) =>
        kind == ManualResourceKind.Fish || kind.IsNamedFauna();

    public static bool IsFauna(this ManualResourceKind kind) =>
        kind.IsLegacyUnsupportedFauna() || kind == ManualResourceKind.Fish ||
            kind.IsNamedFauna();

    public static ManualFaunaCategory FaunaCategory(this ManualResourceKind kind) =>
        kind.IsWaterFauna() ? ManualFaunaCategory.Water :
        kind.IsLandFauna() ? ManualFaunaCategory.Land : ManualFaunaCategory.None;

    // Model-alias identities remain compatibility-only so the editor never offers
    // a label that the installed game renders as another species.
    public static bool IsUserPlaceable(this ManualResourceKind kind) =>
        kind is ManualResourceKind.BerryBush or ManualResourceKind.Gold or ManualResourceKind.Stone or
            ManualResourceKind.Fish || kind.HasDistinctShippedModel();

    public static string WorldUnitToken(this ManualResourceKind kind) => kind switch
    {
        ManualResourceKind.Deer => "Res-Deer",
        ManualResourceKind.BighornSheep => "Res-BighornSheep",
        ManualResourceKind.Bear => "Res-Bear",
        ManualResourceKind.Squirrel => "Res-Squirrel",
        ManualResourceKind.Sheep => "Res-Sheep",
        ManualResourceKind.Chicken => "Res-Chicken",
        ManualResourceKind.Dolphin => "Res-Dolphin",
        ManualResourceKind.WildPig => "Res-WildPig",
        ManualResourceKind.Elk => "Res-Elk",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
            "This resource kind is not emitted as a named world unit.")
    };

    public static string MarkerCode(this ManualResourceKind kind) => kind switch
    {
        ManualResourceKind.Gold => "G",
        ManualResourceKind.Stone => "S",
        ManualResourceKind.BerryBush => "B",
        ManualResourceKind.Fish => "F",
        ManualResourceKind.Deer => "D",
        ManualResourceKind.Goat => "Go",
        ManualResourceKind.BighornSheep => "Bi",
        ManualResourceKind.Tiger => "T",
        ManualResourceKind.Bear => "Be",
        ManualResourceKind.Shark => "Sh",
        ManualResourceKind.Squirrel => "Sq",
        ManualResourceKind.Sheep => "Sp",
        ManualResourceKind.BlueBird => "BB",
        ManualResourceKind.Walrus => "Wa",
        ManualResourceKind.Wolf => "W",
        ManualResourceKind.Chicken => "C",
        ManualResourceKind.Horse => "H",
        ManualResourceKind.Dolphin => "Do",
        ManualResourceKind.WildPig => "P",
        ManualResourceKind.Elk => "E",
        _ => "R"
    };
}
