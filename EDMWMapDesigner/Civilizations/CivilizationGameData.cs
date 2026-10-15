using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;

namespace EDMWMapDesigner.Civilizations;

internal sealed record CivilizationCatalogAvailability(int FirstAge, int LastAge, IReadOnlyList<int> CivilizationRows);

/// <summary>Current, read-only database values and civilization-owned catalog entries.</summary>
public sealed class CivilizationGameData
{
    private readonly byte[] _objects;
    private readonly byte[] _civilizations;
    private readonly byte[] _technologies;
    private readonly CivilizationGameCatalog _catalog;
    private readonly IReadOnlyList<CivilizationTechnologyRecord> _technologyRecords;
    private readonly IReadOnlyDictionary<int, CivilizationTechnologyRecord> _technologyById;
    private readonly Dictionary<(string Library, uint Id), string?> _localizedText = [];
    private readonly Lazy<IReadOnlyDictionary<int, string>> _familyNames;
    private readonly Lazy<IReadOnlyDictionary<int, string>> _weaponNames;
    private readonly Lazy<IReadOnlyList<CivilizationAgeName>> _ageNames;
    private readonly CivilizationAbilityData _abilityData;
    private readonly CivilizationSupplementalData _supplementalData;
    private readonly Lazy<CivilizationGameAssets> _sourceAssets;

    public string GamePath { get; }

    internal IReadOnlyDictionary<int, CivilizationCatalogAvailability> GetTargetAvailability()
    {
        var membership = _catalog.Items.ToDictionary(item => item.SourceId, _ => new List<int>());
        foreach (var row in Enumerable.Range(3, 9))
        foreach (var category in new[] { CivilizationCategory.Units, CivilizationCategory.Buildings })
        foreach (var id in CivilizationTechnologyBindings.GetCivilizationObjectIds(_objects, _technologyRecords, row, category))
            if (membership.TryGetValue(id, out var rows)) rows.Add(row);
        var result = new Dictionary<int, CivilizationCatalogAvailability>();
        foreach (var item in _catalog.Items)
        {
            var technologyId = CivilizationObjectBindings.ReadTechnologyId(_objects, item.SourceId, item.Category);
            if (technologyId <= int.MaxValue && _technologyById.TryGetValue((int)technologyId, out var technology))
                result[item.SourceId] = new(technology.StartAge, technology.EndAge, membership[item.SourceId].AsReadOnly());
        }
        return result;
    }

    public CivilizationGameData(string gamePath, CivilizationGameCatalog catalog)
    {
        GamePath = Path.GetFullPath(gamePath);
        _catalog = catalog;
        _objects = ReadDatabase("dbobjects.dat");
        _civilizations = ReadDatabase("dbphoenixcivtable.dat");
        _technologies = ReadDatabase("dbtechtree.dat");
        _technologyRecords = CivilizationTechnologyBindings.ReadRecords(_technologies);
        _technologyById = _technologyRecords.ToDictionary(record => record.TechId);
        _sourceAssets = new(() => new CivilizationGameAssets(GamePath));
        _familyNames = new(() => ReadReferenceNames("dbfamily.dat", CivilizationObjectDetails.ReadFamilyNames));
        _weaponNames = new(() => ReadReferenceNames("dbweapontohit.dat", CivilizationObjectDetails.ReadWeaponNames));
        _ageNames = new(() => CivilizationAgeNames.ReadNames(_technologyRecords, id => ReadLocalizedText("Language2.dll", id)));
        _abilityData = new(_objects, _technologyRecords, ReadOptionalDatabase("dbevents.dat"),
            ReadOptionalDatabase("dbeffects.dat"), ReadOptionalDatabase("dbcalamity.dat"), _civilizations,
            ReadOptionalAsset("db\\dbcivpowers.dat"));
        _supplementalData = new(GamePath, _objects, _civilizations, _technologyRecords, catalog, ReadLocalizedText);
    }

