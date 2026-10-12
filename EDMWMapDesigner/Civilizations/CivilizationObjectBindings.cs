using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace EDMWMapDesigner.Civilizations;

public enum CivilizationBinaryType { Int32, UInt32, Float32, Byte, String100, BooleanByte }

/// <summary>Numeric display value = stored value / Scale. Offset is relative to a record.</summary>
public sealed record CivilizationBinaryField(string Key, int Offset,
    CivilizationBinaryType Type, double Scale = 1, string? Evidence = null);

/// <summary>
/// Named EDMW object fields corroborated by the game's primary modding schema
/// and the installed fixed-width database. Writes affect a supplied byte array,
/// never a game file. Technology costs and unverified fields are not mapped here.
/// </summary>
public static class CivilizationObjectBindings
{
    public const int RecordSize = 0x790;
    public const int RecordIdentityOffset = 116;
    public const int TechnologyIdOffset = 900;
    private const string SchemaEvidence =
        "https://github.com/aczwink/EDMW-Modding-Tools/blob/master/db/dbobjects.xml";
    private static readonly IReadOnlyList<CivilizationBinaryField> UnitFields = CreateFields("unit", true);
    private static readonly IReadOnlyList<CivilizationBinaryField> BuildingFields = CreateFields("building", false);

    public static IReadOnlyList<CivilizationBinaryField> Fields(CivilizationCategory category) => category switch
    {
        CivilizationCategory.Units => UnitFields,
        CivilizationCategory.Buildings => BuildingFields,
        _ => Array.Empty<CivilizationBinaryField>()
    };

    public static bool CanWrite(CivilizationCategory category, string key) =>
        Fields(category).Any(field => field.Key.Equals(key, StringComparison.Ordinal));

