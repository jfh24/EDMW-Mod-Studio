using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace EDMWMapDesigner.Civilizations;

public sealed record CivilizationTechnologyRecord(
    int Index, int Offset, int Length, string Identifier, int TechId,
    int CivilizationRowIndex, int StartAge, int EndAge,
    uint WoodCost, uint StoneCost, uint GoldCost, uint FoodCost,
    uint ProductionCoefficient, bool EditorOnly)
{
    public uint NameStringId { get; init; }
    public uint ToolTipStringId { get; init; }
    public bool IsEpochDefinition { get; init; }
    // Loader 0x00671F73..0x00671F79 copies disk +180 to node-data +0xA8.
    // Command extractor 0x006714D6 distinguishes immediate actions (3) and
    // calamities (5); purchase consumer 0x006736D6..0x0067373A handles (8).
    public uint NodeKind { get; init; }
    public bool CreatesObject { get; init; }
    public uint EventId { get; init; }
    public uint CommandId { get; init; }
    public uint CommandParameter { get; init; }
    public uint CalamityId { get; init; }
    public uint CreatedObjectId { get; init; }
    public uint ButtonId { get; init; }
    public uint PrerequisiteStringId1 { get; init; }
    public uint PrerequisiteStringId2 { get; init; }
    public IReadOnlyList<int> PrerequisiteTechnologyIds { get; init; } = [];
    // Loader 0x00671E0F..0x00671E2F places the first trailing array in
    // node-data vector +0x0C. Membership routine 0x00672D0E searches it.
    // Callers 0x004FCBDB..0x004FCBE4 and 0x0065F05C..0x0065F06A pass
    // the acting entity's prototype/global object ID, establishing producers.
    public IReadOnlyList<int> ProducerObjectIds { get; init; } = [];
    public IReadOnlyList<int> AdditionalTechnologyIds { get; init; } = [];
    public IReadOnlyList<int> AllowedCivilizationRows { get; init; } = [];
    public bool UniversalCivilizationMembership { get; init; }
    public int CommonButtonSlot { get; init; }
    public IReadOnlyList<int> CivilizationButtonSlots { get; init; } = [];
    public bool AllowsCivilizationRow(int row) => AllowedCivilizationRows.Contains(row);
}

/// <summary>Exact EDMW technology framing and verified production-cost bindings.</summary>
public static class CivilizationTechnologyBindings
{
    public const int UniversalCivilizationMaskOffset = 312;
    public const int ProgressionButtonSlotsOffset = 324;
    public static IReadOnlyList<int> ProgressionMaskOffsets { get; } = Array.AsReadOnly(new[]
        { UniversalCivilizationMaskOffset, 315, 316, 317, 318, 319, 320, 321, 322, 323 });
    private const int FixedRecordBytes = 436;
    private const int MaximumRecords = 100_000;
    private const int MaximumArrayItems = 1_000_000;
    private const string Evidence = "aczwink/EDMW-Modding-Tools db/dbtechtree.xml; installed file consumes 14 epoch groups and 1,495 variable records exactly";
    private const string AgeEvidence = "dbtechtree native initial/final age fields at +108/+112; existing production/research eligibility uses this inclusive interval";

    public static IReadOnlyList<CivilizationBinaryField> GetFields(CivilizationCategory category)
    {
        var prefix = Prefix(category);
        if (prefix is null) return [];
        var fields = new List<CivilizationBinaryField>
        {
            new CivilizationBinaryField(prefix + ".woodCost", 124, CivilizationBinaryType.UInt32, Evidence: Evidence),
            new CivilizationBinaryField(prefix + ".stoneCost", 128, CivilizationBinaryType.UInt32, Evidence: Evidence),
            new CivilizationBinaryField(prefix + ".goldCost", 132, CivilizationBinaryType.UInt32, Evidence: Evidence),
            new CivilizationBinaryField(prefix + ".foodCost", 140, CivilizationBinaryType.UInt32, Evidence: Evidence),
            new CivilizationBinaryField(prefix + ".productionCoefficient", 144, CivilizationBinaryType.UInt32, Evidence: Evidence)
        };
        if (category is CivilizationCategory.Units or CivilizationCategory.Buildings or CivilizationCategory.Technologies)
        {
            fields.Add(new CivilizationBinaryField(prefix + ".initialAge", 108, CivilizationBinaryType.Int32, Evidence: AgeEvidence));
            fields.Add(new CivilizationBinaryField(prefix + ".finalAge", 112, CivilizationBinaryType.Int32, Evidence: AgeEvidence));
        }
        if (category is CivilizationCategory.Technologies or CivilizationCategory.Abilities)
            fields.Add(new CivilizationBinaryField(prefix + ".internalName", 0, CivilizationBinaryType.String100, Evidence: Evidence));
        return fields.AsReadOnly();
    }

    public static bool CanWriteObject(CivilizationCategory category, string key) =>
        GetFields(category).Any(field => field.Key == key) || IsProgressionMembershipKey(category, key) || IsProgressionProducerKey(category, key);

    public static bool IsProgressionMembershipKey(CivilizationCategory category, string key) =>
        category is CivilizationCategory.Units or CivilizationCategory.Buildings or CivilizationCategory.Technologies &&
        key == Prefix(category) + ".progressionIncluded";

    public static bool IsProgressionProducerKey(CivilizationCategory category, string key) =>
        category is CivilizationCategory.Units or CivilizationCategory.Buildings or CivilizationCategory.Technologies &&
        key == Prefix(category) + ".progressionProducers";

