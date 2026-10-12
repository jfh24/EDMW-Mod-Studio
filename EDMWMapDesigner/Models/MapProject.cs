using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EDMWMapDesigner.Models;

public sealed class MapProject
{
    public const sbyte AutomaticElevation = sbyte.MaxValue;
    // Reserved elevation marker for authored cliff faces; valid only on Mountain cells.
    public const sbyte CliffElevation = 126;
    public const int MinGridSize = 8;
    public const int MaxGridSize = 512;

    public string Name { get; set; } = "My Custom Map";
    public string Author { get; set; } = Environment.UserName;
    public int GridSize { get; set; }
    public int Variance { get; set; } = 20;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GenerationSeed { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PreserveCoastline { get; set; }
    public List<ManualResource> ManualResources { get; set; } = [];
    public List<ManualPlayerStart> ManualPlayerStarts { get; set; } = [];
    public int[] Cells { get; set; } = [];
    public byte[] ComponentRoles { get; set; } = [];
    public byte[] RampMask { get; set; } = [];
    // Preserve authored elevation values while making their slopes unconditional cliffs.
    public byte[] CliffMask { get; set; } = [];
    [JsonIgnore]
    public sbyte[] ElevationMask { get; set; } = [];

    [JsonPropertyName("elevationMask")]
    public int[] SerializedElevationMask
    {
        get => ElevationMask.Select(value => (int)value).ToArray();
        set => ElevationMask = value is null
            ? []
            : value.Select(value => value is >= -100 and <= 100 or AutomaticElevation or CliffElevation
                ? checked((sbyte)value)
                : throw new InvalidDataException(
                    "The project contains an invalid elevation value."))
                .ToArray();
    }

    [JsonIgnore]
    public TerrainKind this[int x, int y]
    {
        get => (TerrainKind)Cells[y * GridSize + x];
        set
        {
            var index = y * GridSize + x;
            Cells[index] = (int)value;
            if (value != TerrainKind.Mountain && index < ElevationMask.Length &&
                ElevationMask[index] == CliffElevation)
            {
                ElevationMask[index] = AutomaticElevation;
                if (index < CliffMask.Length) CliffMask[index] = 0;
            }
        }
    }

    public static MapProject CreateDefault(int size = 512)
    {
        ValidateSize(size);
        return new MapProject
        {
            GridSize = size,
            Cells = Enumerable.Repeat((int)TerrainKind.DeepWater, size * size).ToArray(),
            ComponentRoles = new byte[size * size],
            RampMask = new byte[size * size],
            CliffMask = new byte[size * size],
            ElevationMask = Enumerable.Repeat(AutomaticElevation, size * size).ToArray()
        };
    }

    public void SetCell(int x, int y, TerrainKind terrain)
    {
        if (x < 0 || y < 0 || x >= GridSize || y >= GridSize) return;
        this[x, y] = terrain;
        ManualResources.RemoveAll(resource =>
            resource.X == x && resource.Y == y && resource.Kind.RequiresWater() == terrain.IsLand());
    }

    public MapProject Clone() => new()
    {
        Name = Name,
        Author = Author,
        GridSize = GridSize,
        Variance = Variance,
        GenerationSeed = GenerationSeed,
        PreserveCoastline = PreserveCoastline,
        ManualResources = ManualResources.Select(resource => resource.Clone()).ToList(),
        ManualPlayerStarts = ManualPlayerStarts.Select(start => start.Clone()).ToList(),
        Cells = (int[])Cells.Clone(),
        ComponentRoles = (byte[])ComponentRoles.Clone(),
        RampMask = (byte[])RampMask.Clone(),
        CliffMask = (byte[])CliffMask.Clone(),
        ElevationMask = (sbyte[])ElevationMask.Clone()
    };

    public void Resize(int newSize)
    {
        ValidateSize(newSize);
        var sourceSize = GridSize;
        var source = Cells;
        var sourceRoles = ComponentRoles;
        var sourceElevation = ElevationMask;
        var sourceRamps = RampMask;
        var sourceCliffs = CliffMask;
        var resized = new int[newSize * newSize];
        var resizedRoles = new byte[newSize * newSize];
        var resizedElevation = new sbyte[newSize * newSize];
        var resizedRamps = new byte[newSize * newSize];
        var resizedCliffs = new byte[newSize * newSize];
        for (var y = 0; y < newSize; y++)
            for (var x = 0; x < newSize; x++)
            {
                var oldX = Math.Min(sourceSize - 1, x * sourceSize / newSize);
                var oldY = Math.Min(sourceSize - 1, y * sourceSize / newSize);
                resized[y * newSize + x] = source[oldY * sourceSize + oldX];
                resizedRoles[y * newSize + x] = sourceRoles[oldY * sourceSize + oldX];
                resizedElevation[y * newSize + x] = sourceElevation[oldY * sourceSize + oldX];
                resizedRamps[y * newSize + x] = sourceRamps[oldY * sourceSize + oldX];
                resizedCliffs[y * newSize + x] = sourceCliffs[oldY * sourceSize + oldX];
            }
        GridSize = newSize;
        Cells = resized;
        ComponentRoles = resizedRoles;
        ElevationMask = resizedElevation;
        RampMask = resizedRamps;
        CliffMask = resizedCliffs;
        foreach (var resource in ManualResources)
        {
            resource.X = Math.Min(newSize - 1, resource.X * newSize / sourceSize);
            resource.Y = Math.Min(newSize - 1, resource.Y * newSize / sourceSize);
        }
        ManualResources.RemoveAll(resource =>
            resource.Kind.RequiresWater() == this[resource.X, resource.Y].IsLand());
        foreach (var start in ManualPlayerStarts)
        {
            start.X = Math.Min(newSize - 1, start.X * newSize / sourceSize);
            start.Y = Math.Min(newSize - 1, start.Y * newSize / sourceSize);
        }
        ManualPlayerStarts.RemoveAll(start =>
            !IsSafePlayerStartTerrain((TerrainKind)Cells[start.Y * newSize + start.X]));
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var resolved = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(resolved)
            ?? throw new InvalidOperationException("The project path has no parent folder.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(resolved)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, resolved, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static MapProject Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var resolved = Path.GetFullPath(path);
        if (!File.Exists(resolved)) throw new FileNotFoundException("The project file was not found.", resolved);
        var project = JsonSerializer.Deserialize<MapProject>(File.ReadAllText(resolved), JsonOptions)
            ?? throw new InvalidDataException("The project file is empty.");
        project.Validate();
        return project;
    }

    public void Validate()
    {
        ValidateSize(GridSize);
        if (Cells.Length != GridSize * GridSize)
            throw new InvalidDataException("The cell count does not match the grid size.");
        if (ComponentRoles is null || ComponentRoles.Length == 0)
            ComponentRoles = new byte[GridSize * GridSize];
        if (ComponentRoles.Length != GridSize * GridSize)
            throw new InvalidDataException("The component-role count does not match the grid size.");
        if (RampMask is null || RampMask.Length == 0)
            RampMask = new byte[GridSize * GridSize];
        if (RampMask.Length != GridSize * GridSize || RampMask.Any(value => value > 1))
            throw new InvalidDataException("The manual-ramp mask is invalid.");
        if (CliffMask is null || CliffMask.Length == 0)
            CliffMask = new byte[GridSize * GridSize];
        if (CliffMask.Length != GridSize * GridSize || CliffMask.Any(value => value > 1))
            throw new InvalidDataException("The forced-cliff mask is invalid.");
        if (ElevationMask is null || ElevationMask.Length == 0)
            ElevationMask = Enumerable.Repeat(AutomaticElevation, GridSize * GridSize).ToArray();
        if (ElevationMask.Length != GridSize * GridSize)
            throw new InvalidDataException("The elevation-mask count does not match the grid size.");
        if (ElevationMask.Any(value => value is not (>= -100 and <= 100 or AutomaticElevation or CliffElevation)))
            throw new InvalidDataException("The project contains an invalid elevation value.");
        if (ComponentRoles.Any(v => !Enum.IsDefined((SemanticAnchorRole)v)))
            throw new InvalidDataException("The project contains an unknown semantic anchor role.");
        if (Cells.Any(v => !Enum.IsDefined((TerrainKind)v)))
            throw new InvalidDataException("The project contains an unknown terrain value.");
        for (var index = 0; index < Cells.Length; index++)
        {
            if (ElevationMask[index] == AutomaticElevation) CliffMask[index] = 0;
            if (ElevationMask[index] == CliffElevation && Cells[index] != (int)TerrainKind.Mountain)
                throw new InvalidDataException("Cliffs must use mountain terrain.");
        }
        Variance = Math.Clamp(Variance, 0, 50);
        ManualResources ??= [];
        if (ManualResources.Any(resource => !Enum.IsDefined(resource.Kind) ||
            resource.X < 0 || resource.Y < 0 || resource.X >= GridSize || resource.Y >= GridSize ||
            resource.Kind.RequiresWater() == ((TerrainKind)Cells[resource.Y * GridSize + resource.X]).IsLand()))
            throw new InvalidDataException("The project contains an invalid manual resource placement.");
        ManualPlayerStarts ??= [];
        if (ManualPlayerStarts.Any(start =>
                start.PlayerSlot is < ManualPlayerStart.MinimumSlot or > ManualPlayerStart.MaximumSlot ||
                start.X < 0 || start.Y < 0 || start.X >= GridSize || start.Y >= GridSize ||
                !IsSafePlayerStartTerrain((TerrainKind)Cells[start.Y * GridSize + start.X])) ||
            ManualPlayerStarts.Select(start => start.PlayerSlot).Distinct().Count() != ManualPlayerStarts.Count)
            throw new InvalidDataException("The project contains an invalid manual player start.");
    }

    public static bool IsSafePlayerStartTerrain(TerrainKind terrain) =>
        terrain is TerrainKind.Beach or TerrainKind.Grassland or TerrainKind.Hills or TerrainKind.Desert;

    private static void ValidateSize(int size)
    {
        if (size is < MinGridSize or > MaxGridSize)
            throw new ArgumentOutOfRangeException(nameof(size), $"Grid size must be {MinGridSize}-{MaxGridSize}.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