    public static IReadOnlyDictionary<string, string> ReadValues(byte[] objectDatabase,
        int sourceObjectId, CivilizationCategory category)
    {
        var recordOffset = ValidateRecord(objectDatabase, sourceObjectId, category);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in Fields(category))
        {
            var offset = recordOffset + field.Offset;
            values[field.Key] = field.Type switch
            {
                CivilizationBinaryType.String100 => ReadIdentifier(objectDatabase.AsSpan(offset, 100)),
                CivilizationBinaryType.UInt32 => FormatNumber(
                    BinaryPrimitives.ReadUInt32LittleEndian(objectDatabase.AsSpan(offset, 4)), field.Scale),
                CivilizationBinaryType.Int32 => FormatNumber(
                    BinaryPrimitives.ReadInt32LittleEndian(objectDatabase.AsSpan(offset, 4)), field.Scale),
                CivilizationBinaryType.Float32 => ReadFloat(objectDatabase, offset, field.Scale),
                CivilizationBinaryType.Byte => FormatNumber(objectDatabase[offset], field.Scale),
                CivilizationBinaryType.BooleanByte => objectDatabase[offset] switch
                {
                    0 => "false",
                    1 => "true",
                    _ => throw new InvalidDataException("A source object Boolean field is outside its supported range.")
                },
                _ => throw new InvalidDataException("Unsupported object field type.")
            };
        }
        return new ReadOnlyDictionary<string, string>(values);
    }

    public static uint ReadTechnologyId(byte[] objectDatabase, int sourceObjectId,
        CivilizationCategory category)
    {
        var offset = ValidateRecord(objectDatabase, sourceObjectId, category) + TechnologyIdOffset;
        return BinaryPrimitives.ReadUInt32LittleEndian(objectDatabase.AsSpan(offset, 4));
    }

    public static void WriteValue(byte[] database, int sourceObjectId,
        CivilizationCategory category, string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        var field = Fields(category).FirstOrDefault(candidate =>
            candidate.Key.Equals(key, StringComparison.Ordinal)) ??
            throw new NotSupportedException($"The game field '{key}' does not have a verified object binding.");
        var offset = ValidateRecord(database, sourceObjectId, category) + field.Offset;
        if (field.Type == CivilizationBinaryType.String100)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 99 ||
                value.Any(character => character < 32 || character > 126))
                throw new InvalidDataException("Internal names must contain 1–99 printable ASCII characters.");
            if (value.StartsWith("b ", StringComparison.OrdinalIgnoreCase) !=
                (category == CivilizationCategory.Buildings))
                throw new InvalidDataException("Internal names must preserve the source catalog's building prefix.");
            var bytes = Encoding.ASCII.GetBytes(value);
            database.AsSpan(offset, 100).Clear();
            bytes.CopyTo(database.AsSpan(offset, bytes.Length));
            return;
        }

        if (field.Type == CivilizationBinaryType.BooleanByte)
        {
            if (!bool.TryParse(value, out var enabled))
                throw new InvalidDataException($"The game field '{key}' requires true or false.");
            database[offset] = enabled ? (byte)1 : (byte)0;
            return;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            !double.IsFinite(number) || number < 0 ||
            (key.EndsWith(".health", StringComparison.Ordinal) && number < 1))
            throw new InvalidDataException($"The game field '{key}' requires a finite nonnegative number" +
                (key.EndsWith(".health", StringComparison.Ordinal) ? " of at least 1." : "."));
        var stored = number * field.Scale;
        if (!double.IsFinite(stored))
            throw new InvalidDataException($"The value for '{key}' exceeds its binary range.");

        switch (field.Type)
        {
            case CivilizationBinaryType.UInt32:
                if (stored != Math.Truncate(stored) || stored > uint.MaxValue)
                    throw new InvalidDataException($"The game field '{key}' requires an unsigned 32-bit integer.");
                var unsigned = (uint)stored;
                if (key.EndsWith(".projectile", StringComparison.Ordinal) && unsigned != uint.MaxValue &&
                    unsigned >= BinaryPrimitives.ReadUInt32LittleEndian(database.AsSpan(0, 4)))
                    throw new InvalidDataException("The projectile reference points outside the object database.");
                BinaryPrimitives.WriteUInt32LittleEndian(database.AsSpan(offset, 4), unsigned);
                break;
            case CivilizationBinaryType.Int32:
                if (stored != Math.Truncate(stored) || stored > int.MaxValue)
                    throw new InvalidDataException($"The game field '{key}' requires a 32-bit integer.");
                BinaryPrimitives.WriteInt32LittleEndian(database.AsSpan(offset, 4), (int)stored);
                break;
            case CivilizationBinaryType.Float32:
                var single = (float)stored;
                if (!float.IsFinite(single))
                    throw new InvalidDataException($"The game field '{key}' exceeds its 32-bit float range.");
                BinaryPrimitives.WriteInt32LittleEndian(database.AsSpan(offset, 4),
                    BitConverter.SingleToInt32Bits(single));
                break;
            case CivilizationBinaryType.Byte:
                if (stored != Math.Truncate(stored) || stored > byte.MaxValue)
                    throw new InvalidDataException($"The game field '{key}' requires a byte value.");
                database[offset] = (byte)stored;
                break;
            default:
                throw new InvalidDataException("Unsupported object field type.");
        }
    }

    private static IReadOnlyList<CivilizationBinaryField> CreateFields(string prefix, bool unit)
    {
        CivilizationBinaryField Field(string key, int offset, CivilizationBinaryType type, string name) =>
            new($"{prefix}.{key}", offset, type, Evidence: $"{SchemaEvidence} — {name}");
        var fields = new List<CivilizationBinaryField>
        {
            Field("internalName", 0, CivilizationBinaryType.String100, "identifier"),
            Field("health", 120, CivilizationBinaryType.UInt32, "lifepoints"),
            Field("minimumRange", 128, CivilizationBinaryType.Float32, "minimumAttackRange"),
            Field("attackRange", 132, CivilizationBinaryType.Float32, "maximumAttackRange"),
            Field("sight", 136, CivilizationBinaryType.Float32, "lineOfSight"),
            Field("attackInterval", 140, CivilizationBinaryType.Float32, "reloadTime"),
            Field("splashRadius", 144, CivilizationBinaryType.Float32, "detonationRadius"),
            Field("attackType", 192, CivilizationBinaryType.UInt32, "weaponToHitId; row reference to dbweapontohit.dat"),
            Field("attack", 196, CivilizationBinaryType.UInt32, "damage"),
            Field("armorShock", 200, CivilizationBinaryType.UInt32, "armorAgainstShock"),
            Field("armorArrows", 204, CivilizationBinaryType.UInt32, "armorAgainstArrows"),
            Field("armorPierce", 208, CivilizationBinaryType.UInt32, "armorAgainstPierce"),
            Field("armorGuns", 212, CivilizationBinaryType.UInt32, "armorAgainstGuns"),
            Field("projectile", 224, CivilizationBinaryType.UInt32, "projectileObjectId"),
            Field("targetFlags", 228, CivilizationBinaryType.UInt32, "targetFlags; raw mask, individual bit meanings not verified"),
            Field("modelScaleFactor", 236, CivilizationBinaryType.Float32, "modelScaleFactor"),
            Field("icon", 300, CivilizationBinaryType.UInt32, "buttonId"),
            Field("graphicsId", 304, CivilizationBinaryType.UInt32, "graphicsId"),
            Field("buildCoefficient", 308, CivilizationBinaryType.Float32, "buildingRate; raw parameter, conversion to time or throughput not verified"),
            Field("requiredSpace", 408, CivilizationBinaryType.UInt32, "requiredSpace; raw parameter, geometry and units not verified"),
            Field("resourceTypeId", 412, CivilizationBinaryType.UInt32, "resourceType"),
            Field("resourceAmount", 416, CivilizationBinaryType.Float32, "resourceAmount"),
            Field("transportCapacity", 436, CivilizationBinaryType.UInt32, "transportCount"),
            Field("hudShowAttackRadius", 440, CivilizationBinaryType.BooleanByte, "showAttackRadius"),
            Field("hudShowRepairValue", 441, CivilizationBinaryType.BooleanByte, "showRepairValue"),
            Field("hudShowArmorShock", 442, CivilizationBinaryType.BooleanByte, "showArmorAgainstShock"),
            Field("hudShowArmorPierce", 443, CivilizationBinaryType.BooleanByte, "showArmorAgainstPierce"),
            Field("hudShowArmorArrows", 444, CivilizationBinaryType.BooleanByte, "showArmorAgainstArrows"),
            Field("hudShowArmorLaser", 445, CivilizationBinaryType.BooleanByte, "showArmorAgainstLaser; HUD visibility only"),
            Field("hudShowArmorGuns", 446, CivilizationBinaryType.BooleanByte, "showArmorAgainstGuns"),
            Field("hudShowArmorMissiles", 447, CivilizationBinaryType.BooleanByte, "showArmorAgainstMissiles; HUD visibility only"),
            Field("hudShowAttackRange", 448, CivilizationBinaryType.BooleanByte, "showAttackRange")
        };
        if (unit)
        {
            fields.AddRange([
                Field("speed", 148, CivilizationBinaryType.Float32, "velocity; raw movement coefficient"),
                Field("acceleration", 152, CivilizationBinaryType.Float32, "acceleration; raw coefficient"),
                Field("turretTurnSpeed", 164, CivilizationBinaryType.Float32, "turretTurnSpeed; raw coefficient"),
                Field("fuelCapacity", 176, CivilizationBinaryType.UInt32, "fuelCapacity; raw capacity"),
                Field("population", 432, CivilizationBinaryType.UInt32, "populationCount"),
                Field("gatherFieldFoodCoefficient", 316, CivilizationBinaryType.Float32, "fieldFoodGatherRate; raw parameter, conversion to time or throughput not verified"),
                Field("gatherGoldCoefficient", 320, CivilizationBinaryType.Float32, "goldGatherRate; raw parameter, conversion to time or throughput not verified"),
                Field("gatherStoneCoefficient", 324, CivilizationBinaryType.Float32, "stoneGatherRate; raw parameter, conversion to time or throughput not verified"),
                Field("gatherWoodCoefficient", 328, CivilizationBinaryType.Float32, "woodGatherRate; raw parameter, conversion to time or throughput not verified"),
                Field("gatherAnimalFoodCoefficient", 332, CivilizationBinaryType.Float32, "animalFoodGatherRate; includes fish; raw parameter, conversion to time or throughput not verified"),
                Field("gatherTreeFoodCoefficient", 336, CivilizationBinaryType.Float32, "treeFoodGatherRate; raw parameter, conversion to time or throughput not verified"),
                Field("steeringRotation", 464, CivilizationBinaryType.Float32, "steeringRotation; raw parameter, angular units not verified"),
                Field("altitude", 472, CivilizationBinaryType.Float32, "altitude; raw parameter, distance units not verified")
            ]);
        }
        return fields.AsReadOnly();
    }

    internal static int ValidateRecord(byte[] database, int sourceObjectId, CivilizationCategory category)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (category is not (CivilizationCategory.Units or CivilizationCategory.Buildings))
            throw new NotSupportedException("Only source units and buildings use object bindings.");
        if (database.Length < 4) throw new InvalidDataException("The object database header is truncated.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(database.AsSpan(0, 4));
        if (count is 0 or > 100_000 || database.Length != 4L + count * (long)RecordSize)
            throw new InvalidDataException("The object database does not have the supported 0x790 record layout.");
        if (sourceObjectId < 0 || (uint)sourceObjectId >= count)
            throw new ArgumentOutOfRangeException(nameof(sourceObjectId), "The source object is outside the database.");
        var offset = checked(4 + sourceObjectId * RecordSize);
        if (BinaryPrimitives.ReadUInt32LittleEndian(database.AsSpan(offset + RecordIdentityOffset, 4)) !=
            (uint)sourceObjectId)
            throw new InvalidDataException("The source object's stored identity does not match its row index.");
        var name = ReadIdentifier(database.AsSpan(offset, 100));
        if (name.Length == 0 || database[offset + 232] == 0)
            throw new InvalidDataException("The source object does not have an available named game descriptor.");
        var isBuilding = name.StartsWith("b ", StringComparison.OrdinalIgnoreCase);
        if (isBuilding != (category == CivilizationCategory.Buildings))
            throw new InvalidDataException("The source object does not match its catalog category.");
        return offset;
    }

    private static string ReadIdentifier(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end >= 0) bytes = bytes[..end];
        foreach (var value in bytes)
            if (value < 32 || value > 126)
                throw new InvalidDataException("The source object identifier is not printable ASCII.");
        return Encoding.ASCII.GetString(bytes).Trim();
    }

    private static string ReadFloat(byte[] bytes, int offset, double scale)
    {
        var value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)));
        if (!float.IsFinite(value)) throw new InvalidDataException("A source object field contains a non-finite value.");
        return scale == 1 ? value.ToString("R", CultureInfo.InvariantCulture) : FormatNumber(value, scale);
    }

    private static string FormatNumber(double number, double scale) =>
        (number / scale).ToString("R", CultureInfo.InvariantCulture);
}
