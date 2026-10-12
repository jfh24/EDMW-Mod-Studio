using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace EDMWMapDesigner.Civilizations;

public sealed record CivilizationObjectLocalizationReference(string Key, string Library, uint StringId);

/// <summary>
/// Read-only object metadata and explicit technology references. Layout comes from
/// aczwink/EDMW-Modding-Tools dbobjects.xml, dbfamily.xml and dbweapontohit.xml.
/// Names and reference numbers do not imply unverified behavior or availability.
/// </summary>
public static class CivilizationObjectDetails
{
    public const int FamilyIdOffset = 104;
    public const int DefinitionTypeOffset = 240;
    public const int DefinitionTypeBytes = 32;
    public const int CommandTechnologyIdsOffset = 368;
    public const int CommandTechnologyCount = 9;
    public const int ActionTechnologyIdsOffset = 1136;
    public const int ActionTechnologyCountOffset = 896;
    public const int ActionTechnologyCount = 199;

    public static IReadOnlyDictionary<string, string> ReadValues(byte[] objects, int row,
        CivilizationCategory category)
    {
        var offset = CivilizationObjectBindings.ValidateRecord(objects, row, category);
        var prefix = Prefix(category);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [prefix + ".definitionType"] = ReadText(objects.AsSpan(offset + DefinitionTypeOffset, DefinitionTypeBytes)),
            [prefix + ".familyId"] = ReadUInt(objects, offset + FamilyIdOffset).ToString(CultureInfo.InvariantCulture),
            [prefix + ".sourceTechnologyId"] = ReadUInt(objects, offset + CivilizationObjectBindings.TechnologyIdOffset)
                .ToString(CultureInfo.InvariantCulture),
            [prefix + ".commandTechnologyIds"] = FormatIds(ReadCommandTechnologyIds(objects, row, category)),
            [prefix + ".actionTechnologyIds"] = FormatIds(ReadActionTechnologyIds(objects, row, category))
        };
        foreach (var reference in ReadLocalizationReferences(objects, row, category))
            values[reference.Key] = reference.StringId == uint.MaxValue ? "-1" :
                reference.StringId.ToString(CultureInfo.InvariantCulture);
        return new ReadOnlyDictionary<string, string>(values);
    }

    public static uint ReadFamilyId(byte[] objects, int row, CivilizationCategory category) =>
        ReadUInt(objects, CivilizationObjectBindings.ValidateRecord(objects, row, category) + FamilyIdOffset);

    /// <summary>All references, including zero and UINT_MAX sentinels, with their proven string library.</summary>
    public static IReadOnlyList<CivilizationObjectLocalizationReference> ReadLocalizationReferences(
        byte[] objects, int row, CivilizationCategory category)
    {
        var offset = CivilizationObjectBindings.ValidateRecord(objects, row, category);
        var prefix = Prefix(category);
        return Array.AsReadOnly(new[]
        {
            new CivilizationObjectLocalizationReference(prefix + ".nameStringId", "Language2.dll", ReadUInt(objects, offset + 296)),
            new CivilizationObjectLocalizationReference(prefix + ".unitStringId1", "Language.dll", ReadUInt(objects, offset + 612)),
            new CivilizationObjectLocalizationReference(prefix + ".unitStringId2", "Language.dll", ReadUInt(objects, offset + 616)),
            new CivilizationObjectLocalizationReference(prefix + ".tooltipStringId", "Language.dll", ReadUInt(objects, offset + 620)),
            new CivilizationObjectLocalizationReference(prefix + ".appendNameStringId", "Language2.dll", ReadUInt(objects, offset + 624))
        });
    }

    /// <summary>
    /// Nine explicit HUD command slots, int32 IDs with -1 meaning empty. Sample
    /// IDs join dbtechtree.dat's ACTION records, rather than dbbuttons.dat rows.
    /// </summary>
    public static IReadOnlyList<int> ReadCommandTechnologyIds(byte[] objects, int row,
        CivilizationCategory category)
    {
        var offset = CivilizationObjectBindings.ValidateRecord(objects, row, category);
        var ids = new List<int>();
        for (var slot = 0; slot < CommandTechnologyCount; slot++)
        {
            var id = BinaryPrimitives.ReadInt32LittleEndian(objects.AsSpan(offset + CommandTechnologyIdsOffset + slot * 4, 4));
            if (id < -1) throw new InvalidDataException("An object command reference uses an unsupported sentinel.");
            if (id >= 0) ids.Add(id);
        }
        return ids.AsReadOnly();
    }

    /// <summary>
    /// Stored-order active references from the schema's constructionTechIds array.
    /// Workers list construction; buildings also list production and research.
    /// Entries retain their numbers; a separate technology join establishes type
    /// and civilization/age availability. UINT_MAX is the empty-slot sentinel.
    /// </summary>
    public static IReadOnlyList<uint> ReadActionTechnologyIds(byte[] objects, int row,
        CivilizationCategory category)
    {
        var offset = CivilizationObjectBindings.ValidateRecord(objects, row, category);
        var count = BinaryPrimitives.ReadInt32LittleEndian(objects.AsSpan(offset + ActionTechnologyCountOffset, 4));
        if (count is < 0 or > ActionTechnologyCount)
            throw new InvalidDataException("An object progression action count is outside its verified 199-slot capacity.");
        var ids = new List<uint>();
        // Constructor 0x006FDAFC..0x006FDB36 copies only this active prefix.
        for (var slot = 0; slot < count; slot++)
        {
            var id = ReadUInt(objects, offset + ActionTechnologyIdsOffset + slot * 4);
            if (id != uint.MaxValue) ids.Add(id);
        }
        return ids.AsReadOnly();
    }

    public static IReadOnlyDictionary<int, string> ReadFamilyNames(byte[] database) =>
        ReadReferenceNames(database, 412, "family");

    public static IReadOnlyDictionary<int, string> ReadWeaponNames(byte[] database) =>
        ReadReferenceNames(database, 132, "weapon-to-hit");

    private static IReadOnlyDictionary<int, string> ReadReferenceNames(byte[] database,
        int stride, string tableName)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.Length < 4) throw new InvalidDataException($"The {tableName} database header is truncated.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(database);
        if (count is 0 or > 100_000 || database.LongLength != 4L + (long)count * stride)
            throw new InvalidDataException($"The {tableName} database has an unsupported record layout.");
        var names = new Dictionary<int, string>();
        for (var row = 0; row < count; row++)
        {
            var offset = checked(4 + row * stride);
            // Both source tables store contiguous row identity in their second
            // integer. Their first integer is a different, noncontiguous ID.
            if (ReadUInt(database, offset + 104) != (uint)row)
                throw new InvalidDataException($"The {tableName} database identity does not match its row.");
            names.Add(row, ReadText(database.AsSpan(offset, 100)));
        }
        return new ReadOnlyDictionary<int, string>(names);
    }

    private static uint ReadUInt(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

    private static string ReadText(ReadOnlySpan<byte> bytes)
    {
        var terminator = bytes.IndexOf((byte)0);
        if (terminator >= 0) bytes = bytes[..terminator];
        foreach (var value in bytes)
            if (value is < 32 or > 126) throw new InvalidDataException("An object metadata identifier is not printable ASCII.");
        return Encoding.ASCII.GetString(bytes).Trim();
    }

    private static string FormatIds<T>(IEnumerable<T> ids) where T : IFormattable =>
        string.Join(", ", ids.Select(id => id.ToString(null, CultureInfo.InvariantCulture)));

    private static string Prefix(CivilizationCategory category) => category switch
    {
        CivilizationCategory.Units => "unit",
        CivilizationCategory.Buildings => "building",
        _ => throw new NotSupportedException("Only object units and buildings have object details.")
    };
}
