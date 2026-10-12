using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace EDMWMapDesigner.Civilizations;

public sealed record CivilizationEventRecord(int Index, string Identifier, uint TriggerId,
    byte Flag, IReadOnlyList<int> EffectIds);

public sealed record CivilizationEffectRecord(int Index, uint Type, uint Selector,
    int ObjectId, int UnitSetId, int TechnologyId, float Replacement, float Additive,
    float Relative, IReadOnlyList<uint> RawValues);

public sealed record CivilizationPowerRecord(int Index, uint EngineId, uint NameStringId,
    int? ActivationTechnologyId, IReadOnlyList<uint> SourceWords);

/// <summary>
/// Read-only command, prerequisite and effect details. Unknown parameters retain
/// their source numbers; they are never relabelled as durations/ranges/targets.
/// </summary>
public sealed class CivilizationAbilityData
{
    private readonly byte[] _objects;
    private readonly IReadOnlyList<CivilizationTechnologyRecord> _technologies;
    private readonly Dictionary<int, CivilizationTechnologyRecord> _technologyById;
    private readonly IReadOnlyList<CivilizationEventRecord> _events;
    private readonly IReadOnlyList<CivilizationEffectRecord> _effects;
    private readonly IReadOnlyList<string> _calamities;
    private readonly byte[]? _civilizations;
    private readonly IReadOnlyList<CivilizationPowerRecord> _powers;
    private readonly Lazy<IReadOnlyDictionary<int, IReadOnlyList<int>>> _commandObjects;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<int>> _dependentTechnologies;
    private readonly Dictionary<(int Technology, CivilizationCategory Category), IReadOnlyDictionary<string, string>> _values = [];
    private readonly Dictionary<(int Technology, CivilizationCategory Category), IReadOnlyList<CivilizationFieldDefinition>> _effectFields = [];
    private readonly Dictionary<(int Row, int InitialAge, int FinalAge), IReadOnlyList<CivilizationTechnologyRecord>> _rosters = [];
    private readonly Dictionary<int, IReadOnlyList<CivilizationPowerRecord>> _powerRecords = [];
    private readonly Dictionary<int, IReadOnlyDictionary<string, string>> _powerValues = [];
    private readonly Dictionary<int, IReadOnlyList<int>> _assignedObjects = [];
    private readonly Dictionary<int, string> _objectDescriptions = [];

    public CivilizationAbilityData(byte[] objectsDatabase,
        IReadOnlyList<CivilizationTechnologyRecord> technologyRecords,
        byte[]? eventsDatabase = null, byte[]? effectsDatabase = null,
        byte[]? calamityDatabase = null, byte[]? civilizationsDatabase = null,
        byte[]? powersDatabase = null)
    {
        ArgumentNullException.ThrowIfNull(objectsDatabase);
        ArgumentNullException.ThrowIfNull(technologyRecords);
        // This reader owns an immutable snapshot. In-memory patch writers must
        // not invalidate cached source joins by changing their input buffers.
        _objects = (byte[])objectsDatabase.Clone();
        _technologies = Array.AsReadOnly(technologyRecords.ToArray());
        _technologyById = technologyRecords.ToDictionary(record => record.TechId);
        _events = eventsDatabase is null ? [] : ReadEvents(eventsDatabase);
        _effects = effectsDatabase is null ? [] : ReadEffects(effectsDatabase);
        _calamities = calamityDatabase is null ? [] : ReadNamedRecords(calamityDatabase, 264);
        _civilizations = civilizationsDatabase is null ? null : (byte[])civilizationsDatabase.Clone();
        _powers = powersDatabase is null ? [] : ReadPowers(powersDatabase);
        _commandObjects = new(BuildCommandObjectIndex);
        _dependentTechnologies = technologyRecords
            .SelectMany(record => record.PrerequisiteTechnologyIds.Select(id => (Prerequisite: id, Dependent: record.TechId)))
            .GroupBy(item => item.Prerequisite).ToDictionary(group => group.Key,
                group => (IReadOnlyList<int>)Array.AsReadOnly(group.Select(item => item.Dependent).Distinct().ToArray()));
        if (_events.Any(record => record.EffectIds.Any(id => id < 0 || id >= _effects.Count)))
            throw new InvalidDataException("An event refers to an effect outside the supplied effect table.");
    }

