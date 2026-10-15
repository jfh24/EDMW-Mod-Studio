using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace EDMWMapDesigner.Civilizations;

public sealed class CivilizationDraft
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string Name { get; set; } = "New Civilization";
    public int? SourceCivilizationId { get; set; }
    public int? SourceRowIndex { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.Ordinal);
    public List<CivilizationEntityDraft> Entities { get; set; } = [];

    public CivilizationDraft Clone() => new()
    {
        Id = Id, Name = Name, SourceCivilizationId = SourceCivilizationId,
        SourceRowIndex = SourceRowIndex,
        Properties = new Dictionary<string, string>(Properties, StringComparer.Ordinal),
        Entities = Entities.Select(entity => entity.Clone()).ToList()
    };
}

public sealed class CivilizationEntityDraft
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string Name { get; set; } = "New Unit";
    public CivilizationCategory Category { get; set; } = CivilizationCategory.Units;
    public int? SourceObjectId { get; set; }
    public int? SourceTechnologyId { get; set; }
    public int? SourcePowerId { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.Ordinal);

    public CivilizationEntityDraft Clone() => new()
    {
        Id = Id, Name = Name, Category = Category, SourceObjectId = SourceObjectId,
        SourceTechnologyId = SourceTechnologyId,
        SourcePowerId = SourcePowerId,
        Properties = new Dictionary<string, string>(Properties, StringComparer.Ordinal)
    };
}

public sealed record CivilizationProjectChange(
    string CivilizationName, string? EntityName, CivilizationCategory Category,
    string Key, string Label, string Value);

/// <summary>A design project containing intended overrides, never game database bytes.</summary>
public sealed class CivilizationEditProject
{
    public const int CurrentFormatVersion = 1;
    private const int MaximumDocumentBytes = 128 * 1024 * 1024;
    public const int MaximumEmbeddedModelBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private static readonly JsonSerializerOptions HistoryJsonOptions = CreateHistoryJsonOptions();

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Name { get; set; } = "My Civilization Project";
    public string GamePath { get; set; } = "";
    public List<CivilizationDraft> Civilizations { get; set; } = [];
    public List<CivilizationModelAsset> ModelAssets { get; set; } = [];

    public static CivilizationEditProject CreateDefault() => new()
    {
        // Stored Phoenix civilization IDs and zero-based rows from the inspected build.
        // Dormant Spain / Portugal records and the Any sentinel are not active defaults.
        Civilizations =
        [
            CreateCivilization("China", 4, 3), CreateCivilization("Korea", 5, 4),
            CreateCivilization("Franks", 6, 5), CreateCivilization("United Kingdom", 7, 6),
            CreateCivilization("Germany", 8, 7), CreateCivilization("Russia", 9, 8),
            CreateCivilization("France", 10, 9), CreateCivilization("England", 11, 10),
            CreateCivilization("USA", 12, 11)
        ]
    };

    private static CivilizationDraft CreateCivilization(string name, int sourceId, int rowIndex) => new()
    {
        Name = name, SourceCivilizationId = sourceId, SourceRowIndex = rowIndex
    };

    public CivilizationEditProject Clone() => new()
    {
        FormatVersion = FormatVersion, Name = Name, GamePath = GamePath,
        Civilizations = Civilizations.Select(civilization => civilization.Clone()).ToList(),
        ModelAssets = ModelAssets.Select(asset => asset.Clone()).ToList()
    };

