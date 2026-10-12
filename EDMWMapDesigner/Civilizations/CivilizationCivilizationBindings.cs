using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace EDMWMapDesigner.Civilizations;

/// <summary>Verified Phoenix-table fields; methods modify only the supplied byte buffer.</summary>
public static class CivilizationCivilizationBindings
{
    public const int RecordSize = 344;
    private const string SchemaEvidence = "aczwink/EDMW-Modding-Tools db/dbphoenixcivtable.xml; installed x86 loader 0x0051E99D";
    private const string AgeEvidence = "Loader 0x0051EA47/0x0051EA50 maps disk +212/+216 to runtime +0x54/+0x58; enumeration 0x0051E89D/0x0051E8AC checks requested age against both bounds";

    public static IReadOnlyList<CivilizationBinaryField> Fields { get; } = Array.AsReadOnly(new[]
    {
        new CivilizationBinaryField("overview.internalName", 8, CivilizationBinaryType.String100, Evidence: SchemaEvidence),
        new CivilizationBinaryField("assets.nameStringId", 208, CivilizationBinaryType.UInt32, Evidence: SchemaEvidence),
        new CivilizationBinaryField("age.initialAge", 212, CivilizationBinaryType.Int32, Evidence: AgeEvidence),
        new CivilizationBinaryField("age.finalAge", 216, CivilizationBinaryType.Int32, Evidence: AgeEvidence)
    });

    public static bool CanWrite(string key) => Fields.Any(field => field.Key == key) ||
        key is "overview.allowedAges" or "age.supportedAges";

    public static IReadOnlyDictionary<string, string> ReadValues(byte[] database, int sourceRowIndex)
    {
        var record = GetRecord(database, sourceRowIndex);
        var lower = BinaryPrimitives.ReadInt32LittleEndian(record[212..]);
        var upper = BinaryPrimitives.ReadInt32LittleEndian(record[216..]);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["overview.internalName"] = ReadIdentifier(record.Slice(8, 100)),
            ["assets.nameStringId"] = BinaryPrimitives.ReadUInt32LittleEndian(record[208..]).ToString(CultureInfo.InvariantCulture),
            ["age.initialAge"] = lower.ToString(CultureInfo.InvariantCulture),
            ["age.finalAge"] = upper.ToString(CultureInfo.InvariantCulture)
        };
        if (lower >= 0 && upper >= lower && upper - lower <= 100)
        {
            var ages = string.Join(", ", Enumerable.Range(lower, upper - lower + 1));
            values["overview.allowedAges"] = ages;
            values["age.supportedAges"] = ages;
        }
        // The chooser applies bounds to the requested starting age and uses rows 1..11.
        // There is no separate verified selectable flag, so this derived value is read-only.
        values["overview.selectable"] = (sourceRowIndex is > 0 and < 12 &&
            lower <= upper && lower <= 9 && upper >= 5).ToString().ToLowerInvariant();
        return values;
    }

    public static void WriteValue(byte[] database, int sourceRowIndex, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (!CanWrite(key)) throw new InvalidDataException($"Civilization field '{key}' has no verified writable mapping.");
        var record = GetRecord(database, sourceRowIndex);
        if (string.IsNullOrWhiteSpace(value)) return;
        if (key == "overview.internalName")
        {
            if (value.Length > 99 || value.Any(character => character is < ' ' or > '~'))
                throw new InvalidDataException("The internal civilization identifier requires 1 to 99 printable ASCII characters.");
            record.Slice(8, 100).Clear();
            Encoding.ASCII.GetBytes(value, record.Slice(8, 100));
            return;
        }
        if (key is "overview.allowedAges" or "age.supportedAges")
        {
            var (lower, upper) = ParseAgeRange(value);
            BinaryPrimitives.WriteInt32LittleEndian(record[212..], lower);
            BinaryPrimitives.WriteInt32LittleEndian(record[216..], upper);
            return;
        }
        if (key == "assets.nameStringId")
        {
            if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var stringId) || stringId > 65535)
                throw new InvalidDataException("A civilization localization reference must be an integer from 0 to 65535.");
            BinaryPrimitives.WriteUInt32LittleEndian(record[208..], stringId);
            return;
        }
        var age = ParseAge(value);
        BinaryPrimitives.WriteInt32LittleEndian(record[(key == "age.initialAge" ? 212 : 216)..], age);
    }

    public static void ValidateRow(byte[] database, int sourceRowIndex)
    {
        var record = GetRecord(database, sourceRowIndex);
        var lower = BinaryPrimitives.ReadInt32LittleEndian(record[212..]);
        var upper = BinaryPrimitives.ReadInt32LittleEndian(record[216..]);
        if (lower < 0 || upper < lower || upper > 100)
            throw new InvalidDataException("The civilization's age interval is invalid.");
    }

    private static Span<byte> GetRecord(byte[] database, int sourceRowIndex)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.Length < 4) throw new InvalidDataException("The Phoenix civilization table has no record count.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(database);
        if (count is <= 0 or > 100_000 || database.LongLength != 4L + (long)count * RecordSize)
            throw new InvalidDataException("The Phoenix civilization table has an unsupported record layout.");
        if (sourceRowIndex < 0 || sourceRowIndex >= count)
            throw new InvalidDataException("The source civilization row is outside the Phoenix table.");
        var record = database.AsSpan(4 + sourceRowIndex * RecordSize, RecordSize);
        if (BinaryPrimitives.ReadInt32LittleEndian(record[4..]) != sourceRowIndex ||
            BinaryPrimitives.ReadInt32LittleEndian(record) <= 0)
            throw new InvalidDataException("The Phoenix civilization row identity does not match its source reference.");
        return record;
    }

    private static string ReadIdentifier(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException("The civilization identifier is not terminated.");
        bytes = bytes[..end];
        foreach (var value in bytes)
            if (value is < 32 or > 126) throw new InvalidDataException("The civilization identifier is not printable ASCII.");
        return Encoding.ASCII.GetString(bytes);
    }

    private static int ParseAge(string value)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var age) || age is < 0 or > 100)
            throw new InvalidDataException("Use an integer age ID from 0 to 100 (playable EDMW ages are 5 through 9).");
        return age;
    }

    private static (int Lower, int Upper) ParseAgeRange(string value)
    {
        var rangeParts = value.Trim().Split(['-', '–', '…'], StringSplitOptions.TrimEntries);
        if (rangeParts.Length == 2)
        {
            var lower = ParseAge(rangeParts[0]);
            var upper = ParseAge(rangeParts[1]);
            if (lower > upper) throw new InvalidDataException("The civilization age range must be ascending.");
            return (lower, upper);
        }
        if (rangeParts.Length > 2) throw new InvalidDataException("Use an ascending age range or a contiguous list of age IDs.");
        var ages = value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseAge).ToArray();
        if (ages.Length == 0 || ages.Distinct().Count() != ages.Length)
            throw new InvalidDataException("Use distinct, contiguous age IDs.");
        Array.Sort(ages);
        if (ages[^1] - ages[0] + 1 != ages.Length)
            throw new InvalidDataException("The game civilization table supports a contiguous age interval.");
        return (ages[0], ages[^1]);
    }
}