    public IReadOnlyList<CivilizationTechnologyRecord> GetRoster(int civilizationRowIndex,
        int initialAge, int finalAge)
    {
        var key = (civilizationRowIndex, initialAge, finalAge);
        if (_rosters.TryGetValue(key, out var cached)) return cached;
        return _rosters[key] = Array.AsReadOnly(CivilizationTechnologyBindings.GetCivilizationAbilityRecords(
            _objects, _technologies, civilizationRowIndex, initialAge, finalAge).ToArray());
    }

    public IReadOnlyList<CivilizationPowerRecord> GetPowerRecords(int civilizationRowIndex)
    {
        if (_civilizations is null || _powers.Count == 0) return [];
        if (_powerRecords.TryGetValue(civilizationRowIndex, out var cached)) return cached;
        EnsureRange(_civilizations, 0, 4);
        var count = BinaryPrimitives.ReadInt32LittleEndian(_civilizations);
        if (civilizationRowIndex < 0 || civilizationRowIndex >= count ||
            _civilizations.LongLength != 4L + (long)count * 344)
            throw new InvalidDataException("The civilization power-slot source has an unsupported layout.");
        var indices = new List<int>();
        var offset = 4 + civilizationRowIndex * 344 + 220;
        // Phoenix loader 0x0051EAB4 onward copies these 17 nonnegative slots
        // to its power vector. Names lookup0x0060A512 adds38; definition
        // lookup0x00797075 subtracts38 to select this exact logical row.
        for (var slot = 0; slot < 17; slot++)
        {
            var index = BinaryPrimitives.ReadInt32LittleEndian(_civilizations.AsSpan(offset + slot * 4, 4));
            if (index == -1) continue;
            if (index < 0 || index >= _powers.Count)
                throw new InvalidDataException("A civilization power slot is outside the supplied definition table.");
            if (!indices.Contains(index)) indices.Add(index);
        }
        return _powerRecords[civilizationRowIndex] = Array.AsReadOnly(indices.Select(index => _powers[index]).ToArray());
    }