    public static IReadOnlyList<int> ParseProgressionProducerIds(string value)
    {
        var result = new List<int>();
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 0)
                throw new InvalidDataException("Progression producers must be existing nonnegative object IDs separated by commas.");
            if (!result.Contains(id)) result.Add(id);
        }
        if (result.Count is 0 or > 32)
            throw new InvalidDataException("Choose from one to 32 compatible existing producers.");
        return result.AsReadOnly();
    }

    public static bool ReadProgressionIncluded(CivilizationTechnologyRecord technology, int civilizationRow)
    {
        if (civilizationRow is < 3 or > 11)
            throw new InvalidDataException("Progression membership supports the nine playable civilization rows.");
        return technology.AllowsCivilizationRow(civilizationRow);
    }

    /// <summary>Changes one civilization's membership while preserving every other civilization.</summary>
    public static void WriteProgressionIncluded(byte[] database, int techId, int civilizationRow, bool included)
    {
        if (civilizationRow is < 3 or > 11)
            throw new InvalidDataException("Progression membership supports the nine playable civilization rows.");
        var technology = ReadRecords(database, validateAgeIntervals: false).SingleOrDefault(record => record.TechId == techId)
            ?? throw new InvalidDataException($"Technology ID {techId} is not present in the game table.");
        var record = database.AsSpan(technology.Offset, FixedRecordBytes);
        if (record[UniversalCivilizationMaskOffset] != 0)
        {
            if (included) return;
            // Universal membership must be expanded before one civilization can
            // be removed. This is one mask operation, not ten unrelated writes.
            ExpandUniversalProgression(record);
        }
        record[UniversalCivilizationMaskOffset + civilizationRow] = included ? (byte)1 : (byte)0;
    }

    private static void ExpandUniversalProgression(Span<byte> record)
    {
        if (record[UniversalCivilizationMaskOffset] == 0) return;
        for (var row = 3; row <= 11; row++) record[UniversalCivilizationMaskOffset + row] = 1;
        // 0x00672508..0x00672651 switches from the common +276 slot to
        // eleven +324 pairs when universal membership is cleared.
        var commonSlot = BinaryPrimitives.ReadInt32LittleEndian(record[276..]);
        for (var row = 1; row <= 11; row++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(record[(ProgressionButtonSlotsOffset + (row - 1) * 8)..], commonSlot);
            BinaryPrimitives.WriteInt32LittleEndian(record[(ProgressionButtonSlotsOffset + (row - 1) * 8 + 4)..], commonSlot);
        }
        record[UniversalCivilizationMaskOffset] = 0;
    }

    public static int ReadProgressionButtonSlot(CivilizationTechnologyRecord technology, int civilizationRow, byte[] buttons)
    {
        if (civilizationRow is < 1 or > 11) throw new InvalidDataException("The progression civilization row is invalid.");
        var slot = technology.UniversalCivilizationMembership ? technology.CommonButtonSlot : technology.CivilizationButtonSlots[civilizationRow - 1];
        if (slot > 0) return Math.Min(slot, 41);
        // Loader 0x00519253..0x005192B3 maps raw dbbuttons +216 to
        // runtime +0x3C, the HUD fallback at 0x0076CDF0.
        if (buttons.Length < 4) throw new InvalidDataException("The button table is empty.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(buttons);
        if (count <= 0 || count > MaximumRecords || buttons.LongLength != 4L + count * 228L || technology.ButtonId >= count)
            throw new InvalidDataException("The progression button reference is outside the verified button table.");
        return Math.Min(BinaryPrimitives.ReadInt32LittleEndian(buttons.AsSpan(4 + checked((int)technology.ButtonId) * 228 + 216, 4)), 41);
    }

    public static void WriteProgressionButtonSlot(byte[] database, int techId, int civilizationRow, int slot)
    {
        if (civilizationRow is < 3 or > 11 || slot is < 1 or > 41)
            throw new InvalidDataException("Choose a valid civilization and progression menu slot from 1 to 41.");
        var technology = ReadRecords(database, validateAgeIntervals: false).SingleOrDefault(record => record.TechId == techId)
            ?? throw new InvalidDataException("The progression technology is absent from the table.");
        var record = database.AsSpan(technology.Offset, FixedRecordBytes);
        ExpandUniversalProgression(record);
        BinaryPrimitives.WriteInt32LittleEndian(record[(ProgressionButtonSlotsOffset + (civilizationRow - 1) * 8)..], slot);
    }

    public static void ValidateProgressionMasks(byte[] database) => _ = ReadRecords(database, validateAgeIntervals: false);

    /// <summary>Appends verified producers without changing original producer order,
    /// other reference arrays, record IDs or epoch framing.</summary>
    public static byte[] WriteProgressionProducers(byte[] database,
        IReadOnlyDictionary<int, IReadOnlyList<int>> additions)
    {
        var records = ReadRecords(database);
        if (additions.Keys.Any(id => !records.Any(record => record.TechId == id)))
            throw new InvalidDataException("A progression producer target is absent from the technology table.");
        using var output = new MemoryStream();
        var cursor = 0;
        Span<byte> integer = stackalloc byte[4];
        foreach (var record in records)
        {
            output.Write(database.AsSpan(cursor, record.Offset - cursor));
            output.Write(database.AsSpan(record.Offset, FixedRecordBytes));
            var producers = record.ProducerObjectIds.ToList();
            if (additions.TryGetValue(record.TechId, out var added))
                foreach (var id in added)
                {
                    if (id < 0) throw new InvalidDataException("A progression producer ID is invalid.");
                    if (!producers.Contains(id)) producers.Add(id);
                }
            BinaryPrimitives.WriteInt32LittleEndian(integer, producers.Count);
            output.Write(integer);
            foreach (var id in producers)
            {
                BinaryPrimitives.WriteInt32LittleEndian(integer, id);
                output.Write(integer);
            }
            var tail = record.Offset + FixedRecordBytes + 4 + record.ProducerObjectIds.Count * 4;
            output.Write(database.AsSpan(tail, record.Offset + record.Length - tail));
            cursor = record.Offset + record.Length;
        }
        output.Write(database.AsSpan(cursor));
        var result = output.ToArray();
        ValidateAgeIntervals(result);
        return result;
    }

    /// <summary>Adds an item to its chosen actor's existing production/research menu.
    /// Constructor 0x006FDAFC..0x006FDB36 copies raw +896 count and +1136
    /// references to prototype +0x34, enumerated by HUD 0x0076CDAC..0x0076CDC6.</summary>
    public static void WriteProgressionProducerAction(byte[] objectsDatabase, int producerId, int techId)
    {
        if (techId < 0) throw new InvalidDataException("The progression technology ID is invalid.");
        var count = ValidateObjects(objectsDatabase);
        if (producerId < 0 || producerId >= count)
            throw new InvalidDataException("The progression producer is absent from the object table.");
        var name = ReadIdentifier(objectsDatabase.AsSpan(4 + producerId * 0x790, 100));
        var category = name.StartsWith("b ", StringComparison.OrdinalIgnoreCase)
            ? CivilizationCategory.Buildings : CivilizationCategory.Units;
        var recordOffset = CivilizationObjectBindings.ValidateRecord(objectsDatabase, producerId, category);
        var countOffset = recordOffset + CivilizationObjectDetails.ActionTechnologyCountOffset;
        var actionCount = BinaryPrimitives.ReadInt32LittleEndian(objectsDatabase.AsSpan(countOffset, 4));
        if (actionCount is < 0 or > CivilizationObjectDetails.ActionTechnologyCount)
            throw new InvalidDataException("The producer's native progression menu count is invalid.");
        for (var slot = 0; slot < actionCount; slot++)
        {
            var offset = recordOffset + CivilizationObjectDetails.ActionTechnologyIdsOffset + slot * 4;
            var existing = BinaryPrimitives.ReadUInt32LittleEndian(objectsDatabase.AsSpan(offset, 4));
            if (existing == (uint)techId) return;
            if (existing == uint.MaxValue)
                throw new InvalidDataException("The producer's active progression menu contains an invalid empty entry.");
        }
        if (actionCount == CivilizationObjectDetails.ActionTechnologyCount)
            throw new InvalidDataException("This producer's production and research menu is full. Choose another compatible producer.");
        var empty = recordOffset + CivilizationObjectDetails.ActionTechnologyIdsOffset + actionCount * 4;
        if (BinaryPrimitives.ReadUInt32LittleEndian(objectsDatabase.AsSpan(empty, 4)) != uint.MaxValue)
            throw new InvalidDataException("The producer's next menu slot contains unverified inactive data. Choose another compatible producer.");
        BinaryPrimitives.WriteUInt32LittleEndian(objectsDatabase.AsSpan(empty, 4), (uint)techId);
        BinaryPrimitives.WriteInt32LittleEndian(objectsDatabase.AsSpan(countOffset, 4), actionCount + 1);
    }

    public static IReadOnlyList<CivilizationTechnologyRecord> ReadRecords(byte[] database) => ReadRecords(database, validateAgeIntervals: true);

    private static IReadOnlyList<CivilizationTechnologyRecord> ReadRecords(byte[] database, bool validateAgeIntervals)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.Length < 4) throw new InvalidDataException("The technology table is empty.");
        var records = new List<CivilizationTechnologyRecord>();
        var techIds = new HashSet<int>();
        var cursor = 0;
        var groupCount = 0;
        while (cursor < database.Length)
        {
            if (++groupCount > MaximumRecords) throw new InvalidDataException("The technology table contains too many epoch groups.");
            var childCount = ReadCount(database, cursor, MaximumRecords);
            cursor += 4;
            if ((long)records.Count + childCount + 1 > MaximumRecords)
                throw new InvalidDataException("The technology table contains too many records.");
            for (var childIndex = -1; childIndex < childCount; childIndex++)
            {
                var start = cursor;
                EnsureRange(database, start, FixedRecordBytes + 4);
                var identifier = ReadIdentifier(database.AsSpan(start, 100));
                if (childIndex == -1 && !identifier.StartsWith("Epoch ", StringComparison.Ordinal))
                    throw new InvalidDataException("The technology table does not begin each group with an epoch definition.");
                cursor = start + FixedRecordBytes;
                var arrays = new IReadOnlyList<int>[2];
                for (var arrayIndex = 0; arrayIndex < 2; arrayIndex++)
                {
                    var itemCount = ReadCount(database, cursor, MaximumArrayItems);
                    var bytes = checked(itemCount * 4);
                    cursor += 4;
                    EnsureRange(database, cursor, bytes);
                    var references = new int[itemCount];
                    for (var itemIndex = 0; itemIndex < itemCount; itemIndex++)
                        references[itemIndex] = BinaryPrimitives.ReadInt32LittleEndian(database.AsSpan(cursor + itemIndex * 4, 4));
                    arrays[arrayIndex] = Array.AsReadOnly(references);
                    cursor += bytes;
                }
                var record = database.AsSpan(start, FixedRecordBytes);
                if (ProgressionMaskOffsets.Any(offset => database[start + offset] > 1))
                    throw new InvalidDataException("The technology table has an invalid civilization membership mask; use boolean mask values.");
                var techId = BinaryPrimitives.ReadInt32LittleEndian(record[104..]);
                if (techId < 0 || !techIds.Add(techId))
                    throw new InvalidDataException("The technology table has duplicate or negative technology IDs.");
                var civilization = BinaryPrimitives.ReadInt32LittleEndian(record[204..]);
                var lower = BinaryPrimitives.ReadInt32LittleEndian(record[108..]);
                var upper = BinaryPrimitives.ReadInt32LittleEndian(record[112..]);
                if (civilization < 0 || record[261] > 1)
                    throw new InvalidDataException("The technology table has unsupported civilization or availability data.");
                if (validateAgeIntervals && (lower < 0 || upper < lower || upper > 100))
                    throw new InvalidDataException($"Technology {techId} has an invalid age interval. Initial and final ages must be IDs from 0 to 100, with initial age no greater than final age.");
                records.Add(new(records.Count, start, cursor - start, identifier, techId,
                    civilization, lower, upper,
                    BinaryPrimitives.ReadUInt32LittleEndian(record[124..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(record[128..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(record[132..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(record[140..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(record[144..]), record[261] != 0)
                {
                    NameStringId = BinaryPrimitives.ReadUInt32LittleEndian(record[176..]),
                    ToolTipStringId = BinaryPrimitives.ReadUInt32LittleEndian(record[240..]),
                    IsEpochDefinition = childIndex == -1,
                    NodeKind = BinaryPrimitives.ReadUInt32LittleEndian(record[180..]),
                    CreatesObject = record[172] != 0,
                    EventId = BinaryPrimitives.ReadUInt32LittleEndian(record[116..]),
                    CommandId = BinaryPrimitives.ReadUInt32LittleEndian(record[208..]),
                    CommandParameter = BinaryPrimitives.ReadUInt32LittleEndian(record[216..]),
                    CalamityId = BinaryPrimitives.ReadUInt32LittleEndian(record[196..]),
                    CreatedObjectId = BinaryPrimitives.ReadUInt32LittleEndian(record[164..]),
                    ButtonId = BinaryPrimitives.ReadUInt32LittleEndian(record[168..]),
                    PrerequisiteStringId1 = BinaryPrimitives.ReadUInt32LittleEndian(record[232..]),
                    PrerequisiteStringId2 = BinaryPrimitives.ReadUInt32LittleEndian(record[236..]),
                    // Loader 0x00671F0D..0x00671F40 -> runtime +0x10C..+0x118;
                    // 0x00671BE1 renders these as prerequisites, 0x0067349D sets
                    // satisfaction bits when a linked technology is applied.
                    PrerequisiteTechnologyIds = Enumerable.Range(0, 4)
                        .Select(index => BinaryPrimitives.ReadInt32LittleEndian(database.AsSpan(start + 148 + index * 4, 4)))
                        .Where(id => id > 0 && id != techId).Distinct().ToArray(),
                    ProducerObjectIds = arrays[0],
                    AdditionalTechnologyIds = arrays[1],
                    // Executable 0x0067231E..0x006724F6 initializes this exact mask:
                    // raw +312 is universal; raw +315..+323 are rows 3..11.
                    // Rows 1 and 2 are explicitly disabled by the loader.
                    // Availability consumer 0x00672D82..0x00672DB1 tests this mask.
                    AllowedCivilizationRows = ReadCivilizationMask(record),
                    UniversalCivilizationMembership = record[UniversalCivilizationMaskOffset] != 0,
                    CommonButtonSlot = BinaryPrimitives.ReadInt32LittleEndian(record[276..]),
                    CivilizationButtonSlots = Enumerable.Range(0, 11).Select(index =>
                        BinaryPrimitives.ReadInt32LittleEndian(database.AsSpan(start + ProgressionButtonSlotsOffset + index * 8, 4))).ToArray()
                });
            }
        }
        return records.AsReadOnly();
    }

    public static IReadOnlyDictionary<string, string> ReadObjectValues(byte[] database, int techId,
        CivilizationCategory category)
    {
        var prefix = Prefix(category);
        if (prefix is null) return new Dictionary<string, string>();
        var technology = ReadRecords(database).SingleOrDefault(record => record.TechId == techId);
        if (technology is null) return new Dictionary<string, string>();
        return ReadObjectValues(technology, category);
    }

    public static IReadOnlyDictionary<string, string> ReadObjectValues(CivilizationTechnologyRecord technology,
        CivilizationCategory category)
    {
        ArgumentNullException.ThrowIfNull(technology);
        var prefix = Prefix(category);
        if (prefix is null) return new Dictionary<string, string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [prefix + ".woodCost"] = technology.WoodCost.ToString(CultureInfo.InvariantCulture),
            [prefix + ".stoneCost"] = technology.StoneCost.ToString(CultureInfo.InvariantCulture),
            [prefix + ".goldCost"] = technology.GoldCost.ToString(CultureInfo.InvariantCulture),
            [prefix + ".foodCost"] = technology.FoodCost.ToString(CultureInfo.InvariantCulture),
            [prefix + ".productionCoefficient"] = technology.ProductionCoefficient.ToString(CultureInfo.InvariantCulture),
            [prefix + ".initialAge"] = technology.StartAge.ToString(CultureInfo.InvariantCulture),
            [prefix + ".finalAge"] = technology.EndAge.ToString(CultureInfo.InvariantCulture),
            [prefix + ".age"] = technology.StartAge == technology.EndAge
                ? technology.StartAge.ToString(CultureInfo.InvariantCulture)
                : $"{technology.StartAge.ToString(CultureInfo.InvariantCulture)}–{technology.EndAge.ToString(CultureInfo.InvariantCulture)}"
        };
        if (category is CivilizationCategory.Technologies or CivilizationCategory.Abilities)
            values[prefix + ".internalName"] = technology.Identifier;
        // This is configured build/research eligibility, not the current player's
        // dynamically researched/prerequisite state.
        values["available"] = (!technology.EditorOnly).ToString(CultureInfo.InvariantCulture);
        values[prefix + ".prerequisites"] = FormatTechnologyIds(technology.PrerequisiteTechnologyIds);
        if (category == CivilizationCategory.Technologies)
            values["technology.researchedAt"] = FormatObjectIds(technology.ProducerObjectIds);
        else if (category == CivilizationCategory.Abilities)
            values["ability.owners"] = FormatObjectIds(technology.ProducerObjectIds);
        else values[prefix + ".producedAt"] = FormatObjectIds(technology.ProducerObjectIds);
        return values;
    }

    public static void WriteObjectValue(byte[] database, int techId, CivilizationCategory category,
        string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var field = GetFields(category).SingleOrDefault(candidate => candidate.Key == key)
            ?? throw new InvalidDataException($"Production field '{key}' has no verified writable mapping.");
        // Two authored endpoints may temporarily cross while they are applied.
        // The planner and package manager validate the complete resulting table.
        var technology = ReadRecords(database, validateAgeIntervals: false).SingleOrDefault(record => record.TechId == techId)
            ?? throw new InvalidDataException($"Technology ID {techId} is not present in the game table.");
        if (string.IsNullOrWhiteSpace(value)) return;
        if (field.Type == CivilizationBinaryType.String100)
        {
            if (value.Length > 99 || value.Any(character => character is < ' ' or > '~'))
                throw new InvalidDataException("A technology identifier requires 1 to 99 printable ASCII characters.");
            database.AsSpan(technology.Offset + field.Offset, 100).Clear();
            Encoding.ASCII.GetBytes(value, database.AsSpan(technology.Offset + field.Offset, 100));
            return;
        }
        if (field.Type == CivilizationBinaryType.Int32)
        {
            if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var age) || age is < 0 or > 100)
                throw new InvalidDataException("Use an integer age ID from 0 to 100 (playable EDMW ages are 5 through 9).");
            BinaryPrimitives.WriteInt32LittleEndian(database.AsSpan(technology.Offset + field.Offset, 4), age);
            return;
        }
        if (!uint.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw new InvalidDataException("Production costs and coefficients require integers from 0 to 4294967295.");
        BinaryPrimitives.WriteUInt32LittleEndian(database.AsSpan(technology.Offset + field.Offset, 4), number);
    }

    /// <summary>Checks combined native availability intervals after all field writes.</summary>
    public static void ValidateAgeIntervals(byte[] database) => _ = ReadRecords(database);

    /// <summary>Joins the verified object tech-ID field to technology civilization rows.</summary>
    public static IReadOnlyList<int> GetCivilizationObjectIds(byte[] objectsDatabase,
        byte[] technologyDatabase, int civilizationRowIndex, CivilizationCategory category,
        int initialAge = 0, int finalAge = 100) =>
        GetCivilizationObjectIds(objectsDatabase, ReadRecords(technologyDatabase), civilizationRowIndex,
            category, initialAge, finalAge);

    public static IReadOnlyList<int> GetCivilizationObjectIds(byte[] objectsDatabase,
        IReadOnlyList<CivilizationTechnologyRecord> records, int civilizationRowIndex,
        CivilizationCategory category, int initialAge = 0, int finalAge = 100)
    {
        if (category is not (CivilizationCategory.Units or CivilizationCategory.Buildings)) return [];
        var objects = ReadProductionObjects(objectsDatabase, records, initialAge, finalAge);
        var owned = ResolveOwnedObjects(objects, civilizationRowIndex);
        return objects.Where(item => owned.Contains(item.Index) &&
            item.Building == (category == CivilizationCategory.Buildings))
            .Select(item => item.Index).ToArray();
    }

    public static IReadOnlyList<CivilizationTechnologyRecord> GetCivilizationTechnologyRecords(
        byte[] objectsDatabase, byte[] technologyDatabase, int civilizationRowIndex, int initialAge, int finalAge) =>
        GetCivilizationTechnologyRecords(objectsDatabase, ReadRecords(technologyDatabase), civilizationRowIndex, initialAge, finalAge);

    public static IReadOnlyList<CivilizationTechnologyRecord> GetCivilizationTechnologyRecords(
        byte[] objectsDatabase, IReadOnlyList<CivilizationTechnologyRecord> records,
        int civilizationRowIndex, int initialAge, int finalAge)
    {
        var objects = ReadProductionObjects(objectsDatabase, records, initialAge, finalAge);
        var owned = ResolveOwnedObjects(objects, civilizationRowIndex);
        var ownedBuildings = objects.Where(item => item.Building && owned.Contains(item.Index))
            .Select(item => item.Index).ToHashSet();
        var objectTechIds = GetAllObjectTechnologyIds(objectsDatabase);
        return records.Where(record => !record.IsEpochDefinition && !record.EditorOnly &&
            record.AllowsCivilizationRow(civilizationRowIndex) &&
            record.StartAge <= finalAge && record.EndAge >= initialAge &&
            !objectTechIds.Contains(record.TechId) && IsResearchRecord(record) &&
            (record.CivilizationRowIndex == civilizationRowIndex ||
                record.ProducerObjectIds.Any(ownedBuildings.Contains)))
            .ToArray();
    }

    /// <summary>Existing objects whose current producers can create them for this civilization.
    /// Only the selected civilization mask is ignored for candidates; source identity, ages,
    /// editor availability and producer ownership continue to be checked.</summary>
    public static IReadOnlyList<int> GetCivilizationProgressionObjectIds(byte[] objectsDatabase,
        IReadOnlyList<CivilizationTechnologyRecord> records, int civilizationRowIndex,
        CivilizationCategory category, int initialAge, int finalAge, bool includeCandidates)
    {
        if (!includeCandidates)
            return GetCivilizationObjectIds(objectsDatabase, records, civilizationRowIndex, category, initialAge, finalAge);
        if (category is not (CivilizationCategory.Units or CivilizationCategory.Buildings)) return [];
        var objects = ReadProductionObjects(objectsDatabase, records, initialAge, finalAge);
        var owned = ResolveOwnedObjects(objects, civilizationRowIndex);
        var exclusive = ExclusiveProgressionProducers(ReadProductionObjects(objectsDatabase, records, 0, 100), owned, civilizationRowIndex);
        return objects.Where(item => item.Building == (category == CivilizationCategory.Buildings) &&
            (owned.Contains(item.Index) || IsPublicProgressionObject(objectsDatabase, item.Index) &&
                item.Technology.CreatesObject && item.Technology.CreatedObjectId == item.Index &&
                IsPublicProgressionName(item.Technology.Identifier) && item.Technology.ButtonId > 0 &&
                CompatibleProgressionProducers(objectsDatabase, objects, owned, exclusive, item.Technology).Count > 0))
            .Select(item => item.Index).ToArray();
    }

    /// <summary>Research and advances using an existing producer belonging to this civilization.</summary>
    public static IReadOnlyList<CivilizationTechnologyRecord> GetCivilizationProgressionTechnologyRecords(
        byte[] objectsDatabase, IReadOnlyList<CivilizationTechnologyRecord> records,
        int civilizationRowIndex, int initialAge, int finalAge, bool includeCandidates)
    {
        var original = GetCivilizationTechnologyRecords(objectsDatabase, records, civilizationRowIndex, initialAge, finalAge)
            .Concat(GetCivilizationAgeAdvanceRecords(objectsDatabase, records, civilizationRowIndex, initialAge, finalAge))
            .DistinctBy(record => record.TechId).ToArray();
        if (!includeCandidates) return original;
        var originalIds = original.Select(record => record.TechId).ToHashSet();
        var objects = ReadProductionObjects(objectsDatabase, records, initialAge, finalAge);
        var owned = ResolveOwnedObjects(objects, civilizationRowIndex);
        var exclusive = ExclusiveProgressionProducers(ReadProductionObjects(objectsDatabase, records, 0, 100), owned, civilizationRowIndex);
        var objectTechIds = GetAllObjectTechnologyIds(objectsDatabase);
        return records.Where(record => originalIds.Contains(record.TechId) ||
            !record.IsEpochDefinition && !record.EditorOnly && record.StartAge <= finalAge && record.EndAge >= initialAge &&
            IsPublicProgressionName(record.Identifier) && record.ButtonId > 0 &&
            (IsAgeAdvanceRecord(record) || IsResearchRecord(record) && !objectTechIds.Contains(record.TechId)) &&
            CompatibleProgressionProducers(objectsDatabase, objects, owned, exclusive, record).Count > 0)
            .ToArray();
    }

    public static IReadOnlyList<int> GetProgressionProducerChoices(byte[] objectsDatabase,
        IReadOnlyList<CivilizationTechnologyRecord> records, int techId, int civilizationRowIndex,
        int initialAge, int finalAge)
    {
        var technology = records.SingleOrDefault(record => record.TechId == techId);
        if (technology is null || technology.IsEpochDefinition || technology.EditorOnly) return [];
        var objects = ReadProductionObjects(objectsDatabase, records, initialAge, finalAge);
        var owned = ResolveOwnedObjects(objects, civilizationRowIndex);
        var exclusive = ExclusiveProgressionProducers(ReadProductionObjects(objectsDatabase, records, 0, 100), owned, civilizationRowIndex);
        return CompatibleProgressionProducers(objectsDatabase, objects, owned, exclusive, technology);
    }

    public static IReadOnlyDictionary<int, IReadOnlyList<int>> GetProgressionProducerChoicesByTechnology(
        byte[] objectsDatabase, IReadOnlyList<CivilizationTechnologyRecord> records,
        int civilizationRowIndex, int initialAge, int finalAge)
    {
        var objects = ReadProductionObjects(objectsDatabase, records, initialAge, finalAge);
        var owned = ResolveOwnedObjects(objects, civilizationRowIndex);
        var exclusive = ExclusiveProgressionProducers(ReadProductionObjects(objectsDatabase, records, 0, 100), owned, civilizationRowIndex);
        return records.Where(record => !record.IsEpochDefinition && !record.EditorOnly &&
                record.StartAge <= finalAge && record.EndAge >= initialAge)
            .ToDictionary(record => record.TechId,
                record => CompatibleProgressionProducers(objectsDatabase, objects, owned, exclusive, record));
    }

    private static HashSet<int> ExclusiveProgressionProducers(List<ProductionObject> objects, HashSet<int> owned, int selectedRow)
    {
        var exclusive = owned.ToHashSet();
        for (var row = 3; row <= 11; row++)
            if (row != selectedRow) exclusive.ExceptWith(ResolveOwnedObjects(objects, row));
        return exclusive;
    }

    private static IReadOnlyList<int> CompatibleProgressionProducers(byte[] database,
        IReadOnlyList<ProductionObject> objects, HashSet<int> owned, HashSet<int> exclusive, CivilizationTechnologyRecord technology)
    {
        var count = ValidateObjects(database);
        var sourceProducers = technology.ProducerObjectIds.Where(id => id >= 0 && id < count &&
            IsPublicProgressionObject(database, id)).ToArray();
        if (sourceProducers.Length == 0) return [];
        (bool Building, uint Family, string Type) Role(int id)
        {
            var record = database.AsSpan(4 + id * 0x790, 0x790);
            return (ReadIdentifier(record[..100]).StartsWith("b ", StringComparison.OrdinalIgnoreCase),
                BinaryPrimitives.ReadUInt32LittleEndian(record[CivilizationObjectDetails.FamilyIdOffset..]),
                ReadIdentifier(record.Slice(CivilizationObjectDetails.DefinitionTypeOffset, CivilizationObjectDetails.DefinitionTypeBytes)));
        }
        string Name(int id) => ReadIdentifier(database.AsSpan(4 + id * 0x790, 100));
        string NameRole(int id) => Name(id).Split('(')[0].Trim();
        var sourceRoles = sourceProducers.Select(Role).ToHashSet();
        var sourceNames = sourceProducers.Select(NameRole).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return objects.Where(item => owned.Contains(item.Index) && IsPublicProgressionObject(database, item.Index) &&
                sourceRoles.Contains(Role(item.Index)) && (exclusive.Contains(item.Index) ||
                    technology.ProducerObjectIds.Contains(item.Index) && CivilizationObjectDetails.ReadActionTechnologyIds(database,
                        item.Index, item.Building ? CivilizationCategory.Buildings : CivilizationCategory.Units).Contains((uint)technology.TechId)))
            .OrderByDescending(item => technology.ProducerObjectIds.Contains(item.Index))
            .ThenByDescending(item => sourceNames.Contains(NameRole(item.Index)))
            .ThenBy(item => Name(item.Index), StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Index).ToArray();
    }

    private static bool IsPublicProgressionObject(byte[] database, int objectId)
    {
        var offset = 4 + objectId * 0x790;
        return database[offset + 232] != 0 && IsPublicProgressionName(ReadIdentifier(database.AsSpan(offset, 100)));
    }

    private static bool IsPublicProgressionName(string name) => !string.IsNullOrWhiteSpace(name) &&
        !name.StartsWith("zzz", StringComparison.OrdinalIgnoreCase) &&
        !name.StartsWith("XXQ", StringComparison.OrdinalIgnoreCase) &&
        !name.StartsWith("EDITOR ", StringComparison.OrdinalIgnoreCase) &&
        !name.StartsWith("EE ", StringComparison.OrdinalIgnoreCase) &&
        !name.StartsWith("XSP", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("DELETE", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("[unused]", StringComparison.OrdinalIgnoreCase);

    /// <summary>Existing age-advance actions linked to this civilization's actual producers.</summary>
    public static IReadOnlyList<CivilizationTechnologyRecord> GetCivilizationAgeAdvanceRecords(
        byte[] objectsDatabase, IReadOnlyList<CivilizationTechnologyRecord> records,
        int civilizationRowIndex, int initialAge, int finalAge)
    {
        var objects = ReadProductionObjects(objectsDatabase, records, initialAge, finalAge);
        var owned = ResolveOwnedObjects(objects, civilizationRowIndex);
        return records.Where(record => IsAgeAdvanceRecord(record) && !record.EditorOnly &&
            record.AllowsCivilizationRow(civilizationRowIndex) &&
            record.StartAge <= finalAge && record.EndAge >= initialAge &&
            record.ProducerObjectIds.Any(owned.Contains)).ToArray();
    }

    public static bool IsAgeAdvanceRecord(CivilizationTechnologyRecord record) =>
        !record.IsEpochDefinition && !record.CreatesObject && record.NodeKind == 2;

    /// <summary>Command/calamity nodes associated with this civilization's actual producers.</summary>
    public static IReadOnlyList<CivilizationTechnologyRecord> GetCivilizationAbilityRecords(
        byte[] objectsDatabase, IReadOnlyList<CivilizationTechnologyRecord> records,
        int civilizationRowIndex, int initialAge, int finalAge)
    {
        var objects = ReadProductionObjects(objectsDatabase, records, initialAge, finalAge);
        var owned = ResolveOwnedObjects(objects, civilizationRowIndex);
        var commandIds = new HashSet<int>();
        foreach (var objectId in owned)
            for (var slot = 0; slot < 9; slot++)
            {
                // Exact dbobjects command slots. Consumers resolve these values
                // through GetTechnologyById, e.g. 0x0076D148..0x0076D155.
                var id = BinaryPrimitives.ReadInt32LittleEndian(objectsDatabase.AsSpan(4 + objectId * 0x790 + 368 + slot * 4, 4));
                if (id >= 0) commandIds.Add(id);
            }
        return records.Where(record => IsAbilityRecord(record) && !record.IsEpochDefinition && !record.EditorOnly &&
            record.AllowsCivilizationRow(civilizationRowIndex) && record.StartAge <= finalAge && record.EndAge >= initialAge &&
            (record.CivilizationRowIndex == civilizationRowIndex ||
                record.ProducerObjectIds.Any(owned.Contains) || commandIds.Contains(record.TechId))).ToArray();
    }

    public static bool IsResearchRecord(CivilizationTechnologyRecord record) =>
        !record.CreatesObject && record.NodeKind is 0 or 7;

    public static bool IsAbilityRecord(CivilizationTechnologyRecord record) =>
        !record.CreatesObject && record.NodeKind is 3 or 5 or 8;

    public static bool IsTechnologyForCivilization(byte[] objectsDatabase, byte[] technologyDatabase,
        int techId, int civilizationRowIndex, int initialAge, int finalAge) =>
        IsTechnologyForCivilization(objectsDatabase, ReadRecords(technologyDatabase), techId, civilizationRowIndex,
            initialAge, finalAge);

    public static bool IsTechnologyForCivilization(byte[] objectsDatabase,
        IReadOnlyList<CivilizationTechnologyRecord> records, int techId, int civilizationRowIndex,
        int initialAge, int finalAge)
    {
        var technology = records.SingleOrDefault(record => record.TechId == techId);
        if (technology is null || technology.EditorOnly || technology.IsEpochDefinition ||
            !technology.AllowsCivilizationRow(civilizationRowIndex) ||
            technology.StartAge > finalAge || technology.EndAge < initialAge) return false;
        if (IsAbilityRecord(technology))
            return GetCivilizationAbilityRecords(objectsDatabase, records, civilizationRowIndex, initialAge, finalAge)
                .Any(record => record.TechId == techId);
        var objects = ReadProductionObjects(objectsDatabase, records, initialAge, finalAge);
        var owned = ResolveOwnedObjects(objects, civilizationRowIndex);
        return technology.CivilizationRowIndex == civilizationRowIndex ||
            technology.ProducerObjectIds.Any(owned.Contains);
    }

    private static List<ProductionObject> ReadProductionObjects(byte[] database,
        IReadOnlyList<CivilizationTechnologyRecord> records, int initialAge, int finalAge)
    {
        if (initialAge < 0 || finalAge < initialAge) throw new ArgumentException("The civilization age interval is invalid.");
        var count = ValidateObjects(database);
        var technologies = records.ToDictionary(record => record.TechId);
        var objects = new List<ProductionObject>();
        for (var index = 0; index < count; index++)
        {
            var record = database.AsSpan(4 + index * 0x790, 0x790);
            if (record[232] == 0) continue;
            var techId = BinaryPrimitives.ReadInt32LittleEndian(record[900..]);
            if (!technologies.TryGetValue(techId, out var technology) || technology.EditorOnly || technology.IsEpochDefinition ||
                technology.StartAge > finalAge || technology.EndAge < initialAge) continue;
            var name = ReadIdentifier(record[..100]);
            objects.Add(new(index, name.StartsWith("b ", StringComparison.OrdinalIgnoreCase), technology));
        }
        return objects;
    }

    private static HashSet<int> ResolveOwnedObjects(List<ProductionObject> objects, int civilizationRowIndex)
    {
        if (civilizationRowIndex <= 0) throw new ArgumentOutOfRangeException(nameof(civilizationRowIndex));
        var owned = objects.Where(item => item.Technology.CivilizationRowIndex == civilizationRowIndex &&
                item.Technology.AllowsCivilizationRow(civilizationRowIndex))
            .Select(item => item.Index).ToHashSet();
        bool added;
        do
        {
            added = false;
            foreach (var item in objects)
                // Native membership + mask checks do not require raw +204 to
                // equal the selected civ. Stock Citizen(France)6157 is tagged9
                // but produced by Franks1117/mask5; Citizen(French)9237 is
                // tagged5 but produced by French1352/mask9. The +204 tag is
                // therefore only a seed, never a veto of a verified producer.
                if (!owned.Contains(item.Index) &&
                    item.Technology.AllowsCivilizationRow(civilizationRowIndex) &&
                    item.Technology.ProducerObjectIds.Any(owned.Contains)) added |= owned.Add(item.Index);
        } while (added);
        return owned;
    }

    private static HashSet<int> GetAllObjectTechnologyIds(byte[] database)
    {
        var count = ValidateObjects(database);
        var ids = new HashSet<int>();
        for (var index = 0; index < count; index++)
            ids.Add(BinaryPrimitives.ReadInt32LittleEndian(database.AsSpan(4 + index * 0x790 + 900, 4)));
        return ids;
    }

    private static int ValidateObjects(byte[] database)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.Length < 4) throw new InvalidDataException("The object table is empty.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(database);
        if (count is <= 0 or > MaximumRecords || database.LongLength != 4L + (long)count * 0x790)
            throw new InvalidDataException("The object table has an unsupported record layout.");
        return count;
    }

    private sealed record ProductionObject(int Index, bool Building, CivilizationTechnologyRecord Technology);

    private static IReadOnlyList<int> ReadCivilizationMask(ReadOnlySpan<byte> record)
    {
        var rows = new List<int>();
        for (var row = 3; row <= 11; row++)
            if (record[312] != 0 || record[312 + row] != 0) rows.Add(row);
        return rows.AsReadOnly();
    }

    private static string? Prefix(CivilizationCategory category) => category switch
    {
        CivilizationCategory.Units => "unit",
        CivilizationCategory.Buildings => "building",
        CivilizationCategory.Technologies => "technology",
        CivilizationCategory.Abilities => "ability",
        _ => null
    };

    private static string FormatTechnologyIds(IEnumerable<int> ids) => string.Join(", ", ids.Select(id => $"Technology #{id}"));
    private static string FormatObjectIds(IEnumerable<int> ids) => string.Join(", ", ids.Select(id => $"Object #{id}"));

    private static int ReadCount(byte[] database, int offset, int maximum)
    {
        EnsureRange(database, offset, 4);
        var count = BinaryPrimitives.ReadInt32LittleEndian(database.AsSpan(offset, 4));
        if (count < 0 || count > maximum) throw new InvalidDataException("The technology table has an invalid count.");
        return count;
    }

    private static void EnsureRange(byte[] database, int offset, int length)
    {
        if (offset < 0 || length < 0 || (long)offset + length > database.Length)
            throw new InvalidDataException("The technology table contains a truncated record or reference array.");
    }

    private static string ReadIdentifier(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException("A database identifier is not terminated.");
        bytes = bytes[..end];
        foreach (var value in bytes)
            if (value is < 32 or > 126) throw new InvalidDataException("A database identifier is not printable ASCII.");
        return Encoding.ASCII.GetString(bytes);
    }
}