    public string Serialize()
    {
        Validate();
        var json = JsonSerializer.Serialize(CreateOrderedDocument(), JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
            throw new InvalidDataException("The civilization project exceeds the 128 MB document limit.");
        return json;
    }

    private CivilizationEditProject CreateOrderedDocument()
    {
        // Serialization only reads model bytes. Avoid duplicating large embedded arrays.
        var ordered = new CivilizationEditProject
        {
            FormatVersion = FormatVersion, Name = Name, GamePath = GamePath,
            Civilizations = Civilizations.Select(civilization => civilization.Clone()).ToList(),
            ModelAssets = ModelAssets.ToList()
        };
        foreach (var civilization in ordered.Civilizations)
        {
            civilization.Properties = OrderProperties(civilization.Properties);
            foreach (var entity in civilization.Entities)
                entity.Properties = OrderProperties(entity.Properties);
        }
        return ordered;
    }

    internal string SerializeHistoryMetadata()
    {
        Validate();
        var json = JsonSerializer.Serialize(CreateOrderedDocument(), HistoryJsonOptions);
        // Include base64 size in the file budget without allocating its representation.
        var estimatedDocumentBytes = Encoding.UTF8.GetByteCount(json) +
            ModelAssets.Sum(asset => ((asset.Data.LongLength + 2) / 3) * 4 + 32);
        if (estimatedDocumentBytes > MaximumDocumentBytes)
            throw new InvalidDataException("The civilization project exceeds the 128 MB document limit.");
        return json;
    }

    internal static CivilizationEditProject RestoreHistoryMetadata(string json,
        IReadOnlyDictionary<string, byte[]> modelData)
    {
        var project = JsonSerializer.Deserialize<CivilizationEditProject>(json, JsonOptions)
            ?? throw new InvalidDataException("The civilization history snapshot is empty.");
        foreach (var asset in project.ModelAssets)
        {
            if (!modelData.TryGetValue(asset.Id, out var data))
                throw new InvalidDataException("The civilization history snapshot is missing model data.");
            asset.Data = (byte[])data.Clone();
        }
        project.Validate();
        return project;
    }

    private static JsonSerializerOptions CreateHistoryJsonOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(CivilizationModelAsset)) return;
            foreach (var property in info.Properties)
                if (property.Name == "data") property.ShouldSerialize = (_, _) => false;
        });
        return new JsonSerializerOptions(JsonOptions) { TypeInfoResolver = resolver };
    }

    public static CivilizationEditProject Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
            throw new InvalidDataException("The civilization project exceeds the 128 MB document limit.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("formatVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var formatVersion) || formatVersion != CurrentFormatVersion)
                throw new InvalidDataException("The file is not a supported civilization draft project (version 1).");
            var project = JsonSerializer.Deserialize<CivilizationEditProject>(json, JsonOptions)
                ?? throw new InvalidDataException("The civilization project is empty.");
            project.Validate();
            return project;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The civilization project contains invalid JSON or field data.", exception);
        }
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = Serialize();
        if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
            throw new InvalidDataException("The civilization project exceeds the 128 MB document limit.");
        var resolved = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(resolved)
            ?? throw new InvalidOperationException("The project path has no parent folder.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(resolved)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, resolved, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static CivilizationEditProject Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var resolved = Path.GetFullPath(path);
        var information = new FileInfo(resolved);
        if (!information.Exists) throw new FileNotFoundException("The civilization project was not found.", resolved);
        if (information.Length > MaximumDocumentBytes)
            throw new InvalidDataException("The civilization project exceeds the 128 MB document limit.");
        return Deserialize(File.ReadAllText(resolved, Encoding.UTF8));
    }

    public void Validate()
    {
        if (FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException("The civilization project version is unsupported.");
        ValidateName(Name, "Project");
        if (GamePath is null) throw new InvalidDataException("The game folder value cannot be null.");
        if (Civilizations is null) throw new InvalidDataException("The civilization list cannot be null.");
        if (ModelAssets is null) throw new InvalidDataException("The model asset list cannot be null.");
        var ids = new HashSet<Guid>();
        var modelIds = new HashSet<Guid>();
        var embeddedBytes = 0L;
        foreach (var asset in ModelAssets)
        {
            if (asset is null) throw new InvalidDataException("The project contains a null model asset.");
            asset.Validate();
            ValidateId(asset.Id, ids, "Model asset");
            modelIds.Add(Guid.Parse(asset.Id));
            embeddedBytes += asset.Data.Length;
            if (embeddedBytes > MaximumEmbeddedModelBytes)
                throw new InvalidDataException("Embedded replacement models exceed the 64 MB total limit.");
        }
        var civilizationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var civilization in Civilizations)
        {
            if (civilization is null) throw new InvalidDataException("The project contains a null civilization.");
            ValidateId(civilization.Id, ids, "Civilization");
            ValidateName(civilization.Name, "Civilization");
            if (!civilizationNames.Add(civilization.Name.Trim()))
                throw new InvalidDataException($"Civilization name '{civilization.Name}' occurs more than once.");
            if (civilization.SourceCivilizationId is <= 0 || civilization.SourceRowIndex is < 0)
                throw new InvalidDataException($"{civilization.Name} has an invalid source civilization identity.");
            ValidateProperties(civilization.Properties,
                Enum.GetValues<CivilizationCategory>().Where(category => !CivilizationSchema.IsEntityCategory(category))
                    .SelectMany(CivilizationSchema.GetFields), civilization.Name);
            if (civilization.Entities is null)
                throw new InvalidDataException($"{civilization.Name} has a null entity list.");
            foreach (var entity in civilization.Entities)
            {
                if (entity is null) throw new InvalidDataException($"{civilization.Name} contains a null entity.");
                ValidateId(entity.Id, ids, "Entity");
                ValidateName(entity.Name, "Entity");
                if (!CivilizationSchema.IsEntityCategory(entity.Category))
                    throw new InvalidDataException($"{entity.Name} has an invalid entity category.");
                if (entity.SourceObjectId is < 0 || entity.SourceTechnologyId is < 0 || entity.SourcePowerId is < 0 or >= 60 ||
                    (entity.SourceObjectId is not null && (entity.SourceTechnologyId is not null || entity.SourcePowerId is not null)) ||
                    (entity.SourcePowerId is not null && entity.Category != CivilizationCategory.Abilities) ||
                    (entity.SourceTechnologyId is not null && entity.Category is not (CivilizationCategory.Technologies or CivilizationCategory.Abilities)))
                    throw new InvalidDataException($"{entity.Name} has an invalid source object ID.");
                ValidateProperties(entity.Properties, CivilizationSchema.GetFields(entity.Category), entity.Name);
                _ = CivilizationRuntimeEffects.RulesFor(civilization, entity);
                CivilizationUnitEffects.Validate(entity);
                ValidateRangePair(entity.Properties, "unit.minimumRange", "unit.attackRange", entity.Name);
                ValidateRangePair(entity.Properties, "building.minimumRange", "building.attackRange", entity.Name);
                ValidateModelReference(entity.Properties, "unit.modelAsset", modelIds, entity.Name);
                ValidateModelReference(entity.Properties, "building.modelAsset", modelIds, entity.Name);
            }
        }
    }

    public CivilizationModelAsset? GetModelAsset(string? assetId)
    {
        if (!Guid.TryParse(assetId, out var guid)) return null;
        return ModelAssets.FirstOrDefault(asset => Guid.TryParse(asset.Id, out var candidate) && candidate == guid);
    }

    public IReadOnlyList<CivilizationProjectChange> GetChanges()
    {
        var changes = new List<CivilizationProjectChange>();
        foreach (var civilization in Civilizations)
        {
            if (civilization.SourceCivilizationId is null)
                changes.Add(new(civilization.Name, null, CivilizationCategory.Overview,
                    "civilization.create", "New civilization", civilization.Name));
            foreach (var category in Enum.GetValues<CivilizationCategory>()
                .Where(category => !CivilizationSchema.IsEntityCategory(category)))
                AddChanges(changes, civilization.Name, null, category, civilization.Properties);
            foreach (var entity in civilization.Entities)
            {
                if (entity.SourceObjectId is null && entity.SourceTechnologyId is null && entity.SourcePowerId is null)
                    changes.Add(new(civilization.Name, entity.Name, entity.Category,
                        "entity.create", "New definition", entity.Name));
                AddChanges(changes, civilization.Name, entity.Name, entity.Category, entity.Properties);
                foreach (var effect in CivilizationUnitEffects.Read(entity))
                    changes.Add(new(civilization.Name, entity.Name, entity.Category,
                        CivilizationUnitEffects.PropertyKey, "Custom effect (prototype)", CivilizationUnitEffects.Describe(effect)));
            }
        }
        return changes;
    }

    private static void AddChanges(List<CivilizationProjectChange> changes, string civilization,
        string? entity, CivilizationCategory category, Dictionary<string, string> properties)
    {
        foreach (var field in CivilizationSchema.GetFields(category))
            if (properties.TryGetValue(field.Key, out var value) && !string.IsNullOrWhiteSpace(value))
                changes.Add(new(civilization, entity, category, field.Key, field.Label, value));
    }

    private static Dictionary<string, string> OrderProperties(Dictionary<string, string> properties) =>
        properties.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static void ValidateId(string id, HashSet<Guid> ids, string label)
    {
        if (!Guid.TryParse(id, out var guid) || guid == Guid.Empty || !ids.Add(guid))
            throw new InvalidDataException($"{label} IDs must be unique, nonempty GUIDs.");
    }

    private static void ValidateName(string name, string label)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
            throw new InvalidDataException($"{label} name must contain 1 to 200 characters.");
    }

    private static void ValidateProperties(Dictionary<string, string> properties,
        IEnumerable<CivilizationFieldDefinition> definitions, string owner)
    {
        if (properties is null) throw new InvalidDataException($"{owner} has a null property collection.");
        var fields = definitions.ToDictionary(field => field.Key, StringComparer.Ordinal);
        foreach (var (key, value) in properties)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 200 || value is null || value.Length > 1_000_000)
                throw new InvalidDataException($"{owner} has an invalid property key or value.");
            // Preserve extension keys so future mappings do not destroy existing draft data.
            if (fields.TryGetValue(key, out var field) && CivilizationSchema.ValidateValue(field, value) is { } error)
                throw new InvalidDataException($"{owner}: {error}");
        }
    }

    private static void ValidateRangePair(Dictionary<string, string> properties, string minimumKey,
        string maximumKey, string owner)
    {
        if (properties.TryGetValue(minimumKey, out var minimum) &&
            properties.TryGetValue(maximumKey, out var maximum) &&
            double.TryParse(minimum, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var lower) &&
            double.TryParse(maximum, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var upper) && lower > upper)
            throw new InvalidDataException($"{owner}: minimum attack range cannot exceed maximum attack range.");
    }

    private static void ValidateModelReference(Dictionary<string, string> properties, string key,
        HashSet<Guid> modelIds, string owner)
    {
        if (properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) &&
            (!Guid.TryParse(value, out var guid) || !modelIds.Contains(guid)))
            throw new InvalidDataException($"{owner}: the replacement model must reference an embedded project asset.");
    }
}