    public IReadOnlyDictionary<string, string> GetValues(CivilizationDraft civilization)
    {
        if (civilization.SourceRowIndex is not int row) return new Dictionary<string, string>();
        var values = CivilizationCivilizationBindings.ReadValues(_civilizations, row)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (values.TryGetValue("assets.nameStringId", out var identifier) && uint.TryParse(identifier, out var stringId))
            values["overview.displayName"] = ReadLocalizedText("Language.dll", stringId) ?? values["overview.internalName"];
        foreach (var pair in _supplementalData.GetValues(civilization)) values[pair.Key] = pair.Value;
        var initializationId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(_civilizations.AsSpan(4 + row * 344 + 300, 4));
        if (_technologyById.TryGetValue(initializationId, out var initializer))
        {
            var effects = initializer.AdditionalTechnologyIds.Where(_technologyById.ContainsKey)
                .Select(id => _technologyById[id])
                .Select(record => TechnologyReferenceName(record.TechId) + "\n" +
                    _abilityData.GetValues(record, CivilizationCategory.Technologies).GetValueOrDefault("technology.effectOperations", ""));
            values["overview.bonuses"] = string.Join("\n\n", effects);
        }
        return values;
    }

    public IReadOnlyDictionary<string, string> GetValues(CivilizationEntityDraft entity)
    {
        if (entity.Category == CivilizationCategory.Abilities && entity.SourcePowerId is int power)
        {
            var powerValues = _abilityData.GetPowerValues(power).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            powerValues["ability.displayName"] = GetDisplayName(entity);
            return powerValues;
        }
        if (entity.Category is CivilizationCategory.Technologies or CivilizationCategory.Abilities && entity.SourceTechnologyId is int sourceTechnology)
            return GetTechnologyValues(sourceTechnology, entity.Category);
        if (entity.SourceObjectId is not int row || entity.Category is not (CivilizationCategory.Units or CivilizationCategory.Buildings))
            return new Dictionary<string, string>();
        var values = CivilizationObjectBindings.ReadValues(_objects, row, entity.Category)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var pair in CivilizationObjectDetails.ReadValues(_objects, row, entity.Category)) values[pair.Key] = pair.Value;
        var technologyId = CivilizationObjectBindings.ReadTechnologyId(_objects, row, entity.Category);
        if (technologyId <= int.MaxValue && _technologyById.TryGetValue((int)technologyId, out var technology))
            foreach (var pair in CivilizationTechnologyBindings.ReadObjectValues(technology, entity.Category))
                values[pair.Key] = pair.Value;
        var prefix = entity.Category == CivilizationCategory.Units ? "unit" : "building";
        if (technologyId <= int.MaxValue && _technologyById.TryGetValue((int)technologyId, out var ageSource))
            values[prefix + ".age"] = FormatAgeRange(ageSource.StartAge, ageSource.EndAge);
        if (technologyId <= int.MaxValue && _technologyById.TryGetValue((int)technologyId, out var sourceNode))
            foreach (var pair in _abilityData.GetValues(sourceNode, entity.Category)) values[pair.Key] = pair.Value;
        values[prefix + ".displayName"] = GetDisplayName(entity);
        foreach (var reference in CivilizationObjectDetails.ReadLocalizationReferences(_objects, row, entity.Category))
            if (ReadLocalizedText(reference.Library, reference.StringId) is { } text)
            {
                var suffix = reference.Key[(reference.Key.IndexOf('.') + 1)..];
                var displayKey = suffix switch
                {
                    "unitStringId1" => "description", "unitStringId2" => "descriptionDetail",
                    "tooltipStringId" => "tooltip", "appendNameStringId" => "appendName", _ => "displayName"
                };
                values[prefix + "." + displayKey] = text;
            }
        if (int.TryParse(values[prefix + ".familyId"], out var family) && _familyNames.Value.TryGetValue(family, out var familyName))
            values[prefix + ".familyName"] = familyName;
        if (int.TryParse(values[prefix + ".attackType"], out var weapon) && _weaponNames.Value.TryGetValue(weapon, out var weaponName))
            values[prefix + ".weaponClass"] = weaponName;
        if (technologyId <= int.MaxValue && _technologyById.TryGetValue((int)technologyId, out var production))
            values[prefix + (entity.Category == CivilizationCategory.Units ? ".trainedAt" : ".constructedBy")] =
                string.Join("\n", production.ProducerObjectIds.Select(ObjectReferenceName));
        var commands = CivilizationObjectDetails.ReadCommandTechnologyIds(_objects, row, entity.Category)
            .Concat(CivilizationObjectDetails.ReadActionTechnologyIds(_objects, row, entity.Category).Where(id => id <= int.MaxValue).Select(id => (int)id))
            .Where(_technologyById.ContainsKey).Distinct().ToArray();
        values[prefix + ".commands"] = string.Join("\n", commands.Select(TechnologyReferenceName));
        if (entity.Category == CivilizationCategory.Buildings)
        {
            var productionIds = commands.ToHashSet();
            values["building.producedUnits"] = string.Join("\n", _catalog.Items.Where(item => item.Category == CivilizationCategory.Units &&
                CivilizationObjectBindings.ReadTechnologyId(_objects, item.SourceId, item.Category) is var id && id <= int.MaxValue && productionIds.Contains((int)id))
                .Select(item => ObjectReferenceName(item.SourceId)));
            values["building.research"] = string.Join("\n", commands.Where(id => CivilizationTechnologyBindings.IsResearchRecord(_technologyById[id])).Select(TechnologyReferenceName));
        }
        values[prefix + ".abilities"] = string.Join("\n", commands.Where(id => CivilizationTechnologyBindings.IsAbilityRecord(_technologyById[id])).Select(TechnologyReferenceName));
        return values;
    }

    private string ObjectReferenceName(int row) => _catalog.Items.FirstOrDefault(item => item.SourceId == row) is { } item
        ? $"{item.Name} [{row.ToString(CultureInfo.InvariantCulture)}]" : $"Object {row.ToString(CultureInfo.InvariantCulture)}";

    private string TechnologyReferenceName(int id) => _technologyById.TryGetValue(id, out var technology)
        ? $"{ReadLocalizedText("Language2.dll", technology.NameStringId) ?? technology.Identifier} [{id.ToString(CultureInfo.InvariantCulture)}]" : $"Technology {id.ToString(CultureInfo.InvariantCulture)}";

    private IReadOnlyDictionary<int, string> ReadReferenceNames(string name, Func<byte[], IReadOnlyDictionary<int, string>> reader) =>
        File.Exists(Path.Combine(GamePath, "Data", "db", name)) ? reader(ReadDatabase(name)) : new Dictionary<int, string>();

    public IReadOnlyList<CivilizationEntityDraft> GetRoster(CivilizationDraft civilization, CivilizationCategory category)
    {
        if (category is CivilizationCategory.Technologies or CivilizationCategory.Abilities) return GetTechnologyRoster(civilization, category);
        if (civilization.SourceRowIndex is not int row || category is not (CivilizationCategory.Units or CivilizationCategory.Buildings))
            return [];
        var civilizationValues = CivilizationCivilizationBindings.ReadValues(_civilizations, row);
        var lower = int.Parse(civilizationValues["age.initialAge"], CultureInfo.InvariantCulture);
        var upper = int.Parse(civilizationValues["age.finalAge"], CultureInfo.InvariantCulture);
        var ids = CivilizationTechnologyBindings.GetCivilizationObjectIds(_objects, _technologyRecords, row,
            category, lower, upper).ToHashSet();
        return _catalog.Items.Where(item => item.Category == category && ids.Contains(item.SourceId))
            .Where(item =>
            {
                var technologyId = CivilizationObjectBindings.ReadTechnologyId(_objects, item.SourceId, category);
                return technologyId <= int.MaxValue && _technologyById.TryGetValue((int)technologyId, out var technology) &&
                    technology.StartAge <= upper && technology.EndAge >= lower;
            })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new CivilizationEntityDraft
            {
                Id = CatalogIdentity(civilization.Id, category, item.SourceId),
                Name = ReadLocalizedText("Language2.dll", System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_objects.AsSpan(4 + item.SourceId * 1936 + 296, 4))) ?? item.Name,
                Category = category, SourceObjectId = item.SourceId
            }).ToArray();
    }

    public IReadOnlyDictionary<string, string> GetTechnologyValues(int techId, CivilizationCategory category = CivilizationCategory.Technologies)
    {
        if (!_technologyById.TryGetValue(techId, out var technology)) return new Dictionary<string, string>();
        var prefix = category == CivilizationCategory.Abilities ? "ability" : "technology";
        var values = CivilizationTechnologyBindings.ReadObjectValues(technology, category)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var pair in _abilityData.GetValues(technology, category)) values[pair.Key] = pair.Value;
        values[prefix + ".age"] = FormatAgeRange(technology.StartAge, technology.EndAge);
        values[prefix + ".displayName"] = ReadLocalizedText("Language2.dll", technology.NameStringId) ?? technology.Identifier;
        if (ReadLocalizedText("Language.dll", technology.ToolTipStringId) is { } description)
            values[prefix + ".description"] = description;
        foreach (var id in new[] { technology.PrerequisiteStringId1, technology.PrerequisiteStringId2 })
            if (ReadLocalizedText("Language.dll", id) is { } text)
                values[prefix + ".prerequisiteDescription"] = values.GetValueOrDefault(prefix + ".prerequisiteDescription", "") + text + "\n";
        return values;
    }

    public IReadOnlyList<CivilizationEntityDraft> GetTechnologyRoster(CivilizationDraft civilization, CivilizationCategory category = CivilizationCategory.Technologies)
    {
        if (civilization.SourceRowIndex is not int row) return [];
        var values = CivilizationCivilizationBindings.ReadValues(_civilizations, row);
        var lower = int.Parse(values["age.initialAge"], CultureInfo.InvariantCulture);
        var upper = int.Parse(values["age.finalAge"], CultureInfo.InvariantCulture);
        var records = category == CivilizationCategory.Abilities
            ? _abilityData.GetRoster(row, lower, upper)
            : CivilizationTechnologyBindings.GetCivilizationTechnologyRecords(_objects, _technologyRecords, row, lower, upper);
        var entities = records
            .Select(technology => new CivilizationEntityDraft
            {
                Id = CatalogIdentity(civilization.Id, category, technology.TechId),
                Name = ReadLocalizedText("Language2.dll", technology.NameStringId) ?? technology.Identifier,
                Category = category, SourceTechnologyId = technology.TechId
            }).ToList();
        if (category == CivilizationCategory.Abilities)
            foreach (var power in _abilityData.GetPowerRecords(row))
                entities.Add(new()
                {
                    Id = CatalogIdentity(civilization.Id, category, -1 - power.Index),
                    Name = ReadLocalizedText("Language.dll", power.NameStringId) ?? $"Power {power.Index}",
                    Category = category, SourcePowerId = power.Index, SourceTechnologyId = power.ActivationTechnologyId
                });
        return entities.OrderBy(entity => entity.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool CanWrite(CivilizationCategory category, string key, bool entity) => entity
        ? CivilizationObjectBindings.CanWrite(category, key) || CivilizationTechnologyBindings.CanWriteObject(category, key)
        : CivilizationCivilizationBindings.CanWrite(key);

    public bool CanWrite(CivilizationEntityDraft entity, string key) =>
        (CivilizationRuntimeEffects.IsKey(key) && entity.Category == CivilizationCategory.Technologies &&
            entity.SourcePowerId is null && _technologyRecords.Any(record => record.TechId == entity.SourceTechnologyId && CivilizationTechnologyBindings.IsResearchRecord(record))) ||
        (entity.SourcePowerId is null && CanWrite(entity.Category, key, true)) ||
        entity.SourceTechnologyId is int id && _abilityData.CanWriteEffect(id, entity.Category, key);

    public IReadOnlyList<CivilizationFieldDefinition> GetEffectFields(CivilizationEntityDraft entity) =>
        entity.SourceTechnologyId is int id ? _abilityData.GetEffectFields(id, entity.Category) : [];

    public IReadOnlyList<CivilizationAgeProgressionItem> GetAgeProgressionItems(CivilizationDraft civilization) =>
        CivilizationAgeProgression.GetItems(_objects, _civilizations, _technologyRecords, _catalog, civilization,
            id => ReadLocalizedText("Language2.dll", id), id => ReadLocalizedText("Language2.dll", id));

    public IReadOnlyList<CivilizationAgeProgressionItem> GetAgeProgressionCandidates(CivilizationDraft civilization) =>
        CivilizationAgeProgression.GetCandidates(_objects, _civilizations, _technologyRecords, _catalog, civilization,
            id => ReadLocalizedText("Language2.dll", id), id => ReadLocalizedText("Language2.dll", id));

    public IReadOnlyList<CivilizationAgeName> GetAgeNames() => _ageNames.Value;

    public string GetAgeName(int id) => GetAgeNames().FirstOrDefault(age => age.Id == id)?.Name ?? CivilizationAgeNames.GetName(id);

    public string FormatAgeRange(int first, int last) => CivilizationAgeNames.FormatRange(first, last, GetAgeName);

    private byte[]? ReadOptionalDatabase(string name) => File.Exists(Path.Combine(GamePath, "Data", "db", name)) ? ReadDatabase(name) : null;

    private byte[]? ReadOptionalAsset(string path)
    {
        try { return _sourceAssets.Value.ReadAssetFile(path); }
        catch (FileNotFoundException) { return null; }
    }

    public string GetDisplayName(CivilizationDraft civilization)
    {
        if (civilization.SourceRowIndex is not int row) return civilization.Name;
        var id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_civilizations.AsSpan(4 + row * 344 + 208, 4));
        return ReadLocalizedText("Language.dll", id) ?? civilization.Name;
    }

    public string GetDisplayName(CivilizationEntityDraft entity)
    {
        if (entity.SourcePowerId is int power && _abilityData.GetPowerValues(power).TryGetValue("ability.nameStringId", out var identifier) && uint.TryParse(identifier, out var powerName))
            return ReadLocalizedText("Language.dll", powerName) ?? entity.Name;
        if (entity.SourceTechnologyId is int id && _technologyById.TryGetValue(id, out var technology))
            return ReadLocalizedText("Language2.dll", technology.NameStringId) ?? entity.Name;
        if (entity.SourceObjectId is int row && entity.Category is CivilizationCategory.Units or CivilizationCategory.Buildings)
        {
            var nameId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_objects.AsSpan(4 + row * 1936 + 296, 4));
            return ReadLocalizedText("Language2.dll", nameId) ?? entity.Name;
        }
        return entity.Name;
    }

    private string? ReadLocalizedText(string libraryName, uint stringId)
    {
        if (stringId is 0 or > 65535) return null;
        if (_localizedText.TryGetValue((libraryName, stringId), out var cached)) return cached;
        var path = Path.Combine(GamePath, libraryName);
        if (!File.Exists(path)) return null;
        var library = LoadLibraryExW(path, IntPtr.Zero, 0x00000002);
        if (library == IntPtr.Zero) return null;
        try
        {
            var value = new StringBuilder(4096);
            var text = LoadStringW(library, stringId, value, value.Capacity) > 0 ? value.ToString() : null;
            if (string.IsNullOrWhiteSpace(text)) text = null;
            _localizedText[(libraryName, stringId)] = text;
            return text;
        }
        finally { FreeLibrary(library); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int LoadStringW(IntPtr module, uint identifier, StringBuilder buffer, int capacity);
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);

    private static string CatalogIdentity(string civilizationId, CivilizationCategory category, int row)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"EDMW catalog/{civilizationId}/{category}/{row.ToString(CultureInfo.InvariantCulture)}"));
        return new Guid(bytes.AsSpan(0, 16)).ToString("D");
    }

    private byte[] ReadDatabase(string name)
    {
        var path = Path.Combine(GamePath, "Data", "db", name);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException("The game database exceeds the supported size.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