    public IReadOnlyDictionary<string, string> GetPowerValues(int powerIndex)
    {
        if (powerIndex < 0 || powerIndex >= _powers.Count)
            throw new ArgumentOutOfRangeException(nameof(powerIndex));
        if (_powerValues.TryGetValue(powerIndex, out var cached)) return cached;
        var power = _powers[powerIndex];
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ability.sourcePowerId"] = power.Index.ToString(CultureInfo.InvariantCulture),
            ["ability.powerEngineId"] = power.EngineId.ToString(CultureInfo.InvariantCulture),
            ["ability.nameStringId"] = power.NameStringId.ToString(CultureInfo.InvariantCulture),
            ["ability.sourceNodeKind"] = "Configured civilization power"
        };
        if (power.ActivationTechnologyId is int techId && _technologyById.TryGetValue(techId, out var activation))
        {
            foreach (var pair in CivilizationTechnologyBindings.ReadObjectValues(activation, CivilizationCategory.Abilities))
                values[pair.Key] = pair.Value;
            foreach (var pair in GetValues(activation, CivilizationCategory.Abilities)) values[pair.Key] = pair.Value;
            values["ability.sourceNodeKind"] = "Configured civilization power / " + DescribeKind(activation);
            values["ability.activationTechnology"] = DescribeTechnology(techId);
        }
        else
        {
            values["ability.effects"] = "This civilization power uses native behavior without a linked technology. Its scalar meanings are not yet verified.";
        }
        if (_civilizations is not null && _civilizations.Length >= 4)
        {
            var count = BinaryPrimitives.ReadInt32LittleEndian(_civilizations);
            if (count > 0 && _civilizations.LongLength == 4L + (long)count * 344)
                values["ability.owners"] = string.Join(", ", Enumerable.Range(0, count)
                    .Where(row => row > 0 && BinaryPrimitives.ReadInt32LittleEndian(_civilizations.AsSpan(4 + row * 344 + 212, 4)) < 100 &&
                        GetPowerRecords(row).Any(item => item.Index == powerIndex))
                    .Select(row => ReadName(_civilizations.AsSpan(4 + row * 344 + 8, 100))));
        }
        // Mixed integer/float words retain byte-accurate labels while their
        // individual native consumers are being decoded. No write adapter.
        values["ability.powerSourceParameters"] = string.Join(Environment.NewLine,
            Enumerable.Range(3, 9).Where(index => index != 5).Select(index =>
                $"Source +{index * 4}: {FormatSourceNumber(power.SourceWords[index])} (0x{power.SourceWords[index]:X8})"));
        // Assignment to the selected civilization was checked by GetPowerRecords.
        // Native-only powers have no technology eligibility byte to read.
        values["available"] = bool.TrueString;
        return _powerValues[powerIndex] = new ReadOnlyDictionary<string, string>(values);
    }

    public static IReadOnlyList<CivilizationPowerRecord> ReadPowers(byte[] database)
    {
        ArgumentNullException.ThrowIfNull(database);
        EnsureRange(database, 0, 4);
        var count = BinaryPrimitives.ReadInt32LittleEndian(database);
        if (count is <= 0 or > 100_000 || database.LongLength != 4L + (long)count * 56)
            throw new InvalidDataException("The civilization power table has an unsupported layout.");
        var result = new List<CivilizationPowerRecord>(count);
        for (var index = 0; index < count; index++)
        {
            var raw = new uint[14];
            for (var word = 0; word < 14; word++)
                raw[word] = BinaryPrimitives.ReadUInt32LittleEndian(database.AsSpan(4 + index * 56 + word * 4, 4));
            if (raw[1] != index + 38u)
                throw new InvalidDataException("A civilization power's engine identity differs from its logical source row.");
            // 0x00797000 copies the definition. Activation0x00797177 then
            // reads definition +20 as techID, resolves it0x007971A1 and
            // applies it0x007971BD. UINT_MAX means no linked technology.
            if (raw[5] != uint.MaxValue && raw[5] > int.MaxValue)
                throw new InvalidDataException("A civilization power has an unsupported technology reference.");
            result.Add(new(index, raw[1], raw[2], raw[5] == uint.MaxValue ? null : (int)raw[5], Array.AsReadOnly(raw)));
        }
        return result.AsReadOnly();
    }

    public IReadOnlyDictionary<string, string> GetValues(CivilizationTechnologyRecord record,
        CivilizationCategory category)
    {
        ArgumentNullException.ThrowIfNull(record);
        var cacheKey = (record.TechId, category);
        var sourceRecord = _technologyById.TryGetValue(record.TechId, out var ownRecord) && ReferenceEquals(record, ownRecord);
        if (sourceRecord && _values.TryGetValue(cacheKey, out var cached)) return cached;
        var prefix = category switch
        {
            CivilizationCategory.Units => "unit",
            CivilizationCategory.Buildings => "building",
            CivilizationCategory.Technologies => "technology",
            CivilizationCategory.Abilities => "ability",
            _ => null
        };
        if (prefix is null) return new Dictionary<string, string>();
        var result = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [prefix + ".prerequisites"] = DescribeReferences(record.PrerequisiteTechnologyIds),
            [prefix + ".prerequisiteRules"] = DescribeReferences(record.PrerequisiteTechnologyIds),
            [prefix + ".sourceNodeKind"] = DescribeKind(record),
            [prefix + ".sourceTechnologyId"] = record.TechId.ToString(CultureInfo.InvariantCulture)
        };
        foreach (var field in GetEffectFields(record.TechId, category))
        {
            var pieces = field.Key.Split('.');
            var effect = _effects[int.Parse(pieces[2], CultureInfo.InvariantCulture)];
            result[field.Key] = Number(pieces[3] switch
            {
                "replacement" => effect.Replacement,
                "additive" => effect.Additive,
                _ => effect.Relative
            });
        }
        var producers = DescribeObjects(record.ProducerObjectIds);
        switch (category)
        {
            case CivilizationCategory.Units:
                result["unit.trainedAt"] = producers;
                break;
            case CivilizationCategory.Buildings:
                result["building.builders"] = producers;
                break;
            case CivilizationCategory.Technologies:
                result["technology.researchedAt"] = producers;
                break;
            case CivilizationCategory.Abilities:
                result["ability.owners"] = DescribeObjects(GetAssignedObjectIds(record));
                result["ability.commandId"] = FormatSourceNumber(record.CommandId);
                result["ability.commandParameter"] = FormatSourceNumber(record.CommandParameter);
                if (record.NodeKind == 5 || record.NodeKind == 8 && record.CommandId == 41)
                    result["ability.calamityReference"] = DescribeCalamity(record.CalamityId);
                break;
        }
        var operations = DescribeEffects(record);
        if (category is CivilizationCategory.Technologies or CivilizationCategory.Abilities)
        {
            result[prefix + ".effects"] = operations.Summary;
            result[prefix + ".effectOperations"] = operations.Operations;
            if (record.EventId < _events.Count)
                result[prefix + ".affectedEntities"] = string.Join(", ", _events[(int)record.EventId].EffectIds
                    .Select(id => _effects[id]).Select(effect => effect.ObjectId >= 0 ? DescribeObjects([effect.ObjectId])
                        : effect.UnitSetId >= 0 ? $"Unit set #{effect.UnitSetId}" : "")
                    .Where(target => target.Length != 0).Distinct());
            var dependents = _dependentTechnologies.GetValueOrDefault(record.TechId) ?? [];
            result[prefix + ".unlocks"] = DescribeReferences(dependents);
            if (record.AdditionalTechnologyIds.Count != 0)
            {
                // Native application routine 0x00673861..0x00673886 resolves
                // array 2 and applies each linked node. These are not all unlocks:
                // some perform upgrades, modifiers or further initialization.
                result[prefix + ".appliedTechnologies"] = DescribeReferences(record.AdditionalTechnologyIds);
                result[prefix + ".effects"] = Append(operations.Summary,
                    "Applies linked technologies: " + DescribeReferences(record.AdditionalTechnologyIds));
            }
        }
        var readOnlyResult = new ReadOnlyDictionary<string, string>(result);
        if (sourceRecord) _values[cacheKey] = readOnlyResult;
        return readOnlyResult;
    }

    public IReadOnlyList<CivilizationFieldDefinition> GetEffectFields(int technologyId,
        CivilizationCategory category)
    {
        var cacheKey = (technologyId, category);
        if (_effectFields.TryGetValue(cacheKey, out var cached)) return cached;
        var prefix = EffectPrefix(category);
        if (prefix is null || !_technologyById.TryGetValue(technologyId, out var record) || record.EventId >= _events.Count)
            return [];
        var result = new List<CivilizationFieldDefinition>();
        foreach (var id in _events[(int)record.EventId].EffectIds.Distinct())
        {
            var effect = _effects[id];
            if (!HasVerifiedScalarModifier(effect)) continue;
            var section = $"Effect #{id} (selector {effect.Selector})";
            const string help = "Native modifier operands. Nonzero additive takes priority over nonzero relative, then replacement. Zero disables that operand. Shared effect rows affect every technology that references them.";
            result.Add(new($"{prefix}.effect.{id}.replacement", "Replacement value", section, CivilizationFieldKind.Number, Help: help));
            result.Add(new($"{prefix}.effect.{id}.additive", "Additive value", section, CivilizationFieldKind.Number, Help: help));
            result.Add(new($"{prefix}.effect.{id}.relative", "Relative contribution of base value", section, CivilizationFieldKind.Number, Help: help));
        }
        return _effectFields[cacheKey] = result.AsReadOnly();
    }

    public bool CanWriteEffect(int technologyId, CivilizationCategory category, string key) =>
        GetEffectFields(technologyId, category).Any(field => field.Key == key);

    public void WriteEffectValue(byte[] effectsDatabase, int technologyId,
        CivilizationCategory category, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(effectsDatabase);
        ArgumentNullException.ThrowIfNull(value);
        if (!CanWriteEffect(technologyId, category, key))
            throw new InvalidDataException($"Effect field '{key}' is not a verified modifier linked to this technology.");
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !float.IsFinite(number))
            throw new InvalidDataException("Effect operands require a finite 32-bit number.");
        var pieces = key.Split('.');
        var effectId = int.Parse(pieces[2], CultureInfo.InvariantCulture);
        var recordOffset = checked(effectId * 88);
        EnsureRange(effectsDatabase, recordOffset, 88);
        var expected = _effects[effectId];
        if (effectsDatabase.Length != 2000 * 88 ||
            BinaryPrimitives.ReadUInt32LittleEndian(effectsDatabase.AsSpan(recordOffset + 4, 4)) != expected.RawValues[1] ||
            BinaryPrimitives.ReadUInt32LittleEndian(effectsDatabase.AsSpan(recordOffset + 32, 4)) != expected.Type ||
            BinaryPrimitives.ReadUInt32LittleEndian(effectsDatabase.AsSpan(recordOffset + 44, 4)) != expected.Selector)
            throw new InvalidDataException("The target effect identity or opcode differs from the source database.");
        var valueOffset = pieces[3] switch { "replacement" => 8, "additive" => 12, "relative" => 16,
            _ => throw new InvalidDataException("The effect operand is unsupported.") };
        BinaryPrimitives.WriteUInt32LittleEndian(effectsDatabase.AsSpan(recordOffset + valueOffset, 4),
            BitConverter.SingleToUInt32Bits(number));
    }

    public static bool HasVerifiedScalarModifier(CivilizationEffectRecord effect) => effect.Type == 2 &&
        effect.Selector is 1 or 3 or 4 or 5 or 9 or 11 or 16 or 17 or 18 or 19 or 20 or 21 or
            26 or 27 or 28 or 29 or 31 or 32 or 44 or 50;

    // Opcode 2 dispatches to 0x0066A5B9. Jump table at0x0066B302 maps
    // selectors 1,3,4,5,9,11,16..21,44,50 to 0x0066967C, the float
    // replacement/additive/relative helper. Selectors26..29,31,32 call
    // 0x0066B633: it consumes the same float operands for gather intervals
    // and rounds them for integer storage. Other selectors have dedicated
    // consumers and deliberately receive no generic write adapter.

    private static string? EffectPrefix(CivilizationCategory category) => category switch
    {
        CivilizationCategory.Technologies => "technology",
        CivilizationCategory.Abilities => "ability",
        _ => null
    };

    public string DescribeTechnology(int technologyId) =>
        _technologyById.TryGetValue(technologyId, out var record)
            ? $"{record.Identifier} (Technology #{technologyId})" : $"Technology #{technologyId}";

    public IReadOnlyList<int> GetAssignedObjectIds(CivilizationTechnologyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var sourceRecord = _technologyById.TryGetValue(record.TechId, out var ownRecord) && ReferenceEquals(record, ownRecord);
        if (sourceRecord && _assignedObjects.TryGetValue(record.TechId, out var cached)) return cached;
        var ids = record.ProducerObjectIds.ToHashSet();
        if (record.NodeKind == 3 && _commandObjects.Value.TryGetValue(record.TechId, out var assigned))
            ids.UnionWith(assigned);
        var result = Array.AsReadOnly(ids.Order().ToArray());
        if (sourceRecord) _assignedObjects[record.TechId] = result;
        return result;
    }

    private IReadOnlyDictionary<int, IReadOnlyList<int>> BuildCommandObjectIndex()
    {
        if (_objects.Length < 4) throw new InvalidDataException("The object table is empty.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(_objects);
        if (count < 0 || _objects.LongLength != 4L + (long)count * 0x790)
            throw new InvalidDataException("The object table has an unsupported command-slot layout.");
        var commands = new Dictionary<int, HashSet<int>>();
        for (var row = 0; row < count; row++)
            for (var slot = 0; slot < 9; slot++)
            {
                var id = BinaryPrimitives.ReadInt32LittleEndian(_objects.AsSpan(4 + row * 0x790 + 368 + slot * 4, 4));
                if (id < 0) continue;
                if (!commands.TryGetValue(id, out var ids)) commands.Add(id, ids = []);
                ids.Add(row);
            }
        return commands.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<int>)Array.AsReadOnly(pair.Value.Order().ToArray()));
    }

    public string DescribeReferences(IEnumerable<int> technologyIds) =>
        string.Join(", ", technologyIds.Select(DescribeTechnology));

    public string DescribeObjects(IEnumerable<int> objectIds) => string.Join(", ", objectIds.Select(DescribeObject));

    private string DescribeObject(int id)
    {
        if (_objectDescriptions.TryGetValue(id, out var cached)) return cached;
        var offset = 4L + (long)id * 0x790;
        if (id < 0 || offset + 100 > _objects.LongLength) return $"Object #{id}";
        var name = ReadName(_objects.AsSpan((int)offset, 100));
        return _objectDescriptions[id] = string.IsNullOrWhiteSpace(name) ? $"Object #{id}" : $"{name} (Object #{id})";
    }

    private (string Summary, string Operations) DescribeEffects(CivilizationTechnologyRecord record)
    {
        var summaries = new List<string>();
        var details = new List<string>();
        if (record.EventId < _events.Count)
        {
            var sourceEvent = _events[(int)record.EventId];
            if (sourceEvent.EffectIds.Count != 0)
            {
                summaries.Add(string.IsNullOrWhiteSpace(sourceEvent.Identifier)
                    ? $"Event #{sourceEvent.Index}" : sourceEvent.Identifier);
                foreach (var id in sourceEvent.EffectIds)
                {
                    var effect = _effects[id];
                    var target = effect.ObjectId >= 0 ? DescribeObjects([effect.ObjectId])
                        : effect.UnitSetId >= 0 ? $"Unit set #{effect.UnitSetId}" : "No fixed object/unit-set target";
                    var technology = effect.TechnologyId >= 0 ? "; " + DescribeTechnology(effect.TechnologyId) : "";
                    if (HasVerifiedScalarModifier(effect))
                    {
                        // 0x0066967C..0x006696C9 establishes float effect
                        // priority: additive, then relative, then replacement.
                        var operation = effect.Additive != 0 ? $"Add {Number(effect.Additive)}"
                            : effect.Relative != 0 ? $"Relative contribution {Number(effect.Relative)} of base value"
                            : effect.Replacement != 0 ? $"Replace with {Number(effect.Replacement)}"
                            : "Zero modifier parameters";
                        details.Add($"Effect #{id}: modifier selector {effect.Selector}; {target}{technology}; {operation}.");
                    }
                    else if (effect.Type == 2)
                        details.Add($"Effect #{id}: modifier selector {effect.Selector}; {target}{technology}; source operands replacement={Number(effect.Replacement)}, additive={Number(effect.Additive)}, relative={Number(effect.Relative)}. Dedicated consumer; no generic write mapping.");
                    else details.Add($"Effect #{id}: operation type {effect.Type}; selector {effect.Selector}; {target}{technology}.");
                }
            }
        }
        if (record.NodeKind == 5 || record.NodeKind == 8 && record.CommandId == 41)
        {
            summaries.Add(DescribeCalamity(record.CalamityId));
            details.Add($"Calamity table row {record.CalamityId}: {DescribeCalamity(record.CalamityId)}. Its scalar field meanings are not yet verified.");
        }
        if (record.NodeKind is 3 or 8)
            details.Add($"Command #{FormatSourceNumber(record.CommandId)}; source command parameter {FormatSourceNumber(record.CommandParameter)}.");
        return (string.Join(Environment.NewLine, summaries), string.Join(Environment.NewLine, details));
    }

    private string DescribeCalamity(uint id) => id < _calamities.Count && !string.IsNullOrWhiteSpace(_calamities[(int)id])
        ? $"{_calamities[(int)id]} (Calamity #{id})" : $"Calamity #{id}";

    public static string DescribeKind(CivilizationTechnologyRecord record) => record.CreatesObject
        ? "Object production" : record.NodeKind switch
        {
            0 => "Research/modifier",
            2 => "Age transition",
            3 => "Immediate command",
            5 => "Calamity ability",
            7 => "Upgrade",
            8 => "Purchased command/improvement",
            9 => "Storage/garrison node",
            11 => "Civilization power activation",
            12 => "Civilization initialization",
            _ => $"Node kind {record.NodeKind}"
        };

    public static IReadOnlyList<CivilizationEventRecord> ReadEvents(byte[] database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var result = new List<CivilizationEventRecord>(1500);
        var cursor = 0;
        // Loader 0x0066980E..0x00669879 repeats exactly 1,500 times:
        // char[100], uint32 trigger, byte flag, uint32 count, uint32[count].
        for (var index = 0; index < 1500; index++)
        {
            EnsureRange(database, cursor, 109);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(database.AsSpan(cursor + 105, 4));
            if (count > 2000) throw new InvalidDataException("An event has too many effect references.");
            EnsureRange(database, cursor + 109, checked((int)count * 4));
            var references = new int[count];
            for (var item = 0; item < references.Length; item++)
                references[item] = BinaryPrimitives.ReadInt32LittleEndian(database.AsSpan(cursor + 109 + item * 4, 4));
            result.Add(new(index, count == 0 ? "" : ReadName(database.AsSpan(cursor, 100)),
                BinaryPrimitives.ReadUInt32LittleEndian(database.AsSpan(cursor + 100, 4)),
                database[cursor + 104], Array.AsReadOnly(references)));
            cursor += 109 + checked((int)count * 4);
        }
        if (cursor != database.Length) throw new InvalidDataException("The event table contains trailing bytes.");
        return result.AsReadOnly();
    }

    public static IReadOnlyList<CivilizationEffectRecord> ReadEffects(byte[] database)
    {
        ArgumentNullException.ThrowIfNull(database);
        // 0x006697FA..0x00669806 reads 0x2AF80 bytes directly into 2,000
        // 0x58-byte effect records, without a count/header.
        if (database.Length != 2000 * 88) throw new InvalidDataException("The effect table has an unsupported layout.");
        var result = new List<CivilizationEffectRecord>(2000);
        for (var index = 0; index < 2000; index++)
        {
            var record = database.AsSpan(index * 88, 88);
            var raw = new uint[22];
            for (var item = 0; item < raw.Length; item++) raw[item] = BinaryPrimitives.ReadUInt32LittleEndian(record[(item * 4)..]);
            result.Add(new(index, raw[8], raw[11], unchecked((int)raw[5]), unchecked((int)raw[7]),
                unchecked((int)raw[10]), BitConverter.UInt32BitsToSingle(raw[2]),
                BitConverter.UInt32BitsToSingle(raw[3]), BitConverter.UInt32BitsToSingle(raw[4]), Array.AsReadOnly(raw)));
        }
        return result.AsReadOnly();
    }

    private static IReadOnlyList<string> ReadNamedRecords(byte[] database, int size)
    {
        EnsureRange(database, 0, 4);
        var count = BinaryPrimitives.ReadInt32LittleEndian(database);
        if (count < 0 || count > 100_000 || database.LongLength != 4L + (long)count * size)
            throw new InvalidDataException("The named ability table has an unsupported layout.");
        return Enumerable.Range(0, count).Select(index => ReadName(database.AsSpan(4 + index * size, 100))).ToArray();
    }

    private static string ReadName(ReadOnlySpan<byte> value)
    {
        var length = value.IndexOf((byte)0);
        if (length < 0) return "";
        value = value[..length];
        foreach (var character in value) if (character is < 32 or > 126) return "";
        return Encoding.ASCII.GetString(value);
    }

    private static string Append(string first, string second) => string.IsNullOrWhiteSpace(first) ? second : first + Environment.NewLine + second;
    private static string Number(float value) => float.IsFinite(value) ? value.ToString("G9", CultureInfo.InvariantCulture) : "Unmapped";
    private static string FormatSourceNumber(uint value) => value == uint.MaxValue ? "None" : value.ToString(CultureInfo.InvariantCulture);
    private static void EnsureRange(byte[] database, int offset, int length)
    {
        if (offset < 0 || length < 0 || (long)offset + length > database.Length)
            throw new InvalidDataException("An ability table is truncated.");
    }
}
